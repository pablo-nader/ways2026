# Delta for Comprobantes de Venta

## MODIFIED Requirements

### Requirement: Comprobante Schema At Rest

`comprobantes_venta` MUST be operativa-scoped (`id_tenant` + `id_punto_venta`,
doc 09) with `estado_comprobante` enum (`emitido | anulado`), `id_turno_caja`
referencing `turnos_caja` via FK — **required for every new sale**, resolved
server-side from the punto de venta's open turno and never client-supplied.
Comprobantes emitted before this stage keep `id_turno_caja = NULL`
permanently (decision 8: no backfill). `id_comprobante_asociado` nullable,
`UNIQUE (id_punto_venta, id_tipo_comprobante, numero)`. `items_comprobante_venta`
and `pagos_comprobante` MUST reference their comprobante via FK.
(Previously: `id_turno_caja` was written NULL unconditionally, with no FK,
and no open-turno precondition existed.)

#### Scenario: Every new sale carries the resolved open turno
- GIVEN a Vendedor with an open turno at punto de venta 7
- WHEN checkout completes
- THEN the persisted comprobante's `id_turno_caja` equals the open turno's
  id, not NULL

#### Scenario: Duplicate numero within the same punto de venta and tipo is rejected
- GIVEN a non-standard write path bypasses the atomic numeración allocator
- WHEN two rows are inserted with the same `(id_punto_venta, id_tipo_comprobante, numero)`
- THEN Postgres raises 23505 and `ManejadorDeErrores` maps it to 409

#### Scenario: Stage-5 NULL-turno comprobantes stay untouched
- GIVEN a comprobante emitted in stage 5 with `id_turno_caja NULL`
- WHEN the system is queried after stage 6 ships
- THEN the row still has `id_turno_caja NULL` — no backfill process ever runs

### Requirement: Anulación Reverses Stock and CC, Never Restores by Editing, and Is Blocked By A Closed Turno

Anulación MUST reject with `409 turno_cerrado` when the comprobante's
`id_turno_caja` references a turno whose `estado = cerrado` — comprobantes
with `id_turno_caja NULL` (stage-5 era) are exempt from this gate (decision
5). Otherwise, in one transaction: set `estado = anulado`, insert inverse
`movimientos_stock` rows (opposite sign, `motivo = anulacion`) for every item
with `id_articulo NOT NULL`, and insert a `contramovimiento` in
`movimientos_cuenta_corriente` if the original sale used cuenta corriente.
No `restaurar` endpoint MUST exist at any point.
(Previously: no turno-state gate existed — anulación never checked a turno's
estado.)

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
