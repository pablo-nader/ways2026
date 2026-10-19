# Reporte de ventas por artículo

## Objective

A back-office report of units and amounts sold per article in a date range, filterable by
área, grupo, categoría and artículo (any combination), e.g. "which cigarettes and how many did
I sell this week".

## Problem / why

`/api/reportes/articulos/top` already nets quantity and total per article in a range, but only
filters by empresa and PV and is shown only as a dashboard panel. The owner needs a dedicated,
filterable, exportable screen.

## Scope

- Backend: new read model `GET /api/reportes/ventas/por-articulo` (+ `/export` XLSX) in
  `ServicioDeReportesDeArticulos`, reusing the `top` base query (Clase Venta, not Anulado,
  `SinEspejosFiscales`, PVs of the empresa, `RangoDeReporte` UTC bounds).
- Filters: `idEmpresa` (required), `idPuntoVenta?`, `desde`, `hasta`, `idArea?`, `idGrupo?`,
  `idCategoria?` (includes descendants), `idArticulo?`. Classification is the article's CURRENT
  área/grupo/categoría (join to `articulos`), same criterion as the articles report.
- Rows: article id, code, description, área/grupo/categoría labels, cantidad, total; ordered by
  total desc; grand totals. Notas de crédito net out (signed lines). No cost data.
- Web: page `/reportes/ventas-por-articulo` under the Reportes menu (`puedeVerReportes`), with
  date range, empresa/PV, the four filters, table with totals row, and export button.

## Constraints

- `LecturaDeReportes` (Supervisor + Admin). Never expose `CostoUnitario`.
- Soft-deleted catalog FK = NULL (`dangling-fk-read-models`).
- Remitos/presupuestos are out (separate tables, not sales).
- Unit tests + integration tests for the endpoint; Vitest for the page and helpers.

## Delivery strategy

`ask-on-risk`. Forecast ~700 authored lines over two tasks; chain `stacked-to-main` (one PR per
task) per the owner's standing preference.

## Tasks

- [x] T1 — Backend read model + endpoint + export + tests. Route: delegated (2+ non-trivial files). Evidence: 19 new integration tests + 270 Reportes/Exportacion/Espejo tests green, Ways.Application.Tests 1042 green, SuperficieDeAutorizacion 5 green; mutation proof on category descendants and group filter. Commit: see git log (feat(reportes): ventas por artículo).
- [x] T2 — Web page, menu entry, route, API client + Vitest. Route: delegated (2+ non-trivial files). Evidence: build (tsc -b + vite) OK; oxlint 0 errors; new tests green (reportes.test.ts, ReporteDeVentasPorArticulo.test.tsx, menu.test.ts: 101 passed); full web suite 7540 passed, 1 failed in SeccionDeCuentas.test.tsx (fixed fixture date vs real clock, untouched by this task, fails in isolation). `/reportes` is already in RutasDeLaSpa, no .NET change.
- [x] T3 — Follow-ups from judgment-day of #458/#460. Route: inline (one service file + one page, already understood). Backend: the área/grupo/categoría article subset uses `IgnoreQueryFilters(["BajaLogica"])` (named filter, ADR-6: the Tenant filter and RLS stay) as an `IQueryable` subquery instead of a materialized id list, so a soft-deleted article sold in the period keeps appearing under the filters. Web: "Actualizando…" status + `aria-busy` on the stale table while a new filter loads; an invalid range clears the previous error; the article picker searches with `incluirEliminados`; the stale-response test resolves inside `act`. Evidence: ReportesVentasPorArticuloTests 20/20, Ways.Application.Tests 1042 green, page tests 12/12; mutations (restore the BajaLogica filter; drop `setError('')`; `incluirEliminados` false; hide the indicator; drop the generation gate on success) each failed exactly their test, reverted.

## Acceptance criteria

- Filtering by grupo "Cigarrillos" for a week lists each cigarette article with net units and
  amount; combining filters intersects them; NC lines subtract; anuladas and fiscal mirrors excluded.
- Other tenant's empresa → 404; PV not in empresa → 400.

## Checks

- `dotnet test` unit + integration (reportes), structural guard tests.
- `npm run test` (Ways.Web) for the new page.

## Progress

- Exploration done (2026-10-18).
- T1 done (2026-10-18): `GET /api/reportes/ventas/por-articulo` and `/export`; no `Ways.UnitTests` project exists, unit coverage lives in `Ways.Application.Tests`.

## Next step

T1 shipped in #458, T2 in #460 (judgment-day APPROVED). Follow-ups in #461.
T3 commit 0e8990ae, judgment-day APPROVED (no CRITICAL). Open follow-up, flagged as WARNING by both judges: a soft-deleted article matched by a classification filter renders with null code and labels ("Sin asignar"), because the label lookup still applies `BajaLogica`.
