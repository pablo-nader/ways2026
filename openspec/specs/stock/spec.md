# Stock Specification

## Purpose

Defines `stock` (per-articulo, per-punto-de-venta balance, doc 10 §6) as a
cache of `movimientos_stock`, the signed movement ledger, the sale-time
decrement, the anulación reversal, and the admin-only manual `ajuste` path.

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
the `stock` cache in the same transaction.

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

### Requirement: Stock Read Access Under OperacionDePos

Stock read endpoints MUST be reachable under `Politicas.OperacionDePos`
(Vendedor + Admin); the manual ajuste write path stays on
`Politicas.GestionDeCatalogo`.

#### Scenario: Vendedor reads current stock for a cart lookup
- GIVEN a user with role Vendedor
- WHEN they query stock for an articulo at their tenant's punto de venta
- THEN the request succeeds
