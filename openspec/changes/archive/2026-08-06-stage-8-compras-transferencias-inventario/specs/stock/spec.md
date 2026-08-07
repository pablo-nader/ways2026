# Delta for Stock

## Purpose Update (informational — apply manually at archive)

The Purpose sentence "Stage 5 implements `motivo` values `venta`,
`anulacion`, `ajuste` only — `compra`, `transferencia`, `inventario` are
reserved enum values with no write path yet." MUST be removed: all six
`motivo_stock` values now have a write path, provided by the
`comprobantes-compra`, `transferencias-de-stock` and `conteo-de-inventario`
capabilities.

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
it NULL.
(Previously: silent on `id_comprobante_compra` — the column did not exist
until this stage.)

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

### Requirement: Cantidad Is Always The Sum Of Its Movimientos

At any point in time, `stock.cantidad` for a given `(id_articulo,
id_punto_venta)` MUST equal `SUM(movimientos_stock.cantidad)` for that same
pair, across all six `motivo` values (`venta`, `anulacion`, `ajuste`,
`compra`, `transferencia`, `inventario`). For a transferencia, each of the
two mirrored rows counts only toward the `(id_articulo, id_punto_venta)`
pair it names — the invariant is asserted independently per punto de venta,
never as a combined total.
(Previously: scoped to venta/ajuste/anulacion only, silent on the three
motivos this stage adds.)

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
