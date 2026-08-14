# Delta for Reportes De Gestión

## MODIFIED Requirements

### Requirement: Existencias Report Joins Stock To Artículos Under The Same Gate

`GET /api/reportes/stock/existencias` MUST return stock joined to
articulos for a punto de venta, gated by `Politicas.LecturaDeReportes` like
every other reportes-de-gestión route, and MUST expose an `/export` sibling
under the `exportacion-de-reportes` contract. Each row MUST additionally
carry `minimo`, `reposicion`, and a derived `estado` — `bajo` when
`minimo IS NOT NULL AND cantidad <= minimo`, `sin_minimo` when `minimo IS
NULL`, otherwise `ok` — computed with the same boundary the
`reposicion-de-stock` capability uses for its alert, never a second
definition. `minimo`/`reposicion` on this report remain read-only; writing
them is a separate endpoint (`PUT /api/stock/minimos`) under a separate
policy (`Politicas.GestionDeCatalogo`).
(Previously: silent on `minimo`, `reposicion` and `estado` — those columns
were dormant and unread until this stage.)

#### Scenario: Existencias needs no idArticulo, unlike GET /api/stock
- GIVEN a punto de venta with 40 stocked articulos
- WHEN `GET /api/reportes/stock/existencias?idPuntoVenta=7` is requested
- THEN it returns all 40 rows with no `idArticulo` parameter required

#### Scenario: A Supervisor exports existencias
- GIVEN a user with role Supervisor
- WHEN they call `GET /api/reportes/stock/existencias/export?formato=xlsx`
- THEN the response is `200` with a deterministic filename

#### Scenario: An articulo at or below its minimo classifies bajo
- GIVEN articulo 10 has `minimo = 5, cantidad = 5`
- WHEN existencias is requested
- THEN articulo 10's row shows `estado = bajo`

#### Scenario: An articulo with no minimo classifies sin_minimo, never bajo
- GIVEN articulo 11 has `minimo = NULL, cantidad = 0`
- WHEN existencias is requested
- THEN articulo 11's row shows `estado = sin_minimo`, not `bajo`

#### Scenario: An articulo above its minimo classifies ok
- GIVEN articulo 12 has `minimo = 5, cantidad = 20`
- WHEN existencias is requested
- THEN articulo 12's row shows `estado = ok`

#### Scenario: A Supervisor reads the reorder columns but cannot write them
- GIVEN a user with role Supervisor
- WHEN they call `GET /api/reportes/stock/existencias`
- THEN the response includes `minimo`/`reposicion`/`estado`; WHEN the same
  Supervisor calls `PUT /api/stock/minimos` the response is `403`

#### Scenario: The existencias export carries the same reorder columns
- GIVEN `GET /api/reportes/stock/existencias?idPuntoVenta=7` reports
  articulo 10 with `minimo = 5, reposicion = 20, estado = bajo`
- WHEN `GET /api/reportes/stock/existencias/export?formato=xlsx` is
  requested for the same parameters
- THEN the workbook's row for articulo 10 carries the same three values
