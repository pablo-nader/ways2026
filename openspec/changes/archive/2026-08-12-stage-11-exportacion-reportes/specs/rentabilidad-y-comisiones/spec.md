# Delta for Rentabilidad Y Comisiones

## ADDED Requirements

### Requirement: Rentabilidad And Comisiones Exports Stack LecturaDeRentabilidad And Carry Coverage

`GET /api/reportes/rentabilidad/export` and
`GET /api/reportes/comisiones/export` MUST be gated by
`Politicas.LecturaDeRentabilidad` exactly like their source routes, under
the `exportacion-de-reportes` contract. The rentabilidad workbook MUST
repeat the coverage payload (lines included, excluded as estimated, skipped
as unknown-cost, each with its revenue subtotal) inside its header block.

#### Scenario: A Supervisor is rejected on the rentabilidad export
- GIVEN a user with role Supervisor
- WHEN they call `GET /api/reportes/rentabilidad/export?formato=xlsx`
- THEN the response is `403`

#### Scenario: An Admin's rentabilidad export carries the coverage block
- GIVEN a period whose JSON coverage reports 7 lines included, 2 excluded
  as estimated, 1 skipped as unknown
- WHEN an Admin exports rentabilidad for that period
- THEN the workbook's header states the same three counts and their revenue
  subtotals

#### Scenario: The comisiones export is labelled PROVISIONAL
- GIVEN an Admin exports comisiones for a period
- WHEN the workbook is generated
- THEN it carries the label `PROVISIONAL`, matching the JSON response's label
