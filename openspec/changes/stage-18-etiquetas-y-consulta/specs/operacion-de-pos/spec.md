# Delta for Operación de POS

## ADDED Requirements

### Requirement: Etiquetas And Consulta De Precios Read Surfaces Live Under OperacionDePos

`POST /api/etiquetas/datos` and the two read screens it and
`/consulta-precios` compose MUST be gated by the existing
`Politicas.OperacionDePos` (Vendedor + Supervisor + Admin), with nothing
stacked on top. Both surfaces are strictly read-only. `Politicas.cs` MUST NOT
be modified for this stage.

#### Scenario: Vendedor reaches both new read surfaces
- GIVEN a user with role Vendedor
- WHEN they call `POST /api/etiquetas/datos` and use the price-lookup screen
  for their tenant
- THEN both succeed (authorization-wise)

#### Scenario: A role outside OperacionDePos is rejected from both
- GIVEN a user with `RolConocido.Root`
- WHEN they call `POST /api/etiquetas/datos` or attempt the price-lookup
  screen
- THEN both are rejected
