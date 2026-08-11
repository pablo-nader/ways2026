# Delta for Reportes De Gestión

## ADDED Requirements

### Requirement: Every Reportes De Gestión Route Has An Export Sibling Under The Same Policy

The nine existing `/api/reportes/*` report routes (`ventas/resumen`,
`ventas/por-punto-venta`, `ventas/por-vendedor`, `ventas/por-medio-pago`,
`articulos/top`, `compras/por-proveedor`, `gastos/resumen`) MUST each expose
a `GET {ruta}/export?formato=xlsx` sibling, following the
`exportacion-de-reportes` contract, inheriting `Politicas.LecturaDeReportes`
by co-location.

#### Scenario: A ventas resumen export matches its endpoint
- GIVEN `GET /api/reportes/ventas/resumen` reports net sales of `$700` for a
  period
- WHEN `GET /api/reportes/ventas/resumen/export?formato=xlsx` is requested
  for the same period
- THEN the workbook's net sales value equals `$700`

#### Scenario: A Vendedor is rejected from any reportes-de-gestión export
- GIVEN a user with role Vendedor
- WHEN they call `GET /api/reportes/ventas/resumen/export?formato=xlsx`
- THEN the response is `403`

### Requirement: Existencias Report Joins Stock To Artículos Under The Same Gate

`GET /api/reportes/stock/existencias` MUST return stock joined to
articulos for a punto de venta, gated by `Politicas.LecturaDeReportes` like
every other reportes-de-gestión route, and MUST expose an `/export` sibling
under the `exportacion-de-reportes` contract.

#### Scenario: Existencias needs no idArticulo, unlike GET /api/stock
- GIVEN a punto de venta with 40 stocked articulos
- WHEN `GET /api/reportes/stock/existencias?idPuntoVenta=7` is requested
- THEN it returns all 40 rows with no `idArticulo` parameter required

#### Scenario: A Supervisor exports existencias
- GIVEN a user with role Supervisor
- WHEN they call `GET /api/reportes/stock/existencias/export?formato=xlsx`
- THEN the response is `200` with a deterministic filename
