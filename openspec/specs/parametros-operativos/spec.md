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

### Requirement: zona_horaria And comision_porcentaje Are Known Parametro Keys

`ParametroConocido` MUST register two new keys: `zona_horaria` (`string`,
default `"America/Argentina/Buenos_Aires"`) and `comision_porcentaje`
(`decimal`, default `0`). Both MUST resolve through the existing punto de
venta → empresa → declared-default precedence (`ServicioDeParametros`), and
neither MUST require any `parametros` row to exist — the stage adds no
migration and no data statement.

#### Scenario: zona_horaria resolves to its default with no configured row
- GIVEN no `parametros` row exists for `zona_horaria` at any level for a
  given empresa
- WHEN a punto de venta resolves `zona_horaria`
- THEN it receives `"America/Argentina/Buenos_Aires"`

#### Scenario: comision_porcentaje defaults to off
- GIVEN no `parametros` row exists for `comision_porcentaje`
- WHEN a punto de venta resolves `comision_porcentaje`
- THEN it receives `0`, meaning commissions are computed but always zero
  until an Admin sets a rate

### Requirement: zona_horaria Is The First String-Typed Parametro And Must Be Stored Quoted

`zona_horaria` MUST be stored in `parametros.valor` (`jsonb`) as a
JSON-quoted string, because `ServicioDeParametros.ValidarTipo` deserializes
the stored value against the key's declared CLR type
(`JsonSerializer.Deserialize<string>`). An unquoted bare identifier is
invalid JSON for a `string` target and MUST be rejected at write time, not
silently misread at resolution time.

#### Scenario: A quoted IANA identifier is accepted
- GIVEN an Admin writes `valor = "\"America/Argentina/Cordoba\""` for
  `zona_horaria` at punto de venta 3
- WHEN the value is read back through `ServicioDeParametros`
- THEN it resolves to the string `America/Argentina/Cordoba`

#### Scenario: An unquoted value is rejected at write time
- GIVEN an Admin attempts to write `valor = America/Argentina/Cordoba`
  (unquoted) for `zona_horaria`
- WHEN the write is validated against the declared `string` type
- THEN it is rejected as invalid JSON, not stored
