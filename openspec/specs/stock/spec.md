# Stock Specification

## Purpose

Defines `stock` (per-articulo, per-punto-de-venta balance, doc 10 §6) as a
cache of `movimientos_stock`, the signed movement ledger, the sale-time
decrement, the anulación reversal, and the admin-only manual `ajuste` path.
`movimientos_stock` now carries an optional `id_lote` dimension (Etapa 12);
`stock` itself is unmodified in shape — the lot balance lives in a parallel
`stock_lotes` cache owned by the `lotes-y-vencimientos` capability.

## Requirements

### Requirement: Stock Schema At Rest

`stock` MUST be operativa-scoped (`id_tenant` + `id_punto_venta`, doc 09)
with `PRIMARY KEY (id_articulo, id_punto_venta)` and `cantidad` as a cache
maintained in the same transaction as the originating `movimientos_stock`
insert. `movimientos_stock` rows are immutable — no update endpoint MUST
ever exist for a movement row. `movimientos_stock` carries a nullable
`id_comprobante_compra` (composite FK to `comprobantes_compra`,
`ON DELETE RESTRICT`), populated only for `motivo = compra` rows and for
the `motivo = anulacion` rows that reverse them; every other motivo leaves
it NULL. `movimientos_stock` additionally carries a nullable `id_lote`
(`integer NULL`), with a composite FK `fk_movimientos_stock_lote` on
`(id_lote, id_articulo, id_tenant)` against `lotes`'s alternate key,
enforcing at the database level that a movement's lot belongs to the
movement's articulo. `id_lote` MUST be populated for movements of
lot-effective articulos and MUST stay NULL for movements of non-lot
articulos and for the `id_lote IS NULL` half of a `reclasificacion` pair —
"`id_lote` NOT NULL when the articulo is lot-effective" is a cross-table
conditional, not a database CHECK, and is asserted by a dedicated
integration test.
(Previously: silent on `id_comprobante_compra`/`id_lote` — the columns did
not exist until stage 8/stage 12 respectively.)

#### Scenario: First sale of an articulo at a punto de venta creates the stock row
- GIVEN no `stock` row exists for `(articulo 10, punto_venta 1)`
- WHEN a sale decrements 2 units
- THEN a `stock` row is created with `cantidad = -2`

#### Scenario: Movement rows cannot be edited
- GIVEN an existing `movimientos_stock` row
- WHEN any client attempts to call a movement-edit endpoint
- THEN no such endpoint exists (404)

#### Scenario: A compra movement carries its comprobante link
- GIVEN a confirmed comprobante compra of id 12
- WHEN its entry `movimientos_stock` rows are inspected
- THEN each carries `id_comprobante_compra = 12`

#### Scenario: A lot-effective articulo's movement always carries a lot
- GIVEN articulo 40 is lot-effective at its punto de venta's empresa
- WHEN any `movimientos_stock` row for articulo 40 is inspected (venta,
  compra, transferencia, ajuste, inventario, decomiso)
- THEN `id_lote` is never NULL, except for the `id_lote IS NULL` half of a
  `reclasificacion` pair

#### Scenario: A non-lot articulo's movement never carries a lot
- GIVEN articulo 41 has `controla_lote = false`
- WHEN any `movimientos_stock` row for articulo 41 is inspected
- THEN `id_lote` is always NULL

#### Scenario: A movement referencing a foreign articulo's lot is unrepresentable
- GIVEN lot 7 belongs to articulo 40
- WHEN a raw write attempts a `movimientos_stock` row with `id_articulo = 41,
  id_lote = 7`
- THEN Postgres rejects it via `fk_movimientos_stock_lote`

### Requirement: Sale Decrement Inside The Checkout Transaction

For every item with `id_articulo NOT NULL`, checkout MUST serialize concurrent decrements of the same (id_articulo, id_punto_venta) pair — implemented via the atomic `INSERT ... ON CONFLICT DO UPDATE ... RETURNING` upsert whose own row lock provides the serialization (design decision 1, approved at the gate, superseding this spec's original advisory-lock wording; reconciled at verify).cantidad` in the same transaction. Availability is NOT checked —
negative stock is allowed (legacy parity).

#### Scenario: A sale below zero stock succeeds
- GIVEN `stock.cantidad = 2` for an articulo at a punto de venta
- WHEN a sale of 5 units of that articulo is checked out
- THEN the sale succeeds, `stock.cantidad = -3`, and a `movimientos_stock`
  row of `-5` with `motivo = venta` is inserted

#### Scenario: Concurrent sales of the same articulo do not corrupt the cache
- GIVEN two concurrent checkouts each selling 3 units of the same articulo at
  the same punto de venta, starting from `stock.cantidad = 10`
- WHEN both transactions commit
- THEN `stock.cantidad = 4` and both `movimientos_stock` rows exist — the
  advisory lock serializes the two updates

### Requirement: Manual Ajuste Path Is Admin-Only

A manual stock `ajuste` MUST be gated by `Politicas.GestionDeCatalogo`
(Admin only, not Vendedor), MUST require a non-empty `observaciones`/reason,
and MUST insert a `movimientos_stock` row with `motivo = ajuste` that updates
the `stock` cache in the same transaction. For a lot-effective articulo, the
ajuste MUST require `idLote` and MUST update `stock_lotes.cantidad` for that
lot in the same transaction alongside `stock.cantidad`.
(Previously: silent on the lot dimension — ajuste was aggregate-only until
stage 12.)

#### Scenario: Admin loads initial stock via ajuste
- GIVEN no `stock` row exists for an articulo at a punto de venta
- WHEN an admin submits an ajuste of `+100` with a reason
- THEN `stock.cantidad = 100` and a `movimientos_stock` row of `+100` with
  `motivo = ajuste` is inserted

#### Scenario: Vendedor is blocked from ajuste
- GIVEN a user with role Vendedor
- WHEN they call the ajuste endpoint
- THEN the request is rejected with an authorization error

#### Scenario: Ajuste without a reason is rejected
- GIVEN an ajuste request with an empty `observaciones`
- WHEN it is validated
- THEN it is rejected before reaching the database

#### Scenario: Ajuste of a lot-effective articulo requires idLote and updates both caches
- GIVEN articulo 40 is lot-effective and `stock_lotes.cantidad = 10` for
  lot 7, `stock.cantidad = 40` for the pair
- WHEN an admin submits an ajuste of `+5` with `idLote = 7` and a reason
- THEN `stock_lotes.cantidad = 15` for lot 7, `stock.cantidad = 45`, and a
  single `movimientos_stock` row of `+5` with `motivo = ajuste, id_lote = 7`
  is inserted

#### Scenario: Ajuste of a lot-effective articulo without idLote is rejected
- GIVEN articulo 40 is lot-effective
- WHEN an ajuste request for articulo 40 omits `idLote`
- THEN it is rejected before reaching the database

### Requirement: Anulación Inverse Movement

Anulación MUST insert, for every original item with `id_articulo NOT NULL`, a
`movimientos_stock` row with `motivo = anulacion` and `cantidad` equal to the
negation of the original sale movement, updating the `stock` cache in the
same transaction as the comprobante's `estado` change.

#### Scenario: Anulación restores the cache without touching the original movement
- GIVEN a sale movement of `-5` left `stock.cantidad = -3`
- WHEN the comprobante is anulado
- THEN a new `+5` movement with `motivo = anulacion` is inserted,
  `stock.cantidad = 2`, and the original `-5` row is unchanged

### Requirement: Cantidad Is Always The Sum Of Its Movimientos

At any point in time, `stock.cantidad` for a given `(id_articulo,
id_punto_venta)` MUST equal `SUM(movimientos_stock.cantidad)` for that same
pair, across all eight `motivo` values (`venta`, `anulacion`, `ajuste`,
`compra`, `transferencia`, `inventario`, `decomiso`, `reclasificacion`). For
a transferencia, each of the two mirrored rows counts only toward the
`(id_articulo, id_punto_venta)` pair it names — the invariant is asserted
independently per punto de venta, never as a combined total. A
`reclasificacion` pair, by construction, sums to zero across its two rows
and therefore cannot perturb the aggregate.
(Previously: restated over six motivos, silent on `decomiso` and
`reclasificacion`.)

#### Scenario: Consistency holds after a mix of venta, ajuste and anulación
- GIVEN a sequence of movements `venta -5`, `ajuste +100`, `venta -2`,
  `anulacion +5` for the same articulo and punto de venta
- WHEN `stock.cantidad` is compared against the sum of those movements
- THEN both equal `98`

#### Scenario: Consistency holds after a sequence including compra, transferencia and inventario
- GIVEN a sequence of `venta -5`, `ajuste +100`, `compra +30`,
  `transferencia -10` (origen), `transferencia +10` (destino at another
  punto de venta), `inventario -2`, `anulacion +5` for the same articulo
- WHEN each punto de venta's `stock.cantidad` is compared against the sum
  of its own movements
- THEN both equal their respective sums

#### Scenario: Consistency holds across a sequence including decomiso and a reclasificación pair
- GIVEN a sequence of `compra +100`, `reclasificacion -100` (`id_lote NULL`),
  `reclasificacion +100` (`id_lote = <sin-identificar>`),
  `venta -5` (lot-bearing), `decomiso -3` (lot-bearing), `anulacion +5`
  (lot-bearing) for the same articulo and punto de venta
- WHEN `stock.cantidad` is compared against the sum of all eight rows
- THEN both equal `97`, and the two `reclasificacion` rows contribute net
  zero to that sum

### Requirement: Lock Order Extends To The Lot Dimension, Identical At All Three Write Sites

Every transaction that touches stock MUST build one total ascending order
over the keys it will lock, in the exact form
`ORDER BY id_articulo, id_punto_venta, id_lote NULLS FIRST`, where the
aggregate `stock` row is the element with `id_lote = NULL` — for each
`(articulo, punto de venta)` in ascending order, the aggregate `stock`
upsert happens first, then its `stock_lotes` rows upsert in ascending
`id_lote`. This rule MUST be implemented identically and independently at
all three write sites (`ServicioDeVentas`, `ServicioDeCompras`,
`ServicioDeStock`), each with its own concurrency test — the duplication is
not refactored away.

#### Scenario: A checkout locks stock before stock_lotes for the same pair
- GIVEN a sale of a lot-effective articulo at a punto de venta
- WHEN the checkout transaction writes stock
- THEN the `stock` row upserts before any `stock_lotes` row for that same
  `(articulo, punto de venta)`

#### Scenario: A concurrent checkout and reverse transfer of the same articulo and lots do not deadlock
- GIVEN a checkout selling from lot 7 of articulo 40 at punto de venta 1, and
  a concurrent transferencia moving the same lot 7 of articulo 40 from punto
  de venta 1 to punto de venta 2, submitted at the same time
- WHEN both transactions run concurrently
- THEN both complete (one may retry under contention, per the project's
  retry strategy) with no deadlock, because both build the same ascending
  `(id_articulo, id_punto_venta, id_lote NULLS FIRST)` order over their keys

#### Scenario: A multi-lot conteo locks lots in ascending id_lote order
- GIVEN a conteo touching lots 3 and 9 of the same articulo and punto de
  venta in one request
- WHEN the transaction acquires its locks
- THEN lot 3's row locks before lot 9's
  *(Amended at slice-11 judgment-day, judge A MINOR-2: the original scenario
  said "ajuste", but `SolicitudDeAjusteDeStock` carries a single `IdLote` —
  one lot per request, per design's write-site-3 shape. The only stock-write
  request carrying a lot LIST is `SolicitudDeConteo` (slice 12), which is
  where the multi-lot ascending lock order actually lives.)*

### Requirement: Stock Read Access Under OperacionDePos

Stock read endpoints MUST be reachable under `Politicas.OperacionDePos`
(Vendedor + Admin); the manual ajuste write path stays on
`Politicas.GestionDeCatalogo`.

#### Scenario: Vendedor reads current stock for a cart lookup
- GIVEN a user with role Vendedor
- WHEN they query stock for an articulo at their tenant's punto de venta
- THEN the request succeeds

### Requirement: Writing Reorder Parameters Creates The Stock Row Without A Movement

Writing `minimo`/`reposicion` via `PUT /api/stock/minimos` MUST create the
`stock` row with `cantidad = 0` when none exists for the `(id_articulo,
id_punto_venta)` pair, and MUST NOT insert any `movimientos_stock` row — a
reorder parameter is not a ledger fact. This holds under `Cantidad Is
Always The Sum Of Its Movimientos`: a row created at `cantidad = 0` with
zero movements satisfies the invariant trivially (`0 = SUM(∅) = 0`).

#### Scenario: A minimo write for an articulo with no stock row creates it at zero with no movement
- GIVEN no `stock` row exists for `(articulo 20, punto de venta 1)`
- WHEN `PUT /api/stock/minimos` sets `minimo = 10` for that pair
- THEN a `stock` row is created with `cantidad = 0, minimo = 10` and zero
  `movimientos_stock` rows are inserted

#### Scenario: A minimo write for an articulo with an existing stock row touches no movement and no cantidad
- GIVEN `stock.cantidad = 45` for `(articulo 21, punto de venta 1)`
- WHEN `PUT /api/stock/minimos` sets `minimo = 10, reposicion = 60` for
  that pair
- THEN `stock.cantidad` stays `45` and zero `movimientos_stock` rows are
  inserted
