# Delta for Precios

## ADDED Requirements

### Requirement: Price Change Is Attributable, Fail-Closed On Audit Failure

Every price change via `AbrirNuevoPrecioAsync` MUST write an `auditoria` row
(`accion = "precio.cambio"`, owned by the `auditoria-de-operaciones`
capability) in the same transaction as the price row itself, naming the
acting user as `id_actor`. A failure of the audit write MUST fail the
entire price change — the previous row's `vigente_hasta` MUST remain
unchanged and no new `precios` row MUST exist. Today "Price History Never
Overwrites" gives a perfect history of *what* with no trace of *who*; this
requirement closes that gap without touching the `precios` table itself.

#### Scenario: A price change is attributable to its actor
- GIVEN an Admin sets a new price of $130 for an articulo in the General
  lista, replacing a $100 vigente price
- WHEN the operation completes
- THEN the `auditoria` row's `id_actor` identifies that Admin, written in
  the same transaction as the $100 row's close and the $130 row's open

#### Scenario: An audit failure blocks the price change rather than losing attribution
- GIVEN the audit writer is forced to fail during a price change
- WHEN the transaction is attempted
- THEN the price change does not take effect — the previously vigente
  row's `vigente_hasta` is unchanged
