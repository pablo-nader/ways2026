# Design: Stage 15 — Cuenta corriente de proveedores (ledger)

## Technical Approach

**One ledger, one write authority, four write paths, one read seen twice, one screen.**

The proposal's `Modelo de datos propuesto` (§A-§F) is the ratified gate contract and this design
**adds no DDL of any kind** — not a column, not an index, not a constraint, not an enum value.
Everything below is code over that exact table.

Four structural facts decide the shape.

1. **Every write path already owns an authoritative `UPDATE ... RETURNING`, so the ledger never
   re-reads.** `ConfirmarHeaderAsync` (`ServicioDeCompras.cs:663-685`) and `MarcarAnuladaAsync`
   (`:687-700`) are the single race-safe authorities for their transitions; each one **widens its
   `RETURNING` list** to carry the two values the movement needs, at **zero extra round trips** —
   the stage-14 decision-8/9 criterion applied verbatim. A `SELECT id_proveedor, total` taken
   before or beside the lock would be a value another transaction could have answered differently.

2. **In the payment path the movement cannot be written before the payment exists.**
   `InsertarGastoAsync` (`ServicioDeGastos.cs:134-174`) generates `id_gasto` at its
   `SaveChangesAsync` (`:169`), so the ledger write is necessarily the **last thing before the
   commit** (`:171`). The pinned position there is a consequence of the identity column, not a
   preference — which is exactly what makes it stable.

3. **`proveedores` is the last *conflicting* row lock, and the FK's `FOR KEY SHARE` is not one.**
   The gasto INSERT's own FK check on `gastos.id_proveedor` takes `FOR KEY SHARE` on the proveedor
   row **before** the saldo `UPDATE` takes `FOR NO KEY UPDATE` on it. Those two lock modes do not
   conflict in PostgreSQL (`KEY SHARE` conflicts only with `FOR UPDATE`), which is the entire
   reason `KEY SHARE` exists. The checkout — the deadlock-free precedent this stage mirrors —
   already has the identical shape: the comprobante INSERT references `clientes` before
   `ServicioDeVentas.cs:898` updates the saldo. **This is a refinement of proposal decision 9, not
   a change to it**: the pinned order stands, and its invariant is stated as *"`proveedores` is the
   last row lock any transaction takes **for update**, and the ledger `INSERT` follows it
   immediately."*

4. **The per-compra payment status cannot be `SUM(importe)` over all movements.** A compra
   confirmed **before** the cutover has no `compra` movement — its debt lives inside the `apertura`
   (decision 1, which forbids synthetic replay). Under the proposal's literal formula that compra
   sums to `0` and reads **`pagada`**, which is the exact opposite of the retired spec's outcome.
   The status is therefore derived as `pagado = −Σ importe WHERE tipo <> 'compra'` compared against
   `comprobantes_compra.total` — the retired formula's shape, re-sourced from the ledger instead of
   `gastos` (decision 8 below, and the first tension listed at the foot of this document).

The checkout is not opened by this stage at all: `ServicioDeVentas` appears here **only** as the
cited precedent of the contramovimiento (`:657-662`) and of the lock order (`:773`, `:874-878`,
`:898`, `:910`). No file under `src/Ways.Application/Ventas/` is in this stage's diff.

## Architecture Decisions

| # | Decision | Alternatives considered | Rationale |
|---|---|---|---|
| 1 | **`EscriturasDeCuentaCorrienteProveedor` is a static class with exactly two statements and one validator** — structural copy of `EscriturasDeCuentaCorriente.cs:21-145`. Injected the way the original is: **it is not injected at all**; it is `static`, called with the caller's `DbConnection` + `DbTransaction?`, and never opens, flushes or commits anything | A DI-registered `IEscritorDeCuentaCorrienteProveedor`; one method per service | The original's own doc-comment states the invariant — *"la extracción es lo que compra seguridad"*: exactly ONE `UPDATE proveedores ... saldo ... RETURNING` and ONE ledger `INSERT` in the whole codebase. A DI seam would exist only for a test double that no test in this repo needs (stage-14 decision 10: the FK probe proves fail-closed with no production indirection); an interface over a static pair also invites a second implementation, which is the one thing this class exists to prevent |
| 2 | **Raw `UPDATE proveedores SET saldo = saldo + $1 ... RETURNING saldo`, never a tracked `proveedor.Saldo +=`** | EF entity mutation inside the same `SaveChanges` | Copied for its reason, not its shape: `FabricaDeEstrategiaSinReintento` exists precisely because a `CreateExecutionStrategy` replay would apply the increment twice. `id_tenant` travels in the `WHERE` beside the id — RLS already isolates, this is the cheap second layer the whole repo applies |
| 3 | **Every raw-ADO parameter goes through `ParametrosDeComando.Agregar` / `AgregarNulo`** (`src/Ways.Application/Abstracciones/ParametrosDeComando.cs:15-33`), which normalizes any `DateTimeOffset` to UTC | A private `AgregarParametro` per service (the shape `EscriturasDeCuentaCorriente.cs:127-144` still carries) | The unified factory is already in `main` and is the settled outcome of PR #129, whose fix had to reach a fifth call site after four were patched: Npgsql refuses a non-zero offset against `timestamptz` and the EF convention does not reach a hand-built `DbParameter`. Writing a sixteenth private copy would re-open a closed defect class. `ToUniversalTime()` is a re-expression — it never moves the instant |
| 4 | **The two authoritative `RETURNING`s widen; nothing else in either transaction moves.** `ConfirmarHeaderAsync` → `RETURNING id_punto_venta, id_tipo_comprobante, id_proveedor, total`; `MarcarAnuladaAsync` → `RETURNING id_punto_venta, id_proveedor` | Read `id_proveedor`/`total` from `preLectura` (`ServicioDeCompras.cs:274`, `:480`); a second `SELECT` under the lock | `preLectura` runs `AsNoTracking()` **outside** the transaction: a concurrent `PUT` can change `total` between it and the lock, and the whole point of the existing comment at `:320-330` is that the transaction trusts only what **this** lock saw. A second `SELECT` costs a round trip to learn what the statement already knows. Exact precedent: stage-14 decision 8 widened `MarcarAnuladoAsync` for the same reason |
| 5 | **The `compra` movement's importe is the compra's `total`, positive, carrying `id_comprobante_compra` as its origin.** Written as step 5 of `EjecutarConfirmarAsync`, after the costo loop (`:462-465`) and immediately before the commit (`:467`) | Write it right after the header UPDATE (step 1.5, where stage 14 put the audit row) | The proveedor lock must be the last one taken for update, and steps 2b-4 take `lotes`, `stock` and `stock_lotes`. Placing it at step 1.5 would invert the pinned order for every confirm and reintroduce the deadlock this stage is pinning away. The audit row could sit at 1.5 because it locks nothing |
| 6 | **The anulación contramovimiento reads the ledger, with a named pre-cutover fallback**: `importe = −(Σ importe of the `compra` movements of this compra)`; if there is none (compra confirmed before the cutover, its debt inside the `apertura`), `importe = −total` from the widened `RETURNING`, and the `detalle` says so | Always `−total`; refuse to annul a pre-cutover compra | The ledger is the book: reversing what was actually written is the only rule that stays true if a future path ever writes a partial `compra` movement. But a pre-cutover compra is not an edge case — it is the whole population the backfill exists for, and refusing to annul it would be a regression of a shipped operation. Mirrors `ServicioDeVentas.cs:607-662` (read the originals, reverse each) with the one branch that stage does not need |
| 7 | **The `pago` movement is written inside `InsertarGastoAsync`'s existing transaction, after `SaveChangesAsync` (`:169`) and before the commit (`:171`), only when `categoria = proveedor` **and** `id_proveedor` is non-null.** `importe = −gasto.Importe`; `id_comprobante_compra` = the gasto's link (may be NULL); `id_gasto` = the row just flushed | A second transaction; deriving the proveedor from the compra only | The predicate is `ServicioDeSaldoDeProveedor.cs:39-43` verbatim — the retired formula's own predicate, so the ledger cannot silently diverge from the number it replaces. `id_proveedor` may be derived by `ExigirCompraLigableAsync` (`:145`) when the request omits it; the movement uses the **resolved** value, which is the same one the row stores. The turno guard (`:140`) and the arqueo egress term stay untouched: **no new derivation, no new arqueo term** |
| 8 | **Per-compra payment status is `pagado = −Σ importe WHERE id_comprobante_compra = X AND tipo <> 'compra'`, fed to the existing pure `ResolverEstadoPago(pagado, total)` (`ServicioDeSaldoDeProveedor.cs:69-77`), which does not change a line** | The proposal's `SUM(importe) … = total ⇒ impaga, <= 0 ⇒ pagada` | Under the proposal's literal formula a **pre-cutover** confirmed compra (no `compra` movement) sums to `0` and reads `pagada`. The retired spec reads it `impaga`. Since the stage's headline success criterion is that the migration reproduces the retired read, the derivation must be the one that holds on **both** sides of the cutover. `pagado` also stays a real, reportable number, so `CompraConEstadoPago.Pagado` keeps meaning what its name says (`dto-contract-honesty` rule 1) instead of becoming a residual |
| 9 | **`ServicioDeSaldoDeProveedor.ObtenerAsync` keeps its signature and its three DTOs byte-identical**; only its two queries change: `Saldo` comes from `proveedores.saldo`, `pagadoPorCompra` from one indexed ledger aggregation (index 3 of gate §B) instead of the `gastos` `GROUP BY` (`:39-43`) | A new service and a deprecated old one; adding `saldoResultante`/`movimientos` to the response | `dto-contract-honesty`: the response shape is a published contract consumed by `Proveedores.tsx` and `Compras.tsx`. The estado de cuenta is a **different** endpoint with a **different** DTO; widening this one would ship two overlapping read models on day one. The guard `ResolverProveedorAsync` (`:79-88`, ADR-8 404) is reused untouched |
| 10 | **The estado de cuenta is paginated (`PaginaDe*`, `OFFSET`), ordered `fecha DESC, id_movimiento DESC`** | Stage 7's unpaginated single payload (`ServicioDeCuentaCorriente.cs:174-211`) | The proveedor ledger grows monotonically with time and has no natural bound (stage-13 decision-13 criterion, quoted by stage-14 decision 13). The **tiebreaker is not cosmetic**: `fecha` is one `reloj.Ahora` per operation, so a confirm and its contramovimiento — and an entire `RelojFijo` fixture — tie by construction, and without `id_movimiento DESC` pagination duplicates and skips rows (stage-14 decision 12, proven there). Seven `PaginaDe*` records exist and zero keyset ones; the web pager renders "Página N de M", which needs the `COUNT(*)` a cursor cannot give |
| 11 | **The running balance is the stored `saldo_resultante`, never re-derived**, and the header's `saldo` is read from the same `proveedores` row in the same request | A window function; running backwards from today's saldo (the legacy's shape, doc-01:375) | Stage-7 decision 9 verbatim: a backward-running balance is **wrong under a filter**, the stored snapshot is right under any filter **and** any page — which is what makes decision 10 safe at all |
| 12 | **`POST /api/proveedores/{id}/cuenta-corriente/ajustes` is mapped TOP-LEVEL on `app` under `SupervisionDeCuentaDeProveedor` alone**, not inside the `OperacionDePos` group | Stage 7's shape: the route inside the group, stacking the supervision policy (`CuentaCorrienteEndpoints.cs:51-57`) | Proposal decision 8 explicitly rejects the AND-composition. The top-level mapping is precedented **in this stage's own area**: `GET /api/proveedores/{id}/saldo` is mapped top-level for exactly this reason, and its comment names the trap (`ProveedoresEndpoints.cs:50-61`). One policy, one gate, no composition to reason about. Recorded as a deliberate departure from the stage-7 route shape |
| 13 | **The manual ajuste reuses the pure `ReglaDeAjusteDeCuenta.Validar` (`ReglaDeAjusteDeCuenta.cs:20-35`) unchanged** — `importe ≠ 0`, `length(btrim(detalle)) >= 5` — and therefore reuses the codes `ajuste_importe_invalido` / `ajuste_detalle_requerido` | A cloned `ReglaDeAjusteDeCuentaDeProveedor` | The rule mentions no cliente, no saldo and no ledger: it is already client-agnostic. Cloning it would create two thresholds that drift. This is also what satisfies gate §F's *"a zero-importe manual ajuste is refused by the service with a 400"* without a CHECK — the deliberately untouched `importe` micro-gate stays untouched |
| 14 | **The ajuste takes NO turno**; `id_punto_venta` comes from the request as **provenance, not authority**, validated tenant-scoped (ADR-8 404) before the transaction | Require an open turno for symmetry with the payment | It moves no physical money and contributes no term to `CalculadorDeArqueo` — requiring a turno would be theatre. Stage-7 decision 4 and `ServicioDeCuentaCorriente.RegistrarAjusteAsync` (`:110-140`) already settled this exact question on the client side |
| 15 | **`apertura` is refused at three layers and reachable from none**: the API DTO has no `tipo` field at all, the writer's `ValidarFormaPorTipo` throws on it, and the CHECK backs both | Accept `tipo` on the ajuste DTO and validate it | The only writer of `apertura` is the migration. A field that can only ever hold one legal value is a field that should not exist (`dto-contract-honesty` rule 1). The CHECK is still proven by SQLSTATE from a raw insert (gate §E) so the constraint is known to exist rather than assumed |
| 16 | **`MovimientoCuentaCorrienteProveedorConfiguration` mirrors `MovimientoCuentaCorrienteConfiguration.cs:18-133` minus the alternate key, the self-FK and its support index**, and declares all six support indexes by hand with doc-10 names | Let `ForeignKeyIndexConvention` autogenerate the FK indexes | That convention re-adds a support index for any uncovered FK even if removed by hand inside `Configure()` — the documented deviation at `:114-122` and the source of stage-14's gate amendment 1. Declaring all six explicitly is what makes the gate's *"zero indexes the contract did not name"* checkable by reading the migration |

## Interfaces / Contracts

### Application — the one write authority

```csharp
// Ways.Application/CuentaCorriente/EscriturasDeCuentaCorrienteProveedor.cs
// Copia estructural de EscriturasDeCuentaCorriente (misma forma static, misma postura de
// conexión/transacción del llamador, mismos parámetros por ParametrosDeComando).
public static class EscriturasDeCuentaCorrienteProveedor
{
    /// UPDATE ... RETURNING crudo: nunca un `proveedor.Saldo += x` trackeado (un reintento de
    /// CreateExecutionStrategy duplicaría el incremento). id_tenant en el WHERE además del id.
    /// Es el ÚLTIMO lock de fila (for update) de cualquier transacción que lo llame.
    public static Task<decimal> ActualizarSaldoProveedorAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, int idProveedor, decimal importe,
        CancellationToken ct);

    /// El ÚNICO INSERT del ledger. `idPuntoVenta`/`idEmpleado` son int? SOLO para que
    /// `apertura` sea representable en el tipo; ningún llamador de producción pasa null (la
    /// única escritora de `apertura` es la migración) y ValidarFormaPorTipo lo refuerza.
    public static Task<int> InsertarMovimientoCcProveedorAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, int idProveedor, DateTimeOffset fecha,
        int? idPuntoVenta, int? idEmpleado, TipoMovimientoCcProveedor tipo, int? idComprobanteCompra,
        int? idGasto, decimal importe, decimal saldoResultante, string? detalle, CancellationToken ct);
}
```

```sql
-- Statement 1 — el único escritor de proveedores.saldo en todo el codebase.
UPDATE proveedores SET saldo = saldo + $1 WHERE id_proveedor = $2 AND id_tenant = $3 RETURNING saldo

-- Statement 2 — el único INSERT del ledger.
INSERT INTO movimientos_cuenta_corriente_proveedor
  (id_tenant, id_proveedor, fecha, id_punto_venta, id_empleado, tipo,
   id_comprobante_compra, id_gasto, importe, saldo_resultante, detalle)
VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)
RETURNING id_movimiento
```

`ValidarFormaPorTipo` — infrastructure defence in depth, `InvalidOperationException` (never an
`ErrorDominio` 4xx: a violation is a defect of a call site, not a client error), same posture as
`EscriturasDeCuentaCorriente.cs:102-122`:

| tipo | `id_comprobante_compra` | `id_gasto` | `id_punto_venta` / `id_empleado` |
|---|---|---|---|
| `apertura` | MUST be null | MUST be null | MUST both be null |
| `compra` | MUST be non-null | MUST be null | MUST both be non-null |
| `pago` | free (the imputación is optional) | MUST be non-null | MUST both be non-null |
| `ajuste` | free (structurally dual: contramovimiento carries it, manual does not) | MUST be null | MUST both be non-null |

### Domain — pure, no database

```csharp
// Ways.Domain/CuentaCorriente/TipoMovimientoCcProveedor.cs — el ORDEN de los miembros ES el
// orden de valores del tipo nativo (npgsql.MapEnum<T>), gate §A.
public enum TipoMovimientoCcProveedor { Apertura, Compra, Pago, Ajuste }

// Ways.Domain/CuentaCorriente/MovimientoCuentaCorrienteProveedor.cs — inmutable, sin EntidadBase
// y sin EntidadTenant (IdTenant se escribe explícito; EstamparTenant lo pisaría con el tenant de
// la sesión — stage-14 decisión 7). Espeja MovimientoCuentaCorriente.cs:17-56.

// Ways.Domain/CuentaCorriente/CalculadorDeEstadoDeCuentaDeProveedor.cs — puro:
//   EtiquetarAjuste(idComprobanteCompra): non-null ⇒ Contramovimiento, null ⇒ Manual
//   ResolverEstadoPago(pagado, total) se REUSA de ServicioDeSaldoDeProveedor.cs:69-77 sin tocarlo.
```

### Application — read model

```csharp
// Ways.Application/CuentaCorriente/ContratosDeProveedor.cs
public sealed record MovimientoDeCuentaDeProveedor(
    int IdMovimiento, DateTimeOffset Fecha, TipoMovimientoCcProveedor Tipo, decimal Importe,
    decimal SaldoResultante, string? Detalle, int? IdComprobanteCompra, int? IdGasto,
    EtiquetaDeAjuste? Etiqueta);

public sealed record EstadoDeCuentaDeProveedorHeader(int IdProveedor, decimal Saldo);

public sealed record PaginaDeEstadoDeCuentaDeProveedor(
    EstadoDeCuentaDeProveedorHeader Header, IReadOnlyList<MovimientoDeCuentaDeProveedor> Items,
    int Total, int Pagina, int Tamanio, bool Historico, DateTimeOffset? Desde, DateTimeOffset? Hasta);

// ServicioDeCuentaCorrienteDeProveedor
public Task<PaginaDeEstadoDeCuentaDeProveedor> ObtenerEstadoDeCuentaAsync(
    int idProveedor, DateTimeOffset? desde, DateTimeOffset? hasta, bool historico,
    int pagina, int tamanio, CancellationToken ct);
public Task<MovimientoDeCuentaDeProveedor> RegistrarAjusteAsync(
    int idProveedor, SolicitudDeAjusteDeProveedor solicitud, CancellationToken ct);

// El DTO del ajuste NO tiene `tipo` ni `saldoResultante` (decisión 15): no hay campo aceptado y
// descartado, y ningún endpoint acepta un saldo ni un delta calculado por el cliente.
public sealed record SolicitudDeAjusteDeProveedor(int IdPuntoVenta, decimal Importe, string Detalle);
```

```csharp
/// Cláusulas bajo prueba (mutation-proof-tests), en orden de daño si se pierden:
///   Where(m => m.IdProveedor == idProveedor)  → sin él, el ledger de un proveedor filtra otros
///   ThenByDescending(IdMovimiento)            → con `fecha` empatada (RelojFijo, o confirmar +
///                                               contramovimiento) la paginación duplica y saltea
///   historico ? sin rango : default de último mes → un histórico ignorado miente por omisión
///   cada `if (desde/hasta is { } x)`          → un filtro ignorado devuelve de más, en silencio
private IQueryable<MovimientoCuentaCorrienteProveedor> ConstruirQuery(
    int idProveedor, DateTimeOffset? desde, DateTimeOffset? hasta);
```

`ObtenerEstadoDeCuentaAsync` = `CountAsync` + `Skip((pagina-1)*tamanio).Take(tamanio)` with
`pagina = Math.Max(pagina, 1)` and `tamanio = Math.Clamp(tamanio, 1, 200)`
(`ServicioDeGastos.cs:63-64`). `historico` wins over `desde`/`hasta`; with none of the three, the
last-month default applies — the same precedence `ServicioDeCuentaCorriente.cs:174-191` pinned.

## Transactions (binding statement order)

```
── CONFIRMAR COMPRA ──────────────────────────────────────────────────────────────
  ServicioDeCompras.EjecutarConfirmarAsync (:312-470) — pasos 1..4 SIN CAMBIOS
   1. UPDATE comprobantes_compra ... RETURNING id_punto_venta, id_tipo_comprobante,
                                                id_proveedor, total          ← lock header (:331)
   2. items (read set congelado) · 2.b lotes (:401-426)
   3. movimientos_stock + stock + stock_lotes por item (:437-450)
   4. costo_nominal (:462-465)
   5. nuevoSaldo := ActualizarSaldoProveedorAsync(+total)          ← ÚLTIMO lock for update
   6. INSERT movimiento (compra, id_comprobante_compra = id, id_gasto NULL,
                         importe = +total, saldo_resultante = nuevoSaldo)
  COMMIT (:467)

── ANULAR COMPRA ─────────────────────────────────────────────────────────────────
  ServicioDeCompras.EjecutarAnulacionAsync (:504-600) — pasos 1..4 SIN CAMBIOS
   1. UPDATE comprobantes_compra ... RETURNING id_punto_venta, id_proveedor    ← lock header (:513)
   1.5 auditoría (stage 14, :525-538) — intacta, no se mueve
   2. reversa de stock por movimiento original (:549-591)
   4. gastosLigados (informativo, NUNCA bloquea, :594)                         ← intacto
   5. importeOriginal := SUM(importe) del/los movimiento(s) `compra` de esta compra
      (0 filas ⇒ compra pre-cutover ⇒ importeOriginal := total del RETURNING, detalle lo dice)
   6. nuevoSaldo := ActualizarSaldoProveedorAsync(−importeOriginal)  ← ÚLTIMO lock for update
   7. INSERT movimiento (ajuste, id_comprobante_compra = id, importe = −importeOriginal)
  COMMIT (:596)

── GASTO = PAGO A PROVEEDOR ──────────────────────────────────────────────────────
  ServicioDeGastos.InsertarGastoAsync (:134-174) — pasos 1..3 SIN CAMBIOS
   1. ExigirTurnoAbiertoBajoLockAsync           ← FOR SHARE, primer statement (:140)
   2. ExigirCompraLigableAsync (si hay link)    ← FOR SHARE del header (:145) + deriva id_proveedor
   3. INSERT gastos (EF SaveChangesAsync, :169) ← acá nace id_gasto
   si categoria = proveedor Y id_proveedor no es null:
   4. nuevoSaldo := ActualizarSaldoProveedorAsync(−importe)   ← ÚLTIMO lock for update
   5. INSERT movimiento (pago, id_gasto, id_comprobante_compra = el link (o NULL),
                         importe = −importe, saldo_resultante = nuevoSaldo)
  COMMIT (:171)

── AJUSTE MANUAL ─────────────────────────────────────────────────────────────────
  fuera: ReglaDeAjusteDeCuenta.Validar(importe, detalle) ; proveedor (404 ADR-8) ; PV (404)
  EstrategiaSinReintento ⇒ BEGIN
   1. nuevoSaldo := ActualizarSaldoProveedorAsync(importe)     ← único lock de la transacción
   2. INSERT movimiento (ajuste, id_comprobante_compra NULL, id_gasto NULL, detalle)
  COMMIT
```

### Lock order — verified line by line against the real call sites

| Path | Locks taken today, in order | Where the proveedor lock lands | Verdict |
|---|---|---|---|
| `EjecutarConfirmarAsync` | `comprobantes_compra` header FOR UPDATE (`:331`) → `lotes` (`:423`) → `stock` / `stock_lotes` (`:443-448`) | after step 4, before the commit (`:467`) | **Matches the pinned order.** No existing statement moves |
| `EjecutarAnulacionAsync` | `comprobantes_compra` header FOR UPDATE (`:513`) → `movimientos_stock` + `stock` / `stock_lotes` (`:549-591`) | after step 4 (`:594`), before the commit (`:596`) | **Matches.** The audit call at `:525-538` locks nothing and does not move |
| `InsertarGastoAsync` | `turnos_caja` FOR SHARE (`:140`) → `comprobantes_compra` header FOR SHARE (`:145`) → INSERT `gastos` (`:169`) | after the flush, before the commit (`:171`) | **Matches, with the §3 refinement**: the gasto's FK check touches the proveedor row in `FOR KEY SHARE`, which does not conflict with the later `FOR NO KEY UPDATE` |
| Ajuste manual (new) | — | the only lock | Suffix of the total order ⇒ order stays total |
| Checkout (stage-7 precedent, untouched) | `turnos_caja` (`:773`) → `stock` (`:874-878`) → `clientes` (`:898`) → ledger (`:910`) | n/a | Same shape, including the same FK `KEY SHARE` on `clientes` before `:898` |

**Total order: `turnos_caja → comprobantes_compra → lotes → stock/stock_lotes → proveedores →
ledger INSERT`**, operative form: *`proveedores` is the last row lock any transaction takes for
update, and the ledger `INSERT` follows it immediately.*

**Concurrency guarantees.** *Confirm × pago on the same proveedor*: they share only the proveedor
row and neither holds anything the other needs after taking it. *Pago × pago*: serialized on the
proveedor row; both are additive. *Anulación × pago on the same compra*: the payment holds
`FOR SHARE` on the header, so the anulación's `FOR UPDATE` waits for its commit and then computes
its reversal over a ledger that already contains the payment — the existing TOCTOU guard
(`ServicioDeGastos.cs:176-186`) does the whole job with no new machinery.

**Failure semantics.** Any throw rolls back the business operation, `proveedores.saldo` and the
ledger row together — "saldo moved but no movement" and "movement written but the compra is still
borrador" are both unrepresentable. There is no number outside the transaction to leak (unlike the
RC's numeración in stage 7).

## API Surface (ADR-8: uniform 404 cross-tenant)

| Route | Policy | Notes |
|---|---|---|
| `GET /api/proveedores/{idProveedor:int}/cuenta-corriente?desde&hasta&historico&pagina&tamanio` | `OperacionDePos` (group) | Header + page in one payload; `saldo_resultante` per row; empty ledger ⇒ empty page, never a re-query |
| `POST /api/proveedores/{idProveedor:int}/cuenta-corriente/ajustes` | **`SupervisionDeCuentaDeProveedor`**, mapped top-level (decision 12) | `{ idPuntoVenta, importe, detalle }` → 201. No `tipo`, no `saldoResultante` |
| `GET /api/proveedores/{id:int}/saldo` | `OperacionDePos` (unchanged, top-level, `ProveedoresEndpoints.cs:56-61`) | Same route, same DTOs, re-sourced from the ledger |

```csharp
// Ways.Api/Seguridad/Politicas.cs — forma exacta de SupervisionDeCuentaCorriente (:117-122),
// nombre propio (decisión 8 del proposal; precedente LecturaDeAuditoria, :73-79).
public const string SupervisionDeCuentaDeProveedor = "supervision_cuenta_proveedor";
.AddPolicy(SupervisionDeCuentaDeProveedor, p => p.RequireAuthenticatedUser()
    .RequireClaim(ClaimsWays.RolId, ((int)RolConocido.Supervisor).ToString(),
                                    ((int)RolConocido.Admin).ToString()));
```

The stage-5 `SuperficieDeAutorizacionTests` allowlist gains the one new non-GET route.

## Backstop Map (`db-error-backstops`)

| Constraint | Reachable from client input? | Backstop | Test |
|---|---|---|---|
| `fk_..._proveedor` | **Yes** (route value of the ajuste) | `ResolverProveedorAsync` 404 pre-check (reused from `ServicioDeSaldoDeProveedor.cs:79-88`) + generic `fk_`/`23503` → `400 referencia_invalida` (`ManejadorDeErrores.cs:224`) | Integration asserting the **translated domain code**, not the exception type |
| `fk_..._punto_venta` | **Yes** (PV as provenance on the ajuste) | `ResolverPuntoVentaAsync` 404 **before** the transaction (the `ServicioDeGastos.cs:28-31` ordering rule: an apocryphal PV is 404, never 409) + generic mapping | Integration per direction |
| `fk_..._comprobante_compra` | **Yes** (the gasto's link) | Already pre-checked under `FOR SHARE` by `ExigirCompraLigableAsync` (`:187-230`) — the TOCTOU guard, not a UX nicety | Race: imputar a payment to a compra being annulled concurrently |
| `fk_..._tenant`, `fk_..._empleado`, `fk_..._gasto` | No — session/server-derived, or the id of the row just inserted | Generic mapping only. **Exemptions documented** | One SQLSTATE-asserting test anyway (the `fk_auditoria_actor` precedent: prove the path, do not assume it) |
| `ck_..._apertura` | No — refused at three layers (decision 15) | **Exemption documented** | Unit on the writer's validator **plus** integration asserting `23514` from a **raw** insert |
| `ak_gastos_id_gasto_id_tenant` | No — structurally unviolable | No `23505` mapping. **Exemption documented** | — |
| New domain codes: `ajuste_importe_invalido` (400), `ajuste_detalle_requerido` (400) — both **reused**, not new | Raised by pure Domain before any query | — | Unit + integration per code |

**`ManejadorDeErrores.cs` is not modified.** No new `23505` family exists in this stage.

## Web composition

`src/Ways.Web/src/paginas/CuentaCorrienteDeProveedor.tsx` (route
`/proveedores/:id/cuenta-corriente`, entered from a per-row action in `Proveedores.tsx` and from
the filtered header of `Compras.tsx`), built from `CuentaCorriente.tsx`'s ledger half plus
`HistoricoDeCajas.tsx`'s pager:

- `react-async-state` rule 8: `key={idProveedor}` on the subtree (`CuentaCorriente.tsx:1458` is the
  precedent) — no filter, page or modal state survives a proveedor switch.
- Rule 2 `generacionRef` on every fetch; rule 3 the generation is bumped **before** the write, and
  the post-write refetch has its own `try/catch` and its own copy (rule 6: a 2xx ajuste is never
  reported as a failure because the refetch failed).
- Rule 9: first-line re-entrancy guard + full-window disable on the ajuste (a double submit moves
  the saldo twice).
- Rule 10 — sibling surfaces: any recovery path added here is grepped for and replicated in the
  sibling modals in the same commit.
- Filters `desde` / `hasta` / "ver histórico" built with `fechaIsoConOffset` (the browser's own
  offset, never `Z`) — the same helper `cuentaCorriente.ts` already duplicates.
- Columns: `Fecha · Tipo · Comprobante/Gasto · Detalle · Importe · Saldo resultante`. A negative
  saldo renders as **"saldo a favor"** (decision 5 of the proposal), never clamped to zero.
- `ResumenSaldoDeProveedor.tsx:13-26` is re-pointed: the caption stops saying *"compras confirmadas
  menos gastos ligados"* and the negative-saldo callout stops saying *"aproximación, no
  invariante"* — both statements are retired by this stage — and gains the link to the new screen.
  It stays a presentational component taking `saldo: number`.
- **Pre-approved degradation**: the ajuste modal is an isolated component with its own props, so
  dropping it is a clean non-delivery (the endpoint still serves the operation), never a retraction
  of shipped UI.

`web-descriptor-tests`: colocated tests for the pure helpers (`etiquetarAjuste`, the movement
mapper, the filter builder) and for the screen's descriptors.

## File Changes

| File | Action | Description |
|---|---|---|
| `src/Ways.Domain/CuentaCorriente/TipoMovimientoCcProveedor.cs` | Create | 4 values in lifecycle order (= the native type's order) |
| `src/Ways.Domain/CuentaCorriente/MovimientoCuentaCorrienteProveedor.cs` | Create | Immutable, no `EntidadBase`, no `EntidadTenant` |
| `src/Ways.Domain/CuentaCorriente/CalculadorDeEstadoDeCuentaDeProveedor.cs` | Create | `EtiquetarAjuste` + the imputación rule, pure |
| `src/Ways.Infrastructure/Persistencia/Migraciones/…_CuentaCorrienteDeProveedoresEtapa15.cs` | Create | **The only** migration — exactly gate §A-§D, RLS last |
| `src/Ways.Infrastructure/…/Configuraciones/MovimientoCuentaCorrienteProveedorConfiguration.cs` | Create | Mirrors `MovimientoCuentaCorrienteConfiguration.cs:18-133` minus AK/self-FK; 6 named indexes |
| `src/Ways.Infrastructure/…/Configuraciones/GastoConfiguration.cs` | Modify | `HasAlternateKey(g => new { g.Id, g.IdTenant })` (gate §D) |
| `src/Ways.Infrastructure/…/Configuraciones/ProveedorConfiguration.cs` | Modify | `saldo numeric(14,2)` |
| `src/Ways.Infrastructure/Persistencia/WaysDbContext.cs` | Modify | `DbSet` + `AplicarFiltroDeTenantEnMovimientoCuentaCorrienteProveedor` (cloned from the `MovimientoStock` one) |
| `src/Ways.Infrastructure/Persistencia/WaysDbContextFactory.cs` · `DependencyInjection.cs` | Modify | `MapEnum<TipoMovimientoCcProveedor>` in **both** builders, never also `HasPostgresEnum` |
| `src/Ways.Application/Abstracciones/IWaysDbContext.cs` | Modify | `DbSet<MovimientoCuentaCorrienteProveedor>` |
| `src/Ways.Application/CuentaCorriente/EscriturasDeCuentaCorrienteProveedor.cs` | Create | The two statements + `ValidarFormaPorTipo` |
| `src/Ways.Application/CuentaCorriente/ServicioDeCuentaCorrienteDeProveedor.cs` | Create | Estado de cuenta + ajuste manual |
| `src/Ways.Application/CuentaCorriente/ContratosDeProveedor.cs` | Create | Read/write DTOs |
| `src/Ways.Application/Compras/ServicioDeCompras.cs` | Modify | 2 call sites + 2 widened `RETURNING`s (`:663-685`, `:687-700`) |
| `src/Ways.Application/Gastos/ServicioDeGastos.cs` | Modify | 1 call site inside `InsertarGastoAsync` (`:134-174`) |
| `src/Ways.Application/Compras/ServicioDeSaldoDeProveedor.cs` | Modify | Two queries re-sourced; **DTOs and `ResolverEstadoPago` untouched** |
| `src/Ways.Api/Endpoints/CuentaCorrienteDeProveedorEndpoints.cs` | Create | 2 routes (group `GET`, top-level `POST /ajustes`) |
| `src/Ways.Api/Seguridad/Politicas.cs` | Modify | `SupervisionDeCuentaDeProveedor` + its registration |
| `src/Ways.Web/src/paginas/CuentaCorrienteDeProveedor.tsx` (+ `.test.tsx`) | Create | Screen + ajuste modal |
| `src/Ways.Web/src/api/cuentaCorrienteDeProveedor.ts` | Create | Client + pure mappers |
| `src/Ways.Web/src/componentes/ResumenSaldoDeProveedor.tsx` | Modify | Caption, saldo-a-favor state, link |
| `src/Ways.Web/src/App.tsx` · `paginas/Proveedores.tsx` · `paginas/Compras.tsx` | Modify | One route + two entry points |
| `docs/10-modelo-de-datos.md` | Modify | The new table, `proveedores.saldo`, "Estado (Etapa 15)", retirement of the doc-10:832-834 note — from **inside slice 1** |
| `src/Ways.Api/Seguridad/ManejadorDeErrores.cs` | **Unmodified** | Gate §E |

## What does NOT change

- **The checkout.** `ServicioDeVentas.EjecutarTransaccionAsync` (`:762-919`) is not opened; no file
  under `src/Ways.Application/Ventas/` appears in this stage's diff. `ServicioDeVentas.cs:657-662`
  is cited as a **precedent only**, and `VentasCheckoutTests`' command-count constant is a
  non-regression criterion over unedited code.
- **`ManejadorDeErrores.cs`** — the generic `fk_`/`23503` prefix rule at `:224` covers the stage.
- **The client-side ledger.** `EscriturasDeCuentaCorriente.cs`, `movimientos_cuenta_corriente`,
  `tipo_movimiento_cc`, `ServicioDeCuentaCorriente`, `ServicioDeReliquidacion` and
  `clientes.saldo`: this stage adds a **parallel** table, it does not generalize the existing one.
- **The arqueo.** No new term, no new formula: the payment is a `gasto` and
  `CalculadorDeArqueo`'s egress term already counts it. `ExigirTurnoAbiertoBajoLockAsync` stays the
  first statement of the gasto transaction.
- **The gastos rules.** `ExigirImporteValido` (`:101-107`), `ExigirCategoriaCoherenteConLaCompra`
  (`:112-120`) and `ExigirCompraLigableAsync`'s four rejections are untouched; a gasto that is
  rejected today is rejected identically and writes no movement.
- **The anulación's inverted gastos rule.** `gastosLigados` (`ServicioDeCompras.cs:594`) stays
  informational and **never blocks**; no gasto is ever reversed (doc-10:465-466 survives verbatim).
- **The proveedor ABM** and its DTOs — `saldo` is not editable by hand and not added there.
- **The `importe` CHECK micro-gate**, `articulos_empresas`, `ways_owner`, and every other reserved
  carryover.
- **`GET /api/proveedores/{id}/saldo`** — same route, same top-level mapping, same policy, same
  three response records.

## Testing Strategy

| Layer | What | Approach |
|---|---|---|
| Domain unit (no DB) | `ValidarFormaPorTipo`: the 4×3 shape matrix of the table above, one fact per illegal combination, `apertura` from a non-migration caller ⇒ throw. `EtiquetarAjuste` both directions. `ReglaDeAjusteDeCuenta` reuse asserted (importe 0 ⇒ 400, detalle 4 chars ⇒ 400) | xUnit pure, `PoliticaDeRoles` pattern. No fixture, no container |
| Integration — **backfill fidelity BY DATA** (the stage's flagship) | Seed a fixture that mixes, per proveedor: borrador / confirmada / anulada compras, linked / unlinked gastos, a `categoria = proveedor` gasto with **`id_proveedor` NULL**, a **soft-deleted** compra, a **soft-deleted** gasto, a **soft-deleted** proveedor, and a proveedor with no history. Capture `ServicioDeSaldoDeProveedor.ObtenerAsync` **before** the migration for every proveedor, migrate, assert `proveedores.saldo == saldoPrevio` **per proveedor** and the `apertura` row's `importe == saldo_resultante == saldoPrevio` | The test that makes the migration provable rather than claimed. Mutation-proof: removing any single `deleted_at IS NULL` or the `id_proveedor IS NOT NULL` clause from the backfill must fail it (that is what the asymmetric fixture buys) |
| Integration — idempotency / no-op | Re-running the migration writes **no** additional `apertura` row and changes **no** saldo; a proveedor with zero derived saldo gets **no** row and keeps `saldo = 0` | Row counts, not exceptions |
| Integration — RLS (`ways_app`) | `SELECT` with another tenant's GUC ⇒ **0 rows** (row count); `INSERT` with a foreign `id_tenant` ⇒ `42501` by SQLSTATE | `mutation-proof-tests` rule 5: `ways_app` (NOSUPERUSER NOBYPASSRLS), statement level |
| Integration — the CHECK | Raw `INSERT` of `tipo = 'apertura'` **with** a PV/empleado, and of `tipo = 'compra'` **without** them ⇒ `23514` by SQLSTATE | Rule 4: assert the discriminating value (the SQLSTATE), never "it threw" |
| Integration — invariant | `proveedores.saldo == Σ importe` over a scenario mixing apertura, compra, pago, ajuste manual and anulación; every row's `saldo_resultante` equals the running sum up to it | The one assertion the whole stage exists to make true |
| Integration — write paths | Confirm ⇒ **exactly one** `compra` movement with `importe = total`; anular ⇒ **exactly one** reversing `ajuste`, gastos **not** reversed, `gastosLigados` unchanged; gasto with `categoria = proveedor` + `id_proveedor` ⇒ **exactly one** `pago`; gasto of another categoria, or with `id_proveedor` NULL ⇒ **zero** movements; **pre-cutover compra annulled** ⇒ one `ajuste` of `−total` with the fallback detalle | One test per direction; the zero-movement cases are as binding as the one-movement cases |
| Integration — fault points | A failure injected at the ledger write of each of the three business paths ⇒ saldo, ledger **and** the business operation all untouched (compra still `borrador`/`confirmada`, no `gastos` row) | Real Postgres, decide-then-commit proven per path |
| Integration — concurrency (the three races) | confirm × pago, pago × pago, anulación × pago on the **same proveedor** — all commit consistently, no deadlock, final `saldo` equals `Σ importe` | Forced rendezvous, `ParametrosTests` precedent |
| Integration — arqueo no-regression | A proveedor payment still appears in the turno's arqueo with **no new term**; a payment with no open turno still returns `409 turno_no_abierto` **and writes no movement** | The proof that decision 7 reused the path instead of forking it |
| Integration — reloj + offset (rule 10) | Everything under `RelojFijo(2026-08-17T12:00:00Z)`: the movement's `fecha` equals the fixed instant **exactly**. Plus one estado de cuenta boundary test sending `desde`/`hasta` with the real client offset `-03:00` (never `Z`), asserting the rows **and** the displayed period | Midday UTC keeps "hoy" stable in UTC and in `-03:00`; the offset test is the only shape that can see a raw-ADO UTC-normalization regression (stage-14 verify W2) |
| Integration — read model | Each filter returns its subset with **asymmetric seeds** (distinct dates, tipos, importes and imputaciones); order asserted as a **sequence**, not a set; pagination with `fecha` **tied on every row** (`RelojFijo`) ⇒ page 2 repeats and skips nothing; `historico` overrides `desde`/`hasta`; no filter ⇒ last month; empty ledger ⇒ empty page with the header still populated | `mutation-proof-tests` rules 4 and 6 |
| Integration — `/saldo` contract | Response **byte-compatible** with the pre-stage shape over the same data (all three records, all fields, per row); a fully paid compra ⇒ `pagada`, a partially imputed one ⇒ `parcial`, an **unimputed** payment reduces the total saldo **without** settling any compra; a **pre-cutover** confirmed compra with no payments ⇒ `impaga`; cross-tenant ⇒ 404 | Rule 6: every column of every row, with discriminating values per row |
| Integration — authorization | Vendedor ⇒ 200 on the estado de cuenta and on `/saldo`, **403** on `POST /ajustes`; Supervisor ⇒ 200 on the ajuste; Admin ⇒ 200; Root ⇒ 403; tenant B never sees tenant A's movements | One test per role per route + the allowlist |
| Web (vitest) | Pure mappers and `etiquetarAjuste`; filters reset to page 1; a stale response is discarded (**stale promise resolved inside `act`**, rule 7); pager disabled at the edges; a negative saldo renders "saldo a favor"; a double click on "Registrar ajuste" issues exactly one POST | `web-descriptor-tests` + `react-async-state` |
| Exempt | Visual styling of the screen beyond its testids — exemption registered, inherited from stages 12/13/14 | — |

## Mutation targets

`mutation-proof-tests`: name the clause, apply the mutation, watch the named test fail, revert,
record the evidence (applied → failing test → reverted → green) in the PR body. **28 targets,
colocated with the slice that introduces the clause.**

| # | Slice | Clause | Mutation | Test that MUST fail |
|---|---|---|---|---|
| 1 | 1 | `deleted_at IS NULL` on `comprobantes_compra` in the backfill | delete it | fidelity by data (the soft-deleted compra) |
| 2 | 1 | `deleted_at IS NULL` on `gastos` in the backfill | delete it | fidelity by data (the soft-deleted gasto) |
| 3 | 1 | `deleted_at IS NULL` on `proveedores` in the backfill | delete it | fidelity by data (the soft-deleted proveedor gets no row) |
| 4 | 1 | `id_proveedor IS NOT NULL` in the backfill's gastos predicate | delete it | fidelity by data (the `id_proveedor` NULL gasto) |
| 5 | 1 | `estado = 'confirmada'` in the backfill | widen to any estado | fidelity by data (borrador + anulada) |
| 6 | 1 | `WHERE d.saldo <> 0` | delete it | "a proveedor with no history gets no row" |
| 7 | 1 | `NOT EXISTS (...)` idempotency guard | delete it | re-run writes a second `apertura` |
| 8 | 1 | Statement 2 deriving the cache **from** the row of statement 1 | recompute it independently | fidelity by data (both must agree by construction) |
| 9 | 1 | `HabilitarRlsDeTenant("movimientos_cuenta_corriente_proveedor")` | delete the line | cross-tenant row count on `ways_app` + `42501` |
| 10 | 1 | The `ck_..._apertura` CHECK in the migration | delete it | raw-insert `23514` (both directions) |
| 11 | 1 | RLS ordered **last** in the migration | move it before the backfill | migration fails / backfill writes zero rows |
| 12 | 2 | `ValidarFormaPorTipo`, `compra` requires a comprobante | delete the arm | Domain fact |
| 13 | 2 | `ValidarFormaPorTipo`, `apertura` forbids actor/PV | delete the arm | Domain fact |
| 14 | 2 | `ParametrosDeComando.Agregar` on `fecha` | replace with a hand-built parameter without `ToUniversalTime()` | the `-03:00` offset test (a `Z` fixture cannot see it) |
| 15 | 2 | `id_proveedor, total` added to `ConfirmarHeaderAsync`'s `RETURNING` | read them from `preLectura` instead | confirm-under-concurrent-`PUT` test (the movement's importe) |
| 16 | 2 | The saldo `UPDATE` **before** the ledger `INSERT` | swap them | `saldo_resultante` no longer equals the post-update saldo |
| 17 | 2 | `saldo = saldo + $1` raw | replace with a tracked `proveedor.Saldo +=` | double-count under a forced execution-strategy retry |
| 18 | 2 | `AND id_tenant = $3` in the saldo `UPDATE` | delete it | cross-tenant update test routed **below** RLS (rule 3) |
| 19 | 2 | The proveedor lock placed **after** the stock loop | move it to step 1.5 | confirm × pago rendezvous (deadlock/timeout) |
| 20 | 2 | The pre-cutover fallback of the contramovimiento | remove the fallback (always the ledger sum) | annulling a pre-cutover compra leaves the debt on the books |
| 21 | 3 | `categoria = proveedor && id_proveedor is not null` | drop either conjunct | the zero-movement tests (other categoria / NULL proveedor) |
| 22 | 3 | The ledger write placed **after** `SaveChangesAsync` | move it before | `id_gasto` is 0 / FK violation |
| 23 | 3 | `importe = −gasto.Importe` (the sign) | drop the negation | invariant test (`saldo == Σ importe`) |
| 24 | 4 | `pagado = −Σ importe WHERE tipo <> 'compra'` | use the raw `SUM(importe)` | pre-cutover compra reads `pagada` instead of `impaga` |
| 25 | 4 | `ThenByDescending(IdMovimiento)` | delete it | pagination with `fecha` tied on every row |
| 26 | 4 | Each `if (desde/hasta/historico …)` in `ConstruirQuery` | delete one | that filter's test (asymmetric seeds) |
| 27 | 5 | `.RequireAuthorization(Politicas.SupervisionDeCuentaDeProveedor)` | delete the line | **Vendedor ⇒ 403** on `POST /ajustes` |
| 28 | 6 | The saldo-a-favor branch in `ResumenSaldoDeProveedor.tsx` | delete it | colocated descriptor test on a negative saldo |
| — | — | **Non-regression**: `VentasCheckoutTests` | — | verify criterion: the file does not appear in the stage's diff |

## Slicing (6 PRs, stacked-to-main — the proposal's plan, ratified with one re-scoping)

| # | Branch | Content | ~Lines | Test plan |
|---|---|---|---|---|
| 1 | `feat/stage15-slice1-ledger-schema` | Migration (type, table, 6 FKs, 6 indexes, CHECK, both ALTERs, both data statements, RLS last) + entity + enum + EF config + `MapEnum` in both builders + cloned tenant filter + doc 10 | ~450 | RLS on `ways_app`; `42501`; `23514` both directions; **fidelity by data**; idempotency; zero-saldo no-op |
| 2 | `feat/stage15-slice2-escrituras-y-deuda` | `EscriturasDeCuentaCorrienteProveedor` (two statements + validator) + the `compra` movement in `EjecutarConfirmarAsync` + the reversing `ajuste` in `EjecutarAnulacionAsync` (with the pre-cutover fallback) + both widened `RETURNING`s + the pinned lock order | ~410 | Validator matrix; confirm/anulación coverage; the `-03:00` offset test; confirm × pago rendezvous; fault points |
| 3 | `feat/stage15-slice3-pago-por-gasto` | The `pago` movement inside `InsertarGastoAsync` + imputación + the predicate scenarios | ~330 | Zero-movement directions; pago × pago and anulación × pago races; arqueo no-regression; `409 turno_no_abierto` writes nothing |
| 4 | `feat/stage15-slice4-estado-de-cuenta` | `ServicioDeCuentaCorrienteDeProveedor` read half + `GET …/cuenta-corriente` (paginated, running balance, desde/hasta/histórico, empty state) + `ServicioDeSaldoDeProveedor` re-sourced with its DTOs unchanged | ~420 | Filters with asymmetric seeds; tied-`fecha` pagination; `/saldo` byte-compatibility; the pre-cutover `impaga` case; authorization |
| 5 | `feat/stage15-slice5-ajuste-manual` | `SupervisionDeCuentaDeProveedor` + `POST …/ajustes` (top-level) + `ReglaDeAjusteDeCuenta` reuse | ~260 | 403/200 matrix; detalle/importe rejections; PV 404 before turno-less transaction |
| 6 | `feat/stage15-slice6-web` | `CuentaCorrienteDeProveedor.tsx` + ajuste modal + `ResumenSaldoDeProveedor.tsx` + client + route + entry points | ~380 | Descriptor tests; stale inside `act`; pager edges; saldo a favor; single POST on double click |

Total ≈ **2 250**. Merge order `1 → 2 → 3 → 4 → 5 → 6`. Slice 1 blocks everything (it owns the only
migration); 3 depends on 2 only for the writer class; 4 and 5 depend on 1 and 2; 6 depends on 4
and 5.

**Decision needed before apply: No** · **Chained PRs recommended: Yes** · **400-line budget risk:
Medium** (`delivery_strategy: auto-chain`, `chain_strategy: stacked-to-main`, one judgment-day
round per slice).

**Pre-approved degradation**, in priority order:

1. **If slice 1 overflows** — split at the DDL/proof boundary: `1a` (migration + entity + config +
   RLS/CHECK tests) and `1b` (fidelity + idempotency tests + doc 10). The split keeps **one**
   migration, which is the invariant that must not be degraded.
2. **If slice 2 overflows** — split at the write-path boundary: `2a` (writer class + widened
   `RETURNING`s + the `compra` movement on confirm) and `2b` (the anulación contramovimiento, its
   pre-cutover fallback and its races).
3. **If slice 4 overflows** — split at the read/re-sourcing boundary: `4a` (estado de cuenta) and
   `4b` (the `/saldo` re-sourcing and its byte-compatibility proof).
4. **If slice 6 overflows** — ship the list, the running balance and the filters, and drop the
   ajuste modal (the endpoint still serves the operation). A documented reduction, never silent.
5. **Never degraded**: the backfill fidelity proof, the single-write-authority containment, the
   anulación contramovimiento, and the pre-cutover `impaga` case. A ledger that starts wrong, that
   diverges on the first anulación, or that reports a pre-cutover debt as paid is worse than no
   ledger — those are split, never trimmed.

## Binding verify criteria

1. Exactly **one** migration, `CuentaCorrienteDeProveedoresEtapa15`, with the DDL of gate §A-§D and
   nothing else; **7 new indexes** and no unnamed EF-generated FK support index;
   `dotnet ef migrations has-pending-model-changes` clean. Any extra DDL reopens the gate.
2. `ManejadorDeErrores.cs` unchanged; `movimientos_cuenta_corriente`, `tipo_movimiento_cc`,
   `EscriturasDeCuentaCorriente.cs` and `clientes.saldo` unchanged; no file under
   `src/Ways.Application/Ventas/` in the stage's diff.
3. `ServicioDeSaldoDeProveedor`'s three response records and `ResolverEstadoPago` byte-identical.
4. Mutation evidence recorded in the PR body for **every** row of the table above belonging to that
   slice.
5. Domain / Application / Integration / vitest suites green; colocated tests for every new pure web
   helper (`web-descriptor-tests`).

## Threat Matrix

N/A — this stage touches no routing, shell command, subprocess, VCS/PR automation, executable-file
classification or process integration. Its risk surfaces (tenant isolation, authorization, lock
order, migration fidelity) are covered by the mutation-target table, which **is** binding.

## Open Questions / tensions with the proposal

- [ ] **Per-compra payment status: the proposal's formula breaks on pre-cutover compras.**
      Decision 7 pins `SUM(importe) WHERE id_comprobante_compra = X … <= 0 ⇒ pagada`; a compra
      confirmed before the cutover has no `compra` movement (decision 1 forbids the replay), so it
      sums to `0` and reads **`pagada`** where the retired spec reads `impaga`. This design uses
      `pagado = −Σ importe WHERE tipo <> 'compra'` against `comprobantes_compra.total` (decision 8),
      which reproduces the retired outcomes on **both** sides of the cutover. **`sdd-spec` runs in
      parallel and will very likely transcribe the proposal's formula verbatim — reconcile in
      `sdd-tasks`.**
- [ ] **Annulling a pre-cutover compra.** The proposal pins `importe = −(the compra movement's
      importe)` and never states what happens when there is none. This design falls back to
      `−total` with a naming detalle (decision 6). Same reconciliation.
- [ ] **The estado de cuenta is paginated here; the stage-7 precedent is not** (decision 10, from
      the stage-14 offset + tiebreaker pattern). The proposal says only "movement list with running
      balance, desde/hasta, histórico". If `sdd-spec` transcribes the stage-7 unpaginated shape,
      the DTO differs.
- [ ] **The `/export` sibling is Out of Scope in the proposal, and the design task asked for it.**
      The read model is deliberately export-ready — `ConstruirQuery` is shared, and the etapa-11
      infra (`ExportacionDeListados`, `ContextoDeExportacionHttp`, `NombreDeArchivo`,
      `GuardaDeTope.Exigir` twice, the co-located route inheriting the group's policy) needs only
      the route + column set, exactly as `CuentaCorrienteEndpoints.cs:74-99` does for clients. **Not
      designed in, not costed in any slice.** The orchestrator decides whether it enters slice 4;
      if it does, `mutation-proof-tests` rule 9's three per-route gaps and rule 8's header assertion
      apply and add ~120 lines.
- [ ] **`POST /ajustes` is mapped top-level, not stacked** (decision 12), which departs from the
      stage-7 route shape while honouring proposal decision 8's rejection of the AND-composition.
      Both readings are defensible; the design picked the one the proposal argued for.
- [ ] **`EscriturasDeCuentaCorriente.cs:124-144` still carries its own private `AgregarParametro`**
      while `ParametrosDeComando` is already in `main` and used by `ServicioDeCompras`/
      `ServicioDeVentas`. This design writes the **new** class against `ParametrosDeComando` and
      does **not** refactor the old one — that in-flight cleanup is out of this stage's scope and
      must not be smuggled into a slice.
- [ ] **`proveedores` is a catálogo row carrying an operativa cache** (`saldo`), so a proveedor
      shared across empresas via `id_empresa NULL` has **one** saldo for the whole tenant while its
      movements are stamped per PV. That is the proposal's deliberate asymmetry (identical to
      `clientes.saldo`), recorded here because the estado de cuenta screen shows both at once.
- [ ] **Soft-deleting a proveedor with a non-zero saldo stays allowed** (proposal Out of Scope). The
      ledger is append-only so nothing corrupts, but the estado de cuenta of a deleted proveedor is
      unreachable through the ABM. Product question, not silently tightened.
