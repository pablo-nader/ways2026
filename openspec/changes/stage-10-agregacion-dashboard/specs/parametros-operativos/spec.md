# Delta for Parametros Operativos

## ADDED Requirements

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
