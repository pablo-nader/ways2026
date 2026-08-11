# Tablero Specification

## Purpose

Defines the web dashboard (`src/Ways.Web/src/paginas/Tablero.tsx`) that
consumes `/api/reportes/*`: legacy G1 parity first, then breakdown panels,
a role-gated margin panel, and the PROVISIONAL commission card. Charts
render through Recharts, contained behind `componentes/graficos/`.

## Requirements

### Requirement: Tablero Covers Legacy G1 Parity By Default

On load, `Tablero` MUST default to the last 7 days and render: a ventas
time series, a gastos time series, net sales total, gastos total, and
`ticket_promedio` — matching legacy screen G1's scope plus ticket promedio.

#### Scenario: Default load shows the last 7 days
- GIVEN a Supervisor opens `Tablero` with no filters set
- WHEN the page finishes loading
- THEN the date range shown is `[hoy - 6 días, hoy]` and both ventas and
  gastos totals are visible

### Requirement: Recharts Is Contained To componentes/graficos

Recharts components MUST be imported only inside
`src/Ways.Web/src/componentes/graficos/`. Pages and panels MUST consume the
project's own `<GraficoDeBarras>` / `<GraficoDeLineas>` wrapper components,
never `recharts` directly.

#### Scenario: No page imports recharts directly
- GIVEN the full `src/Ways.Web/src/paginas/` tree
- WHEN its imports are inspected
- THEN none of them import from `recharts` — only from
  `componentes/graficos/`

### Requirement: Breakdown Panels Share Range And Granularity Controls

`Tablero` MUST expose `desde`/`hasta`/`granularidad` controls that drive
every panel (ventas por punto de venta, por vendedor, por medio de pago,
top artículos) from the same selected period — no panel MUST fetch its own
independent range.

#### Scenario: Changing granularity re-buckets only the two G1 series
- GIVEN `Tablero` loaded with `granularidad = dia`
- WHEN the user switches to `semana`
- THEN the ventas series and the gastos series re-fetch bucketed by week;
  the four breakdown panels (por punto de venta, por vendedor, por medio de
  pago, top artículos) do not re-fetch on this change — each row is already
  a period subtotal with no time bucket

### Requirement: Margin Panel Is Invisible, Not Disabled, For Non-Admin

The rentabilidad panel MUST be absent from the rendered DOM for a
Supervisor or Vendedor session — not rendered-and-disabled, not
rendered-with-a-blurred-value. For an Admin session, whenever margin
coverage is below 100% (any line excluded as estimated or skipped as
unknown), the panel MUST show a coverage banner stating the excluded and
unknown revenue; a bare margin percentage MUST NOT be shown alone.

#### Scenario: Supervisor never sees the margin panel
- GIVEN a Supervisor session
- WHEN `Tablero` renders
- THEN no DOM node for the rentabilidad panel exists — not `display:none`,
  absent

#### Scenario: Admin sees a coverage banner under partial coverage
- GIVEN an Admin session where the period's margin coverage is 80%
  included, 15% excluded as estimated, 5% skipped as unknown
- WHEN the rentabilidad panel renders
- THEN it shows the margin figure together with a banner stating "15%
  estimado excluido, 5% de costo desconocido" — never the margin number
  alone

### Requirement: Comisiones Card Is Labelled PROVISIONAL

The commission card MUST display a visible "PROVISIONAL" label and MUST be
rendered only for an Admin session, consistent with `LecturaDeRentabilidad`.

#### Scenario: The provisional label is always visible with the card
- GIVEN an Admin session with `comision_porcentaje` configured
- WHEN the comisiones card renders
- THEN the text "PROVISIONAL" is visible alongside the computed amounts
