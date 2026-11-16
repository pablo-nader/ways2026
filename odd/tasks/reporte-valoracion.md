# Reporte de valoración (mercadería + dinero)

## Objective

A back-office report that answers "how much is what I have worth": merchandise valued at cost
and at sale price (positive stock only) plus money (caja general cash and treasury accounts),
for one empresa, today and as an evolution over time. It shows whether money grew while stock
shrank, or the opposite.

## Problem / why

The owner has no single view that adds stock value and money. The libro de tesorería shows money,
existencias shows quantities, nothing values stock or tracks the mix over time.

## Scope

- Backend: `GET /api/reportes/valoracion?idEmpresa&desde&hasta&granularidad` under
  `Politicas.LecturaDeReportes` (Supervisor, Admin). One point per bucket end (the last point is
  "now"), each with merchandise at cost, at sale price, cash, accounts total, money total and
  grand total at cost, plus coverage counters. The current point also carries per-account
  balances and a per-área breakdown of merchandise.
- Web: `/reportes/valoracion` page with KPI tiles, an evolution line chart (merchandise at cost,
  money, total), a composition view, per-account and per-área tables, coverage warnings. Nav link
  under reports.
- No schema changes.

## Decisions (owner delegated the design, 2026-11-15)

- Lives in Ways as a screen (owner choice over a one-off HTML report).
- Stock at instant D = SUM(`movimientos_stock.cantidad`) with `creado_el <= D`, per
  (articulo, sucursal) of the empresa's sucursales, clamped with `max(0, x)` per pair before
  adding ("solo cuenta stock positivo").
- Sale price at D = the empresa's default list (empresa default, else shared default) resolved
  at D through `ServicioDePrecios.PreciosVigentesEnLoteAsync` (real price history).
- Cost: there is no cost history. Current point uses `articulos.costo_nominal`. Past points use
  the last non-anulled purchase line cost of the article with comprobante date <= D, on the same
  basis as `costo_nominal`; without one, the current `costo_nominal` is used and the point is
  marked as estimated (count + value). NULL cost or missing price is never 0: it goes to coverage.
- Money at D = libro de tesorería balances (caja general cash + each account) as of D, computed
  from the sources like `ServicioDeLibroDeTesoreria`. Cash in the drawer of an open turno is not
  included (same as the libro); the UI says so.
- Range limit 366 days, at most 60 points; web default: last 12 months, monthly.

## Tasks

- [x] T1 — Backend: domain aggregation (pure, unit tested), service, endpoint, integration tests.
  Route: delegated (writer trigger, 2+ non-trivial files). Branch `pnader/reporte-valoracion-backend`.
- [ ] T2 — Web page, API client, route, nav link, Vitest tests. Route: delegated (writer trigger).
  Branch `pnader/reporte-valoracion-web`, stacked on T1.

## Acceptance criteria

- Negative stock pairs never subtract value; articles with stock but no cost/price are reported
  in coverage, not valued at 0.
- Points before D ignore movements, prices, purchases and treasury sources after D.
- Other-tenant or missing empresa answers 404; Vendedor gets 403.
- The page renders KPIs, chart and tables; loading/error states gated per `react-async-state`.

## Checks

- `dotnet build -c Release`, unit tests (`tests/Ways.Domain.Tests`, `tests/Ways.Application.Tests`),
  integration `tests/Ways.IntegrationTests` filtered to the new tests plus structural guards
  (`SuperficieDeAutorizacion`, guard tests per `structural-guard-tests`).
- Web: `npm run test`, `npm run build` (tsc) in `src/Ways.Web`.

## Delivery

Forecast ~900 authored lines → `stacked-to-main` (owner's standing choice): T1 PR, T2 PR on top.
Each slice passes judgment-day before its PR.

## Progress

- 2026-11-15: exploration done, document created.
- 2026-11-15 T1 (writer, delegated): code and tests written; verified against Postgres in a second pass. Evidence:
  - RED: `dotnet build tests/Ways.Domain.Tests -c Release` failed with CS0246 (`ArticuloAValorar`, `MercaderiaValorada` missing)
    before `CalculoDeValoracion` existed.
  - GREEN: `dotnet test tests/Ways.Domain.Tests -c Release` 2703 passed; `tests/Ways.Application.Tests` 1707 passed; `dotnet build -c Release` 0 errors.
  - Mutation (domain): replacing `Math.Max(0m, ...)` in `CalculoDeValoracion.StockPositivoPorArticulo` with the plain sum made 2 tests fail
    (`UnParNegativoSeAcotaACeroSinRestarleAlOtroLocal`, `FilasRepetidasDeUnMismoParSeSumanAntesDeAcotar`); reverted, 22 passed.
  - Integration (Docker 29.6.2): `dotnet test tests/Ways.IntegrationTests -c Release --filter "ReportesAutorizacion|LibroDeTesoreria|ReportesValoracion|SuperficieDeAutorizacion"`
    80 passed, 0 failed (first run needed one seeding fix: comprobantes_compra confirmed rows require numero_externo; no production bug).
  - Mutation (SQL): removing `m.CreadoEl <= corte` in `StockPositivoAsync` made 5 of 9 `ReportesValoracionTests` fail (incl.
    `LosPuntosPasadosIgnoranTodoLoPosteriorAlCorte`); reverted, 9 passed.
  - `valoracion` added to the `ReportesAutorizacionTests` role matrix with desde = hasta = yesterday (avoids `rango_en_el_futuro`).
  - Cost basis finding: `items_comprobante_compra.costo_unitario` is NOT the basis of `articulos.costo_nominal` (gross unit price before
    discount/IVA/impuesto interno). The purchase flow writes `CalculadorDeCompra.CalcularCostoEfectivoDesdeItem(total, cantidad, %iva,
    comprobante.DiscriminaIva, PreciosIncluyenIva, impuesto_interno)` for lines passing `ActualizaElCosto`, per family. The report
    reproduces exactly that, from lines of confirmed non-NC comprobantes with `FechaRecepcion <= corte`.
