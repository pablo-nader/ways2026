# Delta for Comprobantes Venta

## ADDED Requirements

### Requirement: Anulación Is Attributable Regardless Of Comprobante Composition

Every anulación via `EjecutarAnulacionAsync` MUST write an `auditoria` row
(`accion = "venta.anulacion"`, owned by the `auditoria-de-operaciones`
capability) in the same transaction as the `estado` transition to
`anulado` — independent of whether the comprobante carries product lines,
service-only lines, or a cuenta corriente movement to reverse. A failure of
the audit write MUST fail the anulación — `estado` MUST remain `emitido`
and no inverse `movimientos_stock`/`movimientos_cuenta_corriente` row MUST
exist. Previously, attribution existed only incidentally, through the
reversal ledgers' `id_empleado`, and was entirely absent for a
100%-servicio comprobante with no cuenta corriente to reverse.

#### Scenario: A 100%-servicio comprobante without cuenta corriente is attributable on anulación
- GIVEN a TX comprobante composed only of service lines (`id_articulo
  NULL` on every item) with no cuenta corriente pago
- WHEN it is anulado
- THEN an `auditoria` row naming the acting user exists, even though no
  `movimientos_stock` or `movimientos_cuenta_corriente` reversal row was
  written

#### Scenario: An audit failure blocks the anulación
- GIVEN the audit writer is forced to fail during anulación of a
  comprobante with 3 product lines and a cuenta corriente consumo
- WHEN the transaction is attempted
- THEN `estado` remains `emitido` and no inverse `movimientos_stock` or
  `movimientos_cuenta_corriente` row exists
