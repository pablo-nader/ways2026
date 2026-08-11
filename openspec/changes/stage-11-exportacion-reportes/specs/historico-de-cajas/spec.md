# Histórico De Cajas Specification

## Purpose

Defines the G2 (Ver Cajas) and G3 (Caja General / Z) read surfaces: closed-
turno listing with totals from already-persisted `arqueos_turno` rows, turno
detail as the existing `ResumenDeTurno` plus its ticket and gasto listings,
the tesorería book, and the role split between the cajero's own close and
management's cross-turno views (proposal decisions 5, 6, doc-01 sección G).

## Requirements

### Requirement: G2 Histórico Lists Closed Turnos Only, With Totals From Persisted Arqueos

`GET /api/reportes/cajas` MUST list turnos with `estado = cerrado` only —
open turnos MUST NEVER appear. Each row's totals (Σ `importe_esperado`, Σ
`importe_declarado`, Σ `diferencia`) MUST be computed by summing that
turno's already-persisted `arqueos_turno` rows, never by re-running the live
resumen-parcial derivation.

#### Scenario: An open turno is excluded from the listing
- GIVEN punto de venta 7 has one turno `abierto` and one turno `cerrado`
- WHEN `GET /api/reportes/cajas` is requested for punto de venta 7
- THEN only the `cerrado` turno appears

#### Scenario: Listed totals equal the sum of the turno's arqueos
- GIVEN a closed turno with `arqueos_turno` rows totaling
  `importe_esperado = 2880`, `importe_declarado = 2850`
- WHEN it appears in the listing
- THEN its row shows `esperado = 2880`, `declarado = 2850`,
  `diferencia = -30`

### Requirement: G2 Detail Reuses ResumenDeTurno Plus Ticket And Gasto Listings

`GET /api/caja/turnos/{id}/detalle` MUST return the existing `ResumenDeTurno`
payload unchanged, plus two additional listings scoped by
`id_turno_caja = {id}`: the turno's `comprobantes_venta` and its `gastos` —
plain reads introducing no new derivation.

#### Scenario: Detail includes resumen, tickets, and gastos
- GIVEN a closed turno with 12 tickets and 3 gastos
- WHEN `GET /api/caja/turnos/{id}/detalle` is requested
- THEN the response contains the turno's `ResumenDeTurno`, a list of its 12
  tickets, and a list of its 3 gastos

### Requirement: G3 Tesorería Book Is A Chained, Paginated Read

The tesorería book endpoint MUST return `movimientos_tesoreria` rows for a
punto de venta and date range, ordered by their chain (`inicio → final`,
ascending id), with no aggregation beyond the persisted columns.

#### Scenario: Book preserves chain order
- GIVEN three chained `movimientos_tesoreria` rows with `final` values 60,
  100, 145 for punto de venta 7
- WHEN the book is requested for punto de venta 7
- THEN the rows return in that order, each row's `inicio` equal to the
  previous row's `final`

### Requirement: Role Split — Turno Detail Under OperacionDePos, Cross-Turno Views Under LecturaDeReportes

The turno detail / Z-report (`GET /api/caja/turnos/{id}/detalle` and its
export) MUST be gated by `Politicas.OperacionDePos`, the same policy as the
existing `GET /api/caja/turnos/{id}/resumen`. The G2 listing
(`GET /api/reportes/cajas`) and the G3 tesorería book, and both of their
exports, MUST be gated by `Politicas.LecturaDeReportes`
(Supervisor + Admin).

#### Scenario: A Vendedor downloads their own turno's Z-report
- GIVEN a Vendedor who just closed turno `412`
- WHEN they call `GET /api/caja/turnos/412/detalle/export?formato=xlsx`
- THEN the response is `200` with the turno's detail workbook

#### Scenario: A Vendedor is rejected from the G2 histórico listing
- GIVEN a user with role Vendedor
- WHEN they call `GET /api/reportes/cajas`
- THEN the response is `403`

#### Scenario: A Supervisor reads the G2 listing and the G3 book
- GIVEN a user with role Supervisor
- WHEN they call `GET /api/reportes/cajas` and the tesorería book endpoint
- THEN both requests succeed with `200`

### Requirement: G2 And G3 Endpoints Have Export Siblings Equal To Their JSON

`GET /api/reportes/cajas/export` and the tesorería book's export MUST
follow the `exportacion-de-reportes` contract (route co-location, row cap,
header block, no-re-query) and their figures MUST equal their JSON
counterpart's figures for identical filters.

#### Scenario: G2 listing export figures equal the JSON listing
- GIVEN `GET /api/reportes/cajas` returns 5 closed turnos for a period with
  a combined `diferencia` of `-120`
- WHEN `GET /api/reportes/cajas/export?formato=xlsx` is requested for the
  same period
- THEN the workbook's combined `diferencia` column sums to `-120`
