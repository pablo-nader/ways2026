# Parametros Operativos Specification

## Purpose

Defines the doc 10 §9 key/value `parametros` table: operational settings
scoped to a punto_venta, with fallback to an empresa-level default.

## Requirements

### Requirement: Parameter Scope and Fallback

`parametros` MUST store `clave`/`valor jsonb` pairs with a nullable
`id_punto_venta`. `NULL` MUST represent the empresa-level default; a value
MUST represent a punto_venta-specific override.

#### Scenario: Resolve punto_venta-specific value

- GIVEN a `tolerancia_pago` row with `id_punto_venta = 1` and value `10`,
  and another with `id_punto_venta NULL` and value `5`
- WHEN punto_venta 1 resolves `tolerancia_pago`
- THEN it receives `10`

#### Scenario: Fallback to empresa default

- GIVEN only an `id_punto_venta NULL` row exists for `vuelto_maximo` with
  value `20`
- WHEN punto_venta 2 (no override) resolves `vuelto_maximo`
- THEN it receives `20`

#### Scenario: No value and no default

- GIVEN no row exists for `slots_tickets_espera` at either level for a
  given empresa
- WHEN a punto_venta resolves that key
- THEN the system returns a documented application default or an explicit
  "not configured" result, never a silent exception

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
