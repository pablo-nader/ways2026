# Ajustes de Cuenta Corriente Specification

## Purpose

Defines the manual `Ajuste` movement (doc-01 F4 "Ajuste personalizado",
legacy `tipo = 5`): a signed, discretionary correction with a required
`detalle`, distinguished from the pre-existing anulación contramovimiento
(also a `tipo = ajuste` row, stage 5), and gated by a tighter authorization
tier than payments.

## Requirements

### Requirement: Ajuste Requires A Detalle

An Ajuste movement MUST be rejected (`ajuste_detalle_requerido`) if
`detalle` is empty or missing. `importe` MAY be positive (increases debt) or
negative (decreases debt), submitted explicitly by the caller.

#### Scenario: Ajuste with no detalle is rejected
- GIVEN an ajuste request with an empty `detalle`
- WHEN it is submitted
- THEN it is rejected with `ajuste_detalle_requerido` before any write

#### Scenario: A negative ajuste reduces saldo
- GIVEN `Cliente.Saldo = 300` and a `detalle = "Descuento por reclamo"`
- WHEN an ajuste of `importe = -50` is posted
- THEN `Cliente.Saldo = 250`

### Requirement: Ajuste Is Distinct From The Anulación Contramovimiento

Both a manual Ajuste and the stage-5 anulación contramovimiento persist as
`tipo = ajuste` rows in `movimientos_cuenta_corriente`, but they MUST remain
distinguishable: the anulación contramovimiento MUST carry
`id_comprobante_venta` set to the annulled comprobante and a
system-generated `detalle`; a manual Ajuste (this capability) MUST carry
`id_comprobante_venta NULL` and a caller-supplied, non-empty `detalle`.

#### Scenario: A manual ajuste carries no comprobante link
- GIVEN a posted manual ajuste
- WHEN its row is inspected
- THEN `id_comprobante_venta` is NULL

#### Scenario: An anulación contramovimiento stays distinguishable
- GIVEN a comprobante's anulación wrote a `tipo = ajuste` contramovimiento
- WHEN it is compared against a manual ajuste for the same cliente
- THEN it is distinguishable by its non-NULL `id_comprobante_venta`

### Requirement: Ajuste Authorization Under Supervisor + Admin

Ajuste manual MUST be gated by the new `Politicas.SupervisionDeCuentaCorriente`
policy (Supervisor + Admin) — narrower than `OperacionDePos`. Vendedor MUST
be rejected.

#### Scenario: Supervisor can post an ajuste
- GIVEN a user with role Supervisor
- WHEN they post an ajuste with a valid detalle
- THEN it succeeds (authorization-wise)

#### Scenario: Vendedor is rejected from ajuste manual
- GIVEN a user with role Vendedor
- WHEN they attempt to post an ajuste
- THEN it is rejected with `403`

### Requirement: Ajuste Updates Saldo Atomically And Snapshots saldo_resultante

Posting an ajuste MUST use the same `UPDATE ... RETURNING` pattern every
other CC writer uses; the inserted row's `saldo_resultante` MUST equal
`Cliente.Saldo` after the update, in the same transaction.

#### Scenario: Ajuste snapshots the resulting saldo
- GIVEN `Cliente.Saldo = 100`
- WHEN an ajuste of `+40` is posted
- THEN the row's `saldo_resultante = 140` and `Cliente.Saldo = 140`
