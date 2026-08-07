# Transferencias de Stock Specification

## Purpose

Defines the punto-de-venta-to-punto-de-venta stock transfer (doc 10 §6,
doc-10:472-474): one atomic transaction writing two mirrored
`movimientos_stock` rows, no schema beyond the columns stage 5 already
created (`motivo = transferencia`, `id_punto_venta_destino`), no in-transit
state, and the deliberate back-office tightening that refuses a transfer
that would leave the origin negative — the opposite of the checkout rule.

## Requirements

### Requirement: Transferencia Writes Two Mirrored Movements In One Transaction

A transferencia of `cantidad` units of an articulo from `origen` to
`destino` MUST, in a single transaction, insert an origin `movimientos_stock`
row (`id_punto_venta = origen`, `cantidad = -cantidad`,
`motivo = transferencia`, `id_punto_venta_destino = destino`) and a
destination row (`id_punto_venta = destino`, `cantidad = +cantidad`,
`motivo = transferencia`, `id_punto_venta_destino = destino`), and upsert
both puntos de venta's `stock` cache in the same transaction. A multi-item
transfer of N items MUST write exactly `2N` movement rows atomically. No
in-transit state and no separate transfer document MUST exist — the
transfer completes or it does not happen at all.

#### Scenario: A single-item transfer moves both caches atomically
- GIVEN `stock.cantidad = 20` at origen and `stock.cantidad = 5` at destino
  for the same articulo
- WHEN a transferencia of 8 units is submitted
- THEN origen's cache becomes `12`, destino's becomes `13`, and exactly 2
  `movimientos_stock` rows exist, both `motivo = transferencia`

#### Scenario: A multi-item transfer writes 2N rows atomically
- GIVEN a transferencia request with 3 items
- WHEN it is submitted
- THEN exactly 6 `movimientos_stock` rows are inserted in the same
  transaction, or none if it fails

#### Scenario: A failure moves neither side
- GIVEN a transferencia that fails while upserting the destination cache
- WHEN the transaction aborts
- THEN neither the origen nor the destino `movimientos_stock` row exists,
  and neither cache changed

### Requirement: Insufficient Origin Stock Is Refused (Back-Office Tightening)

A transferencia MUST be refused with `409
stock_insuficiente_para_transferencia` when it would leave the origin's
`stock.cantidad` negative. This is a deliberate tightening relative to
checkout, governed by the principle: counter operations never block on
stock (a cashier must never be stopped mid-sale), while back-office
stock-reducing operations do (a depot move that would invent units costs
nothing to refuse).

#### Scenario: A transfer that would go negative is refused
- GIVEN `stock.cantidad = 5` for an articulo at origen
- WHEN a transferencia of 8 units is submitted
- THEN it is rejected with `409 stock_insuficiente_para_transferencia` and
  no movement is written

#### Scenario: A sale of the same articulo still goes negative
- GIVEN `stock.cantidad = 5` for an articulo at a punto de venta
- WHEN a sale of 8 units of that articulo is checked out at the same punto
  de venta
- THEN the sale succeeds and `stock.cantidad = -3` — the asymmetry against
  the transfer rule holds

### Requirement: Origen And Destino Must Differ And Share The Same Tenant

A transferencia with `origen = destino` MUST be rejected with `400
transferencia_origen_igual_destino`. A `destino` belonging to another tenant
MUST be treated as an invalid reference (RLS/EF-filter invisible) and
rejected before any write.

#### Scenario: Same-PV transfer is rejected
- GIVEN a transferencia request with `origen = destino = punto de venta 7`
- WHEN it is validated
- THEN it is rejected with `400 transferencia_origen_igual_destino` before
  reaching the database

#### Scenario: A destino from another tenant is rejected
- GIVEN a tenant-1 user submits a transferencia with `destino` belonging to
  tenant 2
- WHEN it is validated
- THEN it is rejected as an invalid reference and no movement is written

### Requirement: Sum-Invariant Holds Per Punto De Venta After A Transfer

After any sequence of movements including transferencias, `stock.cantidad`
for each `(id_articulo, id_punto_venta)` MUST equal
`SUM(movimientos_stock.cantidad)` filtered to that same pair — each row's
`id_punto_venta` is the location it affects, not a shared total.

#### Scenario: The invariant holds per PV after a mixed sequence
- GIVEN a sequence of `venta -5`, `ajuste +100`, `transferencia -20`
  (origen) and `transferencia +20` (destino) across two puntos de venta for
  the same articulo
- WHEN each punto de venta's `stock.cantidad` is compared against the sum of
  its own movements
- THEN both equal their respective sums independently

### Requirement: Authorization

Transferencia write paths MUST be gated by `Politicas.GestionDeCatalogo`
stacked over `Politicas.OperacionDePos` (Admin-only).

#### Scenario: Admin submits a transferencia
- GIVEN a user with role Admin
- WHEN they submit a valid transferencia between two puntos de venta of
  their tenant
- THEN the request succeeds

#### Scenario: Vendedor is blocked from transferencias
- GIVEN a user with role Vendedor
- WHEN they call the transferencia endpoint
- THEN the request is rejected with `403`
