# Delta for Parametros Operativos

## ADDED Requirements

### Requirement: lotes_habilitado And dias_alerta_vencimiento Are Known Parametro Keys

`ParametroConocido` MUST register two new keys: `lotes_habilitado`
(`bool`, default `false`) — the empresa-level lot module switch — and
`dias_alerta_vencimiento` (`int`, default `30`) — the "próximo a vencer"
horizon consumed by the vencimientos report. Both MUST resolve through the
existing punto de venta → empresa → declared-default precedence
(`ServicioDeParametros`), and neither MUST require any `parametros` row to
exist — this stage adds no migration and no data statement, following the
exact pattern stage 10 used for `zona_horaria`/`comision_porcentaje`.

#### Scenario: lotes_habilitado resolves to false with no configured row
- GIVEN no `parametros` row exists for `lotes_habilitado` at any level for a
  given empresa
- WHEN the empresa resolves `lotes_habilitado`
- THEN it receives `false` — the module is off by default

#### Scenario: An empresa turns the module on via a single parametro row
- GIVEN an Admin writes `lotes_habilitado = true` at empresa level (no
  `id_punto_venta`)
- WHEN any punto de venta of that empresa resolves `lotes_habilitado`
- THEN it receives `true`

#### Scenario: dias_alerta_vencimiento defaults to 30
- GIVEN no `parametros` row exists for `dias_alerta_vencimiento`
- WHEN a punto de venta resolves it
- THEN it receives `30`

#### Scenario: dias_alerta_vencimiento can be overridden per punto de venta
- GIVEN punto de venta 3 overrides `dias_alerta_vencimiento = 15` while the
  empresa default is `30`
- WHEN punto de venta 3 resolves the key
- THEN it receives `15`

### Requirement: ServicioDeVentas Batches Its Parametro Reads Into One Query

`ServicioDeVentas`'s private parametro resolution MUST issue a single
`WHERE clave IN (...)` query resolving `tolerancia_pago`, `vuelto_maximo`,
and `lotes_habilitado` together, rather than one query per key. This is a
strict improvement over the pre-stage-12 baseline of two separate queries
for `tolerancia_pago`/`vuelto_maximo` — adding the third key does not add a
third round-trip; it replaces two round-trips with one.

#### Scenario: A single batched query resolves all three keys
- GIVEN a checkout resolves `tolerancia_pago`, `vuelto_maximo`, and
  `lotes_habilitado` for the same punto de venta
- WHEN the resolution runs
- THEN exactly one `parametros` query executes, filtering
  `clave IN ('tolerancia_pago', 'vuelto_maximo', 'lotes_habilitado')`

#### Scenario: The batched query still resolves punto de venta overrides correctly
- GIVEN punto de venta 3 overrides `vuelto_maximo = 30` while
  `tolerancia_pago` and `lotes_habilitado` fall back to empresa defaults
- WHEN the batched query resolves all three for punto de venta 3
- THEN `vuelto_maximo` resolves to `30` and the other two resolve to their
  empresa/default values, all from the same single query
