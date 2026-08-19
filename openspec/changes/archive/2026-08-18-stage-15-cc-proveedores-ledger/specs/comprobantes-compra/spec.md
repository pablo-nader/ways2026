# Delta for Comprobantes de Compra

## ADDED Requirements

### Requirement: Confirmar Writes A Compra Movement To The Proveedor Ledger

`ConfirmarAsync` MUST write exactly one positive `compra` movement to
`movimientos_cuenta_corriente_proveedor` in its existing transaction,
carrying `id_comprobante_compra` and the comprobante's `total` as `importe`,
with `proveedores` locked as the transaction's last row lock before the
ledger INSERT.

#### Scenario: Confirming a compra writes exactly one debt movement
- GIVEN a borrador of `total = 5000`
- WHEN it is confirmed
- THEN exactly one `compra` movement of `importe = 5000` is written
  alongside the existing stock and cost effects

### Requirement: Anulación Writes A Reversing Ajuste, Gastos Are Still Not Reversed

`AnularAsync` MUST write exactly one negative `ajuste` movement — of
magnitude equal to the original `compra` movement, `id_comprobante_compra`
set to the annulled compra — in the same transaction as the existing stock
contramovimientos. Linked `gastos` and their ledger `pago` movements MUST
NOT be touched, and the informational `gastosLigados` count stays unchanged.

#### Scenario: Anulación writes a reversing ajuste without touching linked gastos
- GIVEN a confirmada compra of `1000` with a linked gasto of `600`
- WHEN it is anulada
- THEN a `-1000` reversing `ajuste` is written, the linked gasto and its
  `pago` movement remain untouched, and the compra's `gastosLigados` count
  is unchanged
