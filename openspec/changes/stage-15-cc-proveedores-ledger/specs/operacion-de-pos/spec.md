# Delta for Operación de POS

## ADDED Requirements

### Requirement: SupervisionDeCuentaDeProveedor Policy Gates The Proveedor Ajuste Manual

A new `Politicas.SupervisionDeCuentaDeProveedor` policy (Supervisor + Admin)
MUST gate the manual ajuste on the proveedor ledger — Vendedor MUST be
rejected. It is distinct from `SupervisionDeCuentaCorriente`, reserved for
the client-side ledger and a future cierre-de-caja tightening.

#### Scenario: Supervisor and Admin post a manual proveedor ajuste
- GIVEN a user with role Supervisor, and separately a user with role Admin
- WHEN each posts a manual ajuste with a valid detalle
- THEN both requests succeed (authorization-wise)

#### Scenario: Vendedor is rejected from the proveedor ajuste manual
- GIVEN a user with role Vendedor
- WHEN they attempt to post a proveedor ajuste
- THEN it is rejected with `403`

### Requirement: Proveedor Estado De Cuenta And Saldo Reads Stay Under OperacionDePos

Proveedor estado de cuenta and the existing `/saldo` read MUST stay gated
by `Politicas.OperacionDePos` (Vendedor + Supervisor + Admin) — the cashier
looks up what the tenant owes all day; only the ajuste write needs the
tighter policy.

#### Scenario: Vendedor reads estado de cuenta and saldo
- GIVEN a user with role Vendedor
- WHEN they read a proveedor's estado de cuenta and its saldo
- THEN both requests succeed (authorization-wise)
