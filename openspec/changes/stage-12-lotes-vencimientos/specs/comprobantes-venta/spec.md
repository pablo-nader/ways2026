# Delta for Comprobantes De Venta

## MODIFIED Requirements

### Requirement: Snapshot Immutability of Items

Every `items_comprobante_venta` row MUST copy `descripcion`, `codigo_barra`,
`id_area`, `precio_unitario`, `id_lista_precio`, `id_oferta`, `descuento`,
`id_alicuota_iva`, `porcentaje_iva`, `costo_unitario`, `costo_es_estimado`,
and — for a lot-effective articulo line — `id_lote` at emission time. `id_lote`
MUST be frozen at emission and never re-derived; it is what makes anulación
exact (the reversal reads the item's own lot, with no lookup and no
ambiguity). No endpoint MUST ever update an item after emission — a
reprint MUST NOT re-join `articulos`, `precios`, `ofertas`, or `lotes`.
(Previously: the frozen list did not include `id_lote` — the column did not
exist until this stage.)

#### Scenario: Reprint is unaffected by a later catalog change
- GIVEN a comprobante emitted with an item snapshot `precio_unitario = 150.00`
- WHEN the article's live price later changes to `180.00` and the ticket is reprinted
- THEN the reprinted line still shows `150.00`, unchanged

#### Scenario: No item update endpoint exists
- GIVEN an emitted comprobante
- WHEN any client attempts to call an item-edit endpoint
- THEN no such endpoint exists (404) — the only mutation on a comprobante is anulación

#### Scenario: Emission freezes the live costo_nominal onto the line
- GIVEN an articulo with `costo_nominal = 121.00`
- WHEN a TX line of 2 units for that articulo is emitted
- THEN the item persists `costo_unitario = 121.00`, `costo_es_estimado = false`

#### Scenario: An articulo with no cost produces an honest gap, never zero
- GIVEN an articulo with `costo_nominal = NULL`
- WHEN a line for that articulo is emitted
- THEN the item persists `costo_unitario = NULL`, `costo_es_estimado = false` —
  distinct from an articulo with `costo_nominal = 0`, which persists
  `costo_unitario = 0`

#### Scenario: A lot-effective line freezes its resolved lot onto the snapshot
- GIVEN a sale line for a lot-effective articulo resolves to `id_lote = 7`
  (FEFO-defaulted or supplied)
- WHEN the item is persisted
- THEN `items_comprobante_venta.id_lote = 7`, and a later price/lot change
  does not alter it on reprint

#### Scenario: A non-lot articulo's item never carries a lot
- GIVEN a sale line for an articulo with `controla_lote = false`
- WHEN the item is persisted
- THEN `items_comprobante_venta.id_lote` is NULL

### Requirement: Devoluciones As NCX Comprobantes

A devolución MUST be emitted as a comprobante of tipo NCX (`signo` negative)
through the same checkout flow, with negative-quantity or negative-total
lines. `id_comprobante_asociado` is optional but MUST be populated when the
devolución references an original comprobante — this stage does NOT make it
mandatory for lot-controlled lines. For a lot-effective articulo, an NCX
line MUST carry an explicit `idLote`. The POS MUST suggest it — from the
associated comprobante's item snapshot when `id_comprobante_asociado` is
present, otherwise from the articulo's existing lots — but the suggestion is
never auto-applied without operator confirmation; the sin-identificar lot
remains a valid, always-available choice when the operator cannot identify
the physical lot. Returning into an expired lot MUST be permitted.
(Previously: silent on the lot dimension of NCX lines — no lot reference
existed on `items_comprobante_venta` until this stage.)

#### Scenario: Standalone devolución without an original
- GIVEN a devolución with no referenced comprobante
- WHEN it is emitted
- THEN `id_comprobante_asociado` is NULL and the comprobante persists as NCX

#### Scenario: Devolución referencing an original
- GIVEN an original TX comprobante `id = 501`
- WHEN a devolución is emitted against it
- THEN the new NCX comprobante's `id_comprobante_asociado = 501`

#### Scenario: An NCX line for a lot-effective articulo requires idLote
- GIVEN articulo 40 is lot-effective
- WHEN an NCX line for articulo 40 is submitted with no `idLote`
- THEN it is rejected before reaching the database

#### Scenario: idLote is suggested from the associated comprobante's snapshot
- GIVEN an original TX comprobante `id = 501` whose item for articulo 40
  carries `id_lote = 7`
- WHEN a devolución referencing `id_comprobante_asociado = 501` is prepared
  in the POS
- THEN the suggested `idLote` for the returned line is `7`

#### Scenario: idLote is required even without an associated comprobante
- GIVEN a standalone devolución (no `id_comprobante_asociado`) for a
  lot-effective articulo
- WHEN the operator cannot identify the physical lot
- THEN the sin-identificar lot is accepted as `idLote`, and the request
  succeeds

#### Scenario: Returning into an expired lot is permitted
- GIVEN lot 9 of articulo 40 has `fecha_vencimiento` in the past
- WHEN a devolución line supplies `idLote = 9`
- THEN it is accepted — the returned units are honestly recorded as expired

### Requirement: Anulación Reverses Stock and CC, Never Restores by Editing, and Is Blocked By A Closed Turno

Anulación MUST reject with `409 turno_cerrado` when the comprobante's
`id_turno_caja` references a turno whose `estado = cerrado` — comprobantes
with `id_turno_caja NULL` (stage-5 era) are exempt from this gate (decision
5). Otherwise, in one transaction: set `estado = anulado`, insert inverse
`movimientos_stock` rows (opposite sign, `motivo = anulacion`) for every item
with `id_articulo NOT NULL`, and insert a `contramovimiento` in
`movimientos_cuenta_corriente` if the original comprobante produced a
`consumo` (CC sale) or a `pago` (RC) row — the reversal direction matches
the original row's sign. No `restaurar` endpoint MUST exist at any point.
For a lot-bearing item (`id_lote NOT NULL`), the inverse movement MUST carry
the **exact same `id_lote`** read from the item's own snapshot — no lookup,
no FEFO re-evaluation, no ambiguity — and MUST update that lot's
`stock_lotes` cache in the same transaction.
(Previously: the contramovimiento clause was scoped to a CC-sale consumo
only, and the inverse stock movement carried no lot dimension.)

#### Scenario: Anulación reverses stock movements
- GIVEN a comprobante whose sale decremented stock by 3 units of an articulo
- WHEN it is anulado
- THEN a new `movimientos_stock` row of `+3` with `motivo = anulacion` is
  inserted, and `stock.cantidad` reflects the reversal

#### Scenario: Anulación reverses a cuenta corriente consumo
- GIVEN a comprobante paid partly by cuenta corriente (`consumo = 200`)
- WHEN it is anulado
- THEN a `movimientos_cuenta_corriente` contramovimiento of `-200` is
  inserted and `Cliente.Saldo` decreases by `200`

#### Scenario: Anulación is idempotent-safe against double-anulación
- GIVEN a comprobante already `estado = anulado`
- WHEN a second anulación request is submitted
- THEN it is rejected with a domain validation error and no duplicate inverse
  movements are created

#### Scenario: No restaurar endpoint exists
- GIVEN an anulado comprobante
- WHEN any client attempts to call a restaurar/undo-anulación endpoint
- THEN no such endpoint exists (404)

#### Scenario: Anulación rejected when the comprobante's turno is closed
- GIVEN a comprobante whose `id_turno_caja` points to a turno with `estado =
  cerrado`
- WHEN anulación is requested
- THEN it is rejected with `409 turno_cerrado` and no stock/CC reversal is
  written

#### Scenario: Stage-5 NULL-turno comprobante stays anulable
- GIVEN a comprobante with `id_turno_caja NULL`
- WHEN anulación is requested
- THEN it succeeds — the closed-turno gate only fires when a turno exists
  and is closed

#### Scenario: RC anulación is blocked by a closed turno
- GIVEN an RC comprobante whose turno is now `cerrado`
- WHEN anulación is requested
- THEN it is rejected with `409 turno_cerrado`, same as any other comprobante

#### Scenario: Anulación of a lot-bearing sale reverses the exact lot
- GIVEN a sale item persisted `id_lote = 7`, having decremented
  `stock_lotes.cantidad` for lot 7 by 4
- WHEN the comprobante is anulado
- THEN the inverse `movimientos_stock` row carries `id_lote = 7` (read from
  the item snapshot, not re-derived), and `stock_lotes.cantidad` for lot 7
  increases by 4

## ADDED Requirements

### Requirement: FEFO Lot Is Decided In The Read Phase And Frozen On The Item

For a lot-effective articulo line, `idLote` on the checkout request MUST be
optional. When omitted, `ServicioDeVentas.EmitirAsync` MUST select the FEFO
lot in its decide-then-commit read phase, before the transaction opens —
never inside the retryable transaction lambda. When `idLote` is supplied,
the server MUST validate it and honour it. The per-lot `movimientos_stock`
write MUST occur inside the pinned stock-write lock order (`stock`
capability: `id_articulo, id_punto_venta, id_lote NULLS FIRST`).

#### Scenario: A cart with a lot-controlled and a non-lot articulo mixes both paths
- GIVEN a cart with one line for a lot-effective articulo (no `idLote`
  supplied) and one line for a non-lot articulo
- WHEN checkout runs
- THEN the FEFO lot for the first line is resolved before the transaction
  opens, the second line's stock decrement carries no lot dimension, and
  both writes commit in the same transaction

#### Scenario: A client that knows nothing about lots still transacts correctly
- GIVEN a legacy client submits a sale line for a lot-effective articulo
  with no `idLote` field in the payload
- WHEN checkout runs
- THEN the server silently applies its FEFO default and the sale succeeds

### Requirement: Expired Lot Sale Warns, Never Blocks

A sale or NCX line resolving (by FEFO default or explicit `idLote`) to a
lot whose `fecha_vencimiento` is in the past MUST be accepted. The response
MUST carry a warning flag identifying the expired line so the POS can
display it prominently. FEFO MUST pre-select a non-expired lot whenever one
exists with positive balance, so an expired-lot sale only happens when the
operator explicitly overrides the default or when no non-expired lot has
stock.

#### Scenario: A sale of an explicitly expired lot succeeds with a warning
- GIVEN lot 9 of articulo 40 has `fecha_vencimiento` in the past and
  positive balance, and it is the only lot with stock
- WHEN a sale line for articulo 40 is checked out
- THEN the sale succeeds and the response marks that line with an expired-lot
  warning

#### Scenario: FEFO prefers a non-expired lot when one has stock
- GIVEN articulo 40 has an expired lot with positive balance and a
  non-expired lot also with positive balance
- WHEN a sale line for articulo 40 omits `idLote`
- THEN the server selects the non-expired lot, and the response carries no
  expired-lot warning for that line
