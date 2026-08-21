# Delta for Stock

## MODIFIED Requirements

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
integration test. `movimientos_stock` additionally carries a nullable
`id_remito` (composite FK `fk_movimientos_stock_remito` to `remitos`,
`ON DELETE RESTRICT`), populated only for `motivo = remito` rows and for
the `motivo = anulacion` rows that reverse them; every other motivo leaves
it NULL — the same shape as `id_comprobante_compra`, because a `remito`
movement with no document reference would be the only unattributable row
in an append-only ledger built for reconstruction.
(Previously: silent on `id_comprobante_compra`/`id_lote`/`id_remito` — the
columns did not exist until stage 8/stage 12/stage 17 respectively.)

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
  compra, transferencia, ajuste, inventario, decomiso, remito)
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

#### Scenario: A remito movement carries its remito link
- GIVEN an emitido remito of id 9
- WHEN its entry `movimientos_stock` rows are inspected
- THEN each carries `id_remito = 9`

#### Scenario: An anulación reversing a remito movement carries the same remito link
- GIVEN a remito of id 9 is anulado after being emitido
- WHEN its inverse `movimientos_stock` rows are inspected
- THEN each carries `id_remito = 9` and `motivo = anulacion`

### Requirement: Lock Order Extends To The Lot Dimension, Identical At All Four Write Sites

Every transaction that touches stock MUST build one total ascending order
over the keys it will lock, in the exact form
`ORDER BY id_articulo, id_punto_venta, id_lote NULLS FIRST`, where the
aggregate `stock` row is the element with `id_lote = NULL` — for each
`(articulo, punto de venta)` in ascending order, the aggregate `stock`
upsert happens first, then its `stock_lotes` rows upsert in ascending
`id_lote`. This rule MUST be implemented identically and independently at
all four write sites (`ServicioDeVentas`, `ServicioDeCompras`,
`ServicioDeStock`, `ServicioDeRemitos`), each with its own concurrency
test — the duplication is not refactored away.
(Previously: named three write sites; `ServicioDeRemitos` is added as the
fourth by stage 17, the first stage to extend this guarantee since it was
written.)

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

#### Scenario: A concurrent remito and checkout of the same articulo and lot do not deadlock
- GIVEN a remito emitting from lot 7 of articulo 40 at punto de venta 1, and
  a concurrent checkout selling the same lot 7 of articulo 40, submitted at
  the same time
- WHEN both transactions run concurrently
- THEN both complete (one may retry under contention) with no deadlock,
  because `ServicioDeRemitos` builds the identical ascending
  `(id_articulo, id_punto_venta, id_lote NULLS FIRST)` order, implemented
  independently

### Requirement: Cantidad Is Always The Sum Of Its Movimientos

At any point in time, `stock.cantidad` for a given `(id_articulo,
id_punto_venta)` MUST equal `SUM(movimientos_stock.cantidad)` for that same
pair, across all nine `motivo` values (`venta`, `anulacion`, `ajuste`,
`compra`, `transferencia`, `inventario`, `decomiso`, `reclasificacion`,
`remito`). For a transferencia, each of the two mirrored rows counts only
toward the `(id_articulo, id_punto_venta)` pair it names — the invariant is
asserted independently per punto de venta, never as a combined total. A
`reclasificacion` pair, by construction, sums to zero across its two rows
and therefore cannot perturb the aggregate.
(Previously: restated over eight motivos, silent on `remito` — the ninth
value, introduced by stage 17.)

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

#### Scenario: Consistency holds after a sequence including remito and its anulación
- GIVEN a sequence of `compra +50`, `remito -8` (with `id_remito` set),
  `anulacion +8` (reversing that remito, same `id_remito`) for the same
  articulo and punto de venta
- WHEN `stock.cantidad` is compared against the sum of those movements
- THEN both equal `50`
