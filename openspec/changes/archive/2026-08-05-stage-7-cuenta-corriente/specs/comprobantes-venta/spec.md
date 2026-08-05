# Delta for Comprobantes de Venta

## ADDED Requirements

### Requirement: RC Joins The POS-Emittable Tipos, Non-Fiscal

The `RC` `tipos_comprobante` row MUST be emittable through the same
`ServicioDeVentas` checkout entry point as `TX`/`NCX`, with
`es_fiscal = false`, `afecta_stock = false`, `discrimina_iva = false`,
`letra = NULL`, `signo = +1` — no fiscal receipt path is created.

#### Scenario: RC emission never touches fiscal fields
- GIVEN a valid RC payment request
- WHEN it is emitted
- THEN the persisted comprobante has `discrimina_iva = false`, no
  `neto_gravado`/`iva_total`, and `letra = NULL`

## MODIFIED Requirements

### Requirement: Checkout Is One All-Or-Nothing Transaction

`ServicioDeVentas` checkout MUST write the comprobante, its items, its
pagos, the resulting `movimientos_stock`, the `stock` cache update, the
numeración allocation, and (if cuenta corriente was used, or the comprobante
is an itemless RC pago a cuenta) the `movimientos_cuenta_corriente` row
inside a single database transaction. For an RC comprobante specifically,
items and `movimientos_stock` are empty by construction
(`afecta_stock = false`) — the transaction still covers the comprobante, its
pagos, the numeración allocation, and the single `Pago` movement. Any
failure at any step MUST roll back every write, including the numeración
allocation.
(Previously: described only the CC-sale consumo case; did not cover the
itemless RC path.)

#### Scenario: Successful checkout commits every write together
- GIVEN a cart of 2 items and a full efectivo payment
- WHEN checkout completes
- THEN the comprobante, its 2 items, its 1 pago, 2 movimientos_stock rows,
  the updated stock cache, and the allocated numero are all visible in the
  same read

#### Scenario: A failure after stock decrement rolls back everything
- GIVEN a checkout that decrements stock successfully but then fails CC
  credit-limit validation
- WHEN the transaction aborts
- THEN no comprobante, item, pago, movimiento_stock, or numeración advance is
  visible — the numeración counter is unchanged

#### Scenario: RC checkout commits with zero items and one Pago movement
- GIVEN a valid RC payment of `200.00` efectivo
- WHEN it completes
- THEN the comprobante persists with 0 items, 1 pago, 0 movimientos_stock,
  and 1 `movimientos_cuenta_corriente` `Pago` row, all in the same
  transaction

### Requirement: Numeración Allocation Is Atomic

Numero allocation MUST use `numeraciones_comprobante`'s `UPDATE ... SET
proximo_numero = proximo_numero + 1 ... RETURNING proximo_numero - 1` inside
the sale transaction, per `(id_punto_venta, id_tipo_comprobante)`. No number
is ever client-supplied. The visible format is `PPPP-NNNNNNNN`. `RC` MUST
allocate through the same per-`(id_punto_venta, id_tipo_comprobante)`
counter, independent from `TX`'s series.
(Previously: did not have an explicit scenario naming RC as an independent
series.)

#### Scenario: Concurrent sales at the same punto de venta get consecutive numbers
- GIVEN two concurrent checkouts at the same punto de venta and tipo TX
- WHEN both transactions commit
- THEN one receives `numero = N` and the other `N+1`, with no gap and no
  duplicate

#### Scenario: A rolled-back sale leaves an accepted gap
- GIVEN a checkout allocates `numero = 42` and then fails a later validation,
  rolling back
- WHEN the next successful sale at the same punto de venta and tipo runs
- THEN it receives `numero = 43` — the gap at 42 is accepted (non-fiscal TX/NCX)

#### Scenario: RC and TX numerar independently at the same punto de venta
- GIVEN TX is at `numero 50` and RC has never been emitted at punto de venta 7
- WHEN an RC is emitted there
- THEN it receives `numero = 1`, and the next TX still receives `numero = 51`

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
(Previously: the contramovimiento clause was scoped to a CC-sale consumo
only; RC anulación had no reversal path.)

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
