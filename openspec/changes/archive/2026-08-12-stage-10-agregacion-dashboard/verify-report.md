## Verification Report

**Change**: stage-10-agregacion-dashboard
**Version**: N/A (single-shot stage, no prior spec version)
**Mode**: Standard (Strict TDD not indicated for this project's cached testing capabilities)

### Completeness

| Metric | Value |
|--------|-------|
| Tasks total (10 slices) | 108 |
| Tasks complete (checked, code-verified) | 106 |
| Tasks incomplete (unchecked, but code-verified complete on main) | 2 (4.1, 5.5) |

All 10 slices are merged to main (PRs #76-#86, ad00c3a).

### Build & Tests Execution

**.NET tests**:
```text
dotnet test tests/Ways.Domain.Tests        -> 394 passed (baseline 394 - MATCH)
dotnet test tests/Ways.Application.Tests   -> 219 passed (baseline 219 - MATCH)
dotnet test tests/Ways.IntegrationTests    -> 823 passed (baseline 823 - MATCH, real Postgres via testcontainer)
```

**Web tests**:
```text
cd src/Ways.Web && npx vitest run
Test Files  28 passed (28)
Tests       476 passed (476)   (baseline 476 - MATCH)
```

**Lint**: npm run lint clean except one pre-existing unrelated warning (AuthContext.tsx fast-refresh advisory).

**Build (web)**: npm run build succeeded. Bundle 883 KB (237 KB gzip) - see SUGGESTION-1.

**Migration gate**: dotnet ef migrations has-pending-model-changes -> "No changes have been made to the model since the last migration." 18 migration files; last is 20260811033540_CostoCongeladoEnVentaEtapa9.cs (stage 9). GATE HOLDS.

### Spec Compliance Matrix

#### reportes-de-gestion (13 scenarios)

| Requirement | Scenario | Test | Result |
|---|---|---|---|
| LecturaDeReportes Policy | Vendedor rejected on every reportes route | ReportesAutorizacionTests.UnVendedorEsRechazadoEnLasNueveRutas (x9 routes) | COMPLIANT |
| LecturaDeReportes Policy | Supervisor accepted on volume reports | ReportesAutorizacionTests.UnSupervisorEsAceptadoEnLasSieteRutasSinLecturaDeRentabilidad (x7) | COMPLIANT |
| Business-Day Bucketing | Late-evening sale lands on its own business day | ReportesVentasResumenTests.UnaVentaALas2230ArtBucketeaEnDiasDistintosSegunLaZonaConfigurada + RangoDeReporteTests.UnaVentaDeLas2230EnArtQuedaDentroDelRangoDelMismoDiaLocal | COMPLIANT |
| Business-Day Bucketing | Weekly bucket starts on Monday | RangoDeReporteTests.LosBucketsSemanalesArrancanElLunes | COMPLIANT |
| Net Sales Has No Sign Branch | NCX reduces net sales without a sign branch | ReportesVentasResumenTests.UnaNcxReduceElNetoSinAlterarElTicketPromedioNiLaCantidadDeTx | COMPLIANT |
| Net Sales Has No Sign Branch | Anulado/soft-deleted/cross-tenant excluded | ReportesVentasResumenTests.UnComprobanteAnuladoNuncaApareceEnElResumen / UnaFilaSoftDeletedNuncaApareceEnElResumen / UnaFilaDeOtroTenantNuncaApareceEnElResumen | COMPLIANT |
| Ticket Promedio Excludes NCX | NCX excluded from both sides | same as above (UnaNcxReduceElNetoSin...) | COMPLIANT |
| Ventas Breakdown Endpoints | Grouping by vendedor sums independently | ReportesVentasPorDimensionTests.PorVendedorSumaCadaEmpleadoDeFormaIndependiente | COMPLIANT |
| Top Articulos | NCX line reduces its article's ranking figures | ReportesArticulosTopTests.ElTopCoincideConElCalculoAManoYUnaNcxReduceLaFiguraDelArticulo | COMPLIANT |
| Compras Bucketed By Fecha De Recepcion | Borrador compra never appears | ReportesEgresosTests.UnaCompraBorradorConFechaDeRecepcionNuncaApareceEnElReporteDeCompras | COMPLIANT |
| Gastos Resumen | Soft-deleted gasto excluded | ReportesEgresosTests.UnGastoSoftDeletedNuncaApareceEnElResumenDeGastos | COMPLIANT |
| Raw SQL Soft-Delete/Estado | Soft-deleted row with inflated amount never appears | per-endpoint soft-delete tests across all 6 integration test files (4-test pattern) | COMPLIANT |
| Tenant Isolation Via RLS | Cross-tenant row absent from every report | per-endpoint cross-tenant tests across all 6 integration test files | COMPLIANT |

13/13 compliant.

#### rentabilidad-y-comisiones (9 scenarios)

| Requirement | Scenario | Test | Result |
|---|---|---|---|
| LecturaDeRentabilidad Admin Only | Supervisor rejected on rentabilidad | RentabilidadTests.UnSupervisorEsRechazadoDeLaRentabilidad + ReportesAutorizacionTests.UnSupervisorEsRechazadoEnRentabilidadYComisiones | COMPLIANT |
| LecturaDeRentabilidad Admin Only | Admin accepted | RentabilidadTests.UnAdminLeeLaRentabilidad | COMPLIANT |
| Margin Excludes Estimated By Default | Backfilled-estimated line excluded by default | RentabilidadTests.UnaLineaEstimadaSeExcluyePorDefectoYSeIncluyeSoloConElOptInExplicito | COMPLIANT |
| Margin Excludes Estimated By Default | Same line included with explicit opt-in | same test | COMPLIANT |
| NULL Cost Never Zero + Coverage Mandatory | Unknown-cost line skipped, not zeroed | RentabilidadTests.UnaLineaDeCostoDesconocidoSeSalteaDelMargenYSeReportaAparteNuncaComoCero | COMPLIANT |
| NULL Cost Never Zero + Coverage Mandatory | Coverage reflects a mixed period | RentabilidadTests.LaCoberturaReflejaUnPeriodoMixtoDeCostoRealEstimadoYDesconocido | COMPLIANT |
| Comisiones Provisional | Default rate yields zero commission | ReportesComisionesTests.SinParametroConfiguradoLaTasaDefaultEsCeroYTodaComisionEsCero | COMPLIANT |
| Comisiones Provisional | Configured rate computes non-zero, labelled PROVISIONAL | ReportesComisionesTests.ConTasaConfiguradaLaRespuestaSigueEtiquetadaProvisional | COMPLIANT |
| Comisiones Provisional | Nothing written to the database | ReportesComisionesTests.LlamarComisionesNoEscribeNingunaFilaNueva | COMPLIANT |

9/9 compliant.

#### parametros-operativos (4 scenarios)

| Requirement | Scenario | Test | Result |
|---|---|---|---|
| Known Keys | zona_horaria resolves to default with no configured row | ResolucionDeParametrosTests.ZonaHorariaResuelveASuDefaultSinFilasConfiguradas | COMPLIANT |
| Known Keys | comision_porcentaje defaults to off (0) | ResolucionDeParametrosTests.ComisionPorcentajeResuelveACeroSinFilasConfiguradas | COMPLIANT |
| First String-Typed Parametro | Quoted IANA identifier accepted, round-trips | ServicioDeParametrosTests.EstablecerAsyncAceptaZonaHorariaQuoteadaYLaDevuelveVerbatim | COMPLIANT |
| First String-Typed Parametro | Unquoted value rejected at write time | ServicioDeParametrosTests.EstablecerAsyncRechazaZonaHorariaSinComillas | COMPLIANT |

4/4 compliant. Also over-tested beyond the spec's two scenarios: null-deserialization rejection, non-IANA rejection, Windows-id rejection, empty-string rejection, and the web select component test - all green.

#### tablero (6 scenarios)

| Requirement | Scenario | Test | Result |
|---|---|---|---|
| G1 Parity By Default | Default load shows the last 7 days | Tablero.test.tsx: "por defecto carga el rango de los ultimos 7 dias y muestra ventas, gastos y ticket promedio" | COMPLIANT |
| Recharts Contained | No page imports recharts directly | none - verified only by source inspection (grep shows recharts imported solely under componentes/graficos/); no automated regression test | UNTESTED (see WARNING-1) |
| Breakdown Panels Share Range/Granularity | Changing granularity re-buckets every panel | none - scenario is stale by design | KNOWN DRIFT - pre-recorded, not re-flagged (see note below) |
| Margin Panel Invisible For Non-Admin | Supervisor never sees the margin panel (no DOM node, no fetch) | Tablero.test.tsx rentabilidad describe block (Supervisor-absence + zero-fetch assertion) | COMPLIANT |
| Margin Panel Invisible For Non-Admin | Admin sees coverage banner under partial coverage | Tablero.test.tsx - 4 coverage-banner-state tests (100% / partial / all-unknown / empty) | COMPLIANT |
| Comisiones Card Labelled PROVISIONAL | PROVISIONAL label always visible with the card | Tablero.test.tsx: "Card de comisiones, PROVISIONAL" describe block | COMPLIANT |

4/6 fully compliant, 1 pre-recorded intentional drift (excluded from scoring per the orchestrator's instruction), 1 untested-but-true architectural invariant (WARNING-1).

Note on the stale scenario (per instructions, NOT scored as CRITICAL): specs/tablero's "Changing granularity re-buckets every panel" does not match shipped behavior - granularidad only drives the two G1 series (ventas/resumen, gastos/resumen); the four breakdown panels (por PV / vendedor / medio de pago / top articulos) are period-total subtotals with no time-bucketing and therefore no granularidad parameter (src/Ways.Web/src/api/reportes.ts:41 documents this explicitly; ReportesEndpoints.cs's four breakdown routes do not accept the parameter; design.md's own Endpoints section states "granularidad only on the two series"). This was recorded at slice-8 close (tasks.md, archive-time reconciliation note above Slice 9) for a spec amendment at archive. Confirmed still accurate on main.

Overall spec compliance: 30/32 scored scenarios compliant (93.75%), 1 pre-recorded intentional spec/code drift excluded from scoring, 1 untested-but-verified-true structural invariant.

### Correctness (Static Evidence) - Nine Proposal Decisions

| Decision | Status | Notes |
|---|---|---|
| 1. Direct SQL, no matviews (LectorDeSerieTemporal only raw) | Implemented | LectorDeSerieTemporal.cs is the only raw-ADO.NET surface; all other reports are LINQ GroupBy |
| 2. zona_horaria parametro, PV->empresa->default, business-day cut | Implemented | ParametroConocido.cs registers ZonaHoraria; RangoDeReporte.cs + ResolverZonaAsync in each service |
| 3. Two policies, margin Admin-only | Implemented | Politicas.cs - LecturaDeReportes = Supervisor+Admin, LecturaDeRentabilidad = Admin only, verified against spec text |
| 4. Recharts contained | Implemented (structurally true) | Only 2 files under componentes/graficos/ import recharts; no automated guard (WARNING-1) |
| 5. Comisiones provisional, last, droppable | Implemented | Comisiones record always carries Provisional = true; no persistence path; default rate 0 |
| 6. API-first then web, G1 parity first | Implemented | Slice order 1-5 (API) then 6-10 (web), G1 parity is slice 7 |
| 7. Per-report endpoints, no generic query surface | Implemented | ReportesEndpoints.cs - 9 explicit MapGet routes, typed request/response records in Contratos.cs |
| 8. Recargo por medio de pago NOT activated | Implemented | pagos_comprobante.importe read as-is in por-medio-pago; no write-path change found in the diff |
| 9. Aggregation semantics (net-no-sign, TX-only ticket promedio, compras by fecha_recepcion+confirmada, margin excludes estimated by default) | Implemented | See Spec Compliance Matrix above - every clause has a covering, passing test |

### Coherence (Design)

| Decision | Followed? | Notes |
|---|---|---|
| ADR-1: LINQ GroupBy default, raw SQL only in LectorDeSerieTemporal | Yes | Confirmed - articulos/top, rentabilidad, por-punto-venta/vendedor/medio-pago, compras/por-proveedor are all LINQ |
| ADR-7: LecturaDeReportes AND LecturaDeRentabilidad composition | Yes | ReportesEndpoints.cs stacks LecturaDeRentabilidad on /rentabilidad and /comisiones only; proven by ReportesAutorizacionTests |
| ADR-9: signo > 0 as TX/NCX discriminator | Yes | Confirmed in ServicioDeReportesDeVentas.cs's por-medio-pago sign application (mutation-tested per tasks.md 3.4) |
| ADR-10: Top articulos labels from line snapshot, never re-joins articulos | Yes | ServicioDeReportesDeArticulos.cs:58-62 - explicit comment, OrderByDescending(Fecha).First().Descripcion |
| ADR-11: Recharts containment | Yes (structurally) | See WARNING-1 - true today, unguarded going forward |
| ADR-12: zona_horaria ABM type-awareness + ValidarTipo hardening | Yes | Parametros.tsx renders select for zona_horaria; ValidarTipo rejects null and validates IANA id |

### Issues Found

**CRITICAL**:
1. tasks.md checkboxes 4.1 and 5.5 are unchecked despite the underlying work being complete, tested, and merged to main.
   - Task 4.1 (ObtenerTopArticulosAsync / GET /articulos/top) was explicitly deferred in the recorded apply batch ("NOT done in this batch... /articulos/top remains open") but was subsequently implemented in commit 0ea4c38 (feat(reportes): exponer GET /api/reportes/articulos/top sin costo ni margen) - see src/Ways.Application/Reportes/ServicioDeReportesDeArticulos.cs and tests/Ways.IntegrationTests/ReportesArticulosTopTests.cs (8 tests, all green). Task 4.5's checkbox is [x] but its own body text still reads "NOT done: depends on 4.1 (out of scope)" - a second, contradictory stale note on the same slice.
   - Task 5.5 (complete ReportesAutorizacionTests for the full 9-route matrix) was marked "NOT DONE AS SPECIFIED" in slice 5's parallel-worktree note, with an explicit instruction for "the orchestrator to reconcile... when merging slices 3/4/5." That reconciliation happened in slice 10, task 10.6, which created the dedicated, parameterized ReportesAutorizacionTests.cs covering all 9 routes x 4 roles (36 tests, all green, confirmed against Politicas.cs).
   - Impact: no missing functionality - both items are implemented and covered by passing tests on main. This is a pure documentation-sync gap in tasks.md, but per the verify hard rule ("Unchecked implementation task is CRITICAL and blocks archive readiness"), it must be corrected - check off 4.1 and 5.5, and strike or update 4.5's stale "NOT done" prose - before archive.

**WARNING**:
1. specs/tablero's "Recharts Is Contained To componentes/graficos" scenario has no automated regression test. It is true today (only GraficoDeLineas.tsx and GraficoDeBarras.tsx import recharts; Tablero.test.tsx mocks the module rather than importing it directly), but nothing would fail CI if a future page imported recharts directly. Recommend a lightweight test (e.g. an oxlint/import-boundary rule, or a colocated test that greps src/paginas/** for a recharts import) before or shortly after archive.
2. Documentation drift beyond the two CRITICAL checkboxes: several tasks.md entries record deviations accurately in prose (EF-translation anonymous projections at 3.1, PorCategoria always-present at 5.2, granularidad narrowing at 8.2, porArticulo mirrored-not-rendered at 9.2, panel-independence hook at 7.5/8.2, comisiones record shapes at 10.1) - all independently verified against the code in this pass and found accurate. No action needed; listed here for the archive report's deviation ledger.

**SUGGESTION**:
1. The production web bundle is 883 KB (237 KB gzip), flagged by vite build as exceeding the 500 KB chunk-size guidance. Recharts is the most likely contributor. Not a functional defect and out of this stage's scope to fix, but worth a code-splitting pass (e.g. React.lazy for Tablero) in a future stage since the dashboard is not on the POS's critical path.
2. specs/tablero's "Changing granularity re-buckets every panel" scenario should be rewritten at archive time to match shipped behavior (granularidad drives only the two G1 series; breakdown panels re-fetch on range/PV changes but not on granularity, since they carry no time bucket) - already flagged for archive in tasks.md, restated here as a concrete action item for the archive phase.

### Verdict
**PASS WITH WARNINGS**

Both suites (.NET x3 + vitest) are 100% green at exactly their recorded baselines (394 / 219 / 823 / 476), the no-schema-change gate holds (18 migrations, last is stage 9), all nine proposal decisions are implemented as resolved, the role matrix is byte-for-byte correct against Politicas.cs, and 30/32 spec scenarios have a passing covering test (the 32nd is a pre-recorded, intentional spec/code drift already queued for archive-time amendment). The one CRITICAL finding is a tasks.md documentation-sync issue, not a functional or test gap - both referenced pieces of work exist, are tested, and pass on main. Recommend fixing the two checkboxes (and 4.5's stale prose) before archive; everything else can be carried into the archive report as recorded, verified deviations.
