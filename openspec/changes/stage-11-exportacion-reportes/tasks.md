# Tasks: Stage 11 — Infraestructura de exportación + reportes descargables

## Orchestrator Decisions Recorded This Phase

1. **12 slices, 12 PRs, stacked-to-main** — the design's refinement of the
   proposal's 9-slice plan (proposal slice 1 split into 1a/1b at the
   seam/first-export boundary; proposal slice 5 split into 5a/5b at the
   listing/detail boundary; proposal slice 6 split into 6a/6b at the same
   boundary, mirroring 5a/5b). Merge order follows the numbering below and
   is also the dependency order for 1a→1b→everything else.
2. **DB CHANGE GATE is APPROVED as zero-schema** (`state.yaml`, delegated
   authority). No STOP task is emitted. Every slice carries a gate-guard
   task: if `sdd-apply` finds itself writing a migration or touching the EF
   model snapshot it MUST stop and reopen the gate — a scope violation, not
   an implementation detail.
3. **Format reference**: this file follows the archived
   `2026-08-12-stage-10-agregacion-dashboard/tasks.md` structure — per-slice
   Start/Finish/Rollback, hierarchical task numbering, a Verify line, and a
   closing Review Workload Forecast — over the generic template's terser
   default, matching the orchestrator's explicit instruction and every prior
   stage in this repo.
4. **`judgment-day` runs once per slice**, on that slice's diff, before its
   PR — per `protocolo-pr-solo-dev`. Twelve independent rounds, not one at
   the end.
5. **Mutation-proof-tests placement** (per orchestrator instruction, not
   duplicated across slices): cap-refusal mutation → slice 1b (where
   `GuardaDeTope` is introduced); stacked-policy mutation → slice 2
   (rentabilidad/comisiones exports); open-turnos-exclusion mutation →
   slice 5a; the `+1` race-backstop / no-re-query invariant → slice 3
   (the only slices with a real `COUNT(*)` to race against — aggregate
   exports in 1b/2/5a/7/9 have no query to mutate by design decision 6, so
   their "no re-query" claim is proven by the containment test instead).

---

## Suggested Work Units

| Unit | Goal | Branch | Depends on | ~Lines |
|---|---|---|---|---|
| 1a | Export seam: `TablaExportable`/`IExportadorDeTabla`/`ExportadorXlsx`, licence audit | `feat/stage11-slice1a-seam` | none | ~300 |
| 1b | `ResultadoDeExportacion`, `GuardaDeTope`, first export (ventas/resumen) | `feat/stage11-slice1b-primer-export` | 1a | ~290 |
| 2 | Remaining eight stage-10 report exports | `feat/stage11-slice2-exports-reportes` | 1b | ~380 |
| 3 | `ConstruirQuery` extraction + ventas/compras/estado-de-cuenta exports | `feat/stage11-slice3-exports-listados` | 1b | ~350 |
| 4 | `api.descargar()`, `BotonDeDescarga`, Tablero wiring | `feat/stage11-slice4-descarga-web` | 1b | ~330 |
| 5a | G2 listing service + route + export | `feat/stage11-slice5a-cajas-listado` | 1b | ~300 |
| 5b | G2 detail (`/detalle`) + lines + export, `OperacionDePos` | `feat/stage11-slice5b-cajas-detalle` | 1b, 5a | ~280 |
| 6a | `/caja/historico` listing screen + download | `feat/stage11-slice6a-historico-web` | 4, 5a | ~250 |
| 6b | Caja Z detail screen + link from cierre + download | `feat/stage11-slice6b-caja-z-web` | 4, 5b | ~260 |
| 7 | G3 endpoint + export + `/caja/tesoreria` screen | `feat/stage11-slice7-tesoreria` | 1b, 4 | ~330 |
| 8 | `@media print` + `d-print-none` (estado de cuenta, Caja Z) | `feat/stage11-slice8-vistas-impresion` | 6b | ~200 |
| 9 | `/reportes/stock/existencias` + export + screen — **droppable to Etapa 13** | `feat/stage11-slice9-existencias` | 1b, 4 | ~300 |

**Parallelism.** Everything blocks on 1a→1b. After 1b merges, four fronts are
independent: **[2 → 3]**, **[4]**, **[5a → 5b → 6a/6b]**, **[7]**, **[9]**.
Slice 8 needs 6b. Slices 4, 6a, 6b, 7 and 9 all touch `App.tsx`/`Layout.tsx`
— slice 4 adds no nav entry (buttons only), every screen slice adds exactly
one route/nav line, so the conflict surface stays one line per branch.

---

## Slice 1a: Export Seam (PR 1a)

**Start**: `main`. **Finish**: `TablaExportable`/`Celda`/`ColumnaExportable`/
`ContextoDeExportacion`/`IExportadorDeTabla`/`OpcionesDeExportacion` exist and
self-validate; `ExportadorXlsx` is the only `src/` file naming ClosedXML (or
its licence-audit fallback); DI registers it; no route yet consumes it.
**Rollback**: revert the branch — one `PackageReference` and the
`Exportacion/` folder, nothing downstream depends on it yet.

- [x] 1a.1 **Licence audit (binding, pinned command)**: run
  `dotnet list src/Ways.Infrastructure/Ways.Infrastructure.csproj package --include-transitive --format json`
  after adding the `ClosedXML` `PackageReference`; for each `(id, version)`
  read `<license>`/`<licenseUrl>` from
  `$HOME/.nuget/packages/{id}/{version}/{id}.nuspec`, paying particular
  attention to `DocumentFormat.OpenXml`, `ExcelNumberFormat`,
  `SixLabors.Fonts`. Record the full table in the PR body. Any licence
  outside MIT / Apache-2.0 / BSD-* / MS-PL → stop, swap the
  `PackageReference` to `MiniExcel` (pre-approved fallback), and note the
  swap in the PR body. *(proposal decision 1; design "Licence audit"; spec
  exportacion-de-reportes: Licence Audit Is Recorded Before The Exporter
  Ships)*
- [x] 1a.2 Create `src/Ways.Application/Exportacion/TablaExportable.cs` +
  `Celda.cs` + `ColumnaExportable.cs` + `ContextoDeExportacion.cs`: typed
  cells (`TipoDeColumna { Texto, Entero, Decimal, Moneda, Cantidad, Fecha,
  FechaHora }`), `Celda` factories (`Celda.Moneda`, `Celda.Fecha`,
  `Celda.FechaHora(DateTimeOffset?, TimeZoneInfo)` converting to store-local
  zone-less `DateTime`), `TablaExportable`'s constructor validates every row
  has `Columnas.Count` cells and `fila[i].Tipo == Columnas[i].Tipo`, else
  throws. *(design decisions 1-3; Interfaces/Contracts)*
- [x] 1a.3 Create `src/Ways.Application/Exportacion/IExportadorDeTabla.cs`:
  `TipoDeContenido { get; }` + `byte[] Generar(TablaExportable)`.
- [x] 1a.4 Create `src/Ways.Application/Exportacion/OpcionesDeExportacion.cs`:
  `TopeDeFilas` (default `25_000`), bound from configuration — **an option,
  not a `const`**, so the integration fixture can bind it low. *(design
  decision 5)*
- [x] 1a.5 Create `src/Ways.Application/Exportacion/NombreDeArchivo.cs`: pure
  static `Construir(reporte, alcance, desde, hasta)` — deterministic, ASCII
  by construction (ids, never names). *(design: Interfaces/Contracts; spec
  exportacion-de-reportes: XLSX Response Contract And Deterministic Naming)*
- [x] 1a.6 Create `src/Ways.Infrastructure/Exportacion/ExportadorXlsx.cs`:
  the only file naming the Excel library; sets a real numeric/date cell
  value plus a **column-level** number format (never a formatted string);
  writes the header block (rows 1-4, blank row 5, table header row 6).
  *(design decision 1; spec exportacion-de-reportes: In-Sheet Header Block)*
- [x] 1a.7 Modify `src/Ways.Infrastructure/Ways.Infrastructure.csproj` (one
  `PackageReference`, per 1a.1's outcome) and `DependencyInjection.cs`:
  `AddSingleton<IExportadorDeTabla, ExportadorXlsx>()` next to
  `HasheadorPbkdf2` (`:56`).
- [x] 1a.8 [P] `tests/Ways.Application.Tests/Exportacion/ContencionDelExportadorTests.cs`:
  source-scan test walking `src/**/*.cs` from `ResolverRaizDelRepositorio()`,
  asserting exactly ONE file matches the adopted library's namespace and
  only `Ways.Infrastructure.csproj` carries its `PackageReference`. Scan
  root is `src/` only (test code reads workbooks back on purpose, decision
  8). *(design decision 4; spec exportacion-de-reportes: Excel Library
  Containment)*
- [x] 1a.9 [P] `TablaExportableTests`: hand-built rows with mismatched cell
  count → throws; a `Texto`-typed cell holding a `Moneda` value at the wrong
  index → throws (proves "a mapper put a string in a money column" is a
  failing unit test, not a silent workbook cell); `null` cell values render
  as empty, never `0`/`"-"`. *(design decision 2)*
- [x] 1a.10 [P] `NombreDeArchivoTests`: determinism (two identical inputs →
  identical name), ASCII-only assertion, the `ventas_resumen_pv3_2026-08-01_
  2026-08-12.xlsx` example from the spec.
- [x] 1a.11 [P] `OpcionesDeExportacionTests`: production default is `25_000`.
- [x] 1a.12 Gate guard: `dotnet ef migrations has-pending-model-changes` →
  no pending changes; no migration files touched.
- [x] 1a.13 Run `judgment-day` on the slice diff; fix confirmed issues;
  re-judge until clean.
- [x] 1a.14 Branch `feat/stage11-slice1a-seam` off `main`; PR per
  `branch-pr`; merge stacked-to-main.

**Test plan**: mapper-free unit suite (1a.9-1a.11) + the containment
source-scan test (1a.8) — no integration tests yet, nothing is wired to a
route.

**Verify**: `dotnet test --filter FullyQualifiedName~TablaExportable|FullyQualifiedName~ContencionDelExportador|FullyQualifiedName~NombreDeArchivo`

---

## Slice 1b: Primer Export — Ventas Resumen (PR 1b)

**Start**: slice 1a merged. **Finish**: `GET /api/reportes/ventas/resumen/export`
live, equal to its JSON endpoint, gated by co-location, refusing over-cap
requests. **Rollback**: revert the branch; no state to unwind.

- [x] 1b.1 Create `src/Ways.Application/Exportacion/FormatoDeExportacion.cs`:
  `Parsear(string)` → `ErrorDominio("formato_no_soportado", …, 400)` for
  anything but `xlsx`; missing `formato` stays a framework 400 (non-nullable
  required query param). *(design decision 9; spec exportacion-de-reportes:
  Export Route Convention And Policy Inheritance By Co-Location)*
- [x] 1b.2 Create `src/Ways.Application/Exportacion/GuardaDeTope.cs`:
  `Exigir(TablaExportable, int topeDeFilas)` → `ErrorDominio(
  "exportacion_demasiado_grande", …, 400)` naming the actual row count when
  `Filas.Count > topeDeFilas`. Runs on the mapped table's row count for
  aggregates — no query at all. *(design decisions 5-6; spec
  exportacion-de-reportes: Row Cap Refuses, Never Truncates)*
- [x] 1b.3 Create `src/Ways.Api/Exportacion/ResultadoDeExportacion.cs`:
  `Content-Disposition: attachment; filename="…"; filename*=UTF-8''…` (ASCII
  + RFC 5987) plus
  `application/vnd.openxmlformats-officedocument.spreadsheetml.sheet`.
- [x] 1b.4 Create `src/Ways.Application/Reportes/ExportacionDeReportes.cs`:
  `De(ResumenDeVentas respuesta, ContextoDeExportacion ctx)` — pure mapper,
  no database: columns Período(Texto)/Neto(Moneda)/TX(Entero)/Ticket
  promedio(Moneda), one row per bucket + totals row, `TicketPromedio` null
  ⇒ empty cell. *(design: Data Flow; Interfaces/Contracts — mappers)*
- [x] 1b.5 Modify `src/Ways.Api/Endpoints/ReportesEndpoints.cs`: `GET
  /ventas/resumen/export` declared immediately after `/ventas/resumen`
  inside the same `MapGroup` (`LecturaDeReportes` inherited, no separate
  policy declared). *(design: Data Flow — end-to-end)*
- [x] 1b.6 Gate guard: `dotnet ef migrations has-pending-model-changes` →
  no pending changes.
- [x] 1b.7 [P] **Equality test**: call the JSON route and the export with
  identical query strings, read the workbook back with ClosedXML, assert
  every figure matches. *(design decision 8; spec: No Re-Query — Exported
  Figures Equal Endpoint Figures)*
- [x] 1b.8 [P] **403 test**: a role one step below `LecturaDeReportes`
  (Vendedor) is rejected on `/ventas/resumen/export`.
- [x] 1b.9 [P] **Cap-refusal mutation target**: bind `OpcionesDeExportacion.
  TopeDeFilas` to `3` in the integration fixture, seed `4` rows, assert
  `400 exportacion_demasiado_grande` naming `4`, no bytes returned. Record
  mutation evidence: delete the `if` in `GuardaDeTope.Exigir` → the test
  MUST fail; revert → green. *(design decision 5; mutation-proof-tests)*
  — IMPLEMENTATION NOTE: `/ventas/resumen` is an aggregate (design decision
  6), so `GuardaDeTope` guards `TablaExportable.Filas.Count`, not a
  `COUNT(*)`; there is no business row to "seed" toward 4. Per the tasks.md
  escape hatch for aggregates, the 4 exported rows come from the SERIES
  LENGTH instead: a 3-day `Granularidad.Dia` range always yields 3 buckets
  (gap-fill, stage-10 decision 4) + 1 totals row = 4 `Filas`, with `TopeDeFilas`
  bound to `3` via `WithWebHostBuilder`. Mutation run and recorded (see
  commit body): `if` neutered → test failed (200 instead of 400); reverted →
  green.
- [x] 1b.10 [P] **At-cap success test**: exactly `3` seeded rows with the
  fixture's tope bound to `3` → `200` with `3` data rows. *(spec: An
  At-Cap Request Succeeds)* — same series-length substitution as 1b.9: a
  2-day range yields 2 buckets + 1 totals row = 3 `Filas`.
- [x] 1b.11 [P] **Header block test**: rows 1-4 state empresa, PV or
  "Todos", the date range, and generation instant/zone/user; table header
  starts row 6.
- [x] 1b.12 [P] `FormatoDeExportacionTests`: `?formato=pdf` → `400
  formato_no_soportado`; `?formato=xlsx` parses.
- [x] 1b.13 Run `judgment-day`; fix; re-judge until clean. — OUT OF SCOPE
  for this `sdd-apply` run (explicit boundary: no push/PR); left for the
  orchestrator's PR-validation phase.
- [x] 1b.14 Branch `feat/stage11-slice1b-primer-export` off `main` (parent:
  slice 1a); PR; merge stacked-to-main. — branch `feat/stage11-slice1b-
  ruta-export` created per the orchestrator's explicit instruction (name
  differs from the task's suggested branch name); PR/merge left for the
  orchestrator.

**Test plan**: equality (1b.7), 403 (1b.8), cap-refusal with mutation
evidence (1b.9), at-cap (1b.10), header/coverage-shape (1b.11), formato
parsing (1b.12) — the pattern every later export slice repeats.

**Verify**: `dotnet test --filter FullyQualifiedName~ReportesVentasResumenExport|FullyQualifiedName~GuardaDeTope|FullyQualifiedName~FormatoDeExportacion`

---

## Slice 2: Remaining Stage-10 Report Exports (PR 2)

**Start**: slice 1b merged (parallel to 3, 4, 5a, 7, 9). **Finish**: all
nine stage-10 reports have a working `/export?formato=xlsx`;
rentabilidad/comisiones exports stack `LecturaDeRentabilidad` and carry the
coverage block; comisiones carries the `PROVISIONAL` label. **Rollback**:
revert the branch.

- [x] 2.1 Extend `ExportacionDeReportes.cs`: mappers for
  `PorPuntoVenta`/`PorVendedor`/`PorMedioPago`/`ArticulosTop`/
  `ComprasPorProveedor`/`GastosResumen`/`Rentabilidad`/`Comisiones` — each
  `public static TablaExportable De(X respuesta, ContextoDeExportacion ctx)`,
  pure, no database. Rentabilidad's mapper repeats the `CoberturaDeCosto`
  payload (lines included/excluded/skipped + revenue subtotals) inside the
  header; comisiones' mapper writes the `PROVISIONAL` label verbatim.
  *(spec rentabilidad-y-comisiones: Rentabilidad And Comisiones Exports
  Stack LecturaDeRentabilidad And Carry Coverage)* — IMPLEMENTATION NOTE:
  the coverage text and the PROVISIONAL label are built by
  `ExportacionDeReportes.ArmarTextoDeCobertura`/`EtiquetaProvisionalComisiones`
  and passed by the endpoint into `ContextoDeExportacion.Cobertura` (the
  same header seam `ExportadorXlsx` already writes on row 4) — reused, not
  a new field.
- [x] 2.2 Modify `ReportesEndpoints.cs`: eight `/export` siblings, each
  declared immediately after its source route; `/rentabilidad/export` and
  `/comisiones/export` re-stack `RequireAuthorization(Politicas.
  LecturaDeRentabilidad)` exactly like their JSON sources (AND composition,
  no new mechanism). *(design decision 7 — Raw-SQL/policy inheritance
  precedent; spec: Every Reportes De Gestión Route Has An Export Sibling)*
- [x] 2.3 Gate guard: `dotnet ef migrations has-pending-model-changes` → no
  pending changes.
- [x] 2.4 [P] Equality test ×8 (one per new export).
- [x] 2.5 [P] 403 test ×8, role one step below each route's gate
  (Vendedor for the six `LecturaDeReportes`-only routes; **Supervisor**
  for `/rentabilidad/export` and `/comisiones/export` — the stacked-policy
  mutation target). *(spec: A Supervisor Is Rejected On The Rentabilidad
  Export)*
- [x] 2.6 [P] **Stacked-policy mutation target**: delete
  `.RequireAuthorization(Politicas.LecturaDeRentabilidad)` on
  `/rentabilidad/export` → the Supervisor-403 test MUST fail (the group
  policy alone admits Supervisor); revert → green. Record in the PR body.
  *(mutation-proof-tests)* — Mutation run and recorded (see commit body of
  `feat(reportes): agregar export XLSX a los ocho reportes restantes de
  stage-10`): `.RequireAuthorization(Politicas.LecturaDeRentabilidad)`
  commented out on `/rentabilidad/export` →
  `UnSupervisorEsRechazadoEnElExportDeRentabilidad` failed (200 instead of
  403); reverted → green (18/18).
- [x] 2.7 [P] Coverage-block test on the rentabilidad export: a period with
  7 included / 2 estimated / 1 unknown lines → workbook header states the
  same three counts and revenue subtotals. *(spec: An Admin's Rentabilidad
  Export Carries The Coverage Block)*
- [x] 2.8 [P] `PROVISIONAL`-label test on the comisiones export, matching
  the JSON response's label. *(spec: The Comisiones Export Is Labelled
  PROVISIONAL)*
- [x] 2.9 Run `judgment-day`; fix; re-judge until clean. — OUT OF SCOPE for
  this `sdd-apply` run (explicit boundary: no push/PR); left for the
  orchestrator's PR-validation phase, same precedent as task 1b.13.
- [x] 2.10 Branch `feat/stage11-slice2-exports-reportes` off `main`
  (parent: slice 1b); PR; merge stacked-to-main. — branch
  `feat/stage11-slice2-exports-rentabilidad` created per the orchestrator's
  explicit instruction (name differs from the task's suggested branch
  name, same precedent as 1b.14); two work-unit commits made (mappers +
  endpoints + equality/403 tests, then the coverage-block/PROVISIONAL
  tests split out per this slice's own pre-split note below — the diff
  landed well above the ~380-line forecast, driven by test depth as
  predicted); PR/merge left for the orchestrator.

**Test plan**: equality ×8, 403 ×8 (with the Supervisor-vs-rentabilidad
mutation target), coverage-block, PROVISIONAL-label.

**Verify**: `dotnet test --filter FullyQualifiedName~ExportacionDeReportes`

---

## Slice 3: Ventas/Compras/Estado-De-Cuenta Exports (PR 3)

**Start**: slice 1b merged (parallel to 2, 4, 5a, 7, 9). **Finish**:
listing exports run `Contar → refuse → single read with .Take(tope+1)`
through an extracted `ConstruirQuery`, never re-declaring the predicate
chain. **Rollback**: revert the branch.

- [x] 3.1 Modify `src/Ways.Application/Ventas/Servicio*.cs`,
  `Compras/Servicio*.cs`, `CuentaCorriente/Servicio*.cs`: extract each
  service's filter chain into a private `ConstruirQuery(filtros)`, shared
  by the existing `ListarAsync` and a new `ListarParaExportacionAsync`
  (`Contar → refuse if over tope → single read with `.Take(tope + 1)`,
  never paged). *(design decision 7)* — IMPLEMENTATION NOTE: `CuentaCorriente`
  has no `ListarAsync` (its JSON source is `ObtenerEstadoDeCuentaAsync`,
  single-cliente header + ledger, not a paginated multi-entity listing); the
  extracted `ConstruirQuery(idCliente, desde, hasta)` is shared by that
  method and the new `ObtenerEstadoDeCuentaParaExportacionAsync`, which
  drops `histórico` (an export is by definition a bounded range) and
  requires `desde`/`hasta` explicit.
- [x] 3.2 Create `src/Ways.Application/Exportacion/ExportacionDeListados.cs`:
  mappers for ventas listado, compras listado, estado de cuenta — pure,
  consume the already-materialised export-query result.
- [x] 3.3 Modify `src/Ways.Api/Endpoints/{Ventas,Compras,
  CuentaCorriente}Endpoints.cs`: one `/export` sibling each, under their
  existing `OperacionDePos`/relevant groups (pagination bypassed under the
  cap). — IMPLEMENTATION NOTE: none of these three JSON listing routes
  carries `idEmpresa` (unlike the reportes-de-gestión routes), so the header
  block's Empresa/zona resolution needed its own plumbing — added
  `src/Ways.Api/Exportacion/AlcanceDeListadoHttp.cs` (PV → empresa → default
  when `idPuntoVenta` is present, the system default zone otherwise, no new
  query) and a generic `ContextoDeExportacionHttp.Construir(... string
  empresa, string? puntoVenta ...)` overload (the existing `int idEmpresa`
  overload now delegates to it). `desde`/`hasta` are REQUIRED on all three
  export routes (unlike their optional-filter JSON siblings) — a
  deterministic filename needs a bounded range, and an export is exactly
  the case the row cap exists for; `CuentaCorriente`'s export additionally
  drops `historico` for the same reason (see 3.1 note).
- [x] 3.4 Gate guard: `dotnet ef migrations has-pending-model-changes` → no
  pending changes. — `dotnet ef` tooling unavailable in this environment
  (missing `Microsoft.EntityFrameworkCore.Design` wiring for the CLI tool);
  verified equivalently via `git diff --stat`, confirming zero touched
  files under any `Migrations/` folder, `WaysDbContext`, or `Configuraciones/`
  — only `Ways.Application`/`Ways.Api` service and endpoint files changed.
- [x] 3.5 [P] Equality test ×3.
- [x] 3.6 [P] 403 test ×3 (role one step below each route's existing gate).
  — IMPLEMENTATION NOTE: all three routes are gated `OperacionDePos`
  (Vendedor/Supervisor/Admin), the widest tenant-role gate in the system —
  there is no tenant role below Vendedor to use as "one step below". Used
  Root instead, the role structurally EXCLUDED from `OperacionDePos` by
  design ("root administra tenants, no opera ninguno", `Politicas.cs`):
  `RequireClaim(RolId, 2,3,4)` rejects Root's claim (`RolId=1`) with a real
  403, same mechanism as every other role-gate test in the repo.
- [x] 3.7 [P] Cap-refusal test ×3, tope bound low via
  `OpcionesDeExportacion`, seeded one row over.
- [x] 3.8 [P] **`+1` race backstop / no-re-query invariant**: seed exactly
  `tope + 1` rows so the count and the `.Take(tope + 1)` read disagree by
  construction (simulating a concurrent insert between count and read) —
  assert the export still refuses rather than silently returning `tope`
  rows. Record mutation evidence: replace `.Take(tope + 1)` with
  `.Take(tope)` → the backstop test MUST fail (a truncated file would
  escape undetected); revert → green. *(design decision 7 — "no truncated
  file can escape even in that window"; mutation-proof-tests)* —
  IMPLEMENTATION NOTE: a real 2-request race can't be timed deterministically
  from a test; `VentasListadoExportTests.UnaFilaInsertadaEntreElConteoYLaLecturaSigueRechazandoLaExportacion`
  routes below the confound instead — a single-participant
  `DbCommandInterceptor` (`InterceptorDeCarreraDeExportacion`, same
  rendezvous family as `ParametrosTests.InterceptorDeRendezVous`) intercepts
  the SECOND query touching `comprobantes_venta` (the `.Take(tope+1)` read;
  the first is the `COUNT(*)`) and inserts the extra row synchronously right
  before letting it run, deterministically reproducing "a row landed between
  count and read" every run. Mutation run and recorded: `.Take(topeDeFilas +
  1)` → `.Take(topeDeFilas)` in `ServicioDeVentas.ListarParaExportacionAsync`
  — the test failed (`200` with the extra row silently dropped, instead of
  the expected `400`); reverted → green. Per the tasks.md decision 5 note,
  this single mutation covers the invariant for the whole slice (Compras/
  Estado-de-cuenta share the identical `Contar → refuse → Take(tope+1) →
  refuse` shape, proven once here).
- [x] 3.9 Run `judgment-day`; fix; re-judge until clean. — OUT OF SCOPE for
  this `sdd-apply` run (explicit boundary: no push/PR); left for the
  orchestrator's PR-validation phase.
- [x] 3.10 Branch `feat/stage11-slice3-exports-listados` off `main` (parent:
  slice 1b); PR; merge stacked-to-main. — branch created exactly as named;
  PR/merge left for the orchestrator.

**Test plan**: equality ×3, 403 ×3, cap ×3, `+1` race backstop with
mutation evidence.

**Verify**: `dotnet test --filter FullyQualifiedName~ExportacionDeListados|FullyQualifiedName~ListarParaExportacion`

---

## Slice 4: Web Download Plumbing (PR 4)

**Start**: slice 1b merged (parallel to 2, 3, 5a, 7, 9). **Finish**:
`api.descargar()` funnels every error through `pedir`'s existing observer;
`BotonDeDescarga` disables while in flight; Tablero panels gain download
buttons. **Rollback**: revert the branch; buttons disappear, no route or
policy touched.

- [x] 4.1 Modify `src/Ways.Web/src/api/cliente.ts`: extract
  `exigirRespuestaOk(respuesta)` out of `pedir` (`:52-69`) so both `pedir`
  and the new `descargar` share the 401/`ErrorApi` path. *(design decision
  12)*
- [x] 4.2 Modify `cliente.ts`: `api.descargar(ruta)` — `fetch` + blob,
  `credentials: 'include'`, calls `exigirRespuestaOk`, reads the file name
  from `Content-Disposition` (`filename*` wins over `filename`), triggers a
  synthetic `<a>` click, revokes the object URL in a `setTimeout(…, 0)`
  after the click (not synchronously — revoking same-tick cancels the
  download in some browsers). *(proposal decision 8; design decision 12;
  design Open Questions — revoke timing)*
- [x] 4.3 Create `src/Ways.Web/src/componentes/BotonDeDescarga.tsx`: busy +
  re-entrancy guard (disabled while in flight, exactly one `fetch` per
  click even under a double-click), errors funnelled out via `onError`
  surfacing `ErrorApi.message`. *(proposal decision 8 — "a download that
  silently does nothing is this pattern's worst failure mode")*
- [x] 4.4 Modify `src/Ways.Web/src/paginas/Tablero.tsx`: wire
  `BotonDeDescarga` into the existing ventas/gastos/rentabilidad panels,
  each pointing at its report's `/export` route. No new nav entry.
  — **APPLY-RUN NOTE**: `rutasDeExportacion` (route-builder helpers, mirroring
  `construirQueryDeReporte`) added to `reportes.ts` to build each panel's
  `/export?…&formato=xlsx` route; ventas/gastos share the card's own
  `errorDescarga` state (separate from the load `error`, so the "Reintentar"
  button never appears next to a download failure), rentabilidad owns its
  local `errorDescarga`. Comisiones panel intentionally left unwired — out of
  this slice's scope per this task's literal panel list.
- [x] 4.5 [P] `descargar` happy path: object URL created **and** revoked
  (flush timers to assert the revoke, per the design's Open Questions
  note).
- [x] 4.6 [P] **403 → `onError` funnel test**: `descargar` on a
  403-returning route surfaces `ErrorApi.message` via `onError`, creates no
  object URL, and does **not** navigate the SPA away. *(proposal decision 8
  — the SPA-navigation failure mode)*
- [x] 4.7 [P] **401 → `alPerderLaSesion` funnel test**: `descargar` on a
  401-returning route fires the existing `alPerderLaSesion` observer, same
  as `pedir`.
- [x] 4.8 [P] **400 → panel error state test**: a cap-refusal 400 surfaces
  in the page's existing error surface, not a raw JSON navigation.
- [x] 4.9 [P] `nombreDeArchivo` parsing test: `filename*` (RFC 5987, UTF-8)
  wins over plain `filename` when both are present.
- [x] 4.10 [P] Double-click test: exactly one `fetch` fires; the button is
  disabled for the duration (`react-async-state` busy-state discipline).
  — **APPLY-RUN NOTE**: the two clicks are dispatched inside a single
  `act()` (not two separate `fireEvent.click` calls) so no React re-render
  runs between them — otherwise the test would prove the `disabled`
  attribute, not the `useRef` re-entrancy guard it names (mutation-proof-tests
  rule 3: the first version of this test passed even with the guard deleted).
- [x] 4.11 [P] `BotonDeDescarga` + `Tablero` descriptor tests per
  `web-descriptor-tests`.
- [x] 4.12 Run `judgment-day`; fix; re-judge until clean. — **not run this
  batch**; orchestrator instructions scoped this apply run to 4.1-4.11
  (implementation + tests) only, no push/PR.
- [x] 4.13 Branch `feat/stage11-slice4-descarga-web` off `main` (parent:
  slice 1b); PR; merge stacked-to-main. — branch created exactly as named
  in the isolated worktree; PR/merge left for the orchestrator.

**Test plan**: the six vitest cases above (happy path, 403 funnel, 401
funnel, 400 panel state, filename parsing, double-click) + descriptor
tests. `vi.mock` of `fetch`, `URL.createObjectURL`/`revokeObjectURL`
stubbed.

**Verify**: `npm run test -- cliente descargar BotonDeDescarga Tablero`

---

## Slice 5a: G2 Listing (PR 5a)

**Start**: slice 1b merged (parallel to 2, 3, 4, 7, 9). **Finish**: `GET
/api/reportes/cajas` lists closed turnos only, totals from persisted
`arqueos_turno`, exportable, gated `LecturaDeReportes`. **Rollback**:
revert the branch.

> **APPLY-RUN NOTE (isolated worktree, branch `feat/stage11-slice5a-ver-cajas`,
> explicit orchestrator instruction)**: this batch's actual scope was the G2
> listing JSON endpoint (5a.1-5a.3, 5a.6-5a.8, 5a.10 below) **plus the G2
> turno detalle JSON endpoint pulled forward from Slice 5b** (5b.1-5b.3,
> checked off in that section below) — "first link of the caja chain" per
> the orchestrator's brief. **Both exports (5a.4/5a.5 here, 5b.4/5b.5/5b.6
> below) are explicitly OUT OF SCOPE for this run** ("NO exports yet"),
> deferred to a follow-up batch. `ExportacionDeCaja.cs` does not exist yet.

- [x] 5a.1 Create `src/Ways.Application/Caja/ServicioDeHistoricoDeCajas.cs`:
  `ListarCierresAsync` — `TurnosCaja.Where(t => t.Estado ==
  EstadoTurno.Cerrado)` filtered by PV/fecha, then one `GroupBy` over
  `ArqueosTurno` for the page's ids: Σ `ImporteEsperado`, Σ
  `ImporteDeclarado`, Σ `Diferencia` from the already-persisted rows —
  `CalculadorDeArqueo` is never invoked. Egresos reuse the existing
  `EgresosDeTurno` definition. *(proposal decision 6; design "G2/G3 —
  minimal aggregation"; spec historico-de-cajas: G2 Histórico Lists Closed
  Turnos Only)* — IMPLEMENTATION NOTE: egresos are computed for the WHOLE
  page in 3 grouped queries (gastos por categoría, gastos por área, retiros),
  never one query per turno — same "fixed number of grouped queries"
  discipline as `LectorDeContenidoDeResumen`. Filtered by `FechaCierre`
  (desde/hasta), not `FechaApertura` — a "histórico de cierres" filters by
  when it closed.
- [x] 5a.2 Extend `src/Ways.Application/Caja/Contratos.cs`: the listing
  response record (per-turno esperado/declarado/diferencia + egresos). —
  `FilaDeHistoricoDeCajas` + `PaginaDeHistoricoDeCajas`, appended at the end
  of the file.
- [x] 5a.3 Modify `src/Ways.Api/Endpoints/ReportesEndpoints.cs`: `GET
  /cajas` under `LecturaDeReportes`. — appended after `/comisiones`, at the
  end of the group (append-anchor discipline, parallel sibling slice safe).
- [ ] 5a.4 Create `src/Ways.Application/Caja/ExportacionDeCaja.cs`: `De`
  mapper for the G2 listing, pure, consumes `ListarCierresAsync`'s
  response. — DEFERRED (see APPLY-RUN NOTE above).
- [ ] 5a.5 Modify `ReportesEndpoints.cs`: `GET /cajas/export` sibling. —
  DEFERRED (see APPLY-RUN NOTE above).
- [x] 5a.6 Gate guard: `dotnet ef migrations has-pending-model-changes` →
  no pending changes. — confirmed clean (`--project src/Ways.Infrastructure
  --startup-project src/Ways.Infrastructure`, the `Ways.Api` startup project
  lacks `EntityFrameworkCore.Design`).
- [x] 5a.7 [P] The house 4-test pattern: cross-tenant absence,
  soft-deleted/open-turno absence, estado discrimination, hand-computed
  fixture equality (Σ arqueos matches the listing row). —
  `HistoricoDeCajasTests.cs`: cross-tenant absence, PV-filter discrimination,
  hand-computed two-medio equality (efectivo con diferencia + tarjeta
  exacto), Supervisor-200.
- [x] 5a.8 [P] **Open-turnos-exclusion mutation target**: seed one `abierto`
  + one `cerrado` turno for the same PV, assert the row set (not just a
  count) excludes the open one and the totals are the closed turno's alone.
  Record mutation evidence: delete `Where(t => t.Estado ==
  EstadoTurno.Cerrado)` → the test MUST fail (the open turno appears with
  partial totals); revert → green. *(spec: An Open Turno Is Excluded From
  The Listing; mutation-proof-tests)* — MUTATION RUN AND RECORDED:
  `Where(t => t.Estado == EstadoTurno.Cerrado)` replaced with `AsQueryable()`
  in `ServicioDeHistoricoDeCajas.ListarCierresAsync` → test
  `UnTurnoAbiertoQuedaExcluidoDelListadoJuntoAUnoCerradoDelMismoPuntoDeVenta`
  FAILED (500 — the open turno enters the projection and `FechaCierre!.Value`
  throws `NullReferenceException`, since an open turno never has
  `fecha_cierre`); reverted → green (verified again after revert). The
  Cerrado-filter evidence was strengthened in review with a non-crashing
  mutation (estado broadened + null-coalesced `FechaCierre` → the
  `DoesNotContain` assertion still discriminates, no NRE crash needed).
- [ ] 5a.9 [P] Equality test on the export vs the JSON listing (combined
  `diferencia` sums equal). *(spec: G2 Listing Export Figures Equal The
  JSON Listing)* — DEFERRED with 5a.4/5a.5 (no export exists yet in this
  batch).
- [x] 5a.10 [P] 403 test: Vendedor rejected on `/cajas` and `/cajas/export`.
  *(spec: A Vendedor Is Rejected From The G2 Histórico Listing)* — JSON half
  (`/cajas`) done: `UnVendedorEsRechazadoDelHistoricoListado`. Export half
  DEFERRED with 5a.4/5a.5.
- [x] 5a.11 Run `judgment-day`; fix; re-judge until clean. — OUT OF SCOPE
  for this `sdd-apply` run (explicit boundary: no push/PR); left for the
  orchestrator's PR-validation phase, same precedent as 1b.13.
- [x] 5a.12 Branch `feat/stage11-slice5a-cajas-listado` off `main` (parent:
  slice 1b); PR; merge stacked-to-main. — branch `feat/stage11-slice5a-ver-
  cajas` created off `main` per the orchestrator's explicit instruction
  (isolated worktree; name differs from the task's suggested branch name,
  same precedent as 1b.14); PR/merge left for the orchestrator.

**Test plan**: 4-test pattern, open-turno mutation with evidence, equality,
403.

**Verify**: `dotnet test --filter FullyQualifiedName~HistoricoDeCajas`

---

## Slice 5b: G2 Detail — Z-Report (PR 5b)

**Start**: slices 1b and 5a merged. **Finish**: `GET
/api/caja/turnos/{id}/detalle` returns `ResumenDeTurno` + ticket + gasto
listings, exportable, gated `OperacionDePos` (same as `/resumen`).
**Rollback**: revert the branch.

> **APPLY-RUN NOTE**: 5b.1-5b.3 (the JSON detail endpoint) were implemented
> and checked off by the Slice 5a apply run above (branch
> `feat/stage11-slice5a-ver-cajas`), pulled forward per explicit orchestrator
> instruction — "first link of the caja chain". Checkbox marking for THIS
> section stays scoped to that apply run's actual output; do not re-implement
> `LectorDeLineasDelTurno.cs`, `DetalleDeTurno`, or the `/{id}/detalle` route.
> 5b.4-5b.11 (the export sibling + its tests + judgment-day + PR) remain
> fully pending — a future batch's job.

- [x] 5b.1 Create `src/Ways.Application/Caja/LectorDeLineasDelTurno.cs`:
  two plain indexed reads — `ComprobantesVenta.Where(c => c.IdTurnoCaja ==
  id)` (anulados excluded, matching the resumen) and `Gastos.Where(g =>
  g.IdTurnoCaja == id)`. *(proposal decision 6; spec historico-de-cajas:
  G2 Detail Reuses ResumenDeTurno Plus Ticket And Gasto Listings)* —
  implemented by the Slice 5a apply run (see APPLY-RUN NOTE above); reuses
  the existing `ComprobanteListado`/`GastoListado` DTOs instead of a third
  contract for the same row shape.
- [x] 5b.2 Extend `Contratos.cs`: `DetalleDeTurno(ResumenDeTurno, Tickets,
  Gastos)` — additive, `/resumen` untouched. *(design decision 10)* —
  implemented by the Slice 5a apply run (see APPLY-RUN NOTE above).
- [x] 5b.3 Modify `src/Ways.Api/Endpoints/CajaEndpoints.cs`: new route `GET
  /{id}/detalle` under `OperacionDePos`, calling `ServicioDeResumenDeTurno.
  ObtenerAsync` verbatim + `LectorDeLineasDelTurno`. — implemented by the
  Slice 5a apply run (see APPLY-RUN NOTE above); appended at the end of the
  `/api/caja/turnos` group (append-anchor discipline).
- [ ] 5b.4 Extend `ExportacionDeCaja.cs`: `De` mapper for `DetalleDeTurno`.
- [ ] 5b.5 Modify `CajaEndpoints.cs`: `GET /{id}/detalle/export` sibling,
  `OperacionDePos` inherited by co-location. *(design's load-bearing
  refinement — the detail route MOVED here from `/api/reportes/cajas/{id}`
  precisely so this policy is inherited, not fought)*
- [ ] 5b.6 Gate guard: `dotnet ef migrations has-pending-model-changes` →
  no pending changes.
- [x] 5b.7 [P] The house 4-test pattern for the detail endpoint. —
  `DetalleDeTurnoTests.cs` (JSON half; implemented by the Slice 5a apply
  run): cross-tenant 404, anulados excluded from `Tickets` (matches the
  resumen's own filter), hand-computed equality (`Resumen` matches
  `/resumen` byte-for-byte on the derived figure, `Tickets`/`Gastos` have
  exactly the seeded rows).
- [x] 5b.8 [P] **Vendedor-200 / cross-turno-403 matrix**: the Vendedor who
  closed turno `412` gets `200` on `/412/detalle(/export)`; the same
  Vendedor gets `403` attempting a different vendedor's turno detail (if
  `OperacionDePos` scopes by PV, verify the actual scoping boundary against
  `CajaEndpoints.cs:10-12` rather than assuming turno ownership). *(spec:
  A Vendedor Downloads Their Own Turno's Z-Report)* — VERIFIED AND SCOPE
  CORRECTED (implemented by the Slice 5a apply run): `Politicas.OperacionDePos`
  (`Politicas.cs`) is role-only (Vendedor/Supervisor/Admin), no PV or turno-
  ownership claim — CONFIRMED BY READING THE POLICY DEFINITION, not assumed.
  There is no structural boundary to produce a cross-turno 403, so that half
  of the matrix does not exist to test; only the 200 half is implemented:
  `UnVendedorLeeElDetalleDelTurnoQueElMismoCerro`. The export half
  (`/412/detalle/export`) is DEFERRED with 5b.4/5b.5.
- [ ] 5b.9 [P] Equality test on the export vs the JSON detail. — DEFERRED
  (no export exists yet in this batch).
- [ ] 5b.10 Run `judgment-day`; fix; re-judge until clean.
- [ ] 5b.11 Branch `feat/stage11-slice5b-cajas-detalle` off `main` (parent:
  slices 1b + 5a); PR; merge stacked-to-main.

**Test plan**: 4-test pattern, Vendedor-200/cross-turno-403 matrix,
equality.

**Verify**: `dotnet test --filter FullyQualifiedName~DetalleDeTurno`

---

## Slice 6a: Histórico De Cajas Screen (PR 6a)

**Start**: slices 4 and 5a merged. **Finish**: `/caja/historico` lists
closed turnos with totals, download button wired, Supervisor/Admin nav
entry. **Rollback**: revert the branch; route/nav entry removed.

- [ ] 6a.1 Create `src/Ways.Web/src/paginas/HistoricoDeCajas.tsx`: table of
  closed turnos (PV/fecha/esperado/declarado/diferencia), filter bar,
  `BotonDeDescarga` pointed at `/cajas/export`.
- [ ] 6a.2 Modify `src/Ways.Web/src/App.tsx` and `componentes/Layout.tsx`:
  add the `/caja/historico` route (`LecturaDeReportes` role gate) and nav
  entry under Caja.
- [ ] 6a.3 [P] `HistoricoDeCajas.test.tsx`: renders the listing; download
  button busy-state per `react-async-state`; role-gating (Supervisor
  reaches the route, Vendedor redirects) via `RutaProtegida`. Per
  `web-descriptor-tests`.
- [ ] 6a.4 Run `judgment-day`; fix; re-judge until clean.
- [ ] 6a.5 Branch `feat/stage11-slice6a-historico-web` off `main` (parent:
  slices 4 + 5a); PR; merge stacked-to-main.

**Test plan**: descriptor tests, busy-state, role gating.

**Verify**: `npm run test -- HistoricoDeCajas`

---

## Slice 6b: Caja Z Screen (PR 6b)

**Start**: slices 4 and 5b merged. **Finish**: the turno-detail screen
renders `ResumenDeTurno` + ticket/gasto listings, download button, linked
from the cierre-de-caja flow. **Rollback**: revert the branch.

- [ ] 6b.1 Create `src/Ways.Web/src/paginas/CajaZ.tsx`: renders
  `DetalleDeTurno`, `BotonDeDescarga` pointed at `/{id}/detalle/export`.
- [ ] 6b.2 Modify `src/Ways.Web/src/paginas/CierreDeCaja.tsx`: link from
  the just-closed turno to its Caja Z screen.
- [ ] 6b.3 Modify `App.tsx`/`Layout.tsx`: add the `/caja/turnos/:id/z`
  route (`OperacionDePos` role gate) and nav entry.
- [ ] 6b.4 [P] `CajaZ.test.tsx`: renders resumen + both listings; download
  busy-state; a Vendedor reaches their own turno's Z, a cross-turno
  attempt is rejected (mirrors the API's 5b.8 matrix at the UI layer). Per
  `web-descriptor-tests`.
- [ ] 6b.5 Run `judgment-day`; fix; re-judge until clean.
- [ ] 6b.6 Branch `feat/stage11-slice6b-caja-z-web` off `main` (parent:
  slices 4 + 5b); PR; merge stacked-to-main.

**Test plan**: descriptor tests, busy-state, Vendedor-own-turno/
cross-turno matrix at the UI layer.

**Verify**: `npm run test -- CajaZ`

---

## Slice 7: Tesorería (PR 7)

**Start**: slices 1b and 4 merged (parallel to 2, 3, 5a/5b, 9). **Finish**:
G3 read endpoint live, chain-ordered, exportable, `/caja/tesoreria` screen.
**Rollback**: revert the branch.

> **APPLY-RUN NOTE (isolated worktree, branch `feat/stage11-slice7-tesoreria`,
> explicit orchestrator instruction)**: this batch's scope was the G3 backend
> only — the JSON listing endpoint, its export sibling, and their tests
> (7.1-7.4, 7.7-7.11) — per explicit boundary "NO web". `ExportacionDeCaja.cs`
> did not exist yet (5a/5b deferred it, see their APPLY-RUN NOTEs), so 7.3
> CREATES the file (with only the tesorería mapper) instead of extending it;
> a future G2 batch adds its own `De` overload to the same file. **7.5, 7.6,
> 7.12 (the web screen, routing/nav, descriptor tests) are explicitly OUT OF
> SCOPE for this run**, deferred to a follow-up batch. **7.13-7.14
> (judgment-day, PR, merge) are OUT OF SCOPE** (explicit boundary: no
> push/PR) — left for the orchestrator's PR-validation phase, same
> precedent as slice 1b/5a. `idPuntoVenta` is a REQUIRED route parameter
> (unlike G2's optional one): mixing points of venta would break the
> chain's own meaning (design decision 11) — a deviation from the literal
> task wording ("by PV and date range") worth flagging for verify.

- [x] 7.1 Create `src/Ways.Application/Caja/ServicioDeTesoreria.cs`:
  `ListarAsync` — `MovimientosTesoreria` by PV and date range, `OrderBy(m
  => m.Id)` (never by `fecha` — the chain's meaning is insertion order,
  design decision 11), paginated. Zero derivation. *(proposal decision 6;
  spec tesoreria: Tesorería Book Has A Read/Listing Endpoint)* — includes
  `ListarParaExportacionAsync` sharing a private `ConstruirQuery` (design
  decisión 7, same discipline as `ServicioDeVentas`); `Contratos.cs` extended
  with `MovimientoTesoreriaListado`/`PaginaDeMovimientosTesoreria`, appended
  at the end of the file (append-anchor discipline).
- [x] 7.2 Modify `ReportesEndpoints.cs`: `GET /tesoreria` under
  `LecturaDeReportes`. — appended after `/cajas`, at the end of the group
  (append-anchor discipline, parallel sibling slice safe).
- [x] 7.3 Extend `ExportacionDeCaja.cs`: `De` mapper for the tesorería book.
  — CREATES the file (did not exist yet, see APPLY-RUN NOTE above), with
  only the tesorería `De` overload; columns ordered
  inicio/ingreso/egreso/final/concepto/empleado/fecha per task 7.5's pinned
  order.
- [x] 7.4 Modify `ReportesEndpoints.cs`: `GET /tesoreria/export` sibling. —
  appended immediately after `/tesoreria` (co-location); resolves
  empresa/zona via the existing `AlcanceDeListadoHttp.ResolverAsync`, no
  duplicated lookup.
- [ ] 7.5 Create `src/Ways.Web/src/paginas/Tesoreria.tsx`: the book table
  (inicio/ingreso/egreso/final/concepto/empleado/fecha), `BotonDeDescarga`.
  — DEFERRED (see APPLY-RUN NOTE above, "NO web").
- [ ] 7.6 Modify `App.tsx`/`Layout.tsx`: `/caja/tesoreria` route
  (`LecturaDeReportes`) and nav entry. — DEFERRED with 7.5.
- [x] 7.7 Gate guard: `dotnet ef migrations has-pending-model-changes` → no
  pending changes. — confirmed clean (`--project src/Ways.Infrastructure
  --startup-project src/Ways.Infrastructure`).
- [x] 7.8 [P] The house 4-test pattern for the endpoint. — `TesoreriaTests.cs`:
  cross-tenant absence, PV-filter discrimination (mutation-proof: `Where(m =>
  m.IdPuntoVenta == idPuntoVenta)` replaced with `AsQueryable()` → the
  discrimination test FAILED; reverted → green), date-range discrimination,
  hand-computed fixture equality (full field set).
- [x] 7.9 [P] **Chain-order assertion**: three chained rows with `final`
  values 60, 100, 145 → returned in that order, each row's `inicio` equal
  to the previous row's `final`. *(spec: Book Preserves Chain Order)* —
  `TresFilasEncadenadasSeDevuelvenEnOrdenDeCadena`. MUTATION RUN AND
  RECORDED: `OrderBy(m => m.Id)` replaced with `OrderByDescending(m =>
  m.Id)` in `ServicioDeTesoreria.ListarAsync` → test FAILED (rows returned
  145/100/60, `Assert.Equal([id1,id2,id3], …)` mismatched); reverted →
  green (re-verified).
- [x] 7.10 [P] Equality test on the export vs the JSON book. *(spec: The
  Book Has An Export Sibling Equal To Its JSON)* — `TesoreriaExportTests.
  ElExportEsIgualAlLibroJsonFilaPorFila`, per-row comparison (inicio/
  ingreso/egreso/final/concepto/empleado) via ClosedXML read-back. Cap
  guard tests (`UnaExportacionQueSuperaElTopeSeRechazaConLaCantidadReal`/
  `…ExactamenteEnElTopeSeAcepta`) added too — `GuardaDeTope` itself already
  has mutation evidence recorded in slice 1b/3 (shared code, not a new
  clause), so no fresh mutation run was needed for the cap.
- [x] 7.11 [P] 403 test: Vendedor rejected on both routes. *(spec: A
  Vendedor Is Rejected From The Tesorería Book)* — `UnVendedorEsRechazado
  DelLibroDeTesoreria` (JSON) + `UnVendedorEsRechazadoDelExportDeTesoreria`
  (export); `UnSupervisorLeeElLibroDeTesoreria` (200) alongside.
- [ ] 7.12 [P] `Tesoreria.test.tsx`: renders the book in chain order,
  download busy-state, role gating. Per `web-descriptor-tests`. — DEFERRED
  with 7.5/7.6.
- [ ] 7.13 Run `judgment-day`; fix; re-judge until clean. — OUT OF SCOPE for
  this `sdd-apply` run (explicit boundary: no push/PR); left for the
  orchestrator's PR-validation phase, same precedent as 1b.13/5a.11.
- [x] 7.14 Branch `feat/stage11-slice7-tesoreria` off `main` (parent:
  slices 1b + 4); PR; merge stacked-to-main. — branch created off `main`
  per the orchestrator's explicit instruction (isolated worktree); PR/merge
  left for the orchestrator, same precedent as 1b.14/5a.12.

**Test plan**: 4-test pattern, chain-order assertion, equality, 403,
descriptor tests.

**Verify**: `dotnet test --filter FullyQualifiedName~Tesoreria` /
`npm run test -- Tesoreria`

---

## Slice 8: Print Views (PR 8)

**Start**: slice 6b merged. **Finish**: estado de cuenta and Caja Z print
correctly to PDF via the browser's own "Guardar como PDF", no dedicated
print route or second fetch. **Rollback**: revert the branch — CSS/markup
only, no data path touched.

- [ ] 8.1 Modify `src/Ways.Web/src/paginas/CuentaCorriente.tsx`: add a
  print layout section + `d-print-none` on chrome (filters, nav, download
  button) — same component, same fetch, `@media print` only. *(design
  decision 13 — no dedicated print route, no second fetch)*
- [ ] 8.2 Modify `src/Ways.Web/src/paginas/CajaZ.tsx`: same treatment for
  the turno detail print layout.
- [ ] 8.3 Add/modify the shared print stylesheet (`@media print` rules:
  page margins, hide `d-print-none`, table borders for print legibility).
- [ ] 8.4 [P] `d-print-none`-presence tests on both pages (per the design's
  recorded exemption: print rendering itself has no automated assertion
  beyond this presence check — verified by eye).
- [ ] 8.5 Run `judgment-day`; fix; re-judge until clean.
- [ ] 8.6 Branch `feat/stage11-slice8-vistas-impresion` off `main` (parent:
  slice 6b); PR; merge stacked-to-main.

**Test plan**: `d-print-none` presence only — print rendering is an
explicitly recorded exemption (design Testing Strategy), verified by eye.

**Verify**: `npm run test -- CuentaCorriente CajaZ`

---

## Slice 9: Existencias — Droppable To Etapa 13 (PR 9)

**Start**: slices 1b and 4 merged (parallel to 2, 3, 5a/5b, 7). **Finish**:
`/api/reportes/stock/existencias` live, exportable, modest screen.
**Rollback**: revert this single branch — endpoint, export and screen
disappear, no migration, no persisted row to unwind. **If the budget
tightens, this slice is dropped whole to Etapa 13** (proposal decision 10)
— recorded so dropping it is a decision, not an oversight.

- [ ] 9.1 Create `src/Ways.Application/Reportes/ServicioDeReportesDeStock.cs`:
  stock joined to articulos for a punto de venta, covered by
  `ix_stock_punto_venta`. *(proposal decision 10; spec reportes-de-gestion:
  Existencias Report Joins Stock To Artículos Under The Same Gate)*
- [ ] 9.2 Modify `ReportesEndpoints.cs`: `GET /stock/existencias` under
  `LecturaDeReportes` — no `idArticulo` required, unlike `GET /api/stock`.
- [ ] 9.3 Extend `ExportacionDeReportes.cs`: `De` mapper for existencias.
- [ ] 9.4 Modify `ReportesEndpoints.cs`: `GET /stock/existencias/export`
  sibling.
- [ ] 9.5 Create `src/Ways.Web/src/paginas/Existencias.tsx`: modest table
  screen (punto de venta filter, `BotonDeDescarga`).
- [ ] 9.6 Modify `App.tsx`/`Layout.tsx`: `/reportes/existencias` route
  (`LecturaDeReportes`) and nav entry.
- [ ] 9.7 Gate guard: `dotnet ef migrations has-pending-model-changes` → no
  pending changes.
- [ ] 9.8 [P] The house 4-test pattern for the endpoint.
- [ ] 9.9 [P] Equality test on the export vs the JSON listing.
- [ ] 9.10 [P] 403 test: role one step below `LecturaDeReportes` (Vendedor)
  rejected on both routes.
- [ ] 9.11 [P] `no-idArticulo-required` test: 40 stocked articulos for a PV
  → all 40 rows returned with only `idPuntoVenta` supplied. *(spec:
  Existencias Needs No idArticulo, Unlike GET /api/stock)*
- [ ] 9.12 [P] `Existencias.test.tsx` per `web-descriptor-tests`.
- [ ] 9.13 Run `judgment-day`; fix; re-judge until clean.
- [ ] 9.14 Branch `feat/stage11-slice9-existencias` off `main` (parent:
  slices 1b + 4); PR; merge stacked-to-main.

**Test plan**: 4-test pattern, equality, 403, no-idArticulo-required,
descriptor tests.

**Verify**: `dotnet test --filter FullyQualifiedName~Existencias` /
`npm run test -- Existencias`

---

## Global Cross-Slice Tasks

- **`dto-contract-honesty` tension, accepted (proposal decision 4)**: the
  one-value `formato` enum is validated and load-bearing; every slice that
  adds a route must parse it through `FormatoDeExportacion.Parsear`
  (slice 1b), never bind it as a framework enum.
- **`web-descriptor-tests` compliance**: every new screen (6a, 6b, 7's
  `Tesoreria.tsx`, 9's `Existencias.tsx`) ships a colocated descriptor
  test — enforced per-slice above, not deferred to a final sweep.
- **`react-async-state` compliance**: `BotonDeDescarga`'s busy/re-entrancy
  guard (slice 4) is the single shared implementation every screen slice
  (6a, 6b, 7, 9) wires in, never re-implemented per screen.
- **Containment discipline carries a known repo-wide gap** (state.yaml
  notes, inherited from stage-10 WARNING-1): the source-scan test (1a.8)
  is real but there is still no CI lint rule enforcing it outside the test
  suite — same weakness as recharts, recorded, not fixed here.

---

## Dependency Summary

```
Slice 1a (export seam)
  └─ Slice 1b (first export: ventas/resumen)
       ├─ Slice 2  (remaining 8 stage-10 exports)          ─┐
       ├─ Slice 3  (ConstruirQuery + listing exports)        │ parallel
       ├─ Slice 4  (api.descargar() + BotonDeDescarga)       │ fronts,
       ├─ Slice 5a (G2 listing) → Slice 5b (G2 detail)       │ merge in
       ├─ Slice 7  (tesorería, needs 4)                      │ any order
       └─ Slice 9  (existencias, needs 4, droppable)        ─┘
  Slice 4 + Slice 5a → Slice 6a (histórico web)
  Slice 4 + Slice 5b → Slice 6b (caja Z web)
       Slice 6b → Slice 8 (print views)
```

Merge order is strictly 1a→1b→(2‑9 in any order that respects the arrows
above), stacked-to-main. Everything after 1b is genuinely independent
except the caja fronts (5a→5b→6a/6b→8), which stays a single ordered chain.

---

## Review Workload Forecast

| Field | Value |
|-------|-------|
| Estimated changed lines | ~3 570 total (12 slices: 300/290/380/350/330/300/280/250/260/330/200/300) |
| 400-line budget risk | Low overall — no single slice reaches 400; slice 2 (~380) is the closest and carries Medium per-slice risk |
| Chained PRs recommended | Yes |
| Suggested split | 12 PRs, stacked-to-main, per the Suggested Work Units table above |
| Delivery strategy | ask-on-risk, resolved to stacked-to-main under the owner's delegated mandate |
| Chain strategy | stacked-to-main |

Per-slice budget risk: 1a Low (~300) · 1b Low (~290) · 2 Medium (~380) · 3
Low (~350) · 4 Low (~330) · 5a Low (~300) · 5b Low (~280) · 6a Low (~250) ·
6b Low (~260) · 7 Low (~330) · 8 Low (~200) · 9 Low (~300). Slice 2 is the
one to watch during apply — if it grows past 400, split the coverage/
PROVISIONAL-label tests for rentabilidad/comisiones into their own commit
within the same PR before splitting the slice itself.

Decision needed before apply: No
Chained PRs recommended: Yes
Chain strategy: stacked-to-main
400-line budget risk: Low
