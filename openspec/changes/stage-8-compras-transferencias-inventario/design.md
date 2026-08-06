# Design: Stage 8 — Comprobantes de compra, transferencias e inventario

## Technical Approach

Stage 5 opened the stock ledger, stage 6 pinned the serialization point of the drawer, stage 7
pinned the serialization point of the balance. Stage 8 adds the fourth: **the document header row
is the serialization point of every state transition of a compra**, taken with the same discipline
— transition first with an estado-guarded `UPDATE … RETURNING`, derive the read set under that
lock, commit once.

The centerpiece is that **no new concurrency primitive is invented**. `ServicioDeStock.AjustarAsync`
(`:32-70`, `:97-119`) already owns the whole shape: pre-checks outside, `EstrategiaSinReintento`,
one explicit transaction, raw ledger `INSERT`, then the atomic
`INSERT … ON CONFLICT DO UPDATE … RETURNING` upsert whose own row lock provides the serialization.
Every stage-8 stock writer is that shape, N times. The `RETURNING` of that upsert turns out to be
the answer to three separate questions at once (the anulación refusal, the transfer sufficiency
check, and the conteo's current balance) — see decision 5.

The second idea is **containment**. `ServicioDeVentas.EmitirAsync`/`AnularAsync` and the arqueo
derivation are the project's most-guarded code and are not edited at all: stage 8 *adds* writers
next to them. `ServicioDeStock` gains two methods; nothing existing changes shape.

The third is that **totals are derived server-side, always**. No endpoint in this stage accepts a
delta, a total, or even a `cantidad` when bultos are involved (decision 3) — the pure
`CalculadorDeCompra` owns every arithmetic, mirroring `CalculadorDeArqueo`/`ReliquidadorDeConsumos`.

Everything else is stage-5/6/7 posture reused verbatim: decide-then-commit,
`EstrategiaSinReintento` for every manual op, atomic `UPDATE … RETURNING` as the only
state-transition authority, ascending-`id_articulo` lock discipline, `db-error-backstops` per
constraint, RLS + manual tenant filter, `ManejadorDeErrores` mapping, pure Domain first.

## Architecture Decisions

| # | Decision | Alternatives considered | Rationale |
|---|---|---|---|
| 1 | **The confirm's first statement is the estado-guarded transition**: `UPDATE comprobantes_compra SET estado='confirmada', fecha_recepcion=$now WHERE id=$1 AND id_tenant=$2 AND estado='borrador' RETURNING …`; the items are read **after** it, inside the same transaction | (a) Read items → validate → write stock → set estado last; (b) `SELECT … FOR UPDATE` on the header, then a plain `UPDATE` | This is stage-6's close-first lesson and stage-5's `MarcarAnuladoAsync` transplanted verbatim. Two concurrent confirms of the same borrador serialize on the header row lock; the loser re-evaluates the `WHERE` against the **committed** state, matches 0 rows and gets `409 compra_no_es_borrador` — never a double stock entry, never a 500. It also freezes the item read set against a concurrent borrador edit, which takes the *same* header lock (decision 2). (a) makes "stock entered, estado still borrador" reachable under a crash between statements; (b) is the same guarantee in two statements instead of one |
| 2 | **Borrador editing is a full replace-set (`PUT /api/compras/{id}`) under `SELECT … FOR UPDATE … WHERE estado='borrador'`, with a physical `DELETE` + `INSERT` of the item rows** | (a) Incremental item CRUD (`POST/PATCH/DELETE /items/{id}`); (b) replace-set with no header lock — the shape `ServicioDeArticulos` uses for `articulos_empresas` | (b) is the project's known open wound (carried since stage 4): two concurrent replace-sets both read a stale current set and the union survives. It is **not** repeated here — the header row lock makes "last committer wins" a real guarantee instead of a race, and the `estado='borrador'` predicate in the same statement makes editing a confirmada structurally impossible rather than checked. (a) needs three endpoints, an `orden` renumbering story and the same estado guard on each — more surface for the same result. Item ids churn on every save, which is free: nothing references an item row (movements reference the **header**) |
| 3 | **No endpoint accepts `cantidad`, a line total or a header total.** The request carries `unidades`, `bultos`, `unidadesPorBulto`, `costoUnitario`, `descuento`, `idAlicuotaIva`; the pure `CalculadorDeCompra` derives `cantidad = unidades + bultos × unidadesPorBulto` (doc-01:206), the line net and every header total | Accept `cantidad` directly and treat bultos as UI sugar | Proposal decision 9 says "no endpoint accepts a delta or a stock quantity to store". A client-supplied `cantidad` alongside bultos is exactly that: two sources of truth for the number of units that will enter the ledger. Deriving it makes the D3/D7 bug class unrepresentable at the boundary rather than validated after the fact, and puts the whole arithmetic in one DB-free class the tests can hammer |
| 4 | **`costo_nominal` is written as the IVA-INCLUDED unit cost net of discount, rounded to 2 decimals `AwayFromZero`**, and only for items with `actualiza_costo = true` **AND** `costo_unitario > 0` | (a) Write `costo_unitario` verbatim; (b) write the net-of-IVA cost always | `SugeridorDePrecio` multiplies `costo_nominal` by a margin to produce a **sale price**, and this project's sale prices are IVA-included (TX is not fiscal). On a `C-FA` the supplier's unit cost is net of IVA, so (a) would under-price the entire catalog by the IVA factor on every Factura A — silently. Two narrowings must also be explicit: `costo_unitario` is `numeric(14,4)` while `costo_nominal` is `numeric(14,2)` (`ArticuloConfiguration.cs:80-82`), so the rounding happens in C# with the project's `AwayFromZero` convention, never as an implicit half-even cast in Postgres. The `costo_unitario > 0` guard kills the bonificación foot-gun: a free-goods line must not zero the article's cost |
| 5 | **The upsert's `RETURNING` is the sufficiency check.** For the anulación reversal, the transfer origin and the conteo, the balance is read from the write itself, under the row lock the write already took; a negative result throws and rolls the whole transaction back | (a) `SELECT` the balance, validate, then write; (b) `SELECT … FOR UPDATE` on the stock rows first | (a) is a TOCTOU by construction — a sale can commit between the read and the write, and the refusal would be decided on a stale number. (b) buys the same guarantee the upsert's own row lock already gives, at the cost of a second statement per row and a second lock-order surface. Decide-then-commit is preserved: nothing the transaction wrote is visible to anyone until COMMIT, so throwing after the write is exactly as safe as refusing before it. The conteo uses the same primitive as a **no-op upsert** (`SET cantidad = stock.cantidad`) to create-if-missing and lock in one statement, then derives the delta |
| 6 | **The negative-stock refusal blocks anulación; linked gastos do NOT** | Refuse anulación when any `gasto` links to the compra (the brief's conservative candidate) | *Verified in code*: `GastosEndpoints.cs` exposes only `POST` and `GET` — there is **no** gasto anulación, deletion or reversal path anywhere in stages 1–7. Refusing would trap the operator permanently, and proposal decision 10 explicitly conditions a refusal on the remedy being available to the same role. So the honest rule is the inverse: annulling is allowed, the linked gastos stay linked (the link is history, not a claim of debt), the response reports how many payments the operator has left dangling, and the derived saldo keeps counting them as money paid — which they are. What *is* refused is the mirror case: a **new** gasto cannot link to a compra that is not `confirmada` (decision 7) |
| 7 | **The gasto write path takes `SELECT … FOR SHARE` on the compra header when `idComprobanteCompra` is present**, after its existing turno lock; `id_proveedor` is derived from the compra when absent and rejected when it disagrees | Validate the compra with an unlocked read | Without the lock this is the exact TOCTOU stage 7 paid a judgment-day round to close: the gasto validates `estado='confirmada'`, the anulación commits, and the gasto commits a payment against a cancelled invoice. The anulación holds the header's exclusive lock from its first statement, so the gasto either blocks and then sees `anulada` (`409 compra_anulada`) or wins and is simply visible to the anulación — both states representable, neither corrupt. Lock order stays total: `turnos_caja → clientes → comprobantes_compra → stock (asc id_articulo) → articulos (asc id_articulo)`; every stage-8 writer takes a **suffix** of it |
| 8 | **Applying `precio_sugerido` is a separate endpoint that loops `ServicioDePrecios.AbrirNuevoPrecioAsync` (`:80-82`) once per articulo, each in its own transaction, reporting per-line results** | One transaction wrapping all N applications | `AbrirNuevoPrecioAsync` owns a `pg_advisory_xact_lock` per `(idArticulo, idListaPrecio)` and its own close-and-open logic. Wrapping N calls would hold N advisory locks for the duration and turn one rejected line (a `confirmarReemplazo` conflict) into a total failure of an action that is **explicitly optional** (proposal decision 3). Partial success reported per line is the honest contract; it is not a ledger, and price history is preserved by the existing service either way |
| 9 | **A transferencia rejects a repeated articulo in one request (`400 articulo_repetido`), then applies every `(id_articulo, id_punto_venta)` pair — both sides of every line — in one ascending sort of that key** | (a) Order origin rows then destination rows; (b) allow repeats and net the deltas per key before writing | (a) deadlocks against a simultaneous reverse transfer (B→A) and against a checkout at either PV: the lock order must be over the **key**, never over the role a row plays. Sorting all 2N keys gives one total order that A→B, B→A and every concurrent sale agree on. (b) would need in-memory netting on the cache side while the ledger stays one row per line — more code and an ambiguous per-line refusal, for a UI that has no reason to emit two lines of the same articulo in one move |
| 10 | **The conteo derives its delta at WRITE time under the row lock, never from a balance read when the operator counted** — distinct endpoint (`POST /api/stock/conteos`), distinct `motivo = inventario`, same `ServicioDeStock` | (a) A `motivo` parameter on `/ajustes`; (b) a sibling service; (c) the client sends the delta | (c) is forbidden by proposal decision 9. (a) collapses "I am correcting an error" and "I recounted the shelf" into one row that no report can ever separate again — the traceability that justified shipping inventario at all. (b) would duplicate `InsertarMovimientoStockAsync`/`UpsertStockAsync`, which already live in `ServicioDeStock` and only need `motivo`/`idComprobanteCompra`/`idPuntoVentaDestino` parameters. The honest residual is stated in the spec: a sale committing between the physical count and the submit is absorbed into the delta (the conteo asserts "the shelf holds N units **now**"), which is inherent to counting and is why `observaciones` is required |
| 11 | **The proveedor saldo is a derived read in a dedicated `ServicioDeSaldoDeProveedor`, and the compras list gets its payment status from ONE grouped query, never one per row** | Extend `ServicioDeProveedores`; compute payment status per row | `ServicioDeProveedores` is a plain ABM that knows nothing about compras or gastos; making it depend on two operativa aggregates to serve a read is the wrong dependency direction. The N+1 is the real risk: `SELECT id_comprobante_compra, SUM(importe) FROM gastos WHERE id_comprobante_compra = ANY($ids) GROUP BY 1` keeps a page of compras at a constant 2 queries, guarded by the existing `DbCommand` interceptor budget test |
| 12 | **Only three compra tipos are seeded: `C-FA`, `C-FB`, `C-FC`** | Also seed `C-NCA/B/C` for supplier credit notes | Notas de crédito de proveedor are explicitly out of scope (anulación is the only reversal). Seeding a catalog row with no write path would recreate, in the very stage that pays it off, the exact debt this stage exists to close — three more dead enum-like values. The prefix is binding, not cosmetic: `ux_tipos_comprobante_codigo` is UNIQUE on `codigo` **alone** (`TipoComprobanteConfiguration.cs:54-57`) and `ResolverTipoComprobanteAsync` resolves by codigo alone (`ServicioDeVentas.cs:697-702`), so an unprefixed `FA` could be returned to the sale path |

## Compra Arithmetic (binding — one calculator)

`CalculadorDeCompra` is pure, DB-free, and is the only place these formulas exist. Per line:

```
cantidad  = round(unidades + (bultos ?? 0) × (unidadesPorBulto ?? 0), 3, AwayFromZero)   ← > 0
bruto     = round(cantidad × costoUnitario, 2, AwayFromZero)
total(i)  = bruto − descuento                                    ← descuento es un IMPORTE de línea
                                                                   (misma semántica que items_comprobante_venta)
descuento > bruto  ⇒ 400 descuento_de_item_invalido
```

Header, branching **only** on `tipos_comprobante.discrimina_iva` of the compra's tipo:

```
subtotal        = Σ bruto(i)
descuento_total = Σ descuento(i)

discrimina_iva = true  (C-FA)   costoUnitario es NETO de IVA
    iva_total = Σ round(total(i) × porcentajeIva(i) / 100, 2, AwayFromZero)
    total     = subtotal − descuento_total + iva_total
    costoEfectivo(i) = round(total(i) × (1 + porcentajeIva(i)/100) / cantidad(i), 2, AwayFromZero)

discrimina_iva = false (C-FB / C-FC)   costoUnitario ya incluye IVA
    iva_total = NULL                       ← misma postura que ComprobanteVenta.IvaTotal
    total     = subtotal − descuento_total
    costoEfectivo(i) = round(total(i) / cantidad(i), 2, AwayFromZero)
```

`costoEfectivo(i)` is what the confirm writes to `articulos.costo_nominal` (decision 4) and what is
fed to the **existing** `SugeridorDePrecio.Sugerir(costoEfectivo, null, null, margenGrupo,
margenProveedor)` — grupo-then-proveedor precedence unchanged, `null` when there is no margin.
`precio_sugerido` is recomputed and stored on every borrador save (so the operator sees it while
editing) and **frozen at confirm** as the audit of what was proposed at receiving time. It is never
applied by the confirm.

Two lines of the same articulo in one compra are **allowed** (two costs on one invoice is real):
both write their own ledger row, and the `costo_nominal` write is deduplicated in memory with the
**highest `orden` winning**, so exactly one `UPDATE` per articulo is issued.

## Table Shapes (DB CHANGE GATE)

### A — `comprobantes_compra` (new `[operativa]` table, doc-10:370-384)

Entity: `ComprobanteCompra : EntidadTenant` — same base as `ComprobanteVenta`, so
`created_at`/`updated_at`/`deleted_at` exist by convention. `updated_at` is genuinely meaningful
here (a borrador is the one mutable document in the system); `deleted_at` is **never written** and
no delete endpoint exists — the row transitions state, exactly like `comprobantes_venta`.

| Column | Type | Null | Notes |
|---|---|---|---|
| `id_comprobante_compra` | `integer` identity | NO | `pk_comprobantes_compra`; alternate key `ak_comprobantes_compra_id_comprobante_compra_id_tenant` (required by three composite FKs) |
| `id_tenant` | `integer` | NO | operativa scoping per doc 09 |
| `id_proveedor` | `integer` | NO | composite FK `(id_proveedor, id_tenant)` → `proveedores`, RESTRICT |
| `id_tipo_comprobante` | `integer` | NO | **simple** FK (global catalog, ADR-11); `clase = compra` enforced in the service, not by schema |
| `numero_externo` | `citext` | **YES** | the supplier's number; NULL allowed while `borrador` (doc-10:374-375, decision 4 of the proposal) |
| `fecha_comprobante` | `date` | **YES** | the invoice date; NULL allowed while `borrador` |
| `fecha_recepcion` | `timestamptz` | **YES** | written by the confirm from `IRelojDelSistema.Ahora` — never client input |
| `id_punto_venta` | `integer` | NO | composite FK, RESTRICT — the local the goods enter (doc-10:378) |
| `id_empleado` | `integer` | NO | **simple** FK to `usuarios` (the documented deviation, doc-10:466-470) |
| `subtotal`, `descuento_total`, `total` | `numeric(14,2)` | NO | derived, never client input |
| `iva_total` | `numeric(14,2)` | YES | NULL when the tipo does not discriminate IVA |
| `observaciones` | `text` | YES | |
| `estado` | `estado_compra` | NO | default set in Domain (`Borrador`), not as a DB default — same as `ComprobanteVenta.Estado` |

| Constraint / index | Shape | Justification |
|---|---|---|
| `ux_comprobantes_compra_numero_externo` | **partial UNIQUE** `(id_tenant, id_proveedor, id_tipo_comprobante, numero_externo) WHERE estado <> 'anulada' AND numero_externo IS NOT NULL` | doc-10:384 plus the two gate adjustments: `id_tenant` because every operativa table is tenant-scoped and doc 10's DDL sketches omit it by convention; partial on `estado` so a mistyped invoice that was annulled can be re-entered. No `deleted_at` predicate — the row is never soft-deleted, same as `ux_comprobantes_venta_numero`. It fires on the **draft save** that sets `numero_externo`, which is the earliest honest moment |
| `ck_comprobantes_compra_confirmada_completa` | `estado = 'borrador' OR (numero_externo IS NOT NULL AND fecha_comprobante IS NOT NULL AND fecha_recepcion IS NOT NULL)` | Makes "confirmed without an invoice identity" unrepresentable. An `anulada` was `confirmada` first, so it satisfies it too. Backstop of the service rule, not its substitute |
| `ck_comprobantes_compra_totales_no_negativos` | `subtotal >= 0 AND descuento_total >= 0 AND total >= 0 AND (iva_total IS NULL OR iva_total >= 0)` | `>= 0`, not `> 0`: a fully-bonified remito totalling zero is real. Same family as `ck_comprobantes_venta_numero_positivo` — defence against an out-of-band write |
| indexes | `ix_…_tenant (id_tenant)`; `ix_…_proveedor (id_proveedor, id_tenant)`; `ix_…_punto_venta_fecha (id_punto_venta, id_tenant, fecha_recepcion)`; `ix_…_tipo_comprobante (id_tipo_comprobante)`; `ix_…_empleado (id_empleado)` | Each composite-FK support index leads with the FK's own columns (the "implicit PascalCase index" trap that `ComprobanteVentaConfiguration` documents); the proveedor and punto-venta ones double as the saldo and list accesses |

### B — `items_comprobante_compra` (new `[operativa]` table, doc-10:386-398)

`ItemComprobanteCompra : EntidadTenant`, child scope `id_tenant` only (no FK to `puntos_venta` — it
derives from the header), mirroring `ItemComprobanteVentaConfiguration`.

| Column | Type | Null | Notes |
|---|---|---|---|
| `id_item` | `integer` identity | NO | `pk_items_comprobante_compra` |
| `id_tenant` | `integer` | NO | |
| `id_comprobante_compra` | `integer` | NO | composite FK → the alternate key above, RESTRICT |
| `orden` | `integer` | NO | `ux_items_comprobante_compra_orden (id_comprobante_compra, orden)` UNIQUE, server-assigned |
| `id_articulo` | `integer` | **NO** | **Deliberately NOT NULL**, unlike `items_comprobante_venta` (free-concept lines, doc 10 §4): a compra line with no articulo can move no stock and update no cost — it would be a gasto, and gastos already exist |
| `descripcion` | `text` | NO | snapshot |
| `cantidad` | `numeric(12,3)` | NO | derived (decision 3); `ck_items_comprobante_compra_cantidad_positiva`: `cantidad > 0` |
| `bultos`, `unidades_por_bulto` | `numeric(10,2)` | YES | inputs kept for audit, doc-10:391 |
| `costo_unitario` | `numeric(14,4)` | NO | `ck_items_comprobante_compra_costo_no_negativo`: `>= 0` (bonificación lines are real; decision 4 stops them from zeroing `costo_nominal`) |
| `descuento` | `numeric(14,2)` DEFAULT 0 | NO | line-level **amount** |
| `id_alicuota_iva` | `integer` | NO | **simple** FK (global, ADR-11) |
| `porcentaje_iva` | `numeric(5,2)` | NO | snapshot; informational when the tipo does not discriminate |
| `total` | `numeric(14,2)` | NO | derived; `ck_items_comprobante_compra_importes_no_negativos`: `descuento >= 0 AND total >= 0` |
| `actualiza_costo` | `boolean` DEFAULT true | NO | doc-10:396 |
| `precio_sugerido` | `numeric(14,2)` | YES | suggestion only, never applied by the confirm |

Indexes: `ix_…_tenant`, `ix_…_comprobante (id_comprobante_compra, id_tenant)`,
`ix_…_articulo (id_articulo, id_tenant)`, `ix_…_alicuota_iva (id_alicuota_iva)`.

### C — New Postgres enum

`estado_compra` = `borrador | confirmada | anulada` (doc-10:382), Domain `EstadoCompra`. Registered
**only** through `npgsql.MapEnum<EstadoCompra>("estado_compra")` in **both**
`DependencyInjection.cs:99-105` **and** `WaysDbContextFactory.cs:39-45` — never also declared with
`HasPostgresEnum` in `OnModelCreating` (`WaysDbContext.cs:125-128` documents why: declaring on both
sides emits the type twice, with alphabetical values).

### D — The two deferred FK columns landing (proposal decision 8)

| Object | Shape | Justification |
|---|---|---|
| `movimientos_stock.id_comprobante_compra` | `integer NULL` + `fk_movimientos_stock_comprobante_compra (id_comprobante_compra, id_tenant)` RESTRICT + `ix_movimientos_stock_comprobante_compra` | doc-10:451/457-465 scheduled it for this stage; the deferral note in `MovimientoStock.cs:16-18` and `MovimientoStockConfiguration.cs:48-50` is removed in the same commit |
| `gastos.id_comprobante_compra` | `integer NULL` + `fk_gastos_comprobante_compra (id_comprobante_compra, id_tenant)` RESTRICT + `ix_gastos_comprobante_compra` | doc-10:416/426-434; the deferral note in `Gasto.cs:11-15` is removed |

Both additive and nullable — dropping them restores the pre-stage-8 shape bit-for-bit. **No backfill.**

### E — `tipos_comprobante` (global catalog, three rows)

| `codigo` | `nombre` | `clase` | `letra` | `signo` | `discrimina_iva` | `es_fiscal` | `afecta_stock` |
|---|---|---|---|---|---|---|---|
| `C-FA` | Factura A de compra | `compra` | `A` | `+1` | **true** | false | true |
| `C-FB` | Factura B de compra | `compra` | `B` | `+1` | false | false | true |
| `C-FC` | Factura C de compra | `compra` | `C` | `+1` | false | false | true |

`es_fiscal = false` because the flag means "does it report to AFIP/ARCA when FE exists?"
(doc-10:88) and we never *emit* a supplier's invoice. `signo = +1`: goods come in.

Shipped **twice**, exactly as `RC` was in stage 7: (1) appended to `TiposComprobanteBase`
(`InicializadorDeBaseDeDatos.cs:63-76`), whose tuple gains a `Clase` field because `:424` currently
hardcodes `ClaseComprobante.Venta`; and (2) an idempotent migration insert carrying **both** guards
from `CuentaCorrienteEtapa7.cs:60-66` — `WHERE EXISTS (SELECT 1 FROM tipos_comprobante)` (so a
genuinely empty database is left intact for the seeder, the stage-7 bug) **AND** a per-row
`NOT EXISTS (… WHERE codigo = v.codigo)`.

### F — RLS and migration

`migrationBuilder.HabilitarRlsDeTenant("comprobantes_compra")` and
`…("items_comprobante_compra")` — identical to every other operativa table (stage-6 precedent,
`TurnosCajaYGastosEtapa6.cs:424-428`). No change to any existing column, index, enum, CHECK, or to
`CalculadorDeArqueo`. Migration name: **`ComprasYTransferenciasEtapa8`**.

## Transactions (binding statement order)

```
── CONFIRMAR COMPRA ──────────────────────────────────────────────────────────────
  fuera: momento := reloj.Ahora ; idTenant ; idEmpleado
  EstrategiaSinReintento ⇒ BEGIN
   1. UPDATE comprobantes_compra SET estado='confirmada', fecha_recepcion=$momento,
             updated_at=$momento
        WHERE id_comprobante_compra=$id AND id_tenant=$t AND estado='borrador'
        RETURNING id_proveedor, id_punto_venta, id_tipo_comprobante, numero_externo,
                  fecha_comprobante                      ← lock del header, autoridad única
      0 filas ⇒ 404 si no es visible, si no 409 compra_no_es_borrador
   2. SELECT items ORDER BY id_articulo ASC              ← read set congelado bajo ese lock
      0 items ⇒ 400 compra_sin_items      (rollback del paso 1)
      numero_externo / fecha_comprobante NULL ⇒ 400 compra_incompleta_para_confirmar
   3. por cada item (asc id_articulo):
        INSERT movimientos_stock (motivo='compra', cantidad = +item.cantidad,
                                  id_comprobante_compra=$id, id_punto_venta=header.pv)
        UPSERT stock (+cantidad) RETURNING                ← statement de AjustarAsync, sin cambios
   4. por cada articulo con actualiza_costo AND costo_unitario > 0 (asc id_articulo,
      deduplicado con el mayor `orden` ganando):
        UPDATE articulos SET costo_nominal=$costoEfectivo, updated_at=$momento
         WHERE id_articulo=$a AND id_tenant=$t
  COMMIT                       (los precios de venta NO se tocan — decisión 3 del proposal)

── ANULAR COMPRA ─────────────────────────────────────────────────────────────────
  EstrategiaSinReintento ⇒ BEGIN
   1. UPDATE comprobantes_compra SET estado='anulada', updated_at=$momento
        WHERE id_comprobante_compra=$id AND id_tenant=$t AND estado='confirmada' RETURNING …
      0 filas ⇒ 404 / 409 compra_no_confirmada
   2. SELECT movimientos_stock WHERE id_comprobante_compra=$id AND motivo='compra'
        ORDER BY id_articulo ASC        ← el ledger original, NUNCA recalculado desde items
   3. por cada uno (asc id_articulo):
        INSERT movimientos_stock (motivo='anulacion', cantidad = −original.cantidad,
                                  id_comprobante_compra=$id)
        nueva := UPSERT stock (−cantidad) RETURNING
        nueva < 0 ⇒ throw 409 stock_insuficiente_para_anular (nombra articulo y faltante)
   4. SELECT count(*) FROM gastos WHERE id_comprobante_compra=$id   ← informativo, NO bloquea
  COMMIT                       (costo_nominal NO se revierte — decisión 10 del proposal)

── TRANSFERENCIA ─────────────────────────────────────────────────────────────────
  fuera: articulos y ambos PV resueltos (400/404) ; origen ≠ destino (400) ;
         cantidad > 0 con ≤3 decimales ; observaciones requeridas ;
         articulo repetido ⇒ 400 articulo_repetido
  EstrategiaSinReintento ⇒ BEGIN
   claves := [(articulo, origen, −q), (articulo, destino, +q)] de todas las líneas,
             ORDENADAS ASC por (id_articulo, id_punto_venta)      ← orden total, decisión 9
   por cada clave en ese orden:
        INSERT movimientos_stock (motivo='transferencia', cantidad=delta,
                                  id_punto_venta=clave.pv, id_punto_venta_destino=destino)
        nueva := UPSERT stock (delta) RETURNING
        delta < 0 AND nueva < 0 ⇒ throw 409 stock_insuficiente_para_transferencia
  COMMIT

── CONTEO DE INVENTARIO ──────────────────────────────────────────────────────────
  fuera: contada >= 0 con ≤3 decimales ; observaciones requeridas
  EstrategiaSinReintento ⇒ BEGIN
   1. actual := INSERT INTO stock (…, cantidad=0) ON CONFLICT DO UPDATE
                  SET cantidad = stock.cantidad RETURNING cantidad
                                          ← upsert no-op: crea si falta Y toma el row lock
   2. delta := contada − actual            ← derivado por el servidor, jamás input del cliente
      delta = 0 ⇒ COMMIT sin escribir nada (200 no-op; también evita
                  ck_movimientos_stock_cantidad_no_cero)
   3. INSERT movimientos_stock (motivo='inventario', cantidad=delta, observaciones)
   4. final := UPSERT stock (+delta) RETURNING ; final ≠ contada ⇒ throw
               (imposible bajo el lock del paso 1; defensa en profundidad)
  COMMIT

── GASTO LIGADO A UNA COMPRA (adición al camino existente) ───────────────────────
   0. ExigirTurnoAbiertoBajoLockAsync(turno)                  ← sin cambios, sigue primero
   0b. si idComprobanteCompra != null:
        SELECT estado, id_proveedor FROM comprobantes_compra
          WHERE id=$c AND id_tenant=$t FOR SHARE              ← cierra el TOCTOU (decisión 7)
        estado <> 'confirmada' ⇒ 409 compra_anulada / 409 compra_no_confirmada
        categoria <> 'proveedor' ⇒ 400 gasto_de_compra_debe_ser_de_proveedor
        id_proveedor ausente ⇒ se deriva ; distinto ⇒ 400 proveedor_no_coincide_con_la_compra
   1..n. INSERT gasto (sin otro cambio; el arqueo lo suma como un gasto más)
```

**Lock order (total, extended once): `turnos_caja → clientes → comprobantes_compra → stock
(asc id_articulo, luego asc id_punto_venta) → articulos (asc id_articulo)`.** Every stage-8 writer
takes a suffix of it, so it stays total and deadlock-free against the checkout (which orders its
stock upserts asc `id_articulo`, `ServicioDeVentas.cs:375-377`) and against the stage-6/7 paths.

**Failure semantics.** Any throw rolls back the estado transition, every movement and every cache
upsert together — "stock entered but still borrador", "cost updated but no stock", and "reversed
but still confirmada" are all unrepresentable. Nothing outside a transaction is consumed (there is
no numeración here — a compra has no correlativo propio, proposal decision 4), so a failed confirm
leaves **zero** trace, unlike a failed sale.

**Read budget**: confirm and anular each issue a constant number of statements per item (2 and 2);
the compras list is 2 queries per page regardless of page size (decision 11). Guarded by the
existing `DbCommand` interceptor test.

## API Surface (ADR-8: uniform 404 cross-tenant)

| Endpoint | Policy | Notes |
|---|---|---|
| `GET /api/compras?idProveedor=&estado=&desde=&hasta=` | `OperacionDePos` | List + payment status, 2 queries |
| `GET /api/compras/{id}` | `OperacionDePos` | Header + items + `precioSugerido` per item |
| `POST /api/compras` | **+ `GestionDeCatalogo`** | Creates a `borrador` |
| `PUT /api/compras/{id}` | **+ `GestionDeCatalogo`** | Header + **full item replace-set**, borrador only |
| `POST /api/compras/{id}/confirmar` | **+ `GestionDeCatalogo`** | The centerpiece transaction |
| `POST /api/compras/{id}/anular` | **+ `GestionDeCatalogo`** | Contramovimientos + the negative refusal |
| `POST /api/compras/{id}/precios` | **+ `GestionDeCatalogo`** | Applies suggestions through `AbrirNuevoPrecioAsync`, per-line results |
| `POST /api/stock/transferencias` | **+ `GestionDeCatalogo`** | Mounted in the existing `/api/stock` group |
| `POST /api/stock/conteos` | **+ `GestionDeCatalogo`** | Idem |
| `GET /api/proveedores/{id}/saldo` | `OperacionDePos` | **Mapped top-level, not inside the `/api/proveedores` group** — that group is `GestionDeCatalogo` (`ProveedoresEndpoints.cs:10-12`) and ASP.NET composes with AND, which would make the read Admin-only against proposal decision 11 |
| `POST /api/gastos` | `OperacionDePos` (unchanged) | Learns the optional `idComprobanteCompra` |

"+ `GestionDeCatalogo`" means stacked over the group's `OperacionDePos`, which is the *verified*
shape of the only existing manual stock writer (`StockEndpoints.cs:24-30`) and makes those routes
Admin-only (`Politicas.cs:15`, `:68-70`). **No new policy constant is introduced.** Every new
non-GET route is added to the stage-5 `SuperficieDeAutorizacionTests` allowlist.

## Backstop Map (db-error-backstops)

| Constraint | SQLSTATE → mapping | Test |
|---|---|---|
| `ux_comprobantes_compra_numero_externo` | 23505 → **409 `compra_duplicada`**. ⚠ **Ordering trap**: the name contains `_numero`, which `ClasificarUnicidad` maps to the generic `numero_duplicado` ("Ya existe un cliente con ese número", `ManejadorDeErrores.cs:344-347`). It MUST be resolved by **exact name in the top-level switch, before `ClasificarUnicidad`** — the same treatment `ux_comprobantes_venta_numero` needed (`:39-48`) | **Genuine race**: two concurrent saves of the same `(proveedor, tipo, numero_externo)` ⇒ exactly one winner, the loser gets 409 |
| `ux_items_comprobante_compra_orden` | 23505 → 409 `orden_de_item_duplicado`, exact-name branch | Raw-SQL 23505 only. `orden` is server-assigned inside the replace-set — **documented race-test exemption**, same posture as `ux_arqueos_turno_medio` (`:305-308`) |
| `ck_comprobantes_compra_confirmada_completa` | 23514 → 400 `compra_incompleta_para_confirmar` | Raw SQL + a service-path test proving the Domain rule fires first |
| `ck_comprobantes_compra_totales_no_negativos` | 23514 → 400 `totales_de_compra_invalidos` | Raw SQL (unreachable through `CalculadorDeCompra`) |
| `ck_items_comprobante_compra_cantidad_positiva` / `_costo_no_negativo` / `_importes_no_negativos` | 23514 → 400 `cantidad_de_item_invalida` / `costo_de_item_invalido` / `importes_de_item_invalidos` | Raw SQL each. New `ClasificarCheckDeCompras` behind a `ck_comprobantes_compra_` / `ck_items_comprobante_compra_` prefix guard, exact-name switch inside — the `ck_ofertas_` pattern (`:184-195`), **not** a `Contains` family |
| `fk_comprobantes_compra_*`, `fk_items_comprobante_compra_*`, `fk_movimientos_stock_comprobante_compra`, `fk_gastos_comprobante_compra` | 23503 → existing generic `fk_` branch → 400 `referencia_invalida` — **no code change** | Raw-SQL 23503 per FK |
| `ck_movimientos_stock_cantidad_no_cero` | Already mapped (`:511-514`) | Becomes newly *relevant*: the conteo's zero-delta no-op is what keeps it unreachable — asserted by a test that a zero-difference conteo writes **no** row and returns 200 |
| New Domain codes | `compra_no_es_borrador` (409), `compra_no_confirmada` (409), `compra_anulada` (409), `compra_sin_items` (400), `tipo_de_compra_invalido` (400), `stock_insuficiente_para_anular` (409), `stock_insuficiente_para_transferencia` (409), `punto_venta_destino_invalido` (400) — SUPERSEDED at apply: cross-tenant destino resolves via the ADR-8 uniform 404 (`ResolverPuntoVentaAsync`), see tasks 3.7, `articulo_repetido` (400), `contada_invalida` (400), `gasto_de_compra_debe_ser_de_proveedor` (400), `proveedor_no_coincide_con_la_compra` (400) | Unit + integration per code |

**Genuinely racy surfaces, honestly: five**, each with a forced-rendezvous test
(`ParametrosTests` precedent) — (1) double confirm of the same borrador; (2) confirm × borrador
edit; (3) transferencia × checkout on the same `(articulo, pv)`; (4) anulación de compra × venta
that drives the reversal negative; (5) gasto ligado × anulación de la misma compra. Everything else
is schema defence.

## Data Flow

```
  PUT /api/compras/{id} ──→ CalculadorDeCompra (PURO) ──→ items + totales + precio_sugerido
        │                          │                              ↑
        └── FOR UPDATE header      └──→ SugeridorDePrecio (PURO, existente)

  POST …/confirmar ──→ UPDATE estado (autoridad) ──┬──→ movimientos_stock (motivo=compra, N filas)
                                                    ├──→ stock (upsert × N, RETURNING)
                                                    └──→ articulos.costo_nominal (donde actualiza_costo)

  POST …/precios ──→ ServicioDePrecios.AbrirNuevoPrecioAsync (N transacciones propias) ──→ precios (historia)

  POST /api/stock/transferencias ──→ 2N movimientos + 2N upserts, una transacción, orden total
  POST /api/stock/conteos ────────→ upsert no-op (lock) → delta → 1 movimiento + 1 upsert

  compras confirmadas.total ──┐
                              ├──→ ServicioDeSaldoDeProveedor (derivado, 2 consultas, sin estado)
  gastos (categoria=proveedor)┘
```

## File Changes

| File | Action | Description |
|---|---|---|
| `src/Ways.Domain/Compras/ComprobanteCompra.cs`, `ItemComprobanteCompra.cs`, `EstadoCompra.cs` | Create | Entities + the new enum |
| `src/Ways.Domain/Compras/CalculadorDeCompra.cs` (+ input/result records) | Create | The pure arithmetic — the stage's test mass |
| `src/Ways.Domain/Stock/MotivoStock.cs`, `MovimientoStock.cs` | Modify | The "reserved, no writer" doc-comments removed; `IdComprobanteCompra` added |
| `src/Ways.Domain/Gastos/Gasto.cs` | Modify | `IdComprobanteCompra` added; deferral note removed |
| `src/Ways.Application/Compras/ServicioDeCompras.cs` | Create | Borrador CRUD, confirmar, anular, list, aplicar precios |
| `src/Ways.Application/Compras/ServicioDeSaldoDeProveedor.cs` | Create | The derived read (decision 11) |
| `src/Ways.Application/Stock/ServicioDeStock.cs` | Modify | `TransferirAsync` + `ContarAsync`; the two raw statements gain `motivo`/`idComprobanteCompra`/`idPuntoVentaDestino` parameters |
| `src/Ways.Application/Gastos/ServicioDeGastos.cs` + `Contratos.cs` | Modify | Optional `IdComprobanteCompra`, the `FOR SHARE` guard, proveedor derivation |
| `src/Ways.Infrastructure/…/Configuraciones/ComprobanteCompraConfiguration.cs`, `ItemComprobanteCompraConfiguration.cs` | Create | Table shapes A/B |
| `src/Ways.Infrastructure/…/Configuraciones/MovimientoStockConfiguration.cs`, `GastoConfiguration.cs` | Modify | The two FK columns + support indexes |
| `src/Ways.Infrastructure/…/Migraciones/*_ComprasYTransferenciasEtapa8.cs` | Create | Enum, two tables, two FK columns, partial unique, CHECKs, RLS, the guarded tipo seed |
| `src/Ways.Infrastructure/DependencyInjection.cs`, `Persistencia/WaysDbContextFactory.cs` | Modify | `MapEnum<EstadoCompra>` in **both** |
| `src/Ways.Infrastructure/Persistencia/InicializadorDeBaseDeDatos.cs` | Modify | `TiposComprobanteBase` gains `Clase`; three compra rows |
| `src/Ways.Api/Endpoints/ComprasEndpoints.cs` | Create | Seven routes + the top-level proveedor saldo route |
| `src/Ways.Api/Endpoints/StockEndpoints.cs`, `GastosEndpoints.cs` | Modify | Two new gated writes; the gasto DTO |
| `src/Ways.Api/Seguridad/ManejadorDeErrores.cs` | Modify | The ordering-trap branch + `ClasificarCheckDeCompras` |
| `src/Ways.Web/src/paginas/Compras.tsx`, `CompraEditor.tsx`, `Transferencias.tsx`, `ConteoDeInventario.tsx`, `src/api/compras.ts` | Create | Screens + pure mappers + descriptor tests |
| `docs/10-modelo-de-datos.md` | Modify | §1 compra tipos, §5/§6 status notes (etapa 8; both deferred FKs landed), stage table |

## Interfaces / Contracts

```csharp
// Ways.Domain.Compras — puro, sin DB (mismo listón que CalculadorDeArqueo).
public sealed record LineaDeCompra(
    int Orden, int IdArticulo, string Descripcion,
    decimal Unidades, decimal? Bultos, decimal? UnidadesPorBulto,
    decimal CostoUnitario, decimal Descuento,
    int IdAlicuotaIva, decimal PorcentajeIva, bool ActualizaCosto);

public sealed record ItemCalculado(
    int Orden, int IdArticulo, decimal Cantidad, decimal Total,
    decimal CostoEfectivo, decimal? PrecioSugerido);

public sealed record CompraCalculada(
    decimal Subtotal, decimal DescuentoTotal, decimal? IvaTotal, decimal Total,
    IReadOnlyList<ItemCalculado> Items);

public static class CalculadorDeCompra
{
    // discriminaIva viene del tipos_comprobante de la compra; margenes por articulo alimentan
    // al SugeridorDePrecio existente, que devuelve null cuando no hay margen.
    public static CompraCalculada Calcular(
        IReadOnlyList<LineaDeCompra> lineas, bool discriminaIva,
        IReadOnlyDictionary<int, (decimal? MargenGrupo, decimal? MargenProveedor)> margenes);
}

// Ningún request lleva cantidad, total ni delta (decisión 3 / proposal decisión 9).
public sealed record SolicitudDeCompra(
    int IdProveedor, int IdTipoComprobante, int IdPuntoVenta,
    string? NumeroExterno, DateOnly? FechaComprobante, string? Observaciones,
    IReadOnlyList<LineaDeCompraSolicitada> Items);

public sealed record SolicitudDeTransferencia(
    int IdPuntoVentaOrigen, int IdPuntoVentaDestino, string Observaciones,
    IReadOnlyList<LineaDeTransferencia> Lineas);           // LineaDeTransferencia(IdArticulo, Cantidad)

public sealed record SolicitudDeConteo(
    int IdPuntoVenta, int IdArticulo, decimal Contada, string Observaciones);  // Contada, NUNCA un delta
```

## Web Composition

`Compras.tsx` (list + filters + payment status + entry to the editor), `CompraEditor.tsx`
(`/compras/:id`, header form + item grid + a **non-authoritative** totals mirror in `compras.ts` —
the server's numbers always win, per `dto-contract-honesty` — plus the confirmar/anular actions and
the `precio_sugerido` panel), `Transferencias.tsx`, `ConteoDeInventario.tsx`, and a saldo panel
reachable from `Proveedores.tsx`.

`react-async-state` obligations that carry weight here: rule 8 `key={idCompra}` on the editor
subtree; rule 9 first-line re-entrancy guard **and** full-window disable on confirmar, anular,
transferir and contar — all four are irreversible and a double submit doubles the goods; rule 6 a
2xx confirm is never reported as failure (the post-write refetch has its own try/catch and its own
copy); rule 3 the compra generation is bumped **before** every write; rule 7 proveedores/tipos/
alícuotas failing to load produces a visible aviso **and** a genuinely disabled submit; **rule 10 —
sibling surfaces**: the `stock_insuficiente_*` recovery copy is grepped for and replicated across
the anulación and transferencia surfaces in the same commit, as is `compra_no_es_borrador`.
`web-descriptor-tests` coverage per surface. Role gating is cosmetic (`usuario?.rolId ===
ROL.Admin` hides the write actions); `GestionDeCatalogo` is the enforcement, and every stage-8 write
screen is Admin-only end to end — the stage-7 nav/policy mismatch does not recur.

## Testing Strategy

| Layer | What to Test | Approach |
|---|---|---|
| Unit (Domain) | `CalculadorDeCompra`: cantidad from unidades/bultos, both IVA regimes, `iva_total NULL` when not discriminating, `costoEfectivo` including IVA on `C-FA` and not on `C-FB`, the `numeric(14,4) → numeric(14,2)` narrowing at `AwayFromZero`, `descuento > bruto` rejected, zero-cost bonificación line not updating cost, two lines of the same articulo (highest `orden` wins the cost), empty line set. `SugeridorDePrecio` reuse asserted, not re-implemented | Pure, no DB — the bulk of the stage's test mass |
| Unit (Web) | `compras.ts` mappers + the non-authoritative totals mirror | Colocated `*.test.ts`, `web-descriptor-tests` |
| Component (Web) | Double-click on "Confirmar" issues exactly one POST; the `stock_insuficiente` recovery copy present on both sibling surfaces; empty compras list renders an empty state, never a re-query | RTL + `user-event`, `vi.mock('../api/compras')` |
| Integration (atomicity) | Failure injected at each confirm step ⇒ estado, stock, cache and `costo_nominal` all untouched; idem for anular and transferencia (a failure moves **neither** side) | Real Postgres |
| Integration (concurrency) | The five racy surfaces above, forced rendezvous | `ParametrosTests` precedent |
| Integration (invariant) | `stock.cantidad == SUM(movimientos_stock.cantidad)` **per `(articulo, punto_venta)`** over a mixed sequence of venta, ajuste, anulación, compra, transferencia and conteo — the existing stock invariant test extended, asserted per punto de venta | `Ways.IntegrationTests` |
| Integration (asymmetry) | A **sale** of the same articulo still goes negative while a transferencia and a compra-anulación are refused — proven in both directions in one test | Idem |
| Integration (no-op / no-delta) | A zero-difference conteo writes no ledger row and returns 200; no endpoint of this stage accepts a delta, a total or a `cantidad` (contract test over the DTOs) | Idem |
| Integration (arqueo untouched) | A proveedor gasto linked to a compra changes the turno's arqueo by exactly its importe, with **no new derivation term**; `CalculadorDeArqueo` asserted byte-unchanged | Idem |
| Integration (saldo) | `Σ compras confirmadas − Σ gastos`; borradores and anuladas excluded; nothing stored; the per-compra status uses **linked** gastos only (the declared approximation) | Idem |
| Integration (migration/seed) | Every venta code still resolves to its venta row after the compra seed, on a **fresh** database **and** on one migrated from stage 7; the migration insert is re-run safe | Idem |
| Integration (budget / auth) | Constant command count for a 2 / 20 / 100-item compra and for a 50-row compras list; `SuperficieDeAutorizacionTests` allowlist; Vendedor ⇒ 403 on every write, 2xx on the compras list and the proveedor saldo | `DbCommand` interceptor + the auth-surface test |

## Migration / Rollout

One additive migration, `ComprasYTransferenciasEtapa8`: the `estado_compra` enum, the two tables
with their FKs/indexes/CHECKs, the partial dedupe unique, the two deferred FK columns, RLS on both
new tables, and the doubly-guarded compra-tipo insert. **No backfill, no data rewrite.** The gate
summary must surface: (a) `id_articulo NOT NULL` on compra items, unlike venta items; (b) the
`ck_comprobantes_compra_confirmada_completa` estado-consistency CHECK, which doc 10 does not
specify; (c) `costo_nominal` receiving the **IVA-included** effective cost (decision 4); (d) linked
gastos **not** blocking anulación, with the code evidence that no gasto reversal exists
(decision 6); (e) `numero_externo`/`fecha_comprobante` nullable while borrador; (f) only three
compra tipos seeded (decision 12).

Rollback: drop the two tables (no stage 1–7 read path joins them), drop the two nullable FK columns
(existing rows carry NULL), drop the enum with its tables, and set the three compra tipos
`activo = false` rather than deleting them — the honest `Down` shipped for `RC` in stage 7.
Movements already written stay valid and stay counted: the ledger is append-only and every stage-8
write went through the same cache upsert, so **no repair is ever needed**.

## Open Questions

- [ ] **`fecha_comprobante` is `date`, not `timestamptz`** (doc-10:376). A supplier invoice has no
      meaningful time-of-day, but every other date in the schema is `timestamptz`; the DTO exposes
      `DateOnly` so no timezone conversion can corrupt it.
- [ ] **A borrador has no expiry and no cleanup.** Abandoned drafts accumulate silently and hold a
      `numero_externo` reservation against the partial unique. A list filter by `estado` is the only
      remedy shipped; a TTL is a later decision.
- [ ] **The conteo is per-articulo and therefore not a snapshot.** Counting 400 articulos is 400
      transactions; each is correct, the set is not atomic. That is the explicit scope of proposal
      decision 1, and it is the natural seam where a full-count workflow would attach.
- [ ] **`precio_sugerido` is frozen at confirm but the applied price is not linked back.** Applying
      a suggestion opens a `precios` row that carries no reference to the compra that motivated it;
      linking them would need a column on `precios`, which is a bigger schema decision than this
      stage should take.
- [ ] **Annulling a compra whose goods were partially sold is refused, not compensated**
      (`409 stock_insuficiente_para_anular`). The remedy — a conteo or an ajuste — is in the same
      role's hands, but the operator must take it explicitly.
- [ ] **The proveedor saldo counts unlinked proveedor gastos.** Declared as an approximation, not an
      invariant (proposal decision 6): an unlinked payment still reduces it, because it is still
      money paid to that supplier.
