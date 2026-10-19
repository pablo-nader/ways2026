# Reporte de ventas por hora

## Objective

A back-office report that shows when the business sells the most: a weekday × hour heatmap
(7 rows × 24 one-hour columns) with the number in every cell, averaged over a chosen date range.

## Problem / why

No report groups sales by hour or weekday. The only time buckets are día/semana/mes
(`RangoDeReporte`, `LectorDeSerieTemporal`). The owner needs this view to plan staff and
opening hours.

## Scope

- Backend: new read model `GET /api/reportes/ventas/por-hora` in the reportes group
  (`LecturaDeReportes`). Params: `idEmpresa` (required), `idPuntoVenta?`, `desde`, `hasta`
  (`DateOnly`). It reuses `ExigirEmpresaAsync`, `ResolverPuntosDeVentaAsync`, `ResolverZonaAsync`
  and `RangoDeReporte.Crear(..., Granularidad.Dia, zona)`.
- Base set: `ComprobantesVenta.SinEspejosFiscales()`, Clase Venta, not Anulado, PVs of the
  empresa, `Fecha` within the UTC bounds of the range. Notas de crédito net out of the amount.
- Bucketing: weekday and hour of `Fecha` converted to the empresa/PV zone (`zona_horaria`).
- Per cell (weekday 1=Monday..7=Sunday, hour 0..23): total amount, ticket count, and the
  averages = value / number of times that weekday occurs in the range (inclusive). Cells with
  no sales are returned as zero, so the grid is always 7×24.
- Response totals: total amount, total tickets, occurrences per weekday, and the zone used.
- Web: page `/reportes/ventas-por-hora` under the Reportes menu (`puedeVerReportes`). It has
  date range, empresa/PV, a toggle between average amount and average tickets, and a heatmap
  table with the number in each cell, a color scale, and totals per row (weekday) and per column
  (hour).

## Constraints

- `LecturaDeReportes` (Supervisor + Admin). No cost data.
- Remitos/presupuestos out (separate tables).
- The timezone/bucketing clause ships with recorded mutation evidence (`mutation-proof-tests`).
- Integration tests for the endpoint; Vitest for the page and pure helpers (color scale,
  grid shaping).

## Delivery strategy

`ask-on-risk`. Forecast ~800 authored lines over two tasks; chain `stacked-to-main` (one PR per
task) per the owner's standing preference.

## Tasks

- [x] T1 — Backend read model + endpoint + tests. Route: delegated (2+ non-trivial files). Evidence: Domain.Tests Reportes 57 passed; IntegrationTests (ReportesVentasPorHora + ReportesAutorizacion + SuperficieDeAutorizacion) 71 passed; Application.Tests 1059 passed. Mutation: ConvertTime to UTC failed 7/14 ventas-por-hora tests (incl. Monday/23), `Count(Signo > 0)` -> `Count()` failed the NC test; both reverted, green.
- [ ] T2 — Web page, menu entry, route, API client, heatmap + Vitest. Route: delegated (2+ non-trivial files).

## Acceptance criteria

- A sale at 23:30 local on a Monday (02:30 UTC Tuesday) lands in Monday/23 when the zone is
  America/Argentina/Buenos_Aires.
- Two Mondays in the range with sales of 100 and 300 at 10:15 give an average of 200 in
  Monday/10; a Monday without sales still counts in the divisor.
- NC subtracts from the amount and does not count as a ticket; anuladas and fiscal mirrors are
  excluded.
- Other tenant's empresa → 404; PV not in empresa → 400; inverted or >366-day range → 400.

## Checks

- `dotnet test` integration (reportes + `ReportesAutorizacionTests`), structural guard tests.
- `npm run test` (Ways.Web) for the new page, helpers and menu; `npm run build`.

## Progress

- Exploration done (2026-10-19): pattern is `ServicioDeReportesDeArticulos.ObtenerVentasPorArticuloAsync`.
- T1 done: `ObtenerVentasPorHoraAsync` lives in that service (reuses its private helpers); pure grid in `Ways.Domain.Reportes.GrillaDeVentasPorHora`. Application has no Npgsql reference (no `AtTimeZone`), so (Fecha, Total, Signo) are materialized and converted with `TimeZoneInfo.ConvertTime`. NC = `tipos_comprobante.signo < 0`; ticket = `signo > 0`. Averages rounded to 2 decimals AwayFromZero.

## Next step

T2.
