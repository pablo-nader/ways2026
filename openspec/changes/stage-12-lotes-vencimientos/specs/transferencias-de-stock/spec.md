# Delta for Transferencias De Stock

## MODIFIED Requirements

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
transfer completes or it does not happen at all. For a lot-effective
articulo, **the lot travels**: both mirrored rows MUST carry the same
`id_lote` (a lot is an identity of the merchandise, not of the location),
both puntos de venta's `stock_lotes` cache for that lot MUST be upserted in
the same transaction, and `idLote` per line MUST be optional — when omitted,
the server resolves it via the same FEFO default rule used at checkout, in
the decide-then-commit read phase.
(Previously: silent on the lot dimension — a transfer moved only the
aggregate cache until this stage.)

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

#### Scenario: A lot-effective articulo transfer moves the same lot at both ends
- GIVEN articulo 40 is lot-effective, and lot 7 has `stock_lotes.cantidad = 20`
  at origen
- WHEN a transferencia of 8 units of lot 7 is submitted from origen to
  destino
- THEN both mirrored `movimientos_stock` rows carry `id_lote = 7`, origen's
  `stock_lotes` for lot 7 becomes `12`, and destino's `stock_lotes` for lot 7
  becomes `8` — the expiry information travels with the merchandise

#### Scenario: An omitted idLote resolves via FEFO at transfer time
- GIVEN articulo 40 has lot `L1` (expiring sooner, positive balance) and `L2`
  (expiring later) at origen
- WHEN a transferencia line for articulo 40 omits `idLote`
- THEN the server selects `L1` and both mirrored rows carry `id_lote = L1`

### Requirement: Insufficient Origin Stock Is Refused (Back-Office Tightening)

A transferencia MUST be refused with `409
stock_insuficiente_para_transferencia` when it would leave the origin's
`stock.cantidad` negative. This is a deliberate tightening relative to
checkout, governed by the principle: counter operations never block on
stock (a cashier must never be stopped mid-sale), while back-office
stock-reducing operations do (a depot move that would invent units costs
nothing to refuse). For a lot-effective articulo, the same refusal MUST
apply at the **lot** level: a transferencia that would leave the origin
lot's `stock_lotes.cantidad` negative MUST be refused with
`409 stock_insuficiente_para_transferencia`, even when the origin's
aggregate `stock.cantidad` is sufficient.
(Previously: the refusal checked only the aggregate — a lot-level
underflow behind a sufficient aggregate was undetectable until this stage.)

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

#### Scenario: A lot-level underflow is refused even with a sufficient aggregate
- GIVEN articulo 40 is lot-effective at origen with `stock.cantidad = 30`
  split across `L1 = 5` and `L2 = 25`
- WHEN a transferencia of 8 units of `L1` is submitted
- THEN it is rejected with `409 stock_insuficiente_para_transferencia`,
  even though the aggregate (`30`) would have covered it, and no movement
  is written

## ADDED Requirements

### Requirement: Lock Order Extends To A ≥2N-Key Tuple Order Over (id_articulo, id_punto_venta, id_lote)

Transfers MUST keep their existing rule of one ascending total order over
all keys involved, never "all origin then all destination" — the tuple
gains a third component when a lot-effective articulo is in the transfer,
so a transfer of N lot-bearing lines builds one ascending order over `≥2N`
`(id_articulo, id_punto_venta, id_lote)` tuples (the aggregate `stock` rows
sort with `id_lote NULLS FIRST` within that same order, matching the rule
stated once in the `stock` capability).

#### Scenario: A single ascending order covers both origin and destination lot rows
- GIVEN a transferencia of 2 lot-effective items between origen and destino
- WHEN the transaction builds its lock order
- THEN it is one ascending order over all 4 (or more, if the aggregate rows
  are included) key tuples — never all-origin-then-all-destino

### Requirement: Duplicate-Line Detection Widens To (IdArticulo, IdLote), Evaluated After FEFO Defaulting

A transferencia request MUST be rejected with `400 articulo_repetido` when
two lines resolve to the same `(id_articulo, id_lote)` pair. The duplicate
key widens from `id_articulo` alone to include `id_lote`, and MUST be
evaluated **after** FEFO defaulting resolves any omitted `idLote` — so two
lines of the same articulo that both omit `idLote` and would independently
resolve to the same FEFO lot are also caught, even though neither line
named a lot explicitly. Two lines of the same articulo that resolve to
**different** lots MUST be accepted: moving two lots of the same articulo
in one transfer is a legitimate depot operation, and the pre-stage-12 key
would have refused it for no reason.
(Amended post-design, decision 11: this rule pre-dates this stage at the
`id_articulo`-only key — stage 12 widens the key and moves its evaluation
point after lot defaulting; the `articulo_repetido` code is reused
unchanged.)

#### Scenario: Two lines of the same articulo with different explicit lots are accepted
- GIVEN articulo 40 is lot-effective with lots `L1` and `L2`, both with
  positive balance at origen
- WHEN a transferencia request carries two lines for articulo 40, one with
  `idLote = L1` and one with `idLote = L2`
- THEN both lines are accepted — they are not duplicates

#### Scenario: Two lines resolving to the same explicit lot are rejected
- GIVEN articulo 40 is lot-effective with lot `L1`
- WHEN a transferencia request carries two lines for articulo 40, both with
  `idLote = L1`
- THEN it is rejected with `400 articulo_repetido` before reaching the
  database, and no movement is written

#### Scenario: Two lines both omitting idLote that resolve to the same FEFO lot are rejected
- GIVEN articulo 40 is lot-effective and only one lot, `L1`, has positive
  balance at origen
- WHEN a transferencia request carries two lines for articulo 40, both
  omitting `idLote`
- THEN FEFO defaulting resolves both lines to `L1`, and the request is then
  rejected with `400 articulo_repetido` — the duplicate check runs after
  defaulting, not against the client's (empty) input

### Requirement: Expired Lot Transfer Is Refused

A transferencia line whose `id_lote` (supplied or FEFO-resolved) references
a lot with `fecha_vencimiento` in the past MUST be rejected with
`409 transferencia_lote_vencido` — moving already-expired goods between
puntos de venta is never the right operation; `decomiso` is.

#### Scenario: Transferring an explicitly expired lot is refused
- GIVEN lot 9 of articulo 40 has `fecha_vencimiento = 2026-08-01` (in the
  past relative to today) and positive balance at origen
- WHEN a transferencia line explicitly supplies `idLote = 9`
- THEN it is rejected with `409 transferencia_lote_vencido` and no movement
  is written

#### Scenario: A non-expired lot transfers normally
- GIVEN lot 10 of articulo 40 has `fecha_vencimiento` in the future
- WHEN a transferencia line supplies `idLote = 10`
- THEN it succeeds
