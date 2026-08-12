# Archive Report: Stage 11 — Infraestructura de exportación + reportes descargables

**Change**: `stage-11-exportacion-reportes`
**Archived**: 2026-08-12
**Final state**: `main` @ `9c4074b`, 12/12 slices merged (PRs #87–#98)
**Verdict**: PASS WITH WARNINGS (0 CRITICAL, 2 WARNING — both remediated/accepted, 3 SUGGESTION — backlog)

---

## 1. Shipment Map — 12 Slices, 12 PRs, Stacked-to-Main

The design refined the proposal's 9-slice plan into 12 by splitting three
slices at natural seams (1→1a/1b at the export-seam/first-export boundary;
5→5a/5b at the G2 listing/detail boundary; 6→6a/6b at the same boundary,
mirroring 5a/5b).

| # | Slice | Branch (actual) | PR | Content |
|---|---|---|---|---|
| 1a | Export seam | `feat/stage11-slice1a-seam` | #87 | `TablaExportable`/`IExportadorDeTabla`/`ExportadorXlsx`, licence audit (ClosedXML approved), DI wiring, containment test |
| 1b | First export | `feat/stage11-slice1b-ruta-export` | #88 | `GuardaDeTope`, `ResultadoDeExportacion`, `FormatoDeExportacion`, `/ventas/resumen/export` — the pattern every later slice repeats |
| 2 | Remaining stage-10 exports | `feat/stage11-slice2-exports-rentabilidad` | #89 | Eight `/export` siblings; rentabilidad/comisiones stack `LecturaDeRentabilidad` + coverage/PROVISIONAL |
| 3 | Listing exports | `feat/stage11-slice3-exports-listados` | #90 | `ConstruirQuery` extraction, ventas/compras/estado-de-cuenta exports, `+1` race backstop |
| 4 | Web download plumbing | `feat/stage11-slice4-descarga-web` | #91 | `api.descargar()`, `BotonDeDescarga`, Tablero wiring |
| 5a | G2 listing | `feat/stage11-slice5a-ver-cajas` | #92 | `GET /api/reportes/cajas` + `/api/caja/turnos/{id}/detalle` (pulled forward from 5b) |
| 5b | G2 detail export + Z-report | `feat/stage11-slice5b-exports-caja` | #93 | Both G2 exports (listing + detail), `ExportacionDeCaja.cs` |
| 6a/6b | Caja screens + tesorería web | `feat/stage11-slice6-pantallas-caja` | #94 | `HistoricoDeCajas`, `CajaZ`, `Tesoreria` screens (batched — shared nav wiring) |
| 7 | Tesorería backend | `feat/stage11-slice7-tesoreria` | #95 | G3 endpoint + export (web deferred to slice 6 batch) |
| 8 | Print views | `feat/stage11-slice8-print-views` | #96 | `@media print` + `d-print-none`, estado de cuenta + Caja Z |
| 9 | Existencias | (worktree branch, kept as-is) | #98 | `/reportes/stock/existencias` + export + screen — shipped, not dropped |

Merge order: `1a → 1b → {2, 3, 4, 5a→5b, 7, 9 in any order} → {6a/6b needs 4+5a/5b} → 8 needs 6b`.
Note: PR numbering (#87–#98, 12 PRs) does not map 1:1 to the slice list above by
arithmetic count because slices 5a/5b/6a/6b were partly batched across apply
runs (5a pulled forward 5b's JSON detail route; 6a+6b+7's web half were merged
into one batch) — the final merged state ships all 12 slices' content exactly
as specified, confirmed by the verify report's task-complete matrix (130/130).

**Task completion gate**: `tasks.md` — 130/130 tasks checked (`- [x]`), 0
unchecked. No stale-checkbox reconciliation was needed; verified directly by
reading the persisted `tasks.md` before this archive.

---

## 2. Verify Verdict And Remediations

**Verdict**: PASS WITH WARNINGS (`verify-report.md`, verified against `main`
@ `9c4074b`).

- 29/29 spec scenarios across the 5 spec files have a passing, runtime-verified
  covering test.
- All 4 suites green at exact expected counts (see §6).
- Zero-schema gate held the entire stage (migrations still end at
  `CostoCongeladoEnVentaEtapa9`).
- 0 CRITICAL issues.

**WARNING-1 — Licence audit PR body under-itemized.** Proposal decision 1 and
task 1a.1 require the PR body to record the full per-package licence table.
PR #87's original body stated only the audit's conclusion (8 transitive
packages, 7 MIT + `SixLabors.Fonts 1.0.0` Apache-2.0), not the itemized table.
**REMEDIATED**: an itemized per-package comment (id, version, licence) was
added to PR #87 after verify flagged the gap. The underlying audit conclusion
was independently re-verified by `sdd-verify` and confirmed correct
(re-ran `dotnet list … --include-transitive` and matched the claim exactly).

**WARNING-2 — G2 listing export's `+1` race-backstop mutation test not
duplicated.** `ListarCierresParaExportacionAsync` (slice 5a/5b) introduces its
own `Contar → Take(topeDeFilas + 1)` clause, structurally identical to the
already-proven Slice 3 pattern (`VentasListadoExportTests`, its own
`DbCommandInterceptor`), but no dedicated `turnos_caja`-specific interceptor
test exists for it — only a plain over-cap regression test (task 5a.13),
which passes but does not prove the race window specifically. **ACCEPTED**
with the proportionality rationale recorded at task 5a.13 (building a
`turnos_caja`-specific interceptor was judged out of proportion for that
slice's budget); carried to backlog (§7).

---

## 3. The Judgment-Day Season

Twelve independent judgment-day rounds ran, one per slice, per
`protocolo-pr-solo-dev` (dual blind review, iterate to a clean round before
merge).

**Round-1 REJECTs (3):**
1. **Slice 1b — equality-per-bucket.** The first equality test asserted
   aggregate totals only; rejected for not proving per-bucket agreement
   between the export and the JSON endpoint (the same defect class later
   generalized as mutation-proof-tests rule 6).
2. **Slice 5b — three-workbook MAJORs.** The G2 detail/Z-report export round
   surfaced three MAJOR findings around workbook section completeness/
   ordering before reaching a clean round.
3. **Slice 9 — the UTC-"hoy" production bug.** The existencias export
   resolved "today" in UTC rather than the punto de venta's own timezone for
   its header/filename — a real production-facing defect (a store west of
   UTC could get yesterday's/tomorrow's date on the file), fixed before merge
   (see also the separate post-archive fix `08e7707` on `main`, applied after
   this stage closed, hardening the same "hoy" resolution).

**~20 confirmed findings total** across the 12 rounds, fixed pre-merge.

**Skill growth**: `mutation-proof-tests` grew from 5 to 7 hard rules during
this stage:
- **Rule 6** (equality tests must assert every column of every row, not
  aggregates) — born from five occurrences in this stage alone: unasserted
  bucket rows, coverage counts matched as digit substrings, skipped date
  columns (×2), rotatable per-ticket totals with count+sum preserved, and a
  droppable whole section.
- **Rule 7** (async assertions have their own confound — a retrying matcher
  can pass on its first tick before a stale microtask lands) — born from the
  `CajaZ` stale-response test, which survived its own strengthening until the
  flush was forced inside `act`.

---

## 4. Licence Audit (Binding Condition, Task 1a.1)

Command: `dotnet list src/Ways.Infrastructure/Ways.Infrastructure.csproj
package --include-transitive --format json`, after adding the `ClosedXML`
`PackageReference`. Read from each package's `.nuspec` in `$HOME/.nuget/packages/`.

| Package | Version | Licence |
|---|---|---|
| ClosedXML | 0.104.2 | MIT |
| ClosedXML.Parser | 1.2.0 | MIT |
| DocumentFormat.OpenXml | 3.1.1 | MIT |
| DocumentFormat.OpenXml.Framework | 3.1.1 | MIT |
| ExcelNumberFormat | 1.1.0 | MIT |
| SixLabors.Fonts | 1.0.0 | Apache-2.0 |
| (2 further MIT transitives, matching PR #87's "7 MIT + 1 Apache-2.0" count) | — | MIT |

**Result**: 8 transitive packages, 7 MIT + 1 Apache-2.0 (`SixLabors.Fonts`,
the one flagged for particular attention per task 1a.1). All within the
allowed set (MIT / Apache-2.0 / BSD-* / MS-PL). No fallback to MiniExcel was
needed. Versions pinned exactly (no range). Independently re-verified by
`sdd-verify` (see WARNING-1 remediation, §2) — the conclusion was correct
from the start; only the PR-body itemization was missing, and that gap is
now closed.

---

## 5. Autonomous Decisions

### Proposal-level (11, delegated technical authority, `state.yaml`/`proposal.md`)

1. **XLSX library**: ClosedXML (MIT), conditional on licence audit; MiniExcel
   (Apache 2.0) pre-approved fallback (not needed — audit passed).
2. **No CSV in v1** — XLSX has zero locale ambiguity; CSV is a future additive
   `formato` value.
3. **Synchronous, buffered, hard row cap (25 000) that refuses, never
   truncates** — counted before generation.
4. **Endpoint pattern**: `GET {ruta}/export?formato=xlsx`, same `MapGroup`,
   declared immediately after its source route — structural policy/parameter
   inheritance by co-location.
5. **Role gating inherited structurally, with one explicit caja split**:
   turno detail/Z-report under `OperacionDePos` (the cajero's own close);
   G2 histórico + G3 book under `LecturaDeReportes` (management).
6. **G2/G3 minimal new aggregation**: G3 = zero new aggregation; G2 detail =
   zero new derivation (both already-modeled); G2 listing = the only genuinely
   new aggregation, and it sums already-persisted `arqueos_turno` rows.
7. **File naming/branding**: deterministic ASCII names, in-sheet header block;
   branding beyond a text header (logo, colours, templates) flagged for the
   owner — the one genuinely product-weight call of the stage.
8. **Web downloads via `fetch` + blob (`api.descargar()`)**, not a plain
   anchor — authentication works either way, but failure handling (403/400/
   401) requires the SPA's existing error path, which a plain `<a href>`
   cannot reach.
9. **No server-side PDF library** — browser print view (`@media print`)
   instead; QuestPDF's revenue-triggered licensing cliff explicitly rejected
   for a use case that doesn't exist yet (deferred to Etapas 18/19).
10. **Stock exportable ships minimal and last, droppable to Etapa 13** —
    shipped in full (slice 9), not dropped.
11. **One seam, one containment folder** — `TablaExportable`/
    `IExportadorDeTabla` in Application, `ExportadorXlsx` the only
    Infrastructure file naming the library, containment enforced by a real
    xUnit source-scan test.

### Design-level (13, `design.md`) — notably decisions 5–13 governed
implementation shape: typed cells over pre-formatted strings (1), self-
validating `TablaExportable` (2), zone-less `FechaHora` conversion (3),
xUnit source-scan containment over a CI lint rule (4), row cap as a bound
option not a `const` (5), two cap shapes by report kind — aggregate
(count-after-mapping) vs. listing (`COUNT(*)`-before) (6), `ConstruirQuery`
extraction never duplicated (7), equality proven by reading the workbook back
rather than golden-file comparison (8), `formato` bound and parsed by the
application layer for a pinned error code (9), the caja detail MOVED to
`/api/caja/turnos/{id}/detalle` so `OperacionDePos` is inherited rather than
fought (10, the design's load-bearing refinement of the proposal), tesorería
ordered by `id` never `fecha` (11), `api.descargar()` shares `pedir`'s error
path by extraction (12), no separate print route (13).

### Batching / size:exceptions

Delivery strategy resolved to `stacked-to-main` (chained PRs required per the
tasks-phase Review Workload Forecast — ~3 570 total lines across 12 slices,
no single slice at High risk, slice 2 the closest at Medium/~380). Several
apply runs batched adjacent slices for shared-context efficiency, always
recorded inline in `tasks.md` as APPLY-RUN NOTEs:
- Slice 5a's batch pulled forward 5b's JSON detail route ("first link of the
  caja chain").
- Slice 5b's follow-up batch closed both deferred exports (G2 listing +
  detail) in one run.
- Slices 6a + 6b + slice 7's deferred web half (screen/routing/nav) were
  merged into one apply run/branch/PR — "the three caja/tesorería screens
  share nav wiring and patterns; batching them avoids three trivial PRs".

No `size:exception` was required — every PR stayed under budget once judged.

### The PR-95 Incident (Slice 7, Tesorería) — 3 Permanent Rules

`main` was broken for approximately 30 minutes during the slice-7 merge,
caused by a naive resolution of a conflict on a file created independently by
two branches, compounded by exit codes being swallowed inside a shell pipe
(masking the failure until later). The breakage was repaired deterministically
and the incident produced 3 permanent process rules, recorded to the
orchestrator's persistent memory at the time of the incident (`state.yaml`
apply-phase notes, 2026-08-12). The rule text itself lives in that memory
record rather than in this change's file artifacts; this report cites the
incident's existence and resolution faithfully without restating rule wording
not present in the read artifacts, to avoid drift from the source.

### The Shared-Checkout Stray-Branch Incident (Slice 9, Existencias)

During slice 9's apply run (isolated worktree), an accidental
`git checkout -b feat/stage11-slice9-existencias` was executed against the
**shared** checkout (`C:\ways`) instead of the intended worktree, then caught
and abandoned. The shared checkout was left switched to that stray,
commit-less branch (same tip as `main`, zero divergence — no data at risk).
The worktree itself kept its pre-existing branch name
(`worktree-agent-ac822c711ae33bb08`) rather than being renamed. **Backlog**:
the shared checkout at `C:\ways` needs a `git checkout main` cleanup by
whoever owns that checkout (recorded in task 9.14; not performed as part of
this archive — archive is content-only for this phase, per this run's scope).

### Flakiness

The documented environmental integration-suite flakiness (non-reproducible,
confirmed twice more during this stage per the apply-phase notes) did **not**
reproduce during the final verify run — the 896-test integration suite passed
green on the first run, no re-run needed.

---

## 6. Suites At Close

| Suite | Result |
|---|---|
| Domain | 394 passed / 0 failed / 0 skipped |
| Application | 257 passed / 0 failed / 0 skipped |
| Integration | 896 passed / 0 failed / 0 skipped (first run, no re-run needed) |
| vitest (Ways.Web) | 544 passed / 0 failed, 34/34 test files |

Coverage tooling not wired into this run — consistent with prior stages'
convention, not blocking.

---

## 7. Backlog (Carried Forward, Not Blocking)

- **W2 — G2 listing export race-backstop mutation test.** A dedicated
  `turnos_caja`-specific `DbCommandInterceptor` test (mirroring Slice 3's
  `VentasListadoExportTests` pattern) for `ListarCierresParaExportacionAsync`'s
  `Contar → Take(tope+1)` clause. Accepted-not-blocking per verify WARNING-2;
  worth closing if `turnos_caja` export volume ever approaches a real
  concurrent-insert scenario.
- **`ComprasPorProveedor` zona in the JSON contract.** Its export header
  hardcodes `"N/A"` for zona horaria because the JSON response never echoes
  `ZonaHoraria` (unlike every other reportes-de-gestión response); resolving
  it separately would violate the no-re-query rule. Fix belongs in the JSON
  contract itself (verify SUGGESTION-1).
- **Existencias' aggregate-cap shape guards after materializing the full
  table, not before.** Unlike the other aggregates (bounded by construction —
  buckets, PVs, vendedores), a punto de venta's article catalog has no
  structural upper bound; low risk today at store-scale catalogs, but the one
  aggregate export whose row count isn't bounded by a small fixed dimension
  (verify SUGGESTION-2).
- **Column-title naming consistency across exports** — `ArticulosTop`/
  `Rentabilidad` title their text column "Descripción"; `Existencias` titles
  the equivalent "Nombre". Each faithfully reflects its own upstream DTO field
  name; purely cosmetic, no spec requirement pins column titles across reports
  (verify SUGGESTION-3).
- **Modal / print-safety follow-up** — flagged for a future pass over the
  print-view interaction surface (estado de cuenta, Caja Z) as usage grows
  beyond the `@media print` + `d-print-none` baseline this stage shipped; not
  independently re-verified against this change's own artifact set in this
  archive pass and carried here per direct instruction, for the next stage
  touching print/export UI to weigh.
- **`articulos_empresas` replace-set concurrency gap** and the **importe CHECK
  micro-gate** — both carried over from stage 8, still open, untouched by
  this stage (per `state.yaml`'s deferred/adjacent list).
- **`ways_owner` as a testcontainer superuser** — repo-wide migration-test
  weakness; irrelevant to this stage (no migration), still open.
- **Recharts containment has no CI lint rule** (stage-10 WARNING-1) — this
  stage adds a second containment boundary (the Excel library) with the same
  weakness: a real xUnit source-scan test exists, but nothing enforces it
  outside the test suite. A shared import-boundary/lint rule is the natural
  future fix for both.
- **PascalCase / legacy naming debt** — tracked as part of the program's
  ongoing convention-alignment carryover; not independently re-verified
  against this change's own file artifacts in this archive pass, carried here
  per direct instruction for whichever future stage next touches the affected
  surface.
- **Shared-checkout cleanup** (`C:\ways` left on the stray
  `feat/stage11-slice9-existencias` branch, zero divergence from `main`) —
  see §5.

---

## 8. Handoff — Stage 11 → Stage 12 And Beyond

This was **a pattern-setting stage, not a feature stage**
(`state.yaml`: "Its real deliverable is one decided, tested, licence-clean
way to turn a report into a file, chosen ONCE"). The concrete handoff:

- **The export pattern is now the house standard for any future report.**
  `TablaExportable` + `IExportadorDeTabla` + the `/export` sibling-route
  convention + the counted-refusing row cap + the deterministic-ASCII-name +
  in-sheet-header-block contract is the ONE way to turn a report into a
  downloadable file in this codebase. Adding a new export costs one mapping
  and one route line — demonstrated by `/stock/existencias`, the last report
  added in this stage, at proposal-stated cost.
- **Etapa 12 (lotes/FEFO)** and any future report-bearing stage (13, 14, 18)
  consume this infrastructure directly: any new report gets an `/export`
  sibling for free by following the co-location + mapper convention, with no
  new library, no new route pattern, and no new policy-inheritance question
  to answer.
- **The alert/report infrastructure this stage completed** (nine stage-10
  reports + G2 + G3 + existencias, all exportable) is the full surface Etapa
  12's lotes/FEFO work is expected to extend, not replace.
- **Sección G of doc-01 (Ver Cajas / Caja General) is now fully closed**
  except the deliberately excluded G4 (manual tesorería entry — the
  `tesoreria` spec's standing exclusion, not reopened by this stage).
- **Etapas 18 and 19 own the still-open server-side PDF question** (decision
  9) — deliberately left there since they are the stages that can see the
  actual requirement (etiquetas físicas; comprobante fiscal con QR).
- **Post-archive hardening already landed on `main`** (commit `08e7707`,
  after this stage's PRs merged): a fix to compute the existencias export's
  "hoy" in the punto de venta's own timezone rather than server/UTC time —
  the same production-facing bug class the slice-9 judgment-day round caught
  and fixed pre-merge (§3), now further hardened. `vitest` test
  `86f22cf` fixed a related stale-response assertion to the rule-7 pattern.

---

## Traceability

- Proposal: `openspec/changes/stage-11-exportacion-reportes/proposal.md`
- Delta specs (merged, source retained under the change folder pre-archive):
  `specs/exportacion-de-reportes/spec.md`, `specs/historico-de-cajas/spec.md`,
  `specs/reportes-de-gestion/spec.md`, `specs/rentabilidad-y-comisiones/spec.md`,
  `specs/tesoreria/spec.md`
- Design: `openspec/changes/stage-11-exportacion-reportes/design.md`
- Tasks: `openspec/changes/stage-11-exportacion-reportes/tasks.md` (130/130 complete)
- Verify report: `openspec/changes/stage-11-exportacion-reportes/verify-report.md`
- State: `openspec/changes/stage-11-exportacion-reportes/state.yaml`
- Merged main specs (updated by this archive):
  - `openspec/specs/exportacion-de-reportes/spec.md` (new)
  - `openspec/specs/historico-de-cajas/spec.md` (new)
  - `openspec/specs/reportes-de-gestion/spec.md` (delta merged — 2 requirements added)
  - `openspec/specs/rentabilidad-y-comisiones/spec.md` (delta merged — 1 requirement added)
  - `openspec/specs/tesoreria/spec.md` (delta merged — 1 requirement added)
