# Verification Report

**Change**: stage-11-exportacion-reportes
**Version**: N/A (openspec, no numbered spec version)
**Mode**: Standard (Strict TDD not signaled for this run)
**Verified against**: `main` @ `9c4074b` (12/12 slices merged, PRs #87-#98)

## Completeness

| Metric | Value |
|--------|-------|
| Tasks total | 130 (`- [x]`) |
| Tasks complete | 130 |
| Tasks incomplete | 0 |

No unchecked boxes in `tasks.md`. Every slice (1a, 1b, 2, 3, 4, 5a, 5b, 6a, 6b, 7, 8, 9) carries
a Start/Finish/Rollback header, a gate-guard task, and a Verify line, matching the format the
tasks file declares it followed.

## Build & Tests Execution

**Build**: implicit in `dotnet test` (Release) for all three .NET suites — all built clean, no
warnings-as-errors failures.

**Domain**: PASS — 394 passed / 0 failed / 0 skipped — matches expected exactly.
```text
dotnet test tests/Ways.Domain.Tests/Ways.Domain.Tests.csproj -c Release --no-restore
Correctas! - Con error: 0, Superado: 394, Omitido: 0, Total: 394, Duracion: 63 ms
```

**Application**: PASS — 257 passed / 0 failed / 0 skipped — matches expected exactly.
```text
dotnet test tests/Ways.Application.Tests/Ways.Application.Tests.csproj -c Release --no-restore
Correctas! - Con error: 0, Superado: 257, Omitido: 0, Total: 257, Duracion: 1 s
```

**Integration**: PASS — 896 passed / 0 failed / 0 skipped — matches expected exactly, on the
first run. No re-run was needed for the documented environmental flakiness (the two prior
confirmed episodes did not reproduce this time).
```text
dotnet test tests/Ways.IntegrationTests/Ways.IntegrationTests.csproj -c Release --no-restore
Correctas! - Con error: 0, Superado: 896, Omitido: 0, Total: 896, Duracion: 6 m 24 s
```

**vitest** (`cd src/Ways.Web && npx vitest run`): PASS — 544 passed / 0 failed, 34/34 test files.
```text
Test Files  34 passed (34)
     Tests  544 passed (544)
```

**Coverage**: not measured — no coverage tool wired into this run; not blocking (unchanged from
prior stages' convention).

## Spec Compliance Matrix

### `exportacion-de-reportes`

| Requirement | Scenario | Test | Result |
|---|---|---|---|
| Export Route Convention And Policy Inheritance By Co-Location | An unsupported formato is rejected | `FormatoDeExportacionTests` + `ReportesVentasResumenExportTests.UnFormatoNoSoportadoRechazaConProblemDetailsAtravesDelPipelineHttp` | COMPLIANT |
| Export Route Convention And Policy Inheritance By Co-Location | A caller authorized on the source route is authorized on its export | `ExportacionDeReportesTests` (equality tests imply 200 for the source role); co-location verified by source read `ReportesEndpoints.cs:34` (no `.RequireAuthorization` on plain exports) | COMPLIANT |
| Row Cap Refuses, Never Truncates | A 25001-row request is refused with the count | `ReportesVentasResumenExportTests.UnaExportacionQueSuperaElTopeSeRechazaConLaCantidadReal` (tope bound low via `OpcionesDeExportacion`, same pattern repeated per export) | COMPLIANT |
| Row Cap Refuses, Never Truncates | An at-cap request succeeds | `ReportesVentasResumenExportTests.UnaExportacionExactamenteEnElTopeSeAcepta` | COMPLIANT |
| XLSX Response Contract And Deterministic Naming | Identical requests produce identical filenames / Filename is deterministic and scoped | `NombreDeArchivoTests` (determinism, ASCII, the exact `ventas_resumen_pv3_...` example from the spec) | COMPLIANT |
| In-Sheet Header Block | Header identifies scope and generator | `ReportesVentasResumenExportTests.ElEncabezadoIdentificaElAlcanceYElGenerador` | COMPLIANT |
| In-Sheet Header Block | A cost-bearing export carries the coverage block | `ExportacionDeReportesTests.ElExportDeRentabilidadCargaElBloqueDeCobertura` | COMPLIANT |
| No Re-Query — Exported Figures Equal Endpoint Figures | Export figures equal endpoint figures for identical params | Equality test per export (`ReportesVentasResumenExportTests.ElExportEsIgualAlEndpointJsonParaLosMismosParametros` + 8 in `ExportacionDeReportesTests` + 3 listing exports + G2/G3/existencias — full inventory below) | COMPLIANT |
| Excel Library Containment | A second reference to the library is flagged | `ContencionDelExportadorTests` (source-scan, 2 facts: single `.cs` file, single `.csproj`) | COMPLIANT |
| Licence Audit Is Recorded Before The Exporter Ships | Licence audit is recorded before the exporter ships | PR #87 body records the audit conclusion (8 transitive packages: 7 MIT + `SixLabors.Fonts 1.0.0` Apache-2.0, judge-re-verified from nuspecs) | PARTIAL — see WARNING-1 |

### `historico-de-cajas`

| Requirement | Scenario | Test | Result |
|---|---|---|---|
| G2 Histórico Lists Closed Turnos Only... | An open turno is excluded from the listing | `HistoricoDeCajasTests.UnTurnoAbiertoQuedaExcluidoDelListadoJuntoAUnoCerradoDelMismoPuntoDeVenta` (mutation-proof) | COMPLIANT |
| G2 Histórico Lists Closed Turnos Only... | Listed totals equal the sum of the turno's arqueos | `HistoricoDeCajasTests.LosTotalesDelListadoSonLaSumaExactaDeLosArqueosPersistidos` | COMPLIANT |
| G2 Detail Reuses ResumenDeTurno Plus Ticket And Gasto Listings | Detail includes resumen, tickets, and gastos | `DetalleDeTurnoTests.ElDetalleReponeElMismoResumenMasLosTicketsYGastosSembrados` | COMPLIANT |
| G3 Tesorería Book Is A Chained, Paginated Read | Book preserves chain order | `TesoreriaTests.TresFilasEncadenadasSeDevuelvenEnOrdenDeCadena` (mutation-proof) | COMPLIANT |
| Role Split — OperacionDePos / LecturaDeReportes | A Vendedor downloads their own turno's Z-report | `DetalleDeTurnoTests.UnVendedorDescargaElExportDelTurnoQueElMismoCerro` | COMPLIANT |
| Role Split | A Vendedor is rejected from the G2 histórico listing | `HistoricoDeCajasTests.UnVendedorEsRechazadoDelHistoricoListado` | COMPLIANT |
| Role Split | A Supervisor reads the G2 listing and the G3 book | `HistoricoDeCajasTests.UnSupervisorLeeElHistoricoListado` + `TesoreriaTests.UnSupervisorLeeElLibroDeTesoreria` | COMPLIANT |
| G2/G3 Export Siblings Equal Their JSON | G2 listing export figures equal the JSON listing | `HistoricoDeCajasTests.ElExportDelHistoricoEsIgualAlListadoJsonTurnoPorTurno` (row-by-row, stronger than the scenario's literal "combined sums") | COMPLIANT |

### `reportes-de-gestion` (delta)

| Requirement | Scenario | Test | Result |
|---|---|---|---|
| Every Reportes De Gestión Route Has An Export Sibling | A ventas resumen export matches its endpoint | `ReportesVentasResumenExportTests.ElExportEsIgualAlEndpointJsonParaLosMismosParametros` | COMPLIANT |
| Every Reportes De Gestión Route Has An Export Sibling | A Vendedor is rejected from any reportes-de-gestión export | `ExportacionDeReportesTests.UnVendedorEsRechazadoEnLosSeisExportsDeLecturaDeReportes` (parameterized) | COMPLIANT |
| Existencias Report Joins Stock To Artículos Under The Same Gate | Existencias needs no idArticulo | `ExistenciasTests.LasExistenciasDe40ArticulosVuelvenSinPedirIdArticulo` | COMPLIANT |
| Existencias Report Joins Stock To Artículos Under The Same Gate | A Supervisor exports existencias | `ExistenciasExportTests.UnSupervisorExportaLasExistenciasConUnNombreDeArchivoDeterministico` | COMPLIANT |

### `rentabilidad-y-comisiones` (delta)

| Requirement | Scenario | Test | Result |
|---|---|---|---|
| Rentabilidad/Comisiones Exports Stack LecturaDeRentabilidad And Carry Coverage | A Supervisor is rejected on the rentabilidad export | `ExportacionDeReportesTests.UnSupervisorEsRechazadoEnElExportDeRentabilidad` (mutation-proof) | COMPLIANT |
| ...same | An Admin's rentabilidad export carries the coverage block | `ExportacionDeReportesTests.ElExportDeRentabilidadCargaElBloqueDeCobertura` | COMPLIANT |
| ...same | The comisiones export is labelled PROVISIONAL | `ExportacionDeReportesTests.ElExportDeComisionesLlevaLaEtiquetaProvisional` | COMPLIANT |

### `tesoreria` (delta)

| Requirement | Scenario | Test | Result |
|---|---|---|---|
| Tesorería Book Has A Read/Listing Endpoint | A Vendedor is rejected from the tesorería book | `TesoreriaTests.UnVendedorEsRechazadoDelLibroDeTesoreria` | COMPLIANT |
| ...same | Book preserves chain order | `TesoreriaTests.TresFilasEncadenadasSeDevuelvenEnOrdenDeCadena` (mutation-proof) | COMPLIANT |
| ...same | The book has an export sibling equal to its JSON | `TesoreriaExportTests.ElExportEsIgualAlLibroJsonFilaPorFila` | COMPLIANT |

**Compliance summary**: 29/29 scenarios have a passing covering test. 1 scenario
(Licence Audit Is Recorded Before The Exporter Ships) is marked PARTIAL — the audit itself was
performed and its conclusion is correct and independently re-verifiable (see below), but the PR
description records a summary rather than the itemized per-package table the proposal's binding
condition and the spec scenario literally ask for.

## Correctness (Static Evidence)

| Area | Status | Notes |
|---|---|---|
| Zero-schema gate | HELD | `src/Ways.Infrastructure/Persistencia/Migraciones/` still ends at `20260811033540_CostoCongeladoEnVentaEtapa9`; no migration file, `WaysDbContextModelSnapshot.cs`, or `Configuraciones/*` touched since stage 9/10. Confirmed by directory listing and `git log` on the migrations folder. |
| No CSV shipped | CONFIRMED | `FormatoDeExportacion.Parsear` accepts only `xlsx`; `FormatoDeExportacionTests` covers the rejection path. |
| No PDF library, anywhere | CONFIRMED | `grep -i pdf` across every `.csproj` and `src/Ways.Web/package.json` returns nothing. Print views use `window.print()` + `@media print` only (slice 8). |
| Excel library containment | CONFIRMED | `ContencionDelExportadorTests` — one `.cs` file (`ExportadorXlsx.cs`), one `.csproj` (`Ways.Infrastructure.csproj`) reference `ClosedXML`. Passing (part of the 257 Application tests). |
| Licence audit correctness | CONFIRMED INDEPENDENTLY | Re-ran `dotnet list src/Ways.Infrastructure/Ways.Infrastructure.csproj package --include-transitive` myself: `ClosedXML 0.104.2`, `ClosedXML.Parser 1.2.0`, `DocumentFormat.OpenXml 3.1.1`, `DocumentFormat.OpenXml.Framework 3.1.1`, `ExcelNumberFormat 1.1.0`, `SixLabors.Fonts 1.0.0` — matches PR #87's claim of 8 transitive packages, 7 MIT + 1 Apache-2.0 (`SixLabors.Fonts`), version-pinned exactly (no range). |
| Policy inheritance by co-location (spot-check) | CONFIRMED | `ReportesEndpoints.cs:34` `/ventas/resumen/export` declares no `.RequireAuthorization` (inherits group's `LecturaDeReportes`, `:19`); `:174-198` `/rentabilidad/export` and `:306-328` `/comisiones/export` explicitly re-stack `.RequireAuthorization(Politicas.LecturaDeRentabilidad)`, matching their sources; `CajaEndpoints.cs:110` `/{id}/detalle/export` declares no separate policy, inheriting the group's `OperacionDePos` (`:18`). All three spot-checked shapes match design decisions 5/7/10. |
| Sync + refusing cap, never truncation | CONFIRMED | `GuardaDeTope.Exigir` throws before any bytes are returned; the `+1` race-backstop (`VentasListadoExportTests.UnaFilaInsertadaEntreElConteoYLaLecturaSigueRechazandoLaExportacion`) proves the window is closed for listings. See WARNING-2 for the one place this backstop was not duplicated. |
| Deterministic ASCII names + header block | CONFIRMED | `NombreDeArchivoTests` + header tests per export. |
| fetch+blob with error funnel | CONFIRMED | vitest suite includes the 403 to onError, 401 to alPerderLaSesion, 400 to panel-error, filename-parsing, and double-click cases per slice 4's task list; full vitest run green (544/544). |
| Existencias minimal-and-last | SHIPPED | Slice 9 merged; not dropped to Etapa 13. |
| One containment file + no-re-query + equality | CONFIRMED | Equality test exists and passes for every exportable surface: 1 (ventas/resumen) + 8 (`ExportacionDeReportesTests`) + 3 (ventas/compras/estado-de-cuenta listados) + 1 (G2 listing) + 1 (G2 detail) + 1 (G3 tesorería) + 1 (existencias) = 16 equality tests across the stage. |

## Coherence (Design)

| Decision | Followed? | Notes |
|---|---|---|
| 1 — ClosedXML, licence-conditional | Yes | Audit passed, no fallback needed. See WARNING-1 for the PR-body documentation gap. |
| 2 — No CSV in v1 | Yes | |
| 3 — Sync, buffered, hard refusing cap | Yes | |
| 4 — `GET {ruta}/export?formato=xlsx`, co-located | Yes | |
| 5 — Role gating inherited, caja split | Yes | Turno detail under `OperacionDePos`, G2/G3 under `LecturaDeReportes`, spot-checked in code. |
| 6 — G2/G3 minimal aggregation | Yes | G2 listing totals from persisted `arqueos_turno`; G2 detail reuses `ResumenDeTurno` + two plain reads; G3 zero derivation. |
| 7 — Deterministic ASCII naming + header block | Yes | One recorded gap: `ComprasPorProveedor`'s header shows `"N/A"` in the zone field (no `ZonaHoraria` in its JSON contract) — see SUGGESTION-1. |
| 8 — `api.descargar()` via fetch+blob | Yes | |
| 9 — No server-side PDF | Yes | |
| 10 — Existencias minimal and last | Yes | Not dropped. |
| 11 — One seam, one containment folder | Yes | |
| Design decision 10 (caja detail route moved to `/api/caja/turnos/{id}/detalle`) | Yes | The `historico-de-cajas` spec already reflects the moved route; this is a pre-tasks reconciliation, not a drift — confirmed by reading the merged spec text directly. |
| Design decision 13 (no dedicated print route) | Yes | Same component, `@media print` + `d-print-none`, recorded exemption for automated print-rendering assertions (verified by eye), consistent with the design's own Testing Strategy table. |

### Recorded deviations reconciled (per the caller's known list)

All six items called out as "known recorded deviations" were checked against `tasks.md` and are
properly noted at the task level, not silent drift:

1. **Turno-detail route reconciliation** — the merged `historico-de-cajas` spec already states
   `GET /api/caja/turnos/{id}/detalle`; design decision 10 documents why it moved from the
   proposal's original `/api/reportes/cajas/{id}`. Reconciled pre-tasks, consistent everywhere.
2. **Empresa/PV omitted from print headers** — tasks 8.1/8.2 record this explicitly ("Empresa/PV
   are NOT rendered... recorded gap, not an oversight"), justified by design decision 13's
   no-second-fetch rule. Reconciled.
3. **CajaZ nav-less** — task 6b.3 records the deliberate omission, matching the
   `CompraEditor`/`CuentaCorriente` convention of link-only detail screens. Reconciled.
4. **Existencias aggregate-cap shape** — task 9.4 records `GuardaDeTope` running on
   `TablaExportable.Filas.Count` (no `COUNT(*)`), per design decision 6's aggregate
   classification. Reconciled — see SUGGESTION-2 for a residual low-probability risk.
5. **No desde/hasta on existencias** — task 9.1 records the report has no time dimension.
   Reconciled.
6. **ComprasPorProveedor's N/A zona** — NOT recorded in `tasks.md`; only a source comment at
   `ReportesEndpoints.cs:77-80` explains it. See SUGGESTION-1.

## Issues Found

### CRITICAL
None.

### WARNING

**WARNING-1 — Licence audit PR body records a summary, not the literal itemized table the
binding condition asks for.**
Proposal decision 1 and task 1a.1 require: "Record the full table [package id, version,
licence] in the PR body." The spec's own scenario text says "the PR description enumerates
every transitive package and its declared licence." PR #87's body (`gh pr view 87`) states the
audit's conclusion — "ClosedXML 0.104.2 introduce exactamente 8 paquetes transitivos: 7 MIT +
SixLabors.Fonts 1.0.0 Apache-2.0 (verificado en nuspec...)" — but does not list each of the 8
packages by name/version/licence individually; that per-package table does not appear in the PR
body or in the slice's commit messages either. I independently re-ran
`dotnet list src/Ways.Infrastructure/Ways.Infrastructure.csproj package --include-transitive` and
confirmed the audit's conclusion is correct (8 transitive packages, licences as claimed, versions
pinned exactly). This is a documentation-completeness gap, not a correctness or licence-risk
gap — the gate's substantive purpose (catching a disqualifying licence before shipping) was met
and is independently reproducible from the committed `.csproj`. Does not block archive.

**WARNING-2 — The `+1` race-backstop mutation test was not duplicated for the G2 listing export's
new query path.**
Self-recorded in task 5a.13: `ListarCierresParaExportacionAsync` introduces its own
`Contar -> Take(topeDeFilas + 1)` clause (same shape as `ServicioDeVentas`'s, proven with a
dedicated `DbCommandInterceptor` in Slice 3), but no equivalent `turnos_caja`-specific interceptor
test exists for this new clause — only a plain over-cap regression test
(`UnaExportacionDelHistoricoQueSuperaElTopeSeRechazaConLaCantidadReal`), which passes but does not
prove the race window specifically. The code shape is structurally identical to the already-proven
Slice 3 pattern, so risk is low, but the mutation-proof evidence for this one query path is
missing. Does not block archive; worth closing in a future slice/etapa if `turnos_caja` export
volume ever approaches a real concurrent-insert scenario.

### SUGGESTION

**SUGGESTION-1 — `ComprasPorProveedor` export header shows `"N/A"` as its zone, undocumented in
`tasks.md`.**
`ReportesEndpoints.cs:77-80` hardcodes `zonaHoraria: "N/A"` for `/compras/por-proveedor/export`
because `ComprasPorProveedor`'s JSON contract carries no `ZonaHoraria` field (unlike every other
reportes-de-gestión response). The code comment explains this correctly, but it is not surfaced
in `tasks.md`'s deviation notes the way the other five known deviations are, and no header test
exists for this specific export to pin the `"N/A"` value as intentional rather than an oversight.
Cosmetic only — does not affect the figures-equality invariant. Consider adding one line to
`tasks.md` (or a header test) for consistency with the stage's own documentation discipline.

**SUGGESTION-2 — Existencias' aggregate-cap shape guards after materializing the full table, not
before.**
Per design decision 6, `Existencias` is classified as an "aggregate" (like the nine stage-10
reports), so `GuardaDeTope` runs on `TablaExportable.Filas.Count` after mapping rather than a
`COUNT(*)` pre-check. Unlike the other aggregates (bounded by construction: <=366 buckets, <=PVs,
<=vendedores), a punto de venta's article catalog has no structural upper bound comparable to a
calendar or dimension table — a very large catalog could, in principle, materialize a large table
in memory before the cap trips. This is a deliberate, recorded design classification (not an
oversight) and store-scale catalogs are nowhere near 25 000 rows today, so it is low risk in
practice; flagging only because it is the one aggregate export whose row count is not bounded by
a small fixed dimension.

**SUGGESTION-3 — Column-title naming ("Descripción" vs. "Nombre") differs across exports.**
`ArticulosTop`/`Rentabilidad` exports title their text column "Descripción" (from `p.Descripcion`);
`Existencias` titles the equivalent column "Nombre" (from `f.Nombre`). Each mapper faithfully
reflects its own upstream DTO's field name, so this is not a bug — just a naming inconsistency a
reader who opens two exported files side by side would notice. No spec requirement pins column
titles across reports, so this is purely cosmetic.

## Verdict

**PASS WITH WARNINGS**

All 29 spec scenarios across the 5 spec files have a passing, runtime-verified covering test; the
zero-schema gate held for the whole stage (migrations still end at
`CostoCongeladoEnVentaEtapa9`); all 11 proposal decisions are implemented as resolved, spot-checked
against source for the three riskiest claims (policy inheritance including a stacked route, no PDF
library anywhere, containment); all four test suites pass in full (Domain 394, Application 257,
Integration 896 — no re-run needed, vitest 544) at the exact expected counts; all 130 tasks are
checked with rich, honest deviation notes. The two WARNINGs are both documentation/test-depth gaps
with independently-verified-correct underlying substance (the licence audit's conclusion is right,
just under-itemized in the PR body; the missing mutation test targets an already-proven code
shape) — neither is a functional defect and neither blocks archive.
