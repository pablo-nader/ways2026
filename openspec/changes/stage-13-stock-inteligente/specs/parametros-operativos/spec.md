# Delta for Parametros Operativos

## ADDED Requirements

### Requirement: dias_rotacion And dias_cobertura_objetivo Are Known Parametro Keys

`ParametroConocido` MUST register two new keys: `dias_rotacion` (`int`,
default `30`) — the consumption window the rotation figure aggregates over
— and `dias_cobertura_objetivo` (`int`, default `7`) — the number of days
of average consumption `minimoSugerido` should cover. Both MUST resolve
through the existing punto de venta → empresa → declared-default
precedence (`ServicioDeParametros`), and neither MUST require any
`parametros` row to exist — this stage adds no migration and no data
statement, following the exact pattern stage 10 used for
`zona_horaria`/`comision_porcentaje` and stage 12 used for
`lotes_habilitado`/`dias_alerta_vencimiento`.

#### Scenario: dias_rotacion resolves to its default with no configured row
- GIVEN no `parametros` row exists for `dias_rotacion` at any level for a
  given empresa
- WHEN a punto de venta resolves `dias_rotacion`
- THEN it receives `30`

#### Scenario: dias_rotacion can be overridden per punto de venta
- GIVEN punto de venta 3 overrides `dias_rotacion = 60` while the empresa
  default is `30`
- WHEN punto de venta 3 resolves the key
- THEN it receives `60`

#### Scenario: dias_cobertura_objetivo resolves to its default with no configured row
- GIVEN no `parametros` row exists for `dias_cobertura_objetivo`
- WHEN a punto de venta resolves it
- THEN it receives `7`

#### Scenario: dias_cobertura_objetivo feeds minimoSugerido, never minimo directly
- GIVEN `dias_cobertura_objetivo = 7` and an articulo with an average
  daily consumption of `3`
- WHEN the reposición report computes `minimoSugerido`
- THEN `minimoSugerido = 21`, shown as a suggestion and never written to
  `stock.minimo` automatically
