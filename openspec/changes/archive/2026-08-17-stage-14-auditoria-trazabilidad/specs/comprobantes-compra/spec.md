# Delta for Comprobantes Compra

## ADDED Requirements

### Requirement: Anulación Is Attributable, Same Transaction As The Contramovimientos

Every anulación via `ServicioDeCompras.AnularAsync` MUST write an
`auditoria` row (`accion = "compra.anulacion"`, owned by the
`auditoria-de-operaciones` capability) in the same transaction as its stock
contramovimientos and the `estado` transition to `anulada`. A failure of
the audit write MUST fail the anulación exactly as the existing
negative-stock refusal does — no partial state, no reversal without
attribution.

#### Scenario: A compra anulación is attributable to its actor
- GIVEN a confirmada compra of 50 units, none yet sold
- WHEN an Admin anula it
- THEN the `auditoria` row's `id_actor` identifies that Admin, written in
  the same transaction as the `-50` `movimientos_stock` row

#### Scenario: An audit failure blocks the anulación, same as the negative-stock refusal
- GIVEN the audit writer is forced to fail during anulación of a
  confirmada compra with sufficient stock to reverse
- WHEN the transaction is attempted
- THEN `estado` remains `confirmada` and no `movimientos_stock`
  contramovimiento exists
