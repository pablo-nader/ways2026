# Design: Stage 16 — Órdenes de compra

## Technical Approach

**One document, one book, one projection class, one guarded call twice into an engine that does not
move.**

The proposal's `Modelo de datos propuesto` (§A-§F) is the ratified gate contract and this design
**adds no DDL of any kind** — not a column, not an index, not a constraint, not an enum value.
Everything below is code over that exact schema.

Five structural facts decide the shape.

1. **The reception book already exists and is already locked.** `EjecutarConfirmarAsync`'s step 1
   (`ServicioDeCompras.cs:333`, statement at `:715-737`) commits `estado = 'confirmada'` **inside
   the transaction**, and the comprobante's items were frozen at draft time. From that instant the
   derivation `Σ items_comprobante_compra.cantidad over linked confirmed comprobantes` is complete —
   before lotes, before stock, before `proveedores`. That is what makes the OC lock at position 2
   both possible and sufficient.

2. **A self-referential `UPDATE … FROM (SELECT …)` is wrong here, and the file already knows it.**
   Under `READ COMMITTED` the re-check after a block (`EvalPlanQual`) re-evaluates only the locked
   row; the subquery keeps the snapshot the statement started with. Two receptions of one OC
   confirming concurrently would both project from a pre-winner book and the loser would overwrite
   the winner's estado. The fix is the shape this file uses everywhere else: **lock first
   (`SELECT … FOR UPDATE`), re-read in a separate statement (new snapshot), then `UPDATE …
   RETURNING`** — three statements, never one.

3. **`proveedores` is the last row lock taken for update** (stage-15 decision 5, warning written
   into the code at `ServicioDeCompras.cs:469-472`). The OC lock therefore goes at **position 2**,
   immediately after the header, never after. No other path locks `ordenes_compra` together with
   anything else, so no cycle is reachable — **provided** the anulación's linked-draft guard reads
   `comprobantes_compra` **without** a row lock (decision 9 below).

4. **The number is consumed outside the transaction, by an existing class, unmodified.**
   `AsignadorDeNumeroComprobante.AsignarComprometidoAsync` (`:45-55`) opens and commits **its own**
   small transaction, so it must be called before the `enviar` transaction opens, wrapped in
   `db.Database.CreateExecutionStrategy()` — the exact shape `ServicioDeVentas.cs:278-280` uses. Its
   documented residue (*"el número se consume aunque falle el resto"*, `:29-32`) is inherited
   verbatim: a failed `enviar` burns an OC number, and an OC series is not fiscal.

5. **For a comprobante with no OC — 100% of today's traffic — the engine emits zero extra
   statements.** The coupling is `if (encabezado.IdOrdenCompra is { } idOc)` around one call, twice.
   `ConfirmarHeaderAsync`/`MarcarAnuladaAsync` widen their `RETURNING` by one column each (the
   stage-15 decision-4 criterion: the value **this lock** saw, never `preLectura`, which
   `AsNoTracking()`s outside the transaction at `:276`/`:497`).

`ServicioDeCompras` steps 2, 2.b, 3, 4, 5 and 6 (`:357-482`) are **byte-identical**. No file under
`src/Ways.Application/Ventas/` and no file under `src/Ways.Application/Stock/` is in this stage's
diff; `AsignadorDeNumeroComprobante` is **read, not edited**.

## Architecture Decisions

| # | Decision | Alternatives considered | Rationale |
|---|---|---|---|
| 1 | **`EscriturasDeOrdenDeCompra` is a `static` class with exactly four statements and one pure decision** — structural copy of `EscriturasDeCuentaCorrienteProveedor`: not injected, called with the caller's `DbConnection` + `DbTransaction?`, never opens/flushes/commits anything | A DI-registered `IProyectorDeOrden`; a method on `ServicioDeOrdenesDeCompra` | Containment is the product: exactly ONE place in the codebase writes `ordenes_compra.estado` from the book, called from `EjecutarConfirmarAsync` and `EjecutarAnulacionAsync`. A DI seam exists only for a test double no test here needs (the real Postgres races are the proof), and an interface over it invites the second implementation the class exists to prevent. `ServicioDeCompras` cannot depend on `ServicioDeOrdenesDeCompra` without a cycle |
| 2 | **`ProyectarEstadoAsync` = lock → short-circuit → derive → conditional `UPDATE … RETURNING`.** Statement 1 `SELECT estado::text, (id_empleado_cierre IS NOT NULL) … FOR UPDATE`; if `anulada` **or** manual close ⇒ **return without a second statement**; statement 2 the derivation (new snapshot); statement 3 the `UPDATE`, **skipped when the projected estado equals the current one** | One `UPDATE … FROM (SELECT …)`; lock + `UPDATE` with the derivation inline in the `SET` | The stale-snapshot trap of §2 above. The short-circuits are not optimizations: a terminal `anulada` and a human's manual close are the two facts the book must never overturn (decision 3 of the proposal), and expressing them as *early returns under the lock* makes them unbypassable rather than a `CASE` arm someone can widen. Skipping the no-op `UPDATE` makes idempotency observable (a re-projection writes zero rows) |
| 3 | **The derivation groups by `id_articulo` on BOTH sides and asks two questions, not one.** `completa = NOT EXISTS (ordered artículo whose Σ pedida > Σ recibida)`; `algoRecibido = Σ recibida over the linked confirmed lines > 0`, computed on the **reception side alone** | `algoRecibido` derived from the ordered side (the `LEFT JOIN`'s coalesced sum) | Proposal decision 2 makes line-to-line matching impossible (an artículo may repeat on both sides), so both sides group. Sourcing `algoRecibido` from the ordered side would make a **pure-substitution delivery invisible**: the supplier shipped, a confirmed comprobante is linked, and the OC would still read `enviada`. Received-and-not-ordered is informational (decision 2), not nonexistent |
| 4 | **The target estado is decided by a pure Domain function, `ProyectorDeEstadoDeOrden.Proyectar(estadoActual, cierreManual, completa, algoRecibido)`** — no database, `PoliticaDeRoles` pattern | The `CASE` expression inside the SQL | The five-arm rule of proposal decision 3 is the stage's whole semantics; it gets unit tests without a container, and the read model asserts against the **same** function so the stored estado and the displayed quantities can be proven consistent (testing strategy, "projection fidelity") |
| 5 | **The automatic close writes `fecha_cierre` and leaves `id_empleado_cierre` NULL; a regression out of `cerrada` sets `fecha_cierre = NULL` in the same statement** | Leave `fecha_cierre` on regression; a separate `es_cierre_manual` boolean | `id_empleado_cierre` **is** the manual/automatic discriminator (proposal decision 3, the stage-15 `apertura` precedent: a NULL actor means no human did this), so no second column is needed. Leaving `fecha_cierre` behind on a regression violates `ck_ordenes_compra_cierre` (`(fecha_cierre IS NULL) = (estado <> 'cerrada')`) — the CHECK turns a forgotten clause into a `23514`, which is exactly why it exists |
| 6 | **`enviar` assigns the number BEFORE its transaction, and the transition `UPDATE` pins the punto de venta the series was consumed from**: `… WHERE id_orden_compra = $ AND id_tenant = $ AND estado = 'borrador' AND id_punto_venta = $pv RETURNING numero` | Assign inside the transaction; omit the PV from the `WHERE` | The assigner commits its own transaction (`:48-53`); nesting it would either throw or hold the counter row for the whole caller transaction, reintroducing the contention the class exists to avoid. The PV conjunct closes a real race the proposal does not name: a concurrent `PUT` can move the draft to another punto de venta between the pre-read and the lock, and the number already drawn belongs to the **old** series (`ux_ordenes_compra_numero` is `(id_tenant, id_punto_venta, numero)`). 0 rows ⇒ reclassify under the lock, the number is burnt, nothing is corrupt |
| 7 | **`enviar` refuses an OC with no items** (`orden_compra_sin_items`, 400), mirroring `compra_sin_items` (`ServicioDeCompras.cs:362-365`) | Allow it | With zero ordered lines the derivation's `NOT EXISTS` is vacuously true, so the first projection would read the order **`cerrada`** — an order nobody placed, closed by arithmetic. The guard is one line and it is the only place this can be prevented honestly |
| 8 | **The draft link is validated by `ExigirOrdenLigableAsync` (`SELECT … FOR SHARE`), and the BINDING guard is the confirm-time `FOR UPDATE`.** Linkable estados: `enviada`, `recibida_parcial`, `cerrada`. Refused: `borrador` (`orden_compra_no_enviada`, 409) and `anulada` (`orden_compra_anulada`, 409). Proveedor and punto de venta must agree (400 each) | Refuse `cerrada` too; validate without a lock | The `FOR SHARE` shape is `ServicioDeGastos.ExigirCompraLigableAsync` verbatim (`:224-267`, statement at `:232-234`): under `ActualizarBorradorAsync`'s transaction (`:215`) it is a real TOCTOU guard against a concurrent annulment. **Honest residue**: `CrearBorradorAsync` (`:148-189`) has no transaction, so there the `FOR SHARE` is taken and released immediately — a coherence validation, not a lock. `db-error-backstops` already says a pre-check is best-effort UX and the real contract is the constraint plus the backstop; here the real contract is the OC row lock inside confirm. **`cerrada` stays linkable** because an over-delivery arriving after an automatic close is exactly the informational posture of decision 2 — and a *manually* closed OC is not walked back by the projection anyway (decision 5) |
| 9 | **The anulación of an OC takes the OC row lock as its first statement, and its linked-draft guard reads `comprobantes_compra` WITHOUT any row lock** | `SELECT … FOR SHARE` on the linked drafts, "for symmetry" | This is the one place a cycle is reachable: confirm holds the comprobante header and wants the OC; adding a lock here would make the annulment hold the OC and want a comprobante. A plain snapshot read never blocks under `READ COMMITTED`, so the order stays total. The TOCTOU it appears to leave open is already closed on the other side: a draft created after this read can only become confirmed by passing the position-2 OC lock, where it meets `anulada` and gets `409 orden_compra_anulada` |
| 10 | **`ManejadorDeErrores`'s six new branches ship in slice 1, with the migration** — 2 exact-name `23505` (one of them the `_numero` trap) + 4 exact-name `23514` | Ship them in slice 2 with the write path (the proposal's table) | `db-error-backstops` Execution Step 3: the SQLSTATE test belongs to the work unit that adds the constraint, and at slice 1 every one of them is reachable **only** out of band — which is precisely how they are proven (raw insert asserting the SQLSTATE **and** the translated domain code). Slice 2 then carries only the *concurrency* proof of the assigner. This is the one adjustment this design makes to the proposal's slice table |
| 11 | **The `ux_ordenes_compra_numero` branch resolves by exact name ABOVE the `ClasificarUnicidad` call** (`ManejadorDeErrores.cs:180-182`), in the same `when string.Equals(…, OrdinalIgnoreCase)` shape as `:127-129` and `:136-138` | Extend `ClasificarUnicidad` | Third occurrence of the documented ordering trap: the name contains `_numero`, so the generic `ux_clientes_numero` family would classify it as `numero_duplicado` first. The branch is proven by asserting the **translated code** `numero_de_orden_duplicado`, never the SQLSTATE alone |
| 12 | **The read model derives quantities per artículo and READS the estado from the column** — it never re-derives the estado | Recompute the estado on read; store `cantidad_recibida` | Proposal decisions 2 and 3. `mutation-proof-tests` rule 12(a) then applies literally: a raw `UPDATE` desyncs the stored estado to a sentinel and the endpoint must return the sentinel. Consistency between the two is proven separately by the projection-fidelity test (decision 4), not by making the read recompute |
| 13 | **The detail returns the ordered lines AND a separate per-artículo `cobertura` list** (`Pedida`, `Recibida`, `Pendiente`, `CostoEstimado`, `CostoReal`, `Desvio`), including artículos with `Pedida = 0` | One list, `recibida` per line | Grouping is by artículo on both sides (decision 3), so a per-line `recibida` would be a fabricated split of a number the system does not have — `dto-contract-honesty` rule 1. `Pedida = 0` rows are how received-and-not-ordered becomes visible instead of silently absent |
| 14 | **Price deviation: weighted averages per artículo, `null` when not comparable, never `0`.** `CostoReal` uses the existing `CalculadorDeCompra.CalcularCostoEfectivoDesdeItem(total, cantidad, porcentajeIva, discriminaIva)` (`ServicioDeCompras.cs:459`) with each linked comprobante's own `tipos_comprobante.discrimina_iva` | Compare `costo_unitario` raw | Comparing a raw unit cost against an IVA-discriminating invoice would show a 21% "deviation" that is an accounting artifact. Reusing the calculator makes the comparison consistent with what actually reached `costo_nominal`. The stage-13 honest-nulls discipline governs the empty side |
| 15 | **The listing is paginated (`PaginaDeOrdenesDeCompra`, `CountAsync` + `Skip/Take`), ordered `fecha_emision DESC, id_orden_compra DESC`**, `pagina = Math.Max(pagina, 1)`, `tamanio = Math.Clamp(tamanio, 1, 200)` | `ServicioDeCompras.ListarAsync`'s `OrderByDescending(c => c.Id)` alone | Copied from `ServicioDeCuentaCorrienteDeProveedor.ConstruirQuery` (`:86-102`). The `Id` tiebreaker is **not cosmetic**: `fecha_emision` is one `reloj.Ahora` per operation, so an entire `RelojFijo` fixture ties by construction and pagination duplicates and skips rows without it (stage-14 decision 12, re-proven in stage 15) |
| 16 | **No new policy.** `/api/ordenes-compra` groups under `OperacionDePos`; `POST`/`PUT`/`enviar`/`cerrar`/`anular` stack `GestionDeCatalogo` — the exact shape of `ComprasEndpoints.cs:20-22, 76, 84, 92, 100, 109` | `GestionDeCompras`; a supervision policy | Proposal decision 7, verified against the code. **Consequence the proposal does not draw**: `Reposicion.tsx` lives under `LecturaDeReportes` (Supervisor + Admin, `App.tsx` `rolesPermitidos={[ROL.Supervisor, ROL.Admin]}`) while creating an OC is Admin-only, so the *"generar OC"* action must be gated on `useAuth().usuario.rolId === ROL.Admin` in the web or a Supervisor clicks into a 403 (tension T5) |

## Interfaces / Contracts

### Application — the one projection authority

```csharp
// Ways.Application/Compras/EscriturasDeOrdenDeCompra.cs
// Copia estructural de EscriturasDeCuentaCorrienteProveedor: static, misma postura de
// conexión/transacción del llamador, todos los parámetros por ParametrosDeComando.
public static class EscriturasDeOrdenDeCompra
{
    /// Lock → cortocircuito → derivación → UPDATE. TRES statements como mínimo, nunca uno:
    /// bajo READ COMMITTED un UPDATE ... FROM (SELECT ...) re-evalúa solo la fila lockeada
    /// (EvalPlanQual) y su subconsulta conserva el snapshot inicial — dos recepciones
    /// concurrentes de la MISMA OC proyectarían desde un libro viejo.
    /// Devuelve el estado vigente tras la proyección (== el previo si no escribió).
    public static Task<EstadoOrdenCompra> ProyectarEstadoAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, int idOrdenCompra,
        DateTimeOffset momento, CancellationToken ct);

    /// Guard de defensa en profundidad del camino de confirmación: toma el MISMO lock y
    /// rechaza una OC anulada (409 orden_compra_anulada) antes de que la proyección escriba.
    /// Expuesto aparte para que el call site de ConfirmarAsync no tenga que interpretar estados.
    public static Task<EstadoOrdenCompra> BloquearYExigirNoAnuladaAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, int idOrdenCompra,
        CancellationToken ct);
}
```

```sql
-- Statement 1 — el lock explícito. SIEMPRE primero.
SELECT estado::text, (id_empleado_cierre IS NOT NULL) AS cierre_manual
FROM ordenes_compra
WHERE id_orden_compra = $1 AND id_tenant = $2
FOR UPDATE
-- 0 filas ⇒ InvalidOperationException (la FK garantiza la fila; el 404 vive aguas arriba).
-- estado = 'anulada' O cierre_manual ⇒ return, SIN statement 2 ni 3.

-- Statement 2 — la derivación, en su PROPIO statement (snapshot nuevo: ve el commit del ganador).
WITH pedido AS (
    SELECT i.id_articulo, SUM(i.cantidad_pedida) AS pedida
    FROM items_orden_compra i
    WHERE i.id_orden_compra = $1 AND i.id_tenant = $2 AND i.deleted_at IS NULL
    GROUP BY i.id_articulo),
recibido AS (
    SELECT ic.id_articulo, SUM(ic.cantidad) AS recibida
    FROM items_comprobante_compra ic
    JOIN comprobantes_compra c
      ON c.id_comprobante_compra = ic.id_comprobante_compra AND c.id_tenant = ic.id_tenant
    WHERE c.id_orden_compra = $1 AND c.id_tenant = $2
      AND c.estado = 'confirmada'::estado_compra
      AND c.deleted_at IS NULL AND ic.deleted_at IS NULL
    GROUP BY ic.id_articulo)
SELECT
    NOT EXISTS (SELECT 1 FROM pedido p
                LEFT JOIN recibido r ON r.id_articulo = p.id_articulo
                WHERE p.pedida > COALESCE(r.recibida, 0))          AS completa,
    COALESCE((SELECT SUM(recibida) FROM recibido), 0) > 0          AS algo_recibido

-- Statement 3 — la ÚNICA autoridad de transición. Omitido cuando nuevo == actual (idempotencia
-- observable: una re-proyección escribe cero filas).
UPDATE ordenes_compra
SET estado = $3::estado_orden_compra,
    fecha_cierre = CASE WHEN $3 = 'cerrada' THEN $4 ELSE NULL END,   -- regresión limpia fecha_cierre
    updated_at = $4
WHERE id_orden_compra = $1 AND id_tenant = $2 AND estado = $5::estado_orden_compra
RETURNING estado::text
```

### Domain — pure, no database

```csharp
// Ways.Domain/Compras/EstadoOrdenCompra.cs — el ORDEN de los miembros ES el orden de valores
// del tipo nativo (npgsql.MapEnum<T>, gate §A).
public enum EstadoOrdenCompra { Borrador, Enviada, RecibidaParcial, Cerrada, Anulada }

// Ways.Domain/Compras/ProyectorDeEstadoDeOrden.cs — la regla de decisión 3 del proposal,
// unitaria sin base de datos (patrón PoliticaDeRoles).
public static EstadoOrdenCompra Proyectar(
    EstadoOrdenCompra estadoActual, bool cierreManual, bool completa, bool algoRecibido) =>
        estadoActual is EstadoOrdenCompra.Anulada          ? EstadoOrdenCompra.Anulada
      : cierreManual                                       ? EstadoOrdenCompra.Cerrada
      : completa                                           ? EstadoOrdenCompra.Cerrada
      : algoRecibido                                       ? EstadoOrdenCompra.RecibidaParcial
      :                                                      EstadoOrdenCompra.Enviada;

// Ways.Domain/Compras/OrdenCompra.cs / ItemOrdenCompra.cs — EntidadTenant (⇒ EntidadBase),
// filtro de tenant estándar y EstamparTenant, como ComprobanteCompra (gate §B/§C).
```

### Application — read/write contracts

```csharp
// Ways.Application/Compras/ContratosDeOrdenDeCompra.cs
public sealed record LineaDeOrdenSolicitada(int IdArticulo, string Descripcion,
    decimal CantidadPedida, decimal? CostoUnitarioEstimado);

// `orden` NO viaja en la solicitud: es server-asignado 1..N dentro del replace-set.
public sealed record SolicitudDeOrdenDeCompra(int IdProveedor, int IdPuntoVenta,
    DateOnly? FechaEsperada, string? Observaciones, IReadOnlyList<LineaDeOrdenSolicitada> Items);

public sealed record ItemDeOrden(int Orden, int IdArticulo, string Descripcion,
    decimal CantidadPedida, decimal? CostoUnitarioEstimado);

// Decisión 13: la cobertura es POR ARTÍCULO, nunca por línea. Pedida = 0 ⇒ recibido-no-pedido.
public sealed record CoberturaDeArticulo(int IdArticulo, decimal Pedida, decimal Recibida,
    decimal Pendiente, decimal? CostoEstimado, decimal? CostoReal, decimal? Desvio);

public sealed record OrdenDeCompraDetalle(int Id, int IdProveedor, int IdPuntoVenta, long? Numero,
    DateTimeOffset FechaEmision, DateTimeOffset? FechaEnvio, DateOnly? FechaEsperada,
    DateTimeOffset? FechaCierre, bool CierreManual, string? Observaciones, EstadoOrdenCompra Estado,
    IReadOnlyList<ItemDeOrden> Items, IReadOnlyList<CoberturaDeArticulo> Cobertura,
    decimal? TotalEstimado, decimal? TotalReal, decimal? DesvioTotal,
    IReadOnlyList<int> ComprobantesLigados);

public sealed record OrdenDeCompraListada(int Id, int IdProveedor, int IdPuntoVenta, long? Numero,
    DateTimeOffset FechaEmision, DateOnly? FechaEsperada, EstadoOrdenCompra Estado);

public sealed record PaginaDeOrdenesDeCompra(
    IReadOnlyList<OrdenDeCompraListada> Items, int Total, int Pagina, int Tamanio);
```

```csharp
/// Cláusulas bajo prueba (mutation-proof-tests), en orden de daño si se pierden:
///   Where(o => o.IdProveedor == p) / Where(o => o.Id == id) → una OC filtra las de otra entidad
///   ThenByDescending(o => o.Id)   → con fecha_emision empatada (RelojFijo) la paginación
///                                    duplica y saltea
///   cada if (idProveedor/idPuntoVenta/estado/desde/hasta is { } x) → un filtro ignorado
///                                    devuelve de más, en silencio
private IQueryable<OrdenCompra> ConstruirQuery(
    int? idProveedor, int? idPuntoVenta, EstadoOrdenCompra? estado,
    DateTimeOffset? desde, DateTimeOffset? hasta);
```

`SolicitudDeCompra` gains `int? IdOrdenCompra` **and `CompraDetalle` gains `int? IdOrdenCompra`** —
`dto-contract-honesty` rule 2 requires the round-trip assertion, which a request-only field cannot
satisfy (tension T7). No route, no policy and no other field changes in `ComprasEndpoints`.

## Transactions (binding statement order)

```
── CONFIRMAR COMPRA ──────────────────────────────────────────────────────────────
  ServicioDeCompras.EjecutarConfirmarAsync (:314-487)
   1. UPDATE comprobantes_compra ... RETURNING id_punto_venta, id_tipo_comprobante,
                                     id_proveedor, total, id_orden_compra   ← lock header (:333)
   1.b si id_orden_compra IS NOT NULL  (si es NULL: CERO statements extra)
       1.b.1 BloquearYExigirNoAnuladaAsync   ← SELECT ... FOR UPDATE de la OC (posición 2)
                                               anulada ⇒ 409 orden_compra_anulada
       1.b.2 derivación (statement propio, snapshot nuevo)
       1.b.3 UPDATE ordenes_compra ... RETURNING   (omitido si el estado no cambia)
   2. items (read set congelado, :357) · 2.b lotes (:403-428)      ← SIN CAMBIOS
   3. movimientos_stock + stock + stock_lotes por item (:439-452)  ← SIN CAMBIOS
   4. costo_nominal (:464-467)                                     ← SIN CAMBIOS
   5. ActualizarSaldoProveedorAsync (:473)          ← ÚLTIMO lock for update, SIN CAMBIOS
   6. INSERT movimiento `compra` (:480)                            ← SIN CAMBIOS
  COMMIT (:484)

── ANULAR COMPRA ─────────────────────────────────────────────────────────────────
  ServicioDeCompras.EjecutarAnulacionAsync (:521-649)
   1. UPDATE comprobantes_compra ... RETURNING id_punto_venta, id_proveedor, total,
                                               id_orden_compra          ← lock header (:530)
   1.5 auditoría (stage 14, :549-557) — intacta, NO se mueve (no lockea nada)
   1.6 si id_orden_compra IS NOT NULL ⇒ ProyectarEstadoAsync           ← posición 2
       (puede REGRESAR cerrada→recibida_parcial→enviada; nunca sale de `anulada`
        ni de un cierre manual)
   2. reversa de stock por movimiento original (:568-610)              ← SIN CAMBIOS
   4. gastosLigados (informativo, :613)                                ← SIN CAMBIOS
   5-7. ledger de proveedor (:621-643)                                 ← SIN CAMBIOS
  COMMIT (:645)

── ENVIAR OC ─────────────────────────────────────────────────────────────────────
  fuera: pre-lectura (404 / 409 orden_compra_ya_enviada / 400 orden_compra_sin_items)
  CreateExecutionStrategy ⇒ AsignadorDeNumeroComprobante.AsignarComprometidoAsync(
      db, idTenant, idPuntoVenta, "OC")     ← SU PROPIA transacción, comiteada ANTES
  EstrategiaSinReintento ⇒ BEGIN
   1. UPDATE ordenes_compra SET numero, fecha_envio, estado='enviada'
      WHERE id AND tenant AND estado='borrador' AND id_punto_venta = $pv RETURNING numero
      0 filas ⇒ reclasificar bajo lectura (409); el número queda quemado (residuo aceptado)
  COMMIT

── ANULAR OC ─────────────────────────────────────────────────────────────────────
  EstrategiaSinReintento ⇒ BEGIN
   1. SELECT estado::text FROM ordenes_compra ... FOR UPDATE      ← PRIMER y único lock
      estado ∉ ('borrador','enviada') ⇒ 409
   2. derivación (recibida > 0 en cualquier artículo) ⇒ 409 orden_compra_con_recepciones
   3. EXISTS (comprobante ligado en 'borrador')  ← SIN lock de fila (decisión 9) ⇒ 409
   4. UPDATE ... SET estado='anulada' WHERE ... AND estado IN ('borrador','enviada') RETURNING
  COMMIT

── CERRAR OC (manual) / BORRADOR (replace-set) ────────────────────────────────────
  cerrar : UPDATE ... SET estado='cerrada', fecha_cierre=$m, id_empleado_cierre=$actor
           WHERE ... AND estado IN ('enviada','recibida_parcial') RETURNING estado
  PUT    : BEGIN → SELECT 1 ... WHERE estado='borrador' FOR UPDATE (forma de
           BloquearBorradorAsync, :774-789) → RemoveRange/AddRange items → COMMIT
```

### Lock order — verified against the real call sites

| Path | Locks taken, in order | Verdict |
|---|---|---|
| `EjecutarConfirmarAsync` | `comprobantes_compra` (`:333`) → **`ordenes_compra` (new, 1.b)** → `lotes` (`:425`) → `stock`/`stock_lotes` (`:445-450`) → `proveedores` (`:473`) | Extends the pinned order at position 2. `proveedores` stays last |
| `EjecutarAnulacionAsync` | `comprobantes_compra` (`:530`) → **`ordenes_compra` (new, 1.6)** → `stock`/`stock_lotes` (`:580-599`) → `proveedores` (`:635`) | Same. The audit row (`:552`) locks nothing and does not move |
| `ActualizarBorradorAsync` (compra) | `comprobantes_compra` (`:220`) → **`ordenes_compra` FOR SHARE (new)** | Same prefix, `FOR SHARE` only |
| OC endpoints (`PUT`, `enviar`, `cerrar`, `anular`) | `ordenes_compra` only | Suffix/singleton ⇒ the order stays total |
| `InsertarGastoAsync` (untouched) | `turnos_caja` (`:142`) → `comprobantes_compra` (`:232`) → `proveedores` | Unchanged |

**Total order: `turnos_caja → comprobantes_compra → ordenes_compra → lotes → stock/stock_lotes →
proveedores → ledger INSERT`.** Operative form, unchanged from stage 15 plus one clause:
*`proveedores` is the last row lock any transaction takes for update, the ledger `INSERT` follows it
immediately, and `ordenes_compra` is locked immediately after the comprobante header — never after
`lotes`, never with a second table held.*

**Concurrency guarantees.** *Confirm × confirm of two receptions of one OC*: serialized on the OC
row; the loser's derivation runs in a **new** statement after the winner commits, so the second
projection sees the full book. *Anular OC × confirmar reception*: whoever takes the OC lock first
wins; the other re-reads under the lock and gets `409 orden_compra_anulada` (confirm loses) or `409`
on a non-annullable estado (the annulment loses). *Two `enviar` on different OCs of one PV*: they
contend only inside the assigner's own small transaction ⇒ two distinct numbers, no 409. *Two
`enviar` on the SAME OC*: both draw a number, one `UPDATE` wins, the loser gets `409` and burns its
number (tension T1).

**Failure semantics.** Any throw rolls the comprobante and the OC estado back together: "stock moved
but the OC still says `enviada`" and "the OC closed but the reception did not commit" are both
unrepresentable. The only value that survives a rollback is the OC number, by design.

## API Surface (ADR-8: uniform 404 cross-tenant)

| Route | Policy | Notes |
|---|---|---|
| `GET /api/ordenes-compra?idProveedor&idPuntoVenta&estado&desde&hasta&pagina&tamanio` | `OperacionDePos` (group) | Paginated, `fecha_emision DESC, id DESC` |
| `GET /api/ordenes-compra/{id:int}` | `OperacionDePos` (group) | Header + items + per-artículo cobertura + deviation + linked comprobante ids |
| `POST /api/ordenes-compra` | + `GestionDeCatalogo` | 201; `numero IS NULL`, `estado = 'borrador'` |
| `PUT /api/ordenes-compra/{id:int}` | + `GestionDeCatalogo` | Full replace-set, `borrador` only (409 otherwise) |
| `POST /api/ordenes-compra/{id:int}/enviar` | + `GestionDeCatalogo` | Number + `fecha_envio` together (CHECK 1) |
| `POST /api/ordenes-compra/{id:int}/cerrar` | + `GestionDeCatalogo` | Actor-stamped; never walked back |
| `POST /api/ordenes-compra/{id:int}/anular` | + `GestionDeCatalogo` | Book-governed (decision 9 of the proposal) |

`Politicas.cs` is **unchanged**. The stage-5 `SuperficieDeAutorizacionTests` allowlist gains the five
new non-GET routes.

## Backstop Map (`db-error-backstops`)

| Constraint | Client-reachable? | Backstop | Test |
|---|---|---|---|
| `ux_ordenes_compra_numero` | No (only the assigner writes it) | **Exact-name `23505` ABOVE `ClasificarUnicidad`** (`:180-182`) → `numero_de_orden_duplicado`, 409 — the `_numero` ordering trap, third occurrence | Raw out-of-band insert asserting `23505` **and** the translated code + two concurrent `enviar` on one PV ⇒ two distinct numbers, no 409 |
| `ux_items_orden_compra_orden` | No (`orden` server-assigned) | Exact-name `23505` → `orden_de_item_duplicado`, 409, mirroring `:144-146` | Raw insert; **race-test exemption documented**, same family and reason as the compra precedent |
| `ak_ordenes_compra_id_orden_compra_id_tenant` | No — structurally unviolable | **No mapping. Exemption documented** (`ak_gastos_*` precedent) | — |
| `ck_ordenes_compra_envio_completo` (CHECK 1) | No — server-derived | Exact-name `23514` → `orden_compra_envio_incompleto`, 409 | Raw insert per direction (numero without fecha_envio; `enviada` without numero) |
| `ck_ordenes_compra_cierre` (CHECK 2) | No — server-derived | Exact-name `23514` → `orden_compra_cierre_incoherente`, 409 | Raw insert per direction (fecha_cierre with estado ≠ cerrada; closer without fecha_cierre) |
| `ck_items_orden_compra_cantidad_positiva` (CHECK 3) | **Yes** | Service 400 first (`cantidad_pedida_invalida`) + exact-name `23514` | Service test + raw-insert SQLSTATE |
| `ck_items_orden_compra_costo_no_negativo` (CHECK 4) | **Yes** | Service 400 first (`costo_estimado_invalido`) + exact-name `23514` | Same |
| FK 2 `..._punto_venta`, FK 3 `..._proveedor` | **Yes** (request body) | `ResolverPuntoVentaAsync` / `ResolverProveedorAsync` 404 **before** any write (`ServicioDeCompras.cs:1073-1080`) + generic `23503` → `400 referencia_invalida` (`:224`) | One test per FK asserting the **translated** code |
| FK 8 `..._articulo` | **Yes** (item lines) | Same pre-check shape as the compras draft + generic mapping | Per direction |
| FK 9 `fk_comprobantes_compra_orden_compra` | **Yes** (`idOrdenCompra` on the compra draft) | `ExigirOrdenLigableAsync` (404/409/400 per decision 8) + generic mapping | **Race**: linking to an OC being annulled concurrently |
| FK 1 / FK 6 `..._tenant`, FK 4 / FK 5 `..._empleado*`, FK 7 (items→OC) | No — session/server-derived | Generic mapping. **Exemptions documented** (`fk_auditoria_actor` precedent) | One SQLSTATE-asserting test anyway |

## Web composition

- **`src/Ways.Web/src/paginas/OrdenesDeCompra.tsx`** (route `/ordenes-compra`, `RutaProtegida
  rolesPermitidos={[ROL.Vendedor, ROL.Supervisor, ROL.Admin]}` — the read gate): filters
  (proveedor, punto de venta, estado, desde/hasta) + the `HistoricoDeCajas.tsx` pager. Date limits
  built with `fechaIsoConOffset` (the browser's own offset, **never `Z`**).
- **`src/Ways.Web/src/paginas/OrdenDeCompra.tsx`** (`/ordenes-compra/nueva`, `/ordenes-compra/:id`):
  draft editor (line grid, the `CompraEditor.tsx` shape) + detail with the per-artículo cobertura
  table (`Pendiente`, `Desvío` rendered `—` when `null`, never `0` — the `formatearCantidadNullable`
  precedent, `Reposicion.tsx:15-20`) + the `enviar`/`cerrar`/`anular` actions + **"Registrar
  recepción"** → `navigate('/compras/nueva?idOrdenCompra=' + id)`.
  - `react-async-state` rule 8: `key={idOrden ?? 'nueva'}` on the subtree (the `CompraEditor.cs:1194`
    precedent). Rule 2: `generacionRef` on every fetch. Rule 3: the generation bumps **before** each
    write. Rule 6: the post-write refetch has its own `try/catch` (a 2xx `enviar` is never reported
    as a failure because the refetch failed). Rule 9: first-line re-entrancy guard + full-window
    disable on `enviar`/`cerrar`/`anular` (a double `enviar` burns two numbers).
- **`src/Ways.Web/src/paginas/CompraEditor.tsx`** — reads `idOrdenCompra` from `useSearchParams`,
  pre-fills `idProveedor`, `idPuntoVenta`, `idOrdenCompra` and one line per artículo with
  `Pendiente > 0`; the key becomes `key={idNumerico ?? 'nuevo-' + (idOrdenCompra ?? 's')}`.
- **`src/Ways.Web/src/paginas/Reposicion.tsx`** — a per-group *"Generar OC"* button rendered
  **only** when `grupo.idProveedor !== null` **and** `useAuth().usuario.rolId === ROL.Admin`
  (decision 16), posting `filas.filter(f => f.sugerido !== null)` mapped
  `{IdArticulo, Sugerido} → {IdArticulo, CantidadPedida}`, then navigating to the new draft. The
  `"Sin proveedor"` bucket keeps rendering, without the action.
- **`src/Ways.Web/src/paginas/Compras.tsx`** — the linked OC shown on a compra with a link to it.
- **`src/Ways.Web/src/api/ordenesDeCompra.ts`** — client + pure mappers; `tipos.ts` mirrors.
- `react-async-state` rule 10: any recovery path added on one screen is grepped for and replicated
  in its sibling in the same commit.
- **Pre-approved degradation**: the `Reposicion.tsx` action is an isolated component with its own
  props, so dropping it is a clean non-delivery (the API still serves it), never a retraction.

`web-descriptor-tests`: colocated tests for every new pure helper (the reposición→OC mapper, the
cobertura formatter, the filter builder) and for both screens' descriptors.

## File Changes

| File | Action | Description |
|---|---|---|
| `src/Ways.Domain/Compras/EstadoOrdenCompra.cs` | Create | 5 values in lifecycle order (= the native type's order) |
| `src/Ways.Domain/Compras/OrdenCompra.cs` · `ItemOrdenCompra.cs` | Create | `EntidadTenant` ⇒ `EntidadBase` (gate §B/§C) |
| `src/Ways.Domain/Compras/ProyectorDeEstadoDeOrden.cs` | Create | The pure five-arm rule (decision 4) |
| `src/Ways.Infrastructure/Persistencia/Migraciones/…_OrdenesDeCompraEtapa16.cs` | Create | **The only** migration — exactly gate §A-§D, RLS last |
| `…/Configuraciones/OrdenCompraConfiguration.cs` · `ItemOrdenCompraConfiguration.cs` | Create | Shaped on `ComprobanteCompraConfiguration.cs:17-136` / `ItemComprobanteCompraConfiguration.cs:16-143`; all support indexes declared by hand with doc-10 names |
| `…/Configuraciones/ComprobanteCompraConfiguration.cs` | Modify | `IdOrdenCompra` + FK 9 + `ix_comprobantes_compra_orden_compra` (named, never EF-autogenerated) |
| `src/Ways.Infrastructure/Persistencia/WaysDbContext.cs` | Modify | Two `DbSet`s |
| `…/WaysDbContextFactory.cs` · `DependencyInjection.cs` | Modify | `MapEnum<EstadoOrdenCompra>` in **both** builders, never also `HasPostgresEnum` |
| `src/Ways.Application/Abstracciones/IWaysDbContext.cs` | Modify | Two `DbSet`s |
| `src/Ways.Application/Compras/EscriturasDeOrdenDeCompra.cs` | Create | The four statements + the two entry points |
| `src/Ways.Application/Compras/ServicioDeOrdenesDeCompra.cs` | Create | Draft CRUD, `enviar`, `cerrar`, `anular`, list + detail read model |
| `src/Ways.Application/Compras/ContratosDeOrdenDeCompra.cs` | Create | Read/write DTOs |
| `src/Ways.Application/Compras/ServicioDeCompras.cs` | Modify | 2 widened `RETURNING`s (`:715-737`, `:751-772`), 2 guarded call sites (`:333`+, `:530`+), `ExigirOrdenLigableAsync` in both draft paths, `IdOrdenCompra` in `Proyectar` (`:1112`) |
| `src/Ways.Application/Ventas/AsignadorDeNumeroComprobante.cs` | **Unmodified** | Reused with `tipo_comprobante = 'OC'` |
| `src/Ways.Api/Endpoints/OrdenesDeCompraEndpoints.cs` | Create | 7 routes (API Surface table) |
| `src/Ways.Api/Endpoints/ComprasEndpoints.cs` | Modify | The draft request gains `idOrdenCompra`; no route, policy or response-shape change beyond that field |
| `src/Ways.Api/Seguridad/ManejadorDeErrores.cs` | Modify | 6 exact-name branches, the `_numero` one **above** `:180-182` |
| `src/Ways.Api/Seguridad/Politicas.cs` | **Unmodified** | Decision 16 |
| `src/Ways.Web/src/paginas/OrdenesDeCompra.tsx` · `OrdenDeCompra.tsx` (+ `.test.tsx`) | Create | List + detail/draft |
| `src/Ways.Web/src/api/ordenesDeCompra.ts` (+ `.test.ts`) | Create | Client + pure mappers |
| `src/Ways.Web/src/paginas/Reposicion.tsx` · `Compras.tsx` · `CompraEditor.tsx` · `App.tsx` · `tipos.ts` | Modify | The action, the link, the pre-load, two routes, the DTO mirrors |
| `docs/10-modelo-de-datos.md` | Modify | Both tables, `comprobantes_compra.id_orden_compra`, "Estado (Etapa 16)" — from **inside slice 1** |
| `openspec/specs/reposicion-de-stock/spec.md` | Modify | Only the *"no order-with-state entity exists"* clause; formula and scenarios byte-identical |

## What does NOT change

- **The stock/costo/lote/CC engine.** `ServicioDeCompras.cs:357-482` (steps 2, 2.b, 3, 4, 5, 6) is
  byte-identical. `movimientos_stock`, `stock`, `stock_lotes`, `lotes`, `articulos.costo_nominal`
  and the stage-15 ledger have exactly the writers they had. **An unlinked confirm emits ZERO extra
  statements** — asserted, not claimed.
- **The checkout.** No file under `src/Ways.Application/Ventas/` is in the diff;
  `AsignadorDeNumeroComprobante` is consumed, not edited.
- **The stage-15 ledger.** No new `tipo_movimiento_cc_proveedor`, no new movement: while it is an
  order there is no debt.
- **The reposición formula.** `sugerido = reposicion IS NULL ? null : max(0, reposicion − cantidad)`
  and every scenario of `reposicion-de-stock` survive verbatim; only the justification sentence
  (`spec.md:130-132`) changes. `GET /api/reportes/stock/reposicion` keeps its exact response shape.
- **`Politicas.cs`**, `numeraciones_comprobante`, `tipos_comprobante`, the `gastosLigados` inverted
  rule (`:613`), `numero_externo` uniqueness, `ExigirCompraLigableAsync`'s four rejections.
- **The reserved carryovers**: the `importe` CHECK micro-gate, the `articulos_empresas` replace-set
  gap, `ways_owner` superuser, `stage-13b`.

## Testing Strategy

| Layer | What | Approach |
|---|---|---|
| Domain unit (no DB) | `ProyectorDeEstadoDeOrden`: the full truth table (2 estados × cierreManual × completa × algoRecibido), `anulada` terminal from every input, manual close never moved, `completa` beating `algoRecibido` | xUnit pure, `PoliticaDeRoles` pattern. No fixture, no container |
| Integration — **the three binding gate tests** | (a) **zero-extra-statements**: a confirm with `id_orden_compra IS NULL` issues the exact command count of the pre-stage path; (b) **two concurrent `enviar`** on two OCs of one PV ⇒ two distinct numbers, no 409, and on the **same** OC ⇒ one 200 + one 409; (c) **ordering trap**: a raw duplicate insert on `ux_ordenes_compra_numero` returns `numero_de_orden_duplicado`, **not** `numero_duplicado` | Command counter + forced rendezvous + translated-code assertion |
| Integration — derivation fidelity (rule 11) | Fixtures whose quantities **discriminate**: two OC lines of one artículo (3 + 4 ⇒ 7 pedidas), a reception splitting it (2 then 5), an artículo received but never ordered, an over-delivery (8 against 7), a soft-deleted reception, a linked **borrador** reception, a reception of **another** OC of the same proveedor. Every `Recibida`/`Pendiente` asserted per artículo | Never a fresh 1-line/1-reception seed: with `pedida == recibida == total` any mutant sourcing the number from any in-scope value passes green |
| Integration — projection | `enviada → recibida_parcial → cerrada` inside the confirm's own transaction; annulling the only reception of an **automatically** closed OC ⇒ back to `enviada`; of a **manually** closed one ⇒ stays `cerrada`; annulling one of two receptions ⇒ `recibida_parcial` with `fecha_cierre` back to NULL; a fault injected after the projection leaves the OC untouched | Fault-point test per path; real Postgres |
| Integration — the two races | confirm × confirm of two receptions of one OC (both commit, no deadlock, estado consistent with the book); anular OC × confirmar reception in **both** orders (one 200 + one 409, never a reception on an `anulada` OC, never a deadlock) | Forced rendezvous, `ParametrosTests` precedent |
| Integration — the link | Linking to another proveedor's / another PV's / another tenant's OC refused before any write; linking to a `borrador` OC ⇒ 409; the link frozen once the compra is confirmed; unlink allowed while `borrador`; `CompraDetalle.IdOrdenCompra` round-trips | One test per direction, translated domain codes |
| Integration — RLS | `SELECT` with another tenant's GUC ⇒ **0 rows** on both new tables; `INSERT` with a foreign `id_tenant` ⇒ `42501` by SQLSTATE | `ways_app` (NOSUPERUSER NOBYPASSRLS), statement level (rule 5) |
| Integration — schema | Both CHECKs and both `23505` families by raw insert asserting SQLSTATE **and** translated code; `pg_indexes` shows **exactly 12** new indexes and no unnamed EF-generated FK support index; `has-pending-model-changes` clean | The stage-15 index-count discipline |
| Integration — read model | Sibling OC of the same tenant seeded on **every** listing/detail test with its own items (rule 12c); every projected money/date field asserted with per-row discriminating values (rule 12b); a raw `UPDATE` desyncing `estado` to a sentinel must surface the sentinel (rule 12a); pagination with `fecha_emision` tied on every row ⇒ page 2 repeats and skips nothing; each filter with asymmetric seeds; `costo_unitario_estimado IS NULL` ⇒ `Desvio` **null**, never `0` | Order asserted as a **sequence**, not a set |
| Integration — projection fidelity | For every fixture: the stored `estado` equals `ProyectorDeEstadoDeOrden.Proyectar(...)` recomputed from the **read model's own** cobertura numbers | The one assertion that keeps the raw-ADO derivation and the LINQ derivation from drifting |
| Integration — reloj + offset (rule 10) | Everything under `RelojFijo(2026-08-19T12:00:00Z)`; `fecha_envio`/`fecha_cierre` equal the fixed instant exactly. Plus one listing boundary test sending `desde`/`hasta` at the real client offset `-03:00` (never `Z`), asserting the rows **and** the displayed period | Midday UTC keeps "hoy" stable in UTC and in `-03:00`; only the offset test can see a raw-ADO UTC-normalization regression |
| Integration — authorization | Vendedor ⇒ 200 on both GETs, **403** on all five writes; Supervisor ⇒ same; Admin ⇒ 200; Root ⇒ 403; tenant B never sees tenant A's orders; the allowlist covers the five new non-GET routes | One test per role per route |
| Web (vitest) | Pure mappers (reposición→OC, cobertura formatter, filter builder); the `"Sin proveedor"` bucket offers no action; a Supervisor session renders no action; a `sugerido === null` row is excluded from the pre-load; a double click on `enviar` issues exactly one POST; a stale response is discarded (**stale promise resolved inside `act`**, rule 7); pager disabled at the edges | `web-descriptor-tests` + `react-async-state` |
| Exempt | Visual styling beyond testids — exemption registered, inherited from stages 12-15 | — |

## Mutation targets

`mutation-proof-tests`: name the clause, apply the mutation, watch the named test fail, revert,
record the evidence (applied → failing test → reverted → green) in the PR body. **34 targets,
colocated with the slice that introduces the clause.**

| # | Slice | Clause | Mutation | Test that MUST fail |
|---|---|---|---|---|
| 1 | 1 | `HabilitarRlsDeTenant("ordenes_compra")` | delete the line | cross-tenant row count on `ways_app` + `42501` |
| 2 | 1 | `HabilitarRlsDeTenant("items_orden_compra")` | delete the line | same, child table |
| 3 | 1 | `ck_ordenes_compra_envio_completo` | delete it | raw-insert `23514`, both directions |
| 4 | 1 | `ck_ordenes_compra_cierre` | delete it | raw-insert `23514`, both directions |
| 5 | 1 | `ck_items_orden_compra_cantidad_positiva` / `..._costo_no_negativo` | delete either | its raw-insert `23514` |
| 6 | 1 | `HasFilter("numero IS NOT NULL")` on `ux_ordenes_compra_numero` | delete the filter | two drafts (numero NULL) in one PV ⇒ spurious `23505` |
| 7 | 1 | The exact-name `ux_ordenes_compra_numero` branch **above** `ClasificarUnicidad` | move it below `:180-182` | translated code is `numero_duplicado` instead of `numero_de_orden_duplicado` |
| 8 | 1 | `MapEnum<EstadoOrdenCompra>` in `WaysDbContextFactory` (and in `DependencyInjection`) | delete either | that builder's path fails / `has-pending-model-changes` dirty |
| 9 | 1 | Explicit `ix_comprobantes_compra_orden_compra` name | drop the `HasDatabaseName` | `pg_indexes` count/name audit (an EF `IX_…` appears) |
| 10 | 2 | `WHERE estado = 'borrador'` in the OC draft lock | delete it | `PUT` on an `enviada` OC ⇒ expected 409 |
| 11 | 2 | `AND id_punto_venta = $pv` in the `enviar` `UPDATE` | delete it | concurrent-`PUT`-moves-the-PV test: the number lands in the wrong series |
| 12 | 2 | `AsignadorDeNumeroComprobante.AsignarComprometidoAsync` | replace with `MAX(numero) + 1` | two concurrent `enviar` on one PV ⇒ same number / `23505` |
| 13 | 2 | The assigner call **outside** the `enviar` transaction | move it inside | nested-transaction failure / the burnt-number semantics test |
| 14 | 2 | Server-assigned `orden` 1..N in the replace-set | take `orden` from the request | `ux_items_orden_compra_orden` ⇒ `orden_de_item_duplicado` |
| 15 | 2 | `RemoveRange(itemsExistentes)` in the replace-set | delete it | a `PUT` that drops a line: per-line count + identity assertion |
| 16 | 2 | `ParametrosDeComando.Agregar` on `fecha_envio` | hand-built parameter without `ToUniversalTime()` | the `-03:00` offset test (a `Z` fixture cannot see it) |
| 17 | 2 | `orden_compra_sin_items` guard in `enviar` | delete it | an empty OC projects straight to `cerrada` |
| 18 | 3 | `SELECT … FOR UPDATE` (statement 1 of the projection) | delete the lock, keep derive + update | confirm × confirm rendezvous ⇒ stale estado |
| 19 | 3 | The derivation as a **separate** statement | fold it into one `UPDATE … FROM (SELECT …)` | same rendezvous (EvalPlanQual stale snapshot) |
| 20 | 3 | `id_orden_compra` added to `ConfirmarHeaderAsync`'s `RETURNING` | read it from `preLectura` (`:276`) instead | confirm under a concurrent `PUT` that relinks the draft |
| 21 | 3 | The OC lock at position 2 | move it after the `proveedores` lock (`:473`) | confirm × confirm rendezvous ⇒ deadlock/timeout |
| 22 | 3 | `c.estado = 'confirmada'` in the derivation | widen to any estado | a linked **borrador** reception moves the OC |
| 23 | 3 | `c.deleted_at IS NULL` / `ic.deleted_at IS NULL` in the derivation | delete either | the soft-deleted-reception fixture |
| 24 | 3 | `GROUP BY id_articulo` on the **ordered** side | match line-to-line | the duplicate-OC-lines fixture (3 + 4 ⇒ 7) |
| 25 | 3 | `algoRecibido` sourced from the **reception** side | source it from the ordered side's coalesced sum | the pure-substitution fixture (OC stays `enviada`) |
| 26 | 3 | The `id_empleado_cierre IS NOT NULL` short-circuit | delete it | annulling a reception of a **manually** closed OC reopens it |
| 27 | 3 | The `estado = 'anulada'` terminal short-circuit | delete it | the projection resurrects an annulled OC |
| 28 | 3 | `fecha_cierre = NULL` on the regression branch | keep the old value | `ck_ordenes_compra_cierre` ⇒ `23514` |
| 29 | 3 | `if (encabezado.IdOrdenCompra is { } idOc)` | call the projection unconditionally | the zero-extra-statements command count |
| 30 | 3 | `id_proveedor` / `id_punto_venta` equality in `ExigirOrdenLigableAsync` | drop either conjunct | the cross-proveedor / cross-PV link test (400) |
| 31 | 4 | `WHERE estado IN ('enviada','recibida_parcial')` in `cerrar` | widen it | closing a `borrador`/`anulada` OC succeeds |
| 32 | 4 | `id_empleado_cierre = $actor` on manual close | write NULL | the "a manually closed OC is not reopened" test |
| 33 | 4 | The derived-received-zero guard **and** the linked-`borrador` `EXISTS` guard in `anular` | delete either | `409 orden_compra_con_recepciones`, one test per guard; and adding `FOR SHARE` to the `EXISTS` read ⇒ the anular × confirmar rendezvous deadlocks |
| 34 | 4-6 | `.RequireAuthorization(Politicas.GestionDeCatalogo)` per write route; `ThenByDescending(o => o.Id)`; each `if (filtro is { } x)`; the `Desvio` null branch; the `grupo.idProveedor !== null` and `rolId === ROL.Admin` branches; the `sugerido !== null` filter | delete one at a time | its own named test (403 matrix; tied-`fecha` pagination; that filter's asymmetric-seed test; "no comparable, never 0"; the two descriptor tests; the pre-load exclusion test) |
| — | — | **Non-regression**: the existing `ComprasConfirmarTests`/`ComprasAnularTests` suites | — | verify criterion: green and **unedited** |

## Slicing (6 PRs, stacked-to-main — the proposal's plan, ratified with one re-scoping)

| # | Branch | Content | ~Lines | Test plan |
|---|---|---|---|---|
| 1 | `feat/stage16-slice1-schema` | Migration (type, 2 tables, 9 FKs, 4 CHECKs, 12 indexes, the ALTER, RLS last) + entities + `ProyectorDeEstadoDeOrden` + EF configs + `MapEnum` in both builders + **the 6 `ManejadorDeErrores` branches** (decision 10) + doc 10 | ~500 | RLS/`42501`; both CHECKs and both `23505` families by raw insert with translated codes; the `_numero` ordering trap; `pg_indexes` = 12; Domain truth table |
| 2 | `feat/stage16-slice2-borrador-y-envio` | `ServicioDeOrdenesDeCompra` draft CRUD (replace-set under `FOR UPDATE`) + `POST/PUT/GET {id}` + `enviar` with the `'OC'` numbering and the PV-pinned `UPDATE` | ~410 | Two concurrent `enviar` (same PV, and same OC); `borrador`-only mutation; the `-03:00` offset test; empty-OC refusal |
| 3 | `feat/stage16-slice3-ligadura-y-proyeccion` | `idOrdenCompra` on the compra draft + `ExigirOrdenLigableAsync` + `EscriturasDeOrdenDeCompra` + the two widened `RETURNING`s + the two guarded call sites + the pinned lock order | ~470 | Zero-extra-statements; derivation fidelity; regression of estado; confirm × confirm and anular × confirmar races; fault points |
| 4 | `feat/stage16-slice4-cierre-y-anulacion` | `POST /cerrar` (actor-stamped) + `POST /anular` (book-governed, both guards) + the `anulada` refusal inside confirm + the 409 matrix + the authorization matrix | ~320 | 409 per direction; manual-close non-reopening; the no-lock `EXISTS` proof; role matrix |
| 5 | `feat/stage16-slice5-lectura` | Paginated list + detail read model (per-artículo cobertura, received-not-ordered, price deviation with honest nulls) | ~360 | Rule 12(a)(b)(c); tied-`fecha` pagination; filters with asymmetric seeds; projection fidelity |
| 6 | `feat/stage16-slice6-web` | `OrdenesDeCompra.tsx` + `OrdenDeCompra.tsx` + client + routes + the `Reposicion.tsx` action + the `CompraEditor.tsx` pre-load + the `Compras.tsx` link | ~480 | Descriptor tests; stale inside `act`; single POST on double click; the two gating branches; the pre-load exclusion |

Total ≈ **2 540**. Merge order `1 → 2 → 3 → 4 → 5 → 6`. Slice 1 blocks everything (it owns the only
migration); 3 depends on 2 for the entity surface; 4 depends on 3 for the projection; 5 depends on 3
for the derivation; 6 depends on 5.

**Decision needed before apply: No** · **Chained PRs recommended: Yes** · **400-line budget risk:
High** (`delivery_strategy: auto-chain`, `chain_strategy: stacked-to-main`, one `judgment-day` round
per slice). A **7-8 PR outturn is the expected case**, not the exception.

**Pre-approved degradation**, in priority order:

1. **If slice 1 overflows** — split at the table/link boundary: `1a` (type + both tables + entities +
   configs + RLS/CHECK tests) and `1b` (the `comprobantes_compra` ALTER + the six backstops + doc
   10). The split keeps **one** migration, which is the invariant that must not be degraded.
2. **If slice 3 overflows** — split at the write-path boundary: `3a` (the link + `FOR SHARE` guard +
   the projection class + the confirm call + the confirm × confirm race) and `3b` (the anulación
   call, the estado regression and the anular × confirmar race).
3. **If slice 5 overflows** — split at the read boundary: `5a` (paginated list) and `5b` (detail +
   cobertura + deviation).
4. **If slice 6 overflows** — ship the list, the detail and the draft, and drop the `Reposicion.tsx`
   action (the API still serves it). A documented reduction, never silent.
5. **Never degraded**: the projection's lock-then-re-read-then-update discipline, the
   zero-extra-statements proof for unlinked confirms, the `_numero` ordering-trap assertion, and the
   manual-close short-circuit. An engine regression, a stale-book projection or a silently reopened
   manual close is worse than no OC at all — those are split, never trimmed.

## Binding verify criteria

1. Exactly **one** migration, `OrdenesDeCompraEtapa16`, with the DDL of gate §A-§D and nothing else;
   **12 new indexes** and no unnamed EF-generated FK support index;
   `dotnet ef migrations has-pending-model-changes` clean. Any extra DDL reopens the gate.
2. **Zero data statements** and **no `ALTER TYPE … ADD VALUE`** anywhere in the stage.
3. A confirm with `id_orden_compra IS NULL` emits the pre-stage command count exactly; the existing
   confirm/anular suites are green **and do not appear edited** in the diff.
4. `Politicas.cs` unchanged; no file under `src/Ways.Application/Ventas/` or
   `src/Ways.Application/Stock/` in the diff; `AsignadorDeNumeroComprobante.cs` unchanged.
5. The `reposicion-de-stock` formula and every one of its scenarios byte-identical; only the
   *"no order-with-state entity exists"* clause changed.
6. Mutation evidence recorded in the PR body for **every** row of the table above belonging to that
   slice.
7. Domain / Application / Integration / vitest suites green; colocated tests for every new pure web
   helper (`web-descriptor-tests`).

## Threat Matrix

N/A — this stage touches no routing, shell command, subprocess, VCS/PR automation, executable-file
classification or process integration. Its real risk surfaces (tenant isolation, authorization, lock
order, stale-snapshot projection, derivation fidelity) are covered by the mutation-target table,
which **is** binding.

## Open Questions / tensions with the proposal

- [ ] **T1 — the `enviar` concurrency criterion is ambiguous.** *"Two concurrent `enviar` produce
      two distinct numbers with no 409"* holds for two **different** OCs of one punto de venta. Two
      concurrent `enviar` of the **same** OC must produce one 200 and one 409, and the loser burns a
      number. `sdd-spec` runs in parallel and will likely transcribe the proposal's sentence
      verbatim — **reconcile in `sdd-tasks`**; this design tests both shapes.
- [ ] **T2 — is a `cerrada` OC linkable?** The proposal never says. This design allows linking to
      `enviada`/`recibida_parcial`/**`cerrada`** (an over-delivery after an automatic close is
      informational, decision 2) and refuses `borrador` (`orden_compra_no_enviada`) and `anulada`.
      If the spec pins "only `enviada`/`recibida_parcial`", one `when` arm changes.
- [ ] **T3 — the `FOR SHARE` guard is a real lock only on the `PUT` path.** `CrearBorradorAsync`
      (`ServicioDeCompras.cs:148-189`) has no transaction, so there the statement is a coherence
      validation, not a TOCTOU guard. The binding guard is the confirm-time `FOR UPDATE`
      (decision 8). Wrapping `CrearBorradorAsync` in a transaction would fix it and is deliberately
      **not** smuggled into this stage.
- [ ] **T4 — a citation correction.** The proposal cites `ExigirCompraLigableAsync` at
      `ServicioDeGastos.cs:187-197`; the method is at `:213-267` and its `FOR SHARE` statement at
      `:232-234`. The pattern is exactly as described — only the line numbers are off.
- [ ] **T5 — the reposición screen and the OC write gate disagree.** `Reposicion.tsx` is
      Supervisor + Admin (`LecturaDeReportes`); `POST /api/ordenes-compra` is Admin-only
      (`GestionDeCatalogo`, decision 7 of the proposal). Without the `rolId === ROL.Admin` gate this
      design adds, a Supervisor clicks *"generar OC"* into a 403. The alternative — loosening the OC
      write gate — is refused by proposal decision 7.
- [ ] **T6 — an OC with zero items.** The proposal does not forbid sending one; the derivation's
      `NOT EXISTS` would then be vacuously true and the first projection would read it **`cerrada`**.
      This design refuses at `enviar` (`orden_compra_sin_items`, 400).
- [ ] **T7 — `CompraDetalle` must carry `IdOrdenCompra`.** The proposal says `ComprasEndpoints`
      changes with "no response shape changes"; a request-only field cannot satisfy
      `dto-contract-honesty` rule 2 (the round-trip assertion). The response gains exactly one
      nullable field, and nothing else.
- [ ] **T8 — the detail cannot show `recibida` per line.** Grouping is by artículo on both sides, so
      an OC with two lines of one artículo has no honest per-line split. The detail ships the ordered
      lines **plus** a per-artículo `Cobertura` list (decision 13). A spec that assumes per-line
      quantities conflicts with proposal decision 2.
- [ ] **T9 — what counts as "something was received".** This design sources `algoRecibido` from the
      **reception** side, so a delivery consisting only of substitutions still moves the OC to
      `recibida_parcial`. Sourcing it from the ordered side would leave that OC reading `enviada`
      with a confirmed reception attached.
- [ ] **T10 — the anulación's linked-draft guard must not lock.** Adding `FOR SHARE` there closes a
      lock cycle against the confirm path. The proposal states the guard but not its lock posture;
      this design pins "plain snapshot read" as load-bearing (decision 9, mutation target 33).
- [ ] **Deferred, unchanged**: stock en tránsito in the reposición formula, a blocking price
      control, a `recepciones_orden_compra` bridge, printing/emailing the OC, and auditing OC
      transitions in `auditoria` — all refused in writing by the proposal with their reopen
      conditions.
