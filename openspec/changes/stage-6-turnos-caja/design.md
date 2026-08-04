# Design: Stage 6 — Turnos de caja, arqueos, tesorería y gastos

## Technical Approach

Stage 5 proved that *decide, then commit* works. Stage 6 adds a second structural idea on top of
it: **the turno row is the serialization point of every path that touches the drawer.**

The centerpiece is not a table, it is a **single derivation**. `importe_esperado` is a pure
function of the ledger rows that point at the turno, computed by one DB-free Domain class
(`CalculadorDeArqueo`) fed by one Application reader (`LectorDeMovimientosDelTurno`). The resumen
parcial and the cierre call the **same** two objects with the same inputs — there is no second
formula anywhere, which is the only structural guarantee that the number the cashier saw at 19:00
is the number the close will compare against at 20:00. The client never sends a total; the request
shape has no field for one (structural answer to legacy D7 ⚠).

The second idea is a **lock-order invariant**. Legacy D2 blocks anulación on a closed caja and D7
closes a caja with sales still landing in it; both are the same defect — *no path agrees on when a
turno stops accepting rows*. Here every path that writes into a turno (venta, anulación, gasto,
movimiento de caja) takes a **`FOR SHARE` on the turno row as the first statement of its write
transaction**, and the cierre takes its **exclusive lock as its own first statement**. The turno is
therefore the first lock every path acquires — a total order, so no deadlock — and a sale can never
commit into a turno whose arqueo has already been derived. This is the one place stage 6 adds a
lock where stage 5 needed none, and it is earned: without it, cash lands in a closed turno that no
arqueo ever counted, silently.

Everything else is stage-5 posture reused verbatim: decide-then-commit, no advisory locks, atomic
`UPDATE … WHERE guard … RETURNING` as the only state-transition authority, `EstrategiaSinReintento`
for every manual op, `db-error-backstops` per constraint, RLS + operativa scoping, `ManejadorDeErrores`
mapping, pure Domain first.

## The Derivation (binding — one formula, no second copy)

Let `T` be the turno and `P` its punto de venta.

**Source sets** (every one filtered by `id_turno_caja = T`; nothing else can enter):

| Symbol | Source | Filter |
|---|---|---|
| `pagos(m)` | `Σ pagos_comprobante.importe` joined to `comprobantes_venta` | `c.id_turno_caja = T` **AND `c.estado = 'emitido'`** |
| `vueltos(m)` | `Σ pagos_comprobante.vuelto`, same join | idem |
| `vueltosTotales` | `Σ vueltos(m)` over **every** medio | idem |
| `gastos(m)` | `Σ gastos.importe` | `id_turno_caja = T AND id_medio_pago = m` |
| `refuerzos` | `Σ movimientos_caja.importe` | `tipo = 'refuerzo'` |
| `retiros` | `Σ movimientos_caja.importe` | `tipo = 'retiro'` |
| `fondo` | `turnos_caja.fondo_inicial` | — |

`apertura_cajon` rows carry `importe = 0` and contribute to nothing (they are an audit trail, not
money). Anulados are excluded by `estado = 'emitido'`; an NCX contributes **negative** `importe`
naturally (stage-5 decision 4 — signed arithmetic, no branch).

**The cash anchor.** `ancla` = the unique `medios_pago` row of the tenant with
`Comportamiento = Efectivo`, resolved over **all** rows regardless of `activo` (a medio deactivated
mid-turno still has pagos in it). Zero or two-or-more such rows ⇒ hard stop
`409 caja_sin_medio_efectivo_unico`, raised identically by the resumen and by the cierre — so the
misconfiguration surfaces during the shift, not at the close. See decision 3.

**Per-medio formula** (`m` ranges over medios with `Comportamiento ≠ CuentaCorriente`):

```
importe_esperado(m) =
      pagos(m)
    − gastos(m)
    + [ m = ancla ] × ( fondo + refuerzos − retiros − vueltosTotales )
```

Read the asymmetry out loud, because it is the whole decision:

- **`fondo`, `refuerzos`, `retiros` are physical cash.** They land on the anchor and on nothing else.
- **`vuelto` is physical cash too, always.** Change is handed out of the drawer even when the pago
  that generated it was electronic, so **the anchor absorbs `vueltosTotales`, not each medio its
  own `vueltos(m)`**. With the seeded catalog (`AdmiteVuelto = true` only on efectivo) this
  collapses exactly to doc 10's `pagos + fondo − vueltos − gastos − retiros`; it diverges only in
  the configuration where doc 10's one-medio formula is arithmetically wrong. See decision 2.
- **`gastos(m)` subtract per the gasto's own `id_medio_pago`**, never all on the anchor: a gasto
  paid by transfer never left the drawer.
- **`cuenta_corriente` medios are excluded entirely** (proposal decision 6): nothing physical to
  count. Their income appears in the resumen, never as an arqueo row.

`diferencia = importe_esperado − importe_declarado` (doc 10; the legacy `saldo`, now per medio).
**Positive = faltante.** The column is `GENERATED ALWAYS … STORED`, so it cannot drift from its
operands even under an out-of-band write (decision 6).

**Which medios get a row** — by *row existence*, never by value (a medio can net to exactly 0 and
still owe a declaration):

```
arqueables = { m : ∃ pago of an emitido comprobante of T with medio m }
           ∪ { m : ∃ gasto of T with medio m }
           ∪ { ancla : if fondo ≠ 0 ∨ ∃ movimiento_caja(retiro|refuerzo) of T ∨ vueltosTotales ≠ 0 }
           ∖ { m : Comportamiento = CuentaCorriente }
```

The **server** computes `arqueables`. The cierre payload must declare **exactly** that set: a
missing medio ⇒ `400 arqueo_incompleto`; an extra one ⇒ `400 medio_sin_actividad_en_el_turno`; a CC
medio ⇒ `400 medio_no_arqueable`. Counting zero is a deliberate act the cashier performs, never a
default the server assumes.

## Architecture Decisions

| # | Decision | Alternatives considered | Rationale |
|---|---|---|---|
| 1 | **The cierre's guarded `UPDATE turnos_caja` is statement #1, not statement #3.** Order: guard/close → derive → insert `arqueos_turno` → chain `movimientos_tesoreria`. And every writer into a turno (`EmitirAsync`, `AnularAsync`, gasto, movimiento de caja) opens its write transaction with `SELECT … FROM turnos_caja … FOR SHARE` | The order sketched at proposal level (derive → arqueos → close → tesorería), with no lock on the sale side | **Declared deviation, flagged at the gate.** With derive-first, a sale that resolved the turno as open before the cierre started still commits after it: the comprobante points at a closed turno and its cash is in no arqueo. That is money uncounted, silently — the exact defect class D7 belongs to. Closing first takes the turno's exclusive row lock before any derivation read, and the sale-side `FOR SHARE` makes the two paths block each other: whoever loses re-reads the committed estado and either is included in the arqueo or gets `409 turno_no_abierto` (número consumed, already-accepted stage-5 semantics). Since the turno is the **first** lock in every path, the order is total and cannot deadlock |
| 2 | **`vueltosTotales` lands on the cash anchor, not on each medio** | Per-medio `− vueltos(m)`, doc 10 literal | Change is physically dispensed from the drawer regardless of how the customer paid. Under the seeded catalog the two formulas are identical, so this is a strict generalization, not a divergence; a unit test asserts the collapse. Per-medio subtraction would, on a medio configured `AdmiteVuelto = true` and `Comportamiento = Electronico`, expect *less* than the processor will settle **and** leave the drawer short with no accounting line — two wrong `diferencia` values from one edge |
| 3 | **The cash anchor must be unique or the derivation refuses to run** (`409 caja_sin_medio_efectivo_unico`) | (a) lowest `id_medio_pago` among efectivo; (b) a new `medios_pago.es_caja_fisica` flag now | (a) is silent and arbitrary — a wrong `diferencia` produced quietly is precisely the trust incident this stage exists to prevent. (b) is a real product decision (*which* drawer is the drawer) that deserves its own stage; adding a sixth schema object to a gate that already carries five tables buys nothing today, since every seeded tenant has exactly one efectivo. Failing loudly, early (the resumen raises it too), and reversibly is the honest middle |
| 4 | **`AnularAsync` gains one guard statement, not a joined UPDATE.** `SELECT t.estado … JOIN comprobantes_venta … FOR SHARE OF t` before the existing atomic `UPDATE … WHERE estado = 'emitido' RETURNING`; 0 rows ⇒ the comprobante has `id_turno_caja NULL` (stage-5 era) ⇒ proceed; `'cerrado'` ⇒ `409 turno_cerrado` | Fold `EXISTS (… estado = 'abierto')` into the atomic UPDATE's WHERE | The `EXISTS` variant reads the turno **without locking it**, so a cierre committing mid-flight would still let the anulación through against an already-derived arqueo — it looks atomic and is not. It also collapses three distinct outcomes (404 / already-anulado / turno cerrado) into "0 rows", forcing a disambiguation read anyway. A separate `FOR SHARE` statement keeps the atomic UPDATE as the **only** state-transition authority (stage-5 posture, unchanged) and puts the turno lock first, satisfying decision 1's total order |
| 5 | **One derivation object pair, shared verbatim.** `LectorDeMovimientosDelTurno` (Application, 7 fixed grouped queries) → `MovimientosDelTurno` (data) → `CalculadorDeArqueo` (Domain, pure). Resumen and cierre differ only in what they do with the result | A dedicated read model for the resumen and a leaner in-transaction calculation for the cierre | Two formulas that must agree is the risk register's top item. The query count is **constant in the number of tickets** (grouped aggregates, never per-row) — the same budget discipline stage 5 pinned, so the resumen stays cheap enough to poll |
| 6 | **`arqueos_turno.diferencia` is `GENERATED ALWAYS AS (importe_esperado − importe_declarado) STORED`**; `movimientos_tesoreria` carries `ck_movimientos_tesoreria_cadena (final = inicio + ingreso − egreso)`; `turnos_caja` carries `ck_turnos_caja_cierre_consistente` | Compute `diferencia` in C# and insert it; validate the chain in the service | These three are the stage's *trust artifacts*. A computed column and two CHECKs make "the arithmetic lied" and "the turno is half-closed" unrepresentable at rest, not merely untested — the schema-level answer to D7's "sin transacción: la caja queda cerrada a medias" |
| 7 | **Apertura is a plain INSERT behind `ux_turnos_caja_abierto (id_punto_venta) WHERE estado = 'abierto'`** (proposal decision 7, kept) | Read-then-insert with a pre-check; advisory lock per punto de venta | No read-then-insert window exists to lose. `23505` → `409 turno_ya_abierto`. Note the partial predicate is what lets a punto de venta accumulate closed turnos while admitting exactly one open |
| 8 | **`motivo` is `NOT NULL` with `length(btrim(motivo)) >= 5` for *every* `tipo_movimiento_caja`** | Required only for `retiro`/`refuerzo` (decision 9), `>= 5` only for `apertura_cajon` (legacy F12) | One rule with no per-tipo branch, at the strictest of the two the proposal already binds. Money moving physically and a drawer popping open both deserve a sentence. **Flagged at the gate** alongside proposal decision 9 |
| 9 | **Tesorería `egreso = Σ gastos(T)` over all medios** (legacy `gTotal`), `ingreso = Σ retiros`, `inicio` = `final` of the last row of the same punto de venta (0 if none), `tipo = retiro_caja` | `egreso` = only gastos paid with the cash anchor | Doc 10 says "encadenado inicio→final **como hoy**", and stage 6 ships no manual tesorería ops, so nothing counts this chain physically yet. Changing a number the owner has read for years, silently, in the same stage that changes everything else, is the wrong trade. **Flagged at the gate** as a one-line flip once bank accounts exist |
| 10 | **Domain-first**, mirroring `ValidadorDePagos`: `CalculadorDeArqueo`, `ResolvedorDeMedioDeCajaFisica`, `ReglaDeTurnos` (estado transitions), `ReglaDeMovimientosDeCaja` (importe/motivo per tipo) are DB-free statics in `Ways.Domain.Caja` | Arithmetic inside `ServicioDeTurnos`, tested through HTTP | Same bar as stage 5. The per-medio formula is the one piece of this stage that must be provable by exhaustive enumeration (vueltos, anulados, NCX, gastos por medio, retiros, refuerzos, fondo, zero-activity medios), and that is only affordable without a database |
| 11 | **`EmitirAsync` diff stays surgical**: one `ResolverTurnoAbiertoAsync` immediately after `ResolverPuntoVentaAsync` (so a bogus PV still yields ADR-8 404 *before* `409 turno_no_abierto`), `IdTurnoCaja` as a field of the frozen `PlanDeVenta`, one `FOR SHARE` re-check as the first statement of `EjecutarTransaccionAsync`, one assignment replacing the `null` at `ServicioDeVentas.cs:459` | Resolve the turno inside the transaction only; or pass it through `SolicitudDeVenta` | Resolving inside the transaction would run all pricing before failing — the proposal's fail-fast is a usability requirement, not a nicety. The turno is **never** client input, exactly like `id_empleado` (stage-5 decision 11). Checkout read budget goes from ≤ 16 to ≤ 17; the `FOR SHARE` uses `ExecuteScalarAsync` and is invisible to the interceptor, same family as `UpsertStockAsync` — the budget stays **constant in the number of lines**, which is the invariant the guard actually defends |

## Table Shapes (DB CHANGE GATE — grouped by WRITE PATH)

### Write path A — Turno lifecycle (apertura / cierre)

| Table | EF scope | Key columns | Constraints |
|---|---|---|---|
| `turnos_caja` | **`EntidadTenant`** (mutable: opened then closed, so `updated_at` earns its place) | `id_punto_venta`, `id_empleado_apertura`, `id_empleado_cierre NULL`, `fecha_apertura timestamptz`, `fecha_cierre timestamptz NULL`, `fondo_inicial numeric(14,2) NOT NULL DEFAULT 0`, `estado estado_turno`, `observaciones text NULL` | `pk_turnos_caja`; `ak_turnos_caja_id_turno_caja_id_tenant` (every child FK is composite against it); `ux_turnos_caja_abierto (id_punto_venta) WHERE estado = 'abierto'`; `ck_turnos_caja_fondo_inicial_no_negativo`; `ck_turnos_caja_cierre_consistente` (`estado='abierto'` ⇔ `fecha_cierre IS NULL AND id_empleado_cierre IS NULL`); FKs `fk_turnos_caja_{tenant,punto_venta,empleado_apertura,empleado_cierre}` (empleado FKs **simple** to `usuarios.id_usuario` — stage-5 ADR-11 precedent, the platform NULL sentinel); `ix_turnos_caja_{tenant,punto_venta_fecha}` |
| `arqueos_turno` | append-only, **not** `EntidadTenant` (written once at cierre, never edited — the close is irreversible). Manual `id_tenant` + manual tenant filter, same family as `movimientos_stock`. No date column: its timestamp is `turnos_caja.fecha_cierre` | `id_turno_caja`, `id_medio_pago`, `importe_esperado numeric(14,2)`, `importe_declarado numeric(14,2)`, `diferencia numeric(14,2) GENERATED ALWAYS AS (importe_esperado − importe_declarado) STORED` | `pk_arqueos_turno`; `ux_arqueos_turno_medio (id_turno_caja, id_medio_pago)`; FKs `fk_arqueos_turno_{tenant,turno,medio_pago}` (composite with `id_tenant`); `ix_arqueos_turno_{tenant,turno}` |

### Write path B — Physical cash outside the sale

| Table | EF scope | Key columns | Constraints |
|---|---|---|---|
| `movimientos_caja` | append-only ledger, **not** `EntidadTenant`; manual `id_tenant`, own `creado_el` (doc 10) | `id_turno_caja`, `tipo tipo_movimiento_caja`, `importe numeric(14,2)`, `motivo text NOT NULL`, `id_empleado`, `creado_el timestamptz` | `pk_movimientos_caja`; `ck_movimientos_caja_importe` (`tipo='apertura_cajon' AND importe = 0` OR `tipo<>'apertura_cajon' AND importe > 0`); `ck_movimientos_caja_motivo_minimo` (`length(btrim(motivo)) >= 5`); FKs `fk_movimientos_caja_{tenant,turno,empleado}`; `ix_movimientos_caja_{tenant,turno}` |

### Write path C — Gastos

| Table | EF scope | Key columns | Constraints |
|---|---|---|---|
| `gastos` | **`EntidadTenant`** (a user-authored document, gains `id_comprobante_compra` in stage 8) | `fecha timestamptz`, `id_punto_venta`, `id_turno_caja` (**NOT NULL** — proposal decision 10, gastos require an open turno), `id_empleado`, `categoria categoria_gasto`, `id_proveedor NULL`, `id_area NULL`, `concepto text`, `detalle text NULL`, `id_medio_pago NOT NULL`, `numero_factura text NULL`, `importe numeric(14,2)` | `pk_gastos`; `ck_gastos_importe_positivo (importe > 0)`; FKs `fk_gastos_{tenant,punto_venta,turno,empleado,proveedor,area,medio_pago}`; `ix_gastos_{tenant,turno,punto_venta_fecha,proveedor}`. **`id_comprobante_compra` is NOT created** — stage 8 adds column and FK together (proposal decision 1, `movimientos_stock` precedent) |

### Write path D — Tesorería (one automatic row, at cierre)

| Table | EF scope | Key columns | Constraints |
|---|---|---|---|
| `movimientos_tesoreria` | append-only ledger, **not** `EntidadTenant`; manual `id_tenant`, own `fecha` | `id_punto_venta`, `fecha timestamptz`, `tipo tipo_movimiento_tesoreria`, `id_turno_caja NULL`, `concepto text`, `inicio/ingreso/egreso/final numeric(14,2)`, `id_empleado` | `pk_movimientos_tesoreria`; `ck_movimientos_tesoreria_cadena (final = inicio + ingreso − egreso)`; FKs `fk_movimientos_tesoreria_{tenant,punto_venta,turno,empleado}`; `ix_movimientos_tesoreria_{tenant,punto_venta_id}` (the chain read `ORDER BY id DESC LIMIT 1` per punto de venta) |

### Write path E — Checkout wiring (existing table)

`comprobantes_venta.id_turno_caja` **already exists and stays nullable** — the FK
`fk_comprobantes_venta_turno (id_turno_caja, id_tenant) → turnos_caja` and
`ix_comprobantes_venta_turno (id_turno_caja, id_tenant)` are added now (the index is both the FK
support index and the derivation's access path). Nullable forever: stage-5 rows are never
backfilled (proposal decision 8) and are excluded from every turno derivation by the
`id_turno_caja = T` filter itself.

### Enums (four, native Postgres — same criterion as `comportamiento_medio_pago`)

`estado_turno` (`abierto | cerrado`), `tipo_movimiento_caja` (`retiro | refuerzo | apertura_cajon`),
`tipo_movimiento_tesoreria` (`retiro_caja | deposito | gasto | ajuste` — only `retiro_caja` gets a
writer this stage, the rest are reserved values, same posture as `motivo_stock`), `categoria_gasto`
(`proveedor | sueldos | viaticos | impuestos | servicios | otros`). Column names follow doc 10
(`estado`, `tipo`, `categoria`), not the enum type names.

RLS (`HabilitarRlsDeTenant`) for all five tables **in the same migration that creates them**
(ADR-15). Explicit snake_case `pk_*`/`ux_*`/`ix_*`/`fk_*`/`ck_*` names throughout.

## The Cierre Transaction (binding statement order)

```
── outside the transaction (nothing that decides money) ──────────────────────────
  momento := reloj.Ahora                                  (pinned, never re-read)
  declarados := solicitud.Conteos                         (SOLO conteos — no hay campo de total)
── estrategia = EstrategiaSinReintento; ExecuteAsync(async () => { BeginTransaction ─
  1. UPDATE turnos_caja SET estado='cerrado', fecha_cierre=$m, id_empleado_cierre=$e
       WHERE id_turno_caja=$T AND id_tenant=$t AND estado='abierto'
       RETURNING id_punto_venta, fondo_inicial          ← EXCLUSIVE row lock, held to COMMIT
     0 filas ⇒ ¿existe? no → 404 ; sí → 409 turno_ya_cerrado
  2. movimientos := LectorDeMovimientosDelTurno.LeerAsync(T)   ← 7 grouped queries, bajo el lock
  3. ancla := ResolvedorDeMedioDeCajaFisica.Resolver(medios)   ← puro, 409 si no es única
  4. resultado := CalculadorDeArqueo.Calcular(...)             ← PURO, la única fórmula
     ValidadorDeConteos.Validar(resultado.Arqueables, declarados)  ← 400 si falta/sobra un medio
  5. INSERT arqueos_turno (una fila por medio arqueable; diferencia la calcula la columna)
  6. SELECT final FROM movimientos_tesoreria
       WHERE id_punto_venta=$P AND id_tenant=$t ORDER BY id DESC LIMIT 1   ← inicio (0 si no hay)
     INSERT movimientos_tesoreria (tipo=retiro_caja, ingreso=retiros, egreso=Σgastos,
                                   final=inicio+ingreso−egreso)
  COMMIT }) ──────────────────────────────────────────────────────────────────────
```

**Concurrency.** Two concurrent cierres of the same turno: the second blocks on step 1's row lock,
re-evaluates `estado = 'abierto'` against the committed state, matches 0 rows, and gets
`409 turno_ya_cerrado`. Exactly one winner, zero arqueos from the loser. The tesorería chain needs
no extra lock: one open turno per punto de venta (decision 7) means one cierre per punto de venta at
a time, and step 1's lock already serializes it.

**Failure semantics.** Any throw between 1 and 6 rolls everything back and the turno stays
**abierto** with no arqueo and no tesorería row — the exact inverse of legacy D7's half-close.
Unlike `EmitirAsync` there is nothing consumed outside the transaction, so a failed cierre leaves no
trace at all. `EstrategiaSinReintento` (never the global retry): a cierre is manual, rare and has no
natural idempotency key, so an ambiguous commit must reach the operator as a failure they re-check,
never as an automatic replay that reports `409 turno_ya_cerrado` for a close that in fact succeeded.

## API Surface (ADR-8: uniform 404 cross-tenant)

| Endpoint | Policy | Notes |
|---|---|---|
| `POST /api/caja/turnos` | `OperacionDePos` | Apertura: `{ idPuntoVenta, fondoInicial, observaciones? }` → 201; `409 turno_ya_abierto` |
| `GET /api/caja/turnos/abierto?idPuntoVenta=` | `OperacionDePos` | The POS gate seam's source of truth; 200 with the turno or 200 with `null` |
| `GET /api/caja/turnos/{id}/resumen` | `OperacionDePos` | Resumen parcial (D6 parity) — **same** derivation as the cierre |
| `GET /api/caja/turnos/{id}` | `OperacionDePos` | Turno + its `arqueos_turno` (the Z-report payload; printing is out of scope) |
| `GET /api/caja/turnos?idPuntoVenta=&desde=&hasta=` | `OperacionDePos` | History, paginated |
| `POST /api/caja/turnos/{id}/movimientos` | `OperacionDePos` | `{ tipo, importe, motivo }` — retiro / refuerzo / apertura de cajón |
| `POST /api/caja/turnos/{id}/cierre` | `OperacionDePos` | Body = `{ conteos: [{ idMedioPago, importeDeclarado }] }` **and nothing else** |
| `POST /api/gastos`, `GET /api/gastos` | `OperacionDePos` | Gasto capture against the open turno |

`Politicas.OperacionDePos` unchanged (Vendedor + Supervisor + Admin), proposal decision 2 —
**flagged at the gate**: tightening `POST …/cierre` to Supervisor + Admin is one stacked
`.RequireAuthorization(…)` and a new policy constant, nothing more. The stage-5
`SuperficieDeAutorizacionTests` allowlist gains the five new non-GET routes explicitly, so a future
caja write endpoint added without a policy fails a test instead of shipping open.

`ServicioDeVentas` gains `409 turno_no_abierto` (emisión) and `409 turno_cerrado` (anulación) to its
documented error surface.

## Backstop Map (db-error-backstops)

| Constraint | Mapping | Test |
|---|---|---|
| `ux_turnos_caja_abierto` | 23505 → `409 turno_ya_abierto`. New branch in `ClasificarUnicidad`. **No ordering trap** — the name contains none of `_numero`/`_nombre`/`_cuit`/`_codigo`/`_vigente`/`_default` — but without the branch it falls through to `null` and surfaces as a 500, so it is mandatory, not cosmetic | Rendezvous race: two concurrent aperturas on one punto de venta ⇒ exactly one 201 + one 409 (`ParametrosTests` precedent) |
| `ux_arqueos_turno_medio` | 23505 → `409 arqueo_duplicado` | **Documented exemption from a race test**: the cierre derives the row set inside the exclusive lock, so the normal path cannot raise it. Raw-SQL 23505 test only (same family as `pk_stock`) |
| `ck_turnos_caja_fondo_inicial_no_negativo`, `ck_turnos_caja_cierre_consistente`, `ck_movimientos_caja_importe`, `ck_movimientos_caja_motivo_minimo`, `ck_gastos_importe_positivo`, `ck_movimientos_tesoreria_cadena` | 23514 → 400 via a new `ClasificarCheckDeCaja`, **exact-name switch** (never `Contains`), appended after `ClasificarCheckDeVentas` | Raw-SQL INSERT per constraint asserting 23514 + the translated code |
| All `fk_*` of the five tables | existing generic `fk_` prefix → `400 referencia_invalida` — **no code change** | Integration: `idProveedor`/`idArea`/`idMedioPago` of another tenant ⇒ 400, never 500 |

Genuinely racy surfaces, honestly: **three**, each with a rendezvous test — (1) two aperturas on one
punto de venta; (2) two cierres of one turno; (3) a sale committing against a turno being closed
(decision 1's whole reason for existing: assert the pago is either **in** the arqueo or the sale got
`409 turno_no_abierto`, never neither). Everything else is schema defense against out-of-band writes.

## Data Flow

```
  POS ──POST /api/ventas──→ EmitirAsync
                              │ 1. ResolverPuntoVenta (404)
                              │ 2. ResolverTurnoAbierto ─── no hay ──→ 409 turno_no_abierto
                              │ 3. pricing … PlanDeVenta{ IdTurnoCaja }        (fail-fast, sin pricing)
                              └─ tx: FOR SHARE turno → comprobante(id_turno_caja) → items → pagos → …

  movimientos_caja ─┐
  gastos ───────────┼──→ id_turno_caja = T ──→ LectorDeMovimientosDelTurno (7 queries agrupadas)
  pagos(emitidos) ──┘                                        │
  turnos_caja.fondo_inicial ─────────────────────────────────┤
                                                             ▼
                                            CalculadorDeArqueo  (PURO, DB-free)
                                                   │                    │
                                    GET …/resumen ──┘                    └── POST …/cierre
                                    (D6: áreas, medios,                      + conteos declarados
                                     tickets, egresos)                       → arqueos_turno
                                                                             → tesorería (encadenada)
```

## Web Composition

`src/Ways.Web/src/paginas/Caja.tsx` (turno abierto: estado, movimientos, acceso al resumen) and
`src/Ways.Web/src/paginas/CierreDeCaja.tsx` (resumen + per-medio count inputs + irreversibility
confirmation), plus pure modules `src/api/caja.ts` (request/response mappers) and `src/api/arqueo.ts`
(client-side `diferencia` preview — mirrors the server for instant feedback and is **never**
authoritative, same posture as stage-5 `pagos.ts`). Precedent for the whole shape: `Pos.tsx` /
`Articulos.tsx`.

`react-async-state` obligations, named per rule (the ones that carry weight here):

| Rule | Obligation |
|---|---|
| 1 | Count inputs live in one `conteos` record mutated through a functional updater; no helper reads component state inside an updater |
| 2 | `generacionResumenRef` gates every resumen response (it is polled/refetched after each movimiento) |
| 3 | Registering a movimiento, a gasto or a sale bumps the resumen generation **before** the write |
| 4 | The `finally` clearing `cerrando`/`registrando` is generation-gated |
| 5 | Disabled window runs from the "Finalizar cierre" click until the Z view renders; per-action busy flags, never one page-level boolean |
| 6 | A 2xx cierre is never reported as failure — the post-close Z fetch has its own try/catch and its own copy ("el turno se cerró; no se pudo abrir el comprobante Z") |
| 7 | Medios de pago or resumen failing to load produce a visible aviso **and** an actually-disabled "Finalizar cierre" |
| 8 | `key={idTurno ?? 'sin-turno'}` on the caja subtree, so opening a new turno cannot inherit the previous one's state |
| 9 | While the cierre POST is outstanding **every** superseding action is blocked, plus a first-line `if (cerrando) return`. A cierre is irreversible: a double-submit is the worst defect this screen can ship |

**`Pos.tsx` gate seam.** A `409 turno_no_abierto` from `POST /api/ventas` renders a blocking panel
offering "Abrir turno" (fondo inicial + observaciones) instead of a raw error. After a successful
apertura the checkout is **never auto-resubmitted** — the cashier presses Cobrar again (rule 9: an
automatic replay of a checkout is exactly the double-sale this codebase spends a whole rule
preventing). Descriptor/component tests per `web-descriptor-tests`.

## File Changes

| File | Action | Description |
|---|---|---|
| `src/Ways.Domain/Caja/*.cs` | Create | `TurnoCaja`, `MovimientoCaja`, `ArqueoTurno`, `MovimientoTesoreria`, 3 enums, `CalculadorDeArqueo`, `ResolvedorDeMedioDeCajaFisica`, `ReglaDeTurnos`, `ReglaDeMovimientosDeCaja` |
| `src/Ways.Domain/Gastos/*.cs` | Create | `Gasto`, `CategoriaGasto` |
| `src/Ways.Application/Caja/*.cs` | Create | `ServicioDeTurnos` (apertura, cierre, movimientos), `LectorDeMovimientosDelTurno`, `ServicioDeResumenDeTurno` |
| `src/Ways.Application/Gastos/ServicioDeGastos.cs` | Create | Gasto capture against the open turno |
| `src/Ways.Application/Ventas/ServicioDeVentas.cs` | Modify | `ResolverTurnoAbiertoAsync` after PV resolution; `PlanDeVenta.IdTurnoCaja`; `FOR SHARE` re-check first in `EjecutarTransaccionAsync`; kill the `null` at line 459; `FOR SHARE OF t` guard in `EjecutarAnulacionAsync` |
| `src/Ways.Domain/Ventas/ComprobanteVenta.cs` | Modify | `IdTurnoCaja` doc-comment: the stage-5 promise is fulfilled |
| `src/Ways.Infrastructure/Persistencia/Configuraciones/*.cs` | Create/Modify | 5 new EF configs; `ComprobanteVentaConfiguration` gains the turno FK + index |
| `src/Ways.Infrastructure/Persistencia/WaysDbContext.cs` | Modify | 5 `DbSet`s; manual tenant filters for `movimientos_caja` / `arqueos_turno` / `movimientos_tesoreria` |
| `src/Ways.Infrastructure/Persistencia/Migraciones/*_TurnosCajaYGastosEtapa6.cs` | Create | 5 tables + 4 enums + partial unique index + `comprobantes_venta` FK/index + RLS |
| `src/Ways.Api/Endpoints/CajaEndpoints.cs`, `GastosEndpoints.cs` | Create | Under `OperacionDePos` |
| `src/Ways.Api/Seguridad/ManejadorDeErrores.cs` | Modify | `turno_ya_abierto` / `arqueo_duplicado` branches + `ClasificarCheckDeCaja` |
| `src/Ways.Web/src/paginas/Caja.tsx`, `CierreDeCaja.tsx`, `src/api/caja.ts`, `arqueo.ts` | Create | Caja screens + pure modules + tests |
| `src/Ways.Web/src/paginas/Pos.tsx` | Modify | `409 turno_no_abierto` gate seam |
| `docs/10-modelo-de-datos.md` | Modify | Stage-6 status note: `gastos.id_comprobante_compra` deferred twin of the §6 note; decisions 1/2/8/9 recorded so doc 10 does not drift |

## Interfaces / Contracts

```csharp
// Ways.Domain.Caja — puro, sin DB (mismo listón que ValidadorDePagos)
public readonly record struct ActividadDeMedio(
    int IdMedioPago, ComportamientoMedioPago Comportamiento,
    decimal Pagos, decimal Vueltos, decimal Gastos, bool TuvoFilas);

public sealed record InsumosDeArqueo(
    decimal FondoInicial, decimal Refuerzos, decimal Retiros,
    IReadOnlyList<ActividadDeMedio> Actividad);

public sealed record LineaDeArqueo(int IdMedioPago, decimal ImporteEsperado);

public static class CalculadorDeArqueo
{
    // Devuelve SOLO los medios arqueables, en orden estable por id_medio_pago.
    public static IReadOnlyList<LineaDeArqueo> Calcular(InsumosDeArqueo insumos, int idMedioAncla);
}

// Ways.Application.Caja — el request NO tiene ningún campo de total ni de esperado.
public sealed record SolicitudDeCierre(IReadOnlyList<ConteoDeclarado> Conteos, string? Observaciones);
public readonly record struct ConteoDeclarado(int IdMedioPago, decimal ImporteDeclarado);
```

## Testing Strategy

| Layer | What to Test | Approach |
|---|---|---|
| Unit (Domain) | `CalculadorDeArqueo` over a synthetic turno: vueltos on the anchor **and** on an electronic medio, anulados excluded, NCX negatives, gastos attributed per medio, retiros/refuerzos/fondo on the anchor only, a medio netting to exactly 0 that still gets a row, a CC medio never getting one, zero-activity medios absent. Collapse test: with `AdmiteVuelto` true only on efectivo, the formula equals doc 10's literal one. `ResolvedorDeMedioDeCajaFisica`: 0 / 1 / 2 efectivo medios. `ReglaDeMovimientosDeCaja`: importe/motivo per tipo | Pure, no DB — the bulk of the stage's test mass |
| Unit (Web) | `arqueo.ts` (preview `diferencia`, sign), `caja.ts` mappers | Colocated `*.test.ts`, `web-descriptor-tests` |
| Component (Web) | Cierre confirmation flow; **double-click on "Finalizar cierre" issues exactly one POST**; gate seam renders on 409 and does **not** auto-resubmit the sale | RTL + `user-event`, `vi.mock('../api/cliente')` |
| Integration (atomicity) | Force a failure at each of the 6 cierre steps ⇒ turno still `abierto`, zero `arqueos_turno`, zero `movimientos_tesoreria` | Real Postgres, failure injected via constraint violation / cancellation |
| Integration (concurrency) | The three racy surfaces: two aperturas; two cierres; **a sale racing a cierre** — assert the pago is either counted in the arqueo or the sale got 409, never neither | Forced rendezvous (`ParametrosTests` precedent) |
| Integration (derivation identity) | `GET …/resumen` immediately before a cierre returns per-medio expectations **byte-identical** to the `arqueos_turno.importe_esperado` written by that cierre | The "never two formulas" contract, asserted not assumed |
| Integration (budget) | The resumen over turnos with 2, 50 and 200 tickets issues the **same** command count; checkout stays ≤ 17 reads regardless of line count | `DbCommand` interceptor, stage-4/5 guard |
| Integration (parity/shape) | Grep assertion: no cierre request DTO has a field whose name matches total/esperado/importe_esperado. No `tipo = 95` equivalent anywhere | Success criteria of the proposal |
| Integration (regression) | The entire stage-5 suite stays green except tests that now need an open turno; anulación of a NULL-turno comprobante still succeeds | `Ways.IntegrationTests` |
| Integration (RLS + auth) | Raw-SQL RLS proof per new table; the authorization-surface allowlist test | Idem |

## Migration / Rollout

**One migration**, `TurnosCajaYGastosEtapa6`: 5 tables + 4 enums + the partial unique index + the
`comprobantes_venta.id_turno_caja` FK and index + RLS for all five. Presented as **one DB Change
Gate approval** grouped by write path (A–E above). The gate summary MUST surface: (a) the flagged
tightenings — cierre role (proposal decision 2) and uniform `motivo >= 5` (decision 8 here); (b) the
deferred `gastos.id_comprobante_compra` (proposal decision 1); (c) the **declared deviation** of
decision 1 here (close-first + `FOR SHARE`, changing the statement order the proposal sketched and
adding one lock to the checkout); (d) decision 2's vuelto attribution; (e) decision 3's hard stop on
a non-unique cash medio; (f) decision 9's tesorería `egreso` parity choice; (g) `diferencia` as a
generated column.

No data migration and no backfill (proposal decision 8). Rollback: drop the five tables and the FK;
`comprobantes_venta.id_turno_caja` returns to its dormant nullable state and stage-1–5 data is
untouched. Reverting `ServicioDeVentas` restores stage-5 behaviour exactly, with no data repair.

## Open Questions

- [ ] **Blind arqueo.** The cierre screen shows the derived resumen before the cashier types the
      counts (legacy D6/D7 parity). The server-side derivation is the real protection, but hiding
      `importe_esperado` until after the declaration is submitted would remove the temptation to
      copy it. One flag on the endpoint; a product call, not a technical one.
- [ ] **A second cash medio** (e.g. a second currency, or a "efectivo caja chica") currently makes
      the derivation refuse to run. The answer is `medios_pago.es_caja_fisica`, deferred (decision 3).
- [ ] **Tesorería `egreso` includes gastos paid by transfer** (decision 9, legacy parity). Revisit
      when bank accounts are modelled — until manual tesorería ops ship, nobody counts this chain.
- [ ] **Turno spans midnight / timezone.** A turno is bounded by apertura and cierre, not by a
      calendar day, so no date bucketing is needed this stage — but the resumen's "primer/último
      ticket" and every `fecha` still use server local time (inherited stage-4/5 question).
- [ ] **`apertura_cajon` prints nothing.** The legacy F12 printed a comprobante; here the row is the
      audit trail and printing is out of scope.
- [ ] **No idempotency key on cierre.** Blocked client-side (rule 9) and by `EstrategiaSinReintento`
      server-side; an ambiguous commit still requires the operator to re-check the turno's estado.
      Same posture and same residual risk as stage-5 anulación.
