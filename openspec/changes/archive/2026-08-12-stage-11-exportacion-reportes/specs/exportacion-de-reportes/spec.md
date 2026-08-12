# Exportación De Reportes Specification

## Purpose

Defines the export contract end to end: the `/export` sibling-route
convention, the `formato` enum, policy inheritance by co-location, the row
cap and its refusal, file naming, the in-sheet header block, and the binding
invariant that an export's figures equal its source endpoint's figures for
the same parameters. This is the seam every exportable report, present and
future, is built on (proposal decisions 1, 3, 4, 7, 11).

## Requirements

### Requirement: Export Route Convention And Policy Inheritance By Co-Location

Every exportable report MUST expose `GET {ruta-del-reporte}/export`,
declared inside the same `MapGroup` as its source route, immediately after
it, with no separate authorization policy declared on the export route — the
gate is inherited structurally from co-location. `formato` MUST be a
required query parameter with a single legal value `xlsx` in v1; any other
value MUST be rejected with `400 formato_no_soportado`.

#### Scenario: An unsupported formato is rejected
- GIVEN `GET /api/reportes/ventas/resumen/export?formato=pdf`
- WHEN the request is validated
- THEN it is rejected with `400 formato_no_soportado`

#### Scenario: A caller authorized on the source route is authorized on its export
- GIVEN a Supervisor authorized on `GET /api/reportes/ventas/resumen`
- WHEN they call `GET /api/reportes/ventas/resumen/export?formato=xlsx`
- THEN authorization succeeds with no separate policy check

### Requirement: Row Cap Refuses, Never Truncates

Every export MUST count matching rows via `COUNT(*)` under the same filters
before generating any output. A count exceeding `25000` MUST be rejected
with `400 exportacion_demasiado_grande` naming the actual row count. No
export MUST ever return a file containing fewer rows than the filters match.

#### Scenario: A 25001-row request is refused with the count
- GIVEN a query whose filters match `25001` rows
- WHEN the export is requested
- THEN it is rejected with `400 exportacion_demasiado_grande` and the error
  payload states `25001`, and no file is generated

#### Scenario: An at-cap request succeeds
- GIVEN a query whose filters match exactly `25000` rows
- WHEN the export is requested
- THEN a workbook with `25000` data rows is returned

### Requirement: XLSX Response Contract And Deterministic Naming

Every export MUST respond with content-type
`application/vnd.openxmlformats-officedocument.spreadsheetml.sheet` and a
`Content-Disposition: attachment` header carrying both an ASCII `filename`
and an RFC 5987 `filename*`. The filename MUST follow
`{reporte}_{alcance}_{desde}_{hasta}.xlsx` with no timestamp or random
suffix, so identical requests produce identical names.

#### Scenario: Identical requests produce identical filenames
- GIVEN two identical export requests for the same report and parameters
- WHEN each is downloaded
- THEN both responses carry the same `Content-Disposition` filename

#### Scenario: Filename is deterministic and scoped
- GIVEN a ventas resumen export for punto de venta `3`, `2026-08-01` to
  `2026-08-12`
- WHEN the file is generated
- THEN the filename is `ventas_resumen_pv3_2026-08-01_2026-08-12.xlsx`

### Requirement: In-Sheet Header Block

Every workbook MUST carry a header block in rows 1-4 (empresa; punto de
venta or `"Todos"`; date range; generation instant with its zone and the
generating user), a blank row 5, and the table header at row 6. Any export
carrying an estimated-cost figure MUST repeat the stage-10 coverage block
inside that header.

#### Scenario: Header identifies scope and generator
- GIVEN a ventas resumen export for punto de venta `3`
- WHEN the workbook is opened
- THEN rows 1-4 state the empresa, `"PV 3"`, the date range, and the
  generation instant/zone/user, and the table header starts at row 6

#### Scenario: A cost-bearing export carries the coverage block
- GIVEN a rentabilidad export whose JSON response reports coverage of 7
  lines included, 2 estimated, 1 unknown
- WHEN the workbook is generated
- THEN its header block states the same three counts and their revenue
  subtotals

### Requirement: No Re-Query — Exported Figures Equal Endpoint Figures

Every export MUST map from the exact typed response record its source
endpoint returns — it MUST NOT issue an independent or second query. An
integration test per export MUST assert the exported figures equal the JSON
endpoint's figures for identical parameters.

#### Scenario: Export figures equal endpoint figures for identical params
- GIVEN `GET /api/reportes/ventas/resumen` returns net sales of `$700` for a
  period
- WHEN `GET /api/reportes/ventas/resumen/export?formato=xlsx` is requested
  for the same period
- THEN the workbook's net sales cell equals `$700`

### Requirement: Excel Library Containment

The XLSX library MUST be referenced from exactly one Infrastructure file. An
architecture test MUST assert no other file references it. The slice
adopting the library MUST record the full transitive package licence graph
in its PR description; any package outside MIT / Apache-2.0 / BSD / MS-PL
MUST reopen the library choice.

#### Scenario: A second reference to the library is flagged
- GIVEN a file outside the designated Infrastructure exporter references the
  XLSX library
- WHEN the architecture test runs
- THEN it fails

#### Scenario: Licence audit is recorded before the exporter ships
- GIVEN the slice introducing the XLSX package
- WHEN its PR is opened
- THEN the PR description enumerates every transitive package and its
  declared licence
