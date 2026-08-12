# Delta for Tesorería

## ADDED Requirements

### Requirement: Tesorería Book Has A Read/Listing Endpoint

A new `GET` endpoint MUST list `movimientos_tesoreria` rows for a punto de
venta and date range, ordered by chain (ascending id, matching
`inicio → final` continuity), gated by `Politicas.LecturaDeReportes`
(Supervisor + Admin). This adds a read surface only; the existing "Manual
Tesorería Entries Are Out Of Scope" requirement is unchanged.

#### Scenario: A Vendedor is rejected from the tesorería book
- GIVEN a user with role Vendedor
- WHEN they call the tesorería book endpoint
- THEN the response is `403`

#### Scenario: Book preserves chain order
- GIVEN three chained `movimientos_tesoreria` rows with `final` values 60,
  100, 145
- WHEN the book is requested for that punto de venta
- THEN rows return in that order, each row's `inicio` equal to the
  previous row's `final`

#### Scenario: The book has an export sibling equal to its JSON

- GIVEN an Admin requesting the tesorería book export
- WHEN `GET .../tesoreria/export?formato=xlsx` is called for the same
  filters as the JSON book
- THEN the workbook's rows equal the JSON book's rows
