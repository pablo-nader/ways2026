# Rentabilidad Y Comisiones Specification

## Purpose

Defines the margin contract on top of stage 9's frozen, three-state
`costo_unitario`, and the provisional per-vendedor commission report. Both
are gated by `LecturaDeRentabilidad` (Admin only) — cost is the most
sensitive number in the system and this stage does not widen who sees it.

## Requirements

### Requirement: LecturaDeRentabilidad Policy Admits Admin Only

`Politicas.LecturaDeRentabilidad` MUST admit `RolConocido.Admin` only.
`Vendedor`, `Supervisor`, and `Root` MUST be rejected. It gates
`GET /api/reportes/rentabilidad` and `GET /api/reportes/comisiones`.

#### Scenario: Supervisor is rejected on rentabilidad
- GIVEN a user with role Supervisor
- WHEN they call `GET /api/reportes/rentabilidad`
- THEN the response is 403

#### Scenario: Admin is accepted
- GIVEN a user with role Admin
- WHEN they call `GET /api/reportes/rentabilidad`
- THEN the response is 200

### Requirement: Margin Excludes Estimated Cost Lines By Default

`margen` MUST be computed as `SUM(total - costo_unitario * cantidad)` over
`items_comprobante_venta` rows matching the net-sales filter, IVA-included
on both sides (binding from stage 9 decision 1). Lines with
`costo_es_estimado = true` MUST be excluded from the sum unless the caller
passes `incluirEstimados = true`.

#### Scenario: A backfilled-estimated line is excluded by default
- GIVEN a TX line with `total = 150`, `costo_unitario = 100`,
  `costo_es_estimado = true`
- WHEN `rentabilidad` runs without `incluirEstimados`
- THEN that line's $50 does not contribute to `margen`

#### Scenario: The same line is included with the explicit opt-in
- GIVEN the same line as above
- WHEN `rentabilidad` runs with `incluirEstimados = true`
- THEN that line's $50 contributes to `margen`

### Requirement: NULL Cost Is Never Treated As Zero, And Coverage Is Mandatory

Lines with `costo_unitario IS NULL` MUST be skipped from `margen` entirely
— never summed as zero. Every `rentabilidad` response MUST carry a
coverage payload: line count and revenue for lines included, lines
excluded as estimated, and lines skipped as unknown cost. A response
without coverage MUST NOT be returned.

#### Scenario: An unknown-cost line is skipped, not zeroed
- GIVEN a TX line with `total = 200`, `costo_unitario = NULL`
- WHEN `rentabilidad` runs for that period
- THEN `margen` does not treat the line as `costo = 0`, and the response's
  coverage reports it under `lineasDesconocidas` with its revenue

#### Scenario: Coverage reflects a mixed period
- GIVEN a period with 10 TX lines: 7 with real cost, 2 estimated, 1 unknown
- WHEN `rentabilidad` runs without `incluirEstimados`
- THEN coverage reports 7 lines included, 2 excluded as estimated, 1
  skipped as unknown, each with its own revenue subtotal

### Requirement: Comisiones Is A Provisional, Non-Persisted Report

`GET /api/reportes/comisiones` MUST compute, per `id_empleado`:
`comision = neto_vendido_por_empleado * comision_porcentaje`, where
`neto_vendido_por_empleado` uses the net-sales filter (TX totals net of
NCX, excluding anulados) and `comision_porcentaje` resolves from
`ParametroConocido` (punto de venta → empresa → default `0`). No
liquidación row MUST be persisted. The response and any UI surface MUST be
labelled **PROVISIONAL**.

#### Scenario: Default rate yields zero commission
- GIVEN no `comision_porcentaje` parametro row exists for the tenant
- WHEN `comisiones` runs for a vendedor with $10,000 net sales
- THEN their `comision = 0`, because the resolved default rate is `0`

#### Scenario: A configured rate computes a non-zero commission
- GIVEN `comision_porcentaje = 5` set at empresa level
- WHEN `comisiones` runs for a vendedor with $10,000 net sales
- THEN their `comision = 500`, and the response is labelled PROVISIONAL

#### Scenario: Nothing is written to the database
- GIVEN any call to `comisiones`
- WHEN the response is returned
- THEN no row is inserted or updated in any table — the endpoint is
  read-only, computed on the fly

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
