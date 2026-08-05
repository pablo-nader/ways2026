# Design: Stage 7 — Cuenta corriente y reliquidación a precio del día

## Technical Approach

Stage 5 opened the ledger, stage 6 pinned the serialization point of the drawer. Stage 7 adds
the third structural idea: **the `clientes` row is the serialization point of every path that
touches a balance**, and it is taken with the *same* discipline stage 6 applied to the turno —
lock first, derive under the lock, commit once.

The centerpiece is again **one derivation, no second copy**. `ReliquidadorDeConsumos` is a
DB-free Domain class fed by one Application reader (`LectorDeConsumosReliquidables`); the
preview endpoint and the commit call the **same** object with the same inputs. A re-pricing
formula that exists twice is the single defect that would turn this feature into a trust
incident, so it exists once.

The second idea is **containment**. `ServicioDeVentas.EmitirAsync` is the project's most
guarded transaction and stage 6 already paid a dedicated judgment-day round to touch it. Stage 7
therefore does **not** thread an itemless comprobante through it (decision 1). What it does
instead is *extract* the two raw statements that own the saldo into a shared writer, so the new
service and the sale keep exactly **one** `UPDATE clientes … RETURNING` in the codebase.

Everything else is stage-5/6 posture reused verbatim: decide-then-commit, `EstrategiaSinReintento`
for every manual op, atomic `UPDATE … RETURNING` as the only state-transition authority,
`FOR SHARE` on the turno as the first statement wherever a turno participates, `db-error-backstops`
per constraint, RLS + manual tenant filter, `ManejadorDeErrores` mapping, pure Domain first.

## The Re-Pricing Derivation (binding — one formula)

Verified against what the schema actually stores (`ItemComprobanteVenta`): `precio_unitario` is
the pre-discount price of the day of sale, `descuento` is the **line-level discount amount**
already applied, `total = cantidad × precio_unitario − descuento`, and `id_oferta` only *names*
the highest-priority offer (accumulated offers snapshot one id but the **full** discount). So the
"offer applied" signal for reliquidación is `descuento > 0`, **never** `id_oferta IS NOT NULL`.

For each eligible line `i` of comprobante `c`:

```
totalHistorico(i) = items.total                              (el snapshot, nunca recalculado)
totalDelDia(i)    = round(cantidad × precioActual, 2, AwayFromZero)      ← SIN descuento
delta(i)          = totalDelDia(i) − totalHistorico(i)
```

which decomposes exactly into the legacy's two terms
(`cuenta-corriente.php:70-93`): `delta(i) = cantidad × (precioActual − precioVendido) + descuento`
— re-pricing **plus the annulled discount**. Worked example, 10 units sold at 100 with a 10 %
offer, current price 150: `totalHistorico = 900`, `totalDelDia = 1500`, `delta = 600` = 500 of
re-pricing + 100 of annulled discount. The offer line's delta is **larger** than a plain
re-price, which is the direction doc-01:398 demands and the direction a misread would invert.

**Financed fraction** (declared deviation, flagged): a comprobante may be part cash, part fiado.
`factor(c) = min(1, consumo.importe / comprobante.total)`, and
`delta(c) = round(Σ delta(i) × factor(c), 2, AwayFromZero)`. Only the financed money is
re-indexed. With full financing `factor = 1` and the formula collapses to the legacy's, asserted
by a unit test. Charging 100 % of the delta on a 20 %-financed sale is an over-charge, and the
project already refused the legacy's twin defect (re-pricing fully-cash tickets) by scanning
`Consumo` movements instead of every ticket.

`delta = Σ delta(c)`. **One** `ActualizacionPrecios` movement carries it.

**Lines that cannot be re-priced are skipped, never fatal**: `id_articulo IS NULL` (free-concept
line, doc-10 §4) or no vigente price in the client's lista. The legacy multiplies by a NULL price
and *credits* the whole line — arithmetically catastrophic. Aborting is worse than it looks: the
marker is per-consumo, so one discontinued article would freeze the client's reliquidación
forever and kill the anti-inflation mechanism with no operator remedy. Skipping is neutral, the
detail names every skipped line, and a compensating `Ajuste` is the documented remedy.

**Eligibility** (all predicates required): `tipo = 'consumo'`, `id_movimiento_actualizacion IS NULL`,
`importe > 0`, the comprobante is `estado = 'emitido'` (**an anulado sale must never be
re-priced** — stage 5 leaves its `Consumo` row in place and writes a contra-`Ajuste`, so without
this predicate the ledger would re-index cancelled debt) and `comprobante.total > 0`. Ordered by
`fecha` ASC, capped at **500 consumos per run** (`hayMas: true` when more remain) — one number
that bounds the lock hold time, the transaction size and the `detalle` payload at once.

**Zero delta ⇒ no movement and no marker.** The run is a plain no-op (200 with an empty result,
never a 409), so nothing is consumed and the same consumos are re-evaluated against the prices of
the day the client actually pays. Zero eligible consumos behaves identically.

## Architecture Decisions

| # | Decision | Alternatives considered | Rationale |
|---|---|---|---|
| 1 | **A new lean `ServicioDeCuentaCorriente` / `ServicioDeReliquidacion` owns the write paths; `EmitirAsync` is not threaded.** The two raw statements that own the balance (`ActualizarSaldoClienteAsync`, `InsertarMovimientoCcAsync`) are **extracted verbatim** to `Ways.Application.CuentaCorriente.EscriturasDeCuentaCorriente`, and `ServicioDeVentas` calls them from there | (a) A special-case RC plan through `EmitirAsync` (max reuse); (b) a bare ledger INSERT with no comprobante | (a) needs branches at `ExigirLineasValidas` (throws on zero lines), the oferta resolution, `MaterializarItems`, `ValidadorDePagos`, and the CC loop — whose condition **inverts** (the movement comes from the *physical* pagos, not from a CC medio). Six branches in the revenue path, each a new way to break TX/NCX, for a flow with no items, no stock, no ofertas and no CC medio: reuse of a shape that does not fit. (b) is already refused by proposal decision 1 (cash invisible to the arqueo). The extraction is what actually buys safety — it keeps **one** `UPDATE clientes … saldo … RETURNING` in the codebase, which is the invariant worth defending; the rest is 150 lines of a transaction that reuses `AsignadorDeNumeroComprobante`, `ServicioDeTurnos.ExigirTurnoAbiertoBajoLockAsync` and `ManejadorDeErrores` as-is |
| 2 | **The marker is `movimientos_cuenta_corriente.id_movimiento_actualizacion integer NULL`, a self-FK to the `ActualizacionPrecios` row** | (a) `reliquidado boolean`; (b) derive from timestamps / the movement's `detalle` | Same cost as a boolean (one nullable int + one index) and strictly more information: it answers *which* reliquidación covered *which* consumo, which is exactly the audit the legacy encoded in its `articulos` blob and doc-10 §8 replaced with an auditable ledger. It also gives the anulación guard (decision 5) a free in-memory check. (b) is fragile: a timestamp comparison has no atomicity story against a consumo committing during the run |
| 3 | **The re-pricer reads current prices through `ServicioDePrecios.PreciosVigentesEnLoteAsync`, never through `ServicioDeOfertas.ResolverAsync`** | Reuse the checkout's oferta resolver for symmetry with the sale | `ResolverAsync` applies **today's** offers. Reliquidación annuls discounts (doc-01:398); running the offer resolver would re-apply a discount the rule exists to remove — the exact inversion the risk table ranks Med-High. The lote reader is also the leaner tool: 3 queries, constant in N articles × 1 lista, `decimal?` for "no vigente price" already modelled |
| 4 | **Reliquidación requires NO open turno; its first statement is `SELECT … FROM clientes … FOR UPDATE`, and the eligibility scan runs after it, inside the same transaction** | (a) Require an open turno for symmetry with the pago; (b) scan first, lock later; (c) advisory lock per cliente | It moves no physical money, contributes no term to `CalculadorDeArqueo`, and requiring a turno would be theater. Locking first is stage 6's close-first lesson transplanted: with (b) a sale could commit a `Consumo` between the scan and the saldo update, and that consumo would be either double-counted or silently marked. Under (a)…(c)'s alternative orders the guarantee is a race; here it is a consequence of the statement order. (c) buys nothing the row lock does not already give, and the row lock is the one every other writer already takes |
| 5 | **`ServicioDeVentas.AnularAsync` keeps ownership of every comprobante anulación**; the widening is three lines: the contramovimiento filter becomes `Tipo == Consumo \|\| Tipo == Pago`, `id_pago_comprobante` becomes nullable **per tipo** (a `Consumo` must have one, a `Pago` must not), and a reliquidated consumo raises `409 consumo_reliquidado` | The new service owning RC anulación, consistent with decision 1 | Two writers racing on `comprobantes_venta.estado` is precisely the defect class stage 6 spent a whole decision killing; the atomic `UPDATE … WHERE estado = 'emitido'` must stay the single authority. The reversal arithmetic is already sign-generic (`-consumo.Importe` restores a debt from a negative `Pago` with no branch). The `consumo_reliquidado` guard closes a real leak: anulando an already-re-priced sale would reverse the consumo and leave the `ActualizacionPrecios` delta on the books. It costs **zero extra queries** (the movements are already loaded) and throwing there rolls back the state transition, same posture as the credit-limit backstop |
| 6 | **A sibling pure validator, `ValidadorDePagoACuenta`, instead of branching `ValidadorDePagos`**; the request has **no importe field** — `importeAplicado = Σ importe − Σ vuelto` | Reuse `ValidadorDePagos` with an RC flag (as the proposal sketched) | **`tolerancia_pago` must not apply to a payment.** Reusing rule 2 would let 4 995 received settle 5 000 of debt — crediting money never received, in the one flow whose entire purpose is that money did arrive. Rules 5, 6 and 8 are equally wrong or vacuous here. Seven rules with an observable rejection order, zero branches in the sale's most-tested pure class. Deriving the applied amount (legacy parity, `cuenta-corriente.php:11`) is also the structural answer to "no endpoint accepts a total": there is no field to send |
| 7 | **Numeración needs no new mechanism** — `numeraciones_comprobante` is keyed by `(id_punto_venta, tipo_comprobante)` with the **code** as the key and lazy row creation, so `RC` gets its own series for free. `AsignarNumeroComprometidoAsync` is promoted from a private method of `ServicioDeVentas` to `AsignadorDeNumeroComprobante.AsignarComprometidoAsync` (pure move) | A shared series with `TX`; the counter inside the write transaction | Proposal decision 11. Keeping the number committed in its own transaction preserves the pinned semantics "the number is consumed even if the rest fails; gaps accepted, duplicates never". No idempotency re-read is needed here (unlike `EmitirAsync`) because `EstrategiaSinReintento` never replays — same residual as stage-5 anulación and stage-6 cierre |
| 8 | **`Ajuste` needs no schema change and no CHECK.** `detalle` is required with `length(btrim(…)) >= 5` **in Domain** (`ReglaDeAjusteDeCuenta`); a manual ajuste is distinguished from an anulación contramovimiento **structurally**: `id_comprobante_venta IS NULL` ⇒ manual, `IS NOT NULL` ⇒ anulación (the read model derives that label) | `ck_movimientos_cuenta_corriente_detalle_de_ajuste` + backfilling the detalle of existing anulación rows | Stage-5 anulación already writes `tipo = 'ajuste'` rows with a NULL detalle, so the CHECK would either fail on every existing database or require a `NOT VALID` escape **plus** another write-path change inside `ServicioDeVentas` — paying twice to guard a descriptive column. Deriving the label from data that already exists costs nothing and touches nothing |
| 9 | **One `GET` returns header + page**, and the running balance is the stored `saldo_resultante`, never re-derived | Two endpoints; a window function; the legacy's balance running backwards from today's saldo | The header (saldo / acuerdo / disponibilidad) is read from the same `clientes` row in the same request, so it cannot drift from the ledger the operator is looking at. The legacy's backward-running balance is *wrong under a filter* (it starts from today's saldo whatever the window shows); the stored snapshot is right under any filter and any page. `disponibilidad` is `decimal?` — `NULL` ⇒ "ilimitado", never a fabricated number. The legacy's silent "if the filter returns 0 rows, show the unfiltered query" (`cuenta-corriente.php:134`) is **not** reproduced |

## Table Shapes (DB CHANGE GATE)

### A — `movimientos_cuenta_corriente` (existing operativa table, additive)

| Object | Shape | Justification vs doc 10 |
|---|---|---|
| `id_movimiento_actualizacion` | `integer NULL` | Additive, nullable, no default, no backfill. Doc-10 §8 lists the ledger's business columns; the marker is the persistence of §8's own sentence *"recorre los consumos **no actualizados**"* — the predicate had no column. Written **only** by step 8 of the reliquidación transaction, inside the same transaction as the movement it points at |
| `fk_movimientos_cuenta_corriente_actualizacion` | `(id_movimiento_actualizacion, id_tenant) → movimientos_cuenta_corriente (id_movimiento, id_tenant)`, `ON DELETE RESTRICT` | Composite with `id_tenant`, same family as every other FK of this table (`fk_…_cliente`, `fk_…_comprobante_venta`). Requires the alternate key `(id_movimiento, id_tenant)` on this table — added in the same migration, mirroring `ak_turnos_caja_id_turno_caja_id_tenant` |
| `ix_movimientos_cuenta_corriente_consumos_pendientes` | `(id_cliente, id_tenant) WHERE tipo = 'consumo' AND id_movimiento_actualizacion IS NULL` | The eligibility scan **is** this predicate. Partial so it shrinks as consumos get reliquidated (self-vacuuming, the opposite of a growing ledger index). No second index for the reverse audit lookup: the drill-down always filters by `id_cliente`, already served by `ix_movimientos_cuenta_corriente_cliente_fecha` |

RLS is per-table and already enabled on `movimientos_cuenta_corriente` (stage 5) — a new column
inherits it, no policy change. `WaysDbContext`'s manual tenant filter is unchanged.
**No new enum values**: `pago` and `actualizacion_precios` already exist in `tipo_movimiento_cc`.

### B — `tipos_comprobante` (global catalog, one row)

| Row | Values | Justification |
|---|---|---|
| `RC` | `clase = 'venta'`, `nombre = 'Recibo de cobranza'`, `letra NULL`, `signo = +1`, `discrimina_iva = false`, `es_fiscal = false`, `afecta_stock = false`, `activo = true` | Doc-10 §1 shape, `PRE` is the precedent for `letra NULL` + `afecta_stock false`. `signo = +1` because money comes **in**; the negative sign lives in the CC movement, never in the comprobante total. `es_fiscal = false` keeps it inside `ResolverTipoComprobanteAsync`'s POS filter without relaxing it |

Shipped **twice**: (1) appended to `TiposComprobanteBase` for fresh databases, and (2) as an
idempotent `INSERT INTO tipos_comprobante (…) SELECT … WHERE NOT EXISTS (SELECT 1 FROM
tipos_comprobante WHERE codigo = 'RC')` inside the migration — the seeder only runs when the
table is **empty** (`InicializadorDeBaseDeDatos.cs:417`), so a stage-6 database would never get
the row otherwise. `codigo` is `citext` under `ux_tipos_comprobante_codigo (codigo) WHERE
deleted_at IS NULL`, so the guard is exact and the statement is safe to re-run.

**Nothing else.** No new table, no new enum, no new CHECK, no new unique index — and therefore
**zero new branches in `ManejadorDeErrores`**. Migration name: `CuentaCorrienteEtapa7`.

## Transactions (binding statement order)

```
── PAGO A CUENTA (RC) ────────────────────────────────────────────────────────────
  fuera: momento := reloj.Ahora ; cliente (404 ADR-8, CF ⇒ 400 cliente_sin_cuenta_corriente)
         punto de venta (404) ; turno abierto (409 turno_no_abierto, ANTES de todo lo demás)
         medios + vuelto_maximo ; ValidadorDePagoACuenta.Validar(...) ⇒ importeAplicado
         numero := AsignadorDeNumeroComprobante.AsignarComprometidoAsync('RC')   ← tx propia
  EstrategiaSinReintento ⇒ BEGIN
   1. ExigirTurnoAbiertoBajoLockAsync(turno)                    ← FOR SHARE, primer statement
   2. INSERT comprobantes_venta (RC, numero, total = importeAplicado, id_turno_caja, cliente)
   3. INSERT pagos_comprobante (N filas, mismo SaveChanges)     ← sin items, sin stock
   4. nuevoSaldo := EscriturasDeCuentaCorriente.ActualizarSaldo(−importeAplicado)  ← lock cliente
   5. INSERT movimiento (pago, id_comprobante_venta, id_pago_comprobante NULL,
                         importe = −importeAplicado, saldo_resultante = nuevoSaldo)
  COMMIT

── RELIQUIDACIÓN ─────────────────────────────────────────────────────────────────
  fuera: momento ; cliente (404 / CF ⇒ 400) ; punto de venta (404)   ← sin turno (decisión 4)
  EstrategiaSinReintento ⇒ BEGIN
   1. SELECT saldo, id_lista_precio FROM clientes WHERE … FOR UPDATE      ← lock #1
   2. SELECT consumos elegibles (JOIN comprobantes_venta, estado='emitido'), LIMIT 500
   3. SELECT items WHERE id_comprobante_venta = ANY($1)                   ← 1 consulta
   4. PreciosVigentesEnLoteAsync(articulos, [id_lista_precio], momento)   ← ≤ 3 consultas
   5. resultado := ReliquidadorDeConsumos.Calcular(...)          ← PURO, la única fórmula
      delta = 0 ⇒ COMMIT sin escribir nada (no-op, 200)
   6. nuevoSaldo := UPDATE clientes SET saldo = saldo + delta … RETURNING
   7. INSERT movimiento (actualizacion_precios, importe = delta, detalle = auditoría) RETURNING id
   8. UPDATE movimientos_cuenta_corriente SET id_movimiento_actualizacion = $id
        WHERE id_movimiento = ANY($ids) AND id_movimiento_actualizacion IS NULL
      rowcount ≠ |ids| ⇒ throw (imposible bajo el lock; defensa en profundidad)
  COMMIT

── AJUSTE MANUAL ─────────────────────────────────────────────────────────────────
  fuera: ReglaDeAjusteDeCuenta (importe ≠ 0, detalle ≥ 5 chars) ; cliente ; punto de venta
  EstrategiaSinReintento ⇒ BEGIN
   1. nuevoSaldo := UPDATE clientes SET saldo = saldo + importe … RETURNING   ← lock cliente
   2. INSERT movimiento (ajuste, id_comprobante_venta NULL, detalle)
  COMMIT
```

**Lock order (total, unchanged from stage 6): `turnos_caja` → `clientes` → ledger INSERT.**
Reliquidación and ajuste take a *suffix* of that order, so the order stays total and deadlock-free.

**Concurrency guarantees.**

- *Reliquidación × venta*: the sale's saldo `UPDATE` blocks on step 1's row lock. A consumo that
  commits **after** the read set is frozen carries no marker and stays eligible for the next run
  — no lost consumo, no double count. One that committed before step 1 is visible to step 2
  (READ COMMITTED, the scan runs after the lock). Rendezvous test asserts exactly this.
- *Two reliquidaciones*: the loser blocks at step 1, re-scans the committed state, sees the
  markers, finds an empty set and returns a clean no-op. Exactly one movement, no 409 needed.
- *Reliquidación × pago / pago × pago*: serialized on the same row lock; payments are additive.
- *Pago a cuenta × cierre de turno*: inherits stage 6 decision 1 verbatim — the pago is either
  counted in the arqueo or gets `409 turno_no_abierto`, never neither.

**Failure semantics.** Any throw rolls back saldo, marker and ledger together — the marker is set
in the **same** transaction as the movement it points at, so "marked but not charged" and
"charged but not marked" are both unrepresentable. Only the RC's número survives a failure
(consumed, gap accepted), exactly like a sale.

**Read budget**: reliquidación issues a **constant** ≤ 8 queries regardless of the number of
consumos or lines; the pago a cuenta ≤ 7. Guarded by the existing `DbCommand` interceptor test.

## API Surface (ADR-8: uniform 404 cross-tenant)

| Endpoint | Policy | Notes |
|---|---|---|
| `GET /api/clientes/{id}/cuenta-corriente?desde=&hasta=&historico=` | `OperacionDePos` | Header + movements in one payload (decision 9; no pagination — bound to tasks/spec at verify). No implicit date window — the screen sends last-month by default and clears it for "ver histórico" |
| `POST /api/clientes/{id}/cuenta-corriente/pagos` | `OperacionDePos` | `{ idPuntoVenta, pagos: [{ idMedioPago, importe, referencia?, vuelto? }], observaciones? }` — **no importe field** → 201 with the RC comprobante |
| `POST /api/clientes/{id}/cuenta-corriente/ajustes` | **`SupervisionDeCuentaCorriente`** | `{ idPuntoVenta, importe, detalle }` |
| `GET /api/clientes/{id}/cuenta-corriente/reliquidacion` | **`SupervisionDeCuentaCorriente`** | Preview — same `ReliquidadorDeConsumos`, no lock, never authoritative |
| `POST /api/clientes/{id}/cuenta-corriente/reliquidacion` | **`SupervisionDeCuentaCorriente`** | `{ idPuntoVenta }` → the movement or a no-op result |
| `POST /api/ventas/{id}/anular` | `OperacionDePos` | Unchanged route; learns `Pago` and `409 consumo_reliquidado` |

`Politicas.SupervisionDeCuentaCorriente` (Supervisor + Admin) is the new constant — scoped to CC supervision per the spec files, on
purpose so the deferred cierre tightening (stage-6 open question) can stack on it without a second
policy. The stage-5 `SuperficieDeAutorizacionTests` allowlist gains the four new non-GET routes.
**Reliquidación is not anulable structurally**: no route addresses a movimiento, and
`AnularAsync` only ever loads movements with `IdComprobanteVenta == id`, which an
`ActualizacionPrecios` row never has.

## Backstop Map (db-error-backstops)

| Constraint | Mapping | Test |
|---|---|---|
| `fk_movimientos_cuenta_corriente_actualizacion` | Generic `fk_` prefix → `400 referencia_invalida` — **no code change** | Raw-SQL 23503 only. Unreachable normally: the id comes from step 7's `RETURNING` in the same transaction |
| `ux_comprobantes_venta_numero`, `ux_tipos_comprobante_codigo` | Already mapped (stage 5 / stage 1) | The idempotent insert is proven on a **stage-6-migrated** database, not assumed |
| New Domain codes: `cliente_sin_cuenta_corriente` (400), `pago_a_cuenta_sin_medios_fisicos` (400), `pago_a_cuenta_sin_importe` (400), `ajuste_detalle_requerido` (400), `consumo_reliquidado` (409) | Raised by pure Domain / the services, never by a constraint | Unit + integration per code |

Genuinely racy surfaces, honestly: **three**, each with a rendezvous test — reliquidación × venta,
two reliquidaciones, pago a cuenta × cierre. Everything else is schema defense.

## Data Flow

```
  clientes.id_lista_precio ──┐
  items_comprobante_venta ───┼──→ LectorDeConsumosReliquidables ──→ ReliquidadorDeConsumos (PURO)
  precios (lote, 3 queries) ─┘                │                              │        │
                                     GET …/reliquidacion (preview) ──────────┘        │
                                     POST …/reliquidacion (bajo FOR UPDATE) ──────────┘
                                              │
                                              ├─→ movimientos_cuenta_corriente (1 fila, actualizacion_precios)
                                              ├─→ marker en los consumos cubiertos
                                              └─→ clientes.saldo (UPDATE … RETURNING)

  POS ──POST …/pagos──→ ServicioDeCuentaCorriente
          │ turno abierto (409) → RC comprobante → pagos_comprobante ──→ CalculadorDeArqueo
          └─────────────────────────────────────→ movimiento pago (−) ──→ clientes.saldo
```

## File Changes

| File | Action | Description |
|---|---|---|
| `src/Ways.Domain/CuentaCorriente/ReliquidadorDeConsumos.cs` (+ input/result records) | Create | The pure re-pricer — the stage's centerpiece |
| `src/Ways.Domain/CuentaCorriente/CalculadorDeEstadoDeCuenta.cs` | Create | Disponibilidad (`decimal?`) + movement labelling |
| `src/Ways.Domain/CuentaCorriente/ValidadorDePagoACuenta.cs`, `ReglaDeAjusteDeCuenta.cs` | Create | Pure, DB-free, observable rejection order |
| `src/Ways.Domain/CuentaCorriente/MovimientoCuentaCorriente.cs` | Modify | `IdMovimientoActualizacion`; `TipoMovimientoCc` loses its "reserved" doc-comment |
| `src/Ways.Application/CuentaCorriente/EscriturasDeCuentaCorriente.cs` | Create | The two raw statements **moved** out of `ServicioDeVentas` — one saldo writer in the codebase |
| `src/Ways.Application/CuentaCorriente/ServicioDeCuentaCorriente.cs` | Create | Pago a cuenta, ajuste, estado de cuenta |
| `src/Ways.Application/CuentaCorriente/ServicioDeReliquidacion.cs`, `LectorDeConsumosReliquidables.cs` | Create | Preview + commit through the same reader/calculator |
| `src/Ways.Application/Ventas/ServicioDeVentas.cs` | Modify | Delegates the two extracted statements; `Tipo == Consumo \|\| Tipo == Pago`; nullable `id_pago_comprobante` per tipo; `409 consumo_reliquidado` |
| `src/Ways.Application/Ventas/AsignadorDeNumeroComprobante.cs` | Modify | `AsignarComprometidoAsync` promoted from `ServicioDeVentas` (pure move) |
| `src/Ways.Infrastructure/…/MovimientoCuentaCorrienteConfiguration.cs` | Modify | Column, self-FK, alternate key, partial index |
| `src/Ways.Infrastructure/…/Migraciones/*_CuentaCorrienteEtapa7.cs` | Create | Column + FK + index + the idempotent `RC` insert |
| `src/Ways.Infrastructure/Persistencia/InicializadorDeBaseDeDatos.cs` | Modify | `RC` appended to `TiposComprobanteBase` (fresh databases) |
| `src/Ways.Api/Endpoints/CuentaCorrienteEndpoints.cs`, `Seguridad/Politicas.cs` | Create/Modify | Five routes + `SupervisionDeCuentaCorriente` |
| `src/Ways.Web/src/paginas/CuentaCorriente.tsx`, `src/api/cuentaCorriente.ts` | Create | Screen + pure mappers/preview mirror |
| `src/Ways.Web/src/paginas/Clientes.tsx`, `src/App.tsx` | Modify | Per-row entry point + `/clientes/:id/cuenta-corriente` route |
| `docs/10-modelo-de-datos.md` | Modify | §8 status note: etapa 7, the marker column, the financed-fraction deviation |

## Interfaces / Contracts

```csharp
// Ways.Domain.CuentaCorriente — puro, sin DB (mismo listón que CalculadorDeArqueo)
public readonly record struct LineaAReliquidar(
    int? IdArticulo, decimal Cantidad, decimal PrecioUnitario, decimal Descuento, decimal TotalHistorico);

public sealed record ConsumoAReliquidar(
    int IdMovimiento, int IdComprobanteVenta, decimal ImporteFinanciado, decimal TotalComprobante,
    IReadOnlyList<LineaAReliquidar> Lineas);

public sealed record ResultadoDeReliquidacion(
    decimal Delta, IReadOnlyList<int> IdsMovimientosCubiertos, IReadOnlyList<DetalleDeConsumo> Detalle, bool HayMas);

public static class ReliquidadorDeConsumos
{
    // precioActual: null ⇒ la línea se omite con motivo, nunca aborta ni acredita.
    public static ResultadoDeReliquidacion Calcular(
        IReadOnlyList<ConsumoAReliquidar> consumos, IReadOnlyDictionary<int, decimal?> precioActualPorArticulo);
}

// El request NO tiene ningún campo de importe (decisión 6).
public sealed record SolicitudDePagoACuenta(int IdPuntoVenta, IReadOnlyList<PagoDeCuenta> Pagos, string? Observaciones);
```

## Web Composition

`src/Ways.Web/src/paginas/CuentaCorriente.tsx` (route `/clientes/:id/cuenta-corriente`, entered
from a per-row action in `Clientes.tsx`): header (saldo / acuerdo — `"ilimitado"` when
`credito_ilimitado` / disponibilidad), desde–hasta + "ver histórico" filters, movement table
reading `saldoResultante` per row, and three action modals. `src/api/cuentaCorriente.ts` holds the
pure mappers and a **non-authoritative** disponibilidad mirror (same posture as `arqueo.ts`).
Role gating uses the existing `usuario?.rolId` claim (`ROL.Supervisor | ROL.Admin`) to render the
ajuste and reliquidación actions — cosmetic; `SupervisionDeCuentaCorriente` is the enforcement.

`react-async-state` obligations that carry weight here: rule 8 `key={idCliente}` on the subtree;
rule 9 first-line re-entrancy guard + full-window disable on **reliquidación** (irreversible, and
a double-submit charges the client twice) and on the pago; rule 6 a 2xx reliquidación is never
reported as failure — the post-write ledger refetch has its own try/catch and its own copy; rule 3
the ledger generation is bumped **before** every write; rule 7 medios de pago failing to load
produces a visible aviso **and** an actually-disabled "Registrar pago"; **rule 10 — the three
modals are sibling surfaces**: the `turno_no_abierto` recovery path added to the pago modal is
grepped for and replicated across every sibling that can raise it, in the same commit.
`web-descriptor-tests` coverage per surface. The legacy's `echo $listaCliente;` debug leak
(doc-01:377) is not reproduced.

## Testing Strategy

| Layer | What to Test | Approach |
|---|---|---|
| Unit (Domain) | `ReliquidadorDeConsumos`: plain re-price up/down, **offer reversion** in both directions (discount annulled ⇒ larger delta), the worked example asserted numerically, `factor = 1` collapsing to the legacy formula, partial financing, missing price / `IdArticulo NULL` skipped with motivo, all-lines-skipped ⇒ delta 0, empty input, rounding at `AwayFromZero`, the 500 cap. `ValidadorDePagoACuenta`: seven rules, order observable, CC medio rejected. `ReglaDeAjusteDeCuenta`, `CalculadorDeEstadoDeCuenta` (`credito_ilimitado` ⇒ `null`) | Pure, no DB — the bulk of the stage's test mass |
| Unit (Web) | `cuentaCorriente.ts` mappers, disponibilidad mirror | Colocated `*.test.ts`, `web-descriptor-tests` |
| Component (Web) | Double-click on "Reliquidar" issues exactly one POST; `turno_no_abierto` recovery present in every sibling modal; empty ledger renders an empty state, never a re-query | RTL + `user-event`, `vi.mock('../api/cliente')` |
| Integration (derivation identity) | `GET …/reliquidacion` immediately before the `POST` returns a delta **byte-identical** to the committed movement's `importe` | The "never two formulas" contract, asserted |
| Integration (atomicity) | Failure injected at each of the 8 reliquidación steps ⇒ saldo, marker and ledger all untouched | Real Postgres |
| Integration (concurrency) | The three racy surfaces; plus: a consumo committing during a run is **not** marked and is picked up by the next run | Forced rendezvous (`ParametrosTests` precedent) |
| Integration (invariant) | `clientes.saldo == Σ importe` over a scenario mixing consumo, pago, ajuste, reliquidación and anulación; running reliquidación twice writes **one** movement | `Ways.IntegrationTests` |
| Integration (RC shape) | Zero items; stock untouched; `pagos(m)` picked up by the arqueo with no new term; no open turno ⇒ 409; CC medio ⇒ 400; CF ⇒ 400; overpayment ⇒ negative saldo, not rejected; anulación reverses the `Pago`; `409 consumo_reliquidado` | Idem |
| Integration (migration) | `RC` resolves on a database **migrated from stage 6** (idempotent insert proven, re-run safe) | Idem |
| Integration (budget / auth) | Constant command count over 2 / 50 / 200 consumos; the authorization-surface allowlist; Vendedor ⇒ 403 on ajuste + reliquidación, 2xx on pago | `DbCommand` interceptor + `SuperficieDeAutorizacionTests` |

## Migration / Rollout

One additive migration, `CuentaCorrienteEtapa7`: the marker column, its alternate key, self-FK and
partial index, plus the idempotent `RC` insert. No backfill, no data rewrite. The gate summary must
surface: (a) the marker as a self-FK rather than a boolean (decision 2); (b) the **financed-fraction
deviation** from strict legacy parity; (c) the skip-not-abort choice for unpriceable lines;
(d) reliquidación running with **no** turno (decision 4); (e) `ValidadorDePagoACuenta` as a sibling
class instead of the proposal's `ValidadorDePagos` branch (decision 6); (f) the new
`409 consumo_reliquidado` guard on anulación.

Rollback: drop the column, its FK and its index (reliquidación simply cannot run again — no
stage-5/6 behaviour depends on it), and set `RC.activo = false` rather than deleting the row so any
emitted RC stays readable and its `Pago` movement stays valid. Reverting `ServicioDeVentas` restores
stage-6 behaviour bit-for-bit for `TX`/`NCX`; the extracted `EscriturasDeCuentaCorriente` is a pure
move, so its revert is mechanical.

## Open Questions

- [ ] **Anulación after reliquidación is refused, not compensated.** `409 consumo_reliquidado` is
      the honest answer today (the remedy is a manual `Ajuste`); automatically reversing the
      re-priced portion would require storing a per-consumo delta, which is a bigger schema
      decision than this stage should take.
- [ ] **The financed fraction is a declared deviation** from the legacy's whole-ticket re-pricing.
      It collapses to the legacy formula whenever the sale was fully fiado, which is the normal
      case — but the owner should confirm the partial-financing semantics.
- [ ] **500 consumos per run** bounds the transaction and the `detalle` payload. A client who ever
      hits it will need two runs, each writing its own movement — correct, but visible.
- [ ] **The reliquidación `detalle` is JSON in a `text` column** (doc-10 §8 says `text`). If it ever
      needs querying, `jsonb` is a later, separate decision.
- [ ] **`id_punto_venta` on a reliquidación / ajuste is provenance, not authority** — it comes from
      the request (validated tenant-scoped, ADR-8), since neither operation has a turno to derive
      it from.
- [ ] **RC cash in the D6 resumen: counted per-medio, absent per-área — accepted legacy parity**
      (judgment-day slice-2 finding, judge B). An RC has zero items, so its cash flows into the
      arqueo's per-medio `esperado` (via `pagos(m)`) but never into `ingresosPorArea` (derived
      from items). The legacy behaved identically: tipo=3 rows had no article lines, so they were
      absent from the área breakdown while their efectivo entered the caja totals. Deliberate
      parity, documented in `LectorDeContenidoDeResumen`; the resumen's primer/último ticket carry
      the tipo código so RC's independent numeración series reads honestly alongside TX.
- [ ] **Anulación×reliquidación TOCTOU** (judgment-day slice-2 finding, judge A): the
      `consumo_reliquidado` guard's unlocked read must be re-checked under the cliente-row lock
      before Slice 3 ships the marker writer — scheduled as task 3.13.
