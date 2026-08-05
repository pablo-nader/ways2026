# Delta for Operación de POS

## ADDED Requirements

### Requirement: Compra, Transferencia And Conteo Write Paths Stack GestionDeCatalogo Over OperacionDePos

Compra borrador/confirmar/anular, transferencias, conteos de inventario,
and the price-application action MUST stack `Politicas.GestionDeCatalogo`
over `Politicas.OperacionDePos` — the same composition the existing manual
stock `ajuste` path uses (Admin-only). Compra list, compra detail, and the
proveedor saldo read MUST stay on `Politicas.OperacionDePos` alone. Paying
a compra is an ordinary gasto and keeps the gastos endpoint's existing
`Politicas.OperacionDePos` gate, unaffected by this stage.

#### Scenario: Admin performs every stage-8 write path
- GIVEN a user with role Admin
- WHEN they create/confirm/anular a compra, submit a transferencia, and
  submit a conteo
- THEN every request succeeds (authorization-wise)

#### Scenario: Vendedor is blocked from every stage-8 write path
- GIVEN a user with role Vendedor
- WHEN they attempt any compra write, a transferencia, or a conteo
- THEN every request is rejected with `403`

#### Scenario: Vendedor still reads the compra list and proveedor saldo
- GIVEN a user with role Vendedor
- WHEN they list compras and read a proveedor's saldo for their tenant
- THEN both requests succeed

#### Scenario: Paying a compra keeps the existing gastos gate
- GIVEN a user with role Vendedor and an open turno
- WHEN they submit a gasto linked to a compra
- THEN the request succeeds under the unchanged `OperacionDePos` gate — no
  new tier was introduced for payment
