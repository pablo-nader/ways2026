# Delta for Operación de POS

## ADDED Requirements

### Requirement: SupervisionDeCuentaCorriente Policy Gates Reliquidación And Ajuste Manual

A new `Politicas.SupervisionDeCuentaCorriente` policy (Supervisor + Admin)
MUST gate reliquidación a precio del día and ajuste manual — Vendedor MUST
be rejected. This is the stage's one deliberate departure from legacy parity
(the legacy has no role gate on cuenta corriente at all).

#### Scenario: Supervisor can run reliquidación and post an ajuste
- GIVEN a user with role Supervisor
- WHEN they request reliquidación or post an ajuste for their tenant
- THEN both requests succeed (authorization-wise)

#### Scenario: Admin can run reliquidación and post an ajuste
- GIVEN a user with role Admin
- WHEN they request reliquidación or post an ajuste
- THEN both requests succeed (authorization-wise)

#### Scenario: Vendedor is rejected from reliquidación and ajuste manual
- GIVEN a user with role Vendedor
- WHEN they attempt reliquidación or an ajuste post
- THEN both are rejected with `403`

### Requirement: Pago A Cuenta And Estado De Cuenta Reads Live Under OperacionDePos

RC emission, RC anulación, and estado de cuenta reads (header + movement
list) MUST be gated by the existing `Politicas.OperacionDePos` (Vendedor +
Supervisor + Admin) — legacy parity, the cashier takes payments and looks up
accounts all day.

#### Scenario: Vendedor posts a pago a cuenta and reads estado de cuenta
- GIVEN a user with role Vendedor
- WHEN they post an RC payment and read estado de cuenta for their tenant
- THEN both requests succeed (authorization-wise)

#### Scenario: A role outside OperacionDePos is rejected from both
- GIVEN a user with `RolConocido.Root`
- WHEN they call RC emission or estado de cuenta
- THEN authorization fails
