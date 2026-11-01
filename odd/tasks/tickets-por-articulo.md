# Tickets por artículo (drill-down del reporte de ventas por artículo)

## Objective

From the sales-by-article report (`/reportes/ventas-por-articulo`), clicking an article opens
the list of tickets (comprobantes de venta) where it was sold in the same period and filters,
and each ticket opens its detail.

## Problem / why

The owner sees totals per article but cannot audit which sales make them up. No back-office
screen shows a single sale today: the only detail view (`ModalDetalleDeVenta`) lives inside the
POS shift screen.

## Scope

- Backend: `GET /api/reportes/ventas/por-articulo/{idArticulo}/tickets` with the same period,
  empresa and PV parameters as the report. One row per comprobante that has lines of that
  article, same inclusion rules as `LineasVendidas` (no anulled, no fiscal mirrors, NC subtract).
- Web: article row → tickets modal; ticket → back-office sale detail modal (read only, no
  reprint or anulación), opened by URL query parameters.
- No schema changes.

## Decisions

- Drill-down inside the existing report, chosen by the owner (2026-11-01) over a separate flat report.
- Sale detail reuses `GET /api/ventas/{id}` (Supervisor and Admin already allowed).

## Tasks

- [x] T1 — Backend endpoint + integration tests. Route: delegated (writer trigger, 2+ non-trivial files). Branch `pnader/tickets-por-articulo-t1`.

  Evidence T1 (2026-11-01):
  - RED: `dotnet build tests/Ways.IntegrationTests -c Release` failed with CS0246 (`TicketsDeArticulo` missing) before the implementation.
  - GREEN: `dotnet test tests/Ways.IntegrationTests -c Release --filter "FullyQualifiedName~ReportesTicketsPorArticulo|FullyQualifiedName~ReportesVentasPorArticulo|FullyQualifiedName~SuperficieDeAutorizacion"` -> 37 passed, 0 failed.
  - `dotnet test tests/Ways.Application.Tests -c Release` -> 1637 passed. The guard `TodoLectorExcluyeLosEspejosFiscalesOEstaJustificado` caught the header query without `SinEspejosFiscales()`; fixed.
  - Mutation (scope predicate): replacing `idsPuntoVenta.Contains(...)` and the `idArticulo` filter in `LineasVendidas` with `true` made 3 of 12 tests fail (PV filter, other-tenant/inexistent article, per-ticket sum); reverted, all green.
  - Route and contract: `GET /api/reportes/ventas/por-articulo/{idArticulo:int}/tickets` (policy `LecturaDeReportes`), no pagination or cap.
  - Detail: `GET /api/ventas/{id}` is under `OperacionDePos` (Vendedor, Supervisor, Admin); no `RequiereDispositivo` on that route.

- [x] T2 — Web drill-down + sale detail modal + Vitest. Route: delegated (writer trigger). Branch `pnader/tickets-por-articulo-t2`, stacked on T1.

  Evidence T2 (2026-11-01):
  - `npx tsc -b --noEmit` exit 0; `npm run lint` without new findings (the date helper moved to `formato/fechas.ts`).
  - `npx vitest run` full suite: 306 files, 9385 tests passed. New: 12 in `ModalTicketsDeArticulo.test.tsx`, 6 in the report page test, 3 in `api/reportes.test.ts`.
  - Mutation: removing the per-effect stale-response gate turns "una respuesta vieja del mismo artículo no pisa la de los filtros nuevos" red.
  - Sale detail reuses the shared read-only `ModalDetalleDeVenta` (`componentes/DetalleDeVenta.tsx`); POS screens untouched.
  - URL params `articulo` and `ticket`; the report filters are not in the URL (a shared link uses the default filters).
  - Judgment-day T1 and T2: APPROVED in round one, no severe findings.

## Acceptance criteria

- Ticket totals for an article add up to the article's row in the report for the same filters.
- An article of another tenant/empresa or out of the PV scope returns an empty list.
- The modals follow `react-async-state` (gated responses, focus restore).

## Delivery

Strategy `ask-on-risk`, chain `stacked-to-main` (owner's standing preference). Forecast ~700 lines across two PRs.

## Progress

- 2026-11-01: exploration done, scope set.
