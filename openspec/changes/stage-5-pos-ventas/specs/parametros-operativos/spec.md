# Delta for Parametros Operativos

## ADDED Requirements

### Requirement: tolerancia_pago and vuelto_maximo Are Server-Authoritative At Checkout

`tolerancia_pago` and `vuelto_maximo` MUST be resolved server-side through
`ServicioDeParametros` (punto de venta > empresa > default) at checkout time
— they MUST NOT be hardcoded anywhere in the payment-validation path, and a
client-supplied tolerancia/vuelto value MUST NOT override the resolved one.

#### Scenario: No hardcoded tolerancia or vuelto value exists
- GIVEN the payment-validation Domain class
- WHEN its source is inspected
- THEN both values are read from `ServicioDeParametros`, never literal `10`
  or `20`

#### Scenario: Client-supplied override is ignored
- GIVEN a checkout request that includes a `toleranciaPago` field in its
  payload
- WHEN checkout validates payment
- THEN the server-resolved value is used, not the client-supplied one

### Requirement: Read Access Under OperacionDePos For UI Preview

`parametros` read endpoints MUST be reachable under `Politicas.OperacionDePos`
(Vendedor + Admin) so the POS can preview `tolerancia_pago` and
`vuelto_maximo` before checkout. Write endpoints stay on `GestionDeCatalogo`.

#### Scenario: Vendedor reads parametros for the payment panel
- GIVEN a user with role Vendedor
- WHEN they query `tolerancia_pago` for their punto de venta
- THEN the request succeeds

#### Scenario: Vendedor blocked from writing parametros
- GIVEN a user with role Vendedor
- WHEN they call the parametros update endpoint
- THEN the request is rejected with an authorization error
