# Archive Report: stage-10-agregacion-dashboard

**Change**: stage-10-agregacion-dashboard
**Archived**: 2026-08-12 (content merge/report by sdd-archive; folder relocation performed
by the orchestrator's deterministic script per this run's CONTENT ONLY boundary)
**Verify verdict inherited**: PASS WITH WARNINGS (1 CRITICAL remediated pre-archive, 3
WARNING, 2 SUGGESTION carried forward)
**Artifact store**: openspec

---

## 1. What Shipped

Etapa 10 of `docs/11-programa-post-paridad.md` (doc-11:98-119): the system's first
server-side aggregation layer (`/api/reportes/*`, direct parameterized SQL / LINQ, no
schema change) and a web dashboard (`Tablero`) on top of it. Ten slices, eleven PRs
(#76-#86 — slice 4 "artículos y margen" split into two PRs because the orchestrator
narrowed one apply batch to `/rentabilidad` only, with `/articulos/top` completed via a
parallel branch), all merged stacked-to-main:

| Slice | Scope | Notes |
|---|---|---|
| 1 | Parámetros y políticas | `zona_horaria` + `comision_porcentaje` ParametroConocido keys, `LecturaDeReportes` + `LecturaDeRentabilidad` policies |
| 2 | Ventas resumen | The stage's only raw-SQL surface (`LectorDeSerieTemporal`); business-day bucketing, ticket promedio |
| 3 | Ventas por dimensión | `/por-punto-venta`, `/por-vendedor`, `/por-medio-pago` — PR #82 |
| 4 | Artículos + margen (rentabilidad) | Two PRs: `/rentabilidad` landed first; `/articulos/top` completed via a parallel branch (PR #80, `ServicioDeReportesDeArticulos.cs`) |
| 5 | Egresos | `/compras/por-proveedor`, `/gastos/resumen` |
| 6 | Recharts + `componentes/graficos/` | Containment wrappers, no page consumer yet |
| 7 | Tablero G1 parity | 7-day default, ventas/gastos series, ticket promedio |
| 8 | Tablero dimensiones | 4 breakdown panels, own generation/`cargando` per panel — 2 judgment-day rounds |
| 9 | Tablero rentabilidad | Margin panel, mandatory coverage banner, Admin-only — 2 judgment-day rounds, both REJECT on round 1 |
| 10 | Comisiones (PROVISIONAL) | Droppable-in-full report; full 9-route × 4-role authorization matrix landed here (PR #86, `ReportesAutorizacionTests`, 36 tests) |

No-schema-change gate held for the entire stage: 18 migration files, last is
`20260811033540_CostoCongeladoEnVentaEtapa9` (stage 9) — zero migrations added by stage
10, confirmed via `dotnet ef migrations has-pending-model-changes` at slice close and
again at verify.

---

## 2. Verify Verdict And Remediation

`sdd-verify` returned **PASS WITH WARNINGS** (2026-08-12):

- **1 CRITICAL (remediated before archive)**: `tasks.md` checkboxes 4.1
  (`ObtenerTopArticulosAsync` / `GET /articulos/top`) and 5.5 (full
  `ReportesAutorizacionTests` matrix) were unchecked despite the underlying work being
  complete, tested, and merged to main (4.1 via the PR #80 parallel branch; 5.5 via
  slice 10 task 10.6 / PR #86). The orchestrator corrected the checkboxes and struck the
  stale "NOT done" prose in task 4.5 with a dated post-verify correction note. Zero
  unchecked implementation tasks remain — confirmed before this archive run began (Task
  Completion Gate).
- **3 WARNING**: (1) `specs/tablero`'s "Recharts Is Contained To componentes/graficos"
  scenario has no automated regression test — true today by source inspection, not CI-
  enforced. (2) Documentation-drift entries in `tasks.md` (EF-translation anonymous
  projections at 3.1, `PorCategoria` always-present at 5.2, granularidad narrowing at
  8.2, `porArticulo` mirrored-not-rendered at 9.2, panel-independence hook at 7.5/8.2,
  comisiones record shapes at 10.1) — independently verified against code, all accurate,
  no action needed. (3) The stale `tablero` spec scenario on granularity — see the
  authorized amendment below.
- **2 SUGGESTION**: (1) production web bundle 883 KB / 237 KB gzip, flagged by Vite's
  chunk-size guidance — a future code-splitting candidate, not a defect. (2) Rewrite the
  stale granularity scenario at archive time — done in this run (see §7).

Suites at verify time matched the recorded baseline exactly: Domain 394, Application
219, Integration 823 (real Postgres via testcontainer), vitest 476 — see §5.

---

## 3. Judgment-Day Season Summary

Ten independent judgment-day rounds (one per slice, per `protocolo-pr-solo-dev`), not
one pass at the end. ~15 confirmed findings pre-merge across the season, split roughly
into two production-facing MAJORs and the rest test-honesty findings surfaced by
`mutation-proof-tests` (per state.yaml's apply-phase note).

- **Slice 2 (ventas/resumen)** drew a mutation-proven finding early in the API phase:
  the slice's mutation-testing pass (tasks 2.9-2.13 — the NCX-sign clause, the timezone
  edge case, the ticket-promedio exclusion) surfaced a test that did not actually fail
  when its guarding clause was mutated, forcing a fix before the round could close clean.
  This slice set the mutation-proof-tests precedent every later slice's 4-test pattern
  followed.
- **Slice 8 (tablero dimensiones), round 1**: Judge A REJECT/MAJOR — the panel set had
  been narrowed to 3 of the 4 required breakdown panels. Resolved by completing the 4th
  panel (top artículos) in the same branch; Judge B's two coverage-gap minors (a panel-
  independence test and a table-half assertion per panel) were folded into the same fix.
  Round 2: Judge A APPROVE with the 4-panel contract verified.
- **Slice 9 (tablero rentabilidad), round 1**: **both** judges REJECT. Judge A MAJOR —
  `bannerDeCobertura` falsely claimed "100% real" when `incluirEstimados=true` and part
  of the margin was actually estimated (a banner-honesty defect: the UI would say
  "costo real" while showing a margin that included estimated cost), plus a minor on the
  `ventaTotal === 0` division-by-zero fallback. Judge B MAJOR — missing stale-response
  test for `PanelDeRentabilidad`; MINOR — the `incluirEstimados` toggle test only
  asserted the query parameter, not the rendered figure. All three fixed in the same
  branch (tasks.md 9.1-9.3). Round 2 re-judge closed clean.
- The remaining slices (1, 3, 4, 5, 6, 7, 10) each ran their own judgment-day round with
  minor findings absorbed into the same PR before merge — recorded per-slice in
  `tasks.md` (e.g. slice 5's role-matrix consolidation deviation, slice 7's carried-over
  `titulo`/`aria-label` prop debt paid from slice 6's judges).

These two MAJORs (the coverage-banner honesty defect and the panel-narrowing) are the
two production-facing findings state.yaml's apply note calls out explicitly; the rest of
the ~15 confirmed findings are test-honesty gaps closed via mutation-proof evidence.

---

## 4. Autonomous Decisions Of The Stage

Proposal was written under **autonomous overnight delegation** (owner asleep, decisions
delegated to the orchestrator with a conservative reversible bias, owner reviews the
log). Nine proposal decisions, each with code- or doc-verified provenance (state.yaml
`propose` phase, `notes`):

1. **Direct SQL, no materialized views** — an MV would be an RLS hole in this
   architecture (force RLS cannot apply to a matview); freshness and volume both favor
   direct queries. Reversible: a `security_invoker` view or indexed rollup is additive.
2. **Business-day granularity + timezone via a new `zona_horaria` ParametroConocido
   key**, resolved punto de venta → empresa → default, first string-typed parametro
   (gotcha: must be stored JSON-quoted, `ServicioDeParametros.ValidarTipo` deserializes
   against the declared CLR type).
3. **Two new policies**: `LecturaDeReportes` (Supervisor+Admin, volume/operational) and
   `LecturaDeRentabilidad` (Admin only, cost/margin/commission) — narrow-policy
   precedent (`SupervisionDeCuentaCorriente`), asymmetric-risk rationale (widening later
   is one line; un-showing a number already seen is not a technical fix).
4. **Recharts** as the chart library — MIT, React-19-compatible, SVG (jsdom/RTL-
   friendly), contained to `componentes/graficos/`.
5. **Comisiones shipped PROVISIONALLY, last, droppable in full** — the formula is
   explicitly a business decision the owner hadn't made (doc-11:118-119); nothing
   persisted, default rate `0`, PROVISIONAL label in both API and UI.
6. **API first (slices 1-4/5), then web (5/6-10), G1 parity before anything new** — the
   dashboard is demonstrable at parity before breadth is added.
7. **Explicit per-report endpoints under `/api/reportes/*`**, no generic
   groupBy/metric/filter surface — role-gating, scenario-testing, and indexing all
   require typed, named endpoints.
8. **Recargo por medio de pago NOT activated** — the column exists but no write path
   applies it; activation is a checkout write-path change, out of scope for a read-only
   stage.
9. **Aggregation semantics pinned and verified in code**: net sales with no sign branch
   (NCX is already negative by construction), ticket promedio excludes NCX both sides,
   compras bucketed by `fecha_recepcion` + `confirmada` only, margin excludes estimated
   by default with mandatory coverage.

**Size exceptions**: most slices ran under `size:exception` — every overflow was test-
depth (the 4-test mutation-proof pattern per endpoint), never scope creep. Review
Workload Forecast (tasks.md) projected ~2,920 total changed lines across 10 slices, no
single slice over 400 lines budgeted, delivery strategy `ask-on-risk` resolved to
chained/stacked-to-main under the overnight mandate.

**Orchestrator briefing errors and corrections** (from tasks.md deviation notes and the
verify-report CRITICAL):
- Slice 4's apply batch was narrowed mid-flight to `/rentabilidad` only (parallel-
  worktree scope split, three slices merging the same night); `/articulos/top` (task
  4.1) and its test suite (task 4.5) were left "NOT done" in the recorded prose but were
  actually completed via a separate parallel branch that merged as PR #80. The tasks.md
  prose was not updated at the time, producing the verify-phase CRITICAL (stale
  checkboxes for merged work) — corrected by the orchestrator with a dated post-verify
  note (2026-08-12) rather than by re-running apply.
- Slice 5's task 5.5 (full 9-route authorization matrix) was explicitly deferred with an
  instruction for "the orchestrator to reconcile... when merging slices 3/4/5" — that
  reconciliation did not happen at the 3/4/5 merge point as planned; it landed instead
  in slice 10 (task 10.6, PR #86), one slice later than briefed.
- Slice 5's branch was created as `feat/stage10-slice3-compras-gastos` against the
  file's own documented `feat/stage10-slice5-egresos` — a recorded branch-name mismatch
  from the orchestrator's launch instructions, left as-is (no functional impact,
  recorded for the record).
- Across slices 2-7, 9, 10 `sdd-apply` explicitly did not run `judgment-day` itself
  (documented per-slice as "NOT run by sdd-apply — requires sub-agent delegation, out of
  the apply executor's scope"), correctly deferring that boundary to the orchestrator on
  every slice rather than silently skipping the review.

---

## 5. Suites At Close

| Suite | Result |
|---|---|
| `dotnet test tests/Ways.Domain.Tests` | 394 passed |
| `dotnet test tests/Ways.Application.Tests` | 219 passed |
| `dotnet test tests/Ways.IntegrationTests` | 823 passed (real Postgres via testcontainer) |
| `npx vitest run` (`src/Ways.Web`) | 476 passed (28 test files) |

Migration gate: `dotnet ef migrations has-pending-model-changes` → "No changes have been
made to the model since the last migration." (18 migration files, last is stage 9's
`CostoCongeladoEnVentaEtapa9`).

---

## 6. Backlog / Deferred Items

- **`specs/tablero`'s stale granularity scenario** — amended in this archive run (§7);
  no longer open.
- **Per-article rentabilidad breakdown (`porArticulo`) is mirrored in `tipos.ts` for
  contract completeness but not rendered** in `PanelDeRentabilidad` (task 9.2, explicit
  scope call: banner + figure only this slice). Open UI work for a future slice.
- **Accessibility (a11y) carried forward** — no dedicated a11y pass in this stage beyond
  the `titulo`/`aria-label` props paid down on the chart wrappers in slice 7 from
  slice 6's judges. No automated a11y regression coverage added.
- **Comisiones formula is PROVISIONAL, awaiting the owner's decision** (proposal
  decision 5) — `comision_porcentaje` defaults to `0` (off), nothing persisted, the
  endpoint and card are labelled PROVISIONAL end to end. Dropping the slice in full
  remains a clean revert (no migration, no data to unwind).
- **`articulos_empresas` replace-set concurrency gap and the importe CHECK micro-gate**
  — unchanged, carried over from the stage-8 archive and still open (state.yaml
  "CARRYOVER WATCH" note); unrelated to this stage's scope, not touched here.
- **PascalCase / loose debt** referenced by the same carryover note (`ways_owner` as a
  testcontainer superuser, weakening every migration-level test repo-wide) — irrelevant
  to stage 10 (no migration) but still open for a future stage.
- **Recharts containment has no CI guard** (verify WARNING-1) — true today, structurally
  enforced only by source inspection and a mocked component test, not an import-boundary
  lint rule.
- **Web bundle size** (883 KB / 237 KB gzip, verify SUGGESTION-1) — a future code-
  splitting candidate (`React.lazy` for `Tablero`), out of this stage's scope.

---

## 7. Authorized Content Amendment (Tablero Spec)

Pre-recorded in `tasks.md`'s "Archive-time reconciliation" note above Slice 9: the
`tablero` spec's granularity scenario was stale relative to shipped behavior (confirmed
against `src/Ways.Web/src/paginas/Tablero.tsx`: the four breakdown panels' `cargarDatos`
dependency arrays are `[idEmpresa, idPuntoVenta, desde, hasta]` — `granularidad` is
never in scope for them; only the G1 series `cargar` callback depends on it).

**Before**:
```
#### Scenario: Changing granularity re-buckets every panel
- GIVEN `Tablero` loaded with `granularidad = dia`
- WHEN the user switches to `semana`
- THEN the ventas series and every breakdown panel re-fetch bucketed by
  week, not just the series panel
```

**After**:
```
#### Scenario: Changing granularity re-buckets only the two G1 series
- GIVEN `Tablero` loaded with `granularidad = dia`
- WHEN the user switches to `semana`
- THEN the ventas series and the gastos series re-fetch bucketed by week;
  the four breakdown panels (por punto de venta, por vendedor, por medio de
  pago, top artículos) do not re-fetch on this change — each row is already
  a period subtotal with no time bucket
```

This is the ONLY non-verbatim edit performed during this archive. Every other merged
block (below) is a byte-for-byte copy of its source.

---

## 8. Stage-10 → Stage-11 Handoff

Stage 11 (exportación, per doc-11) depends on this stage: the export stage consumes
these same aggregates (`/api/reportes/*`) rather than re-deriving them. Explicit
out-of-scope carried forward from this stage's proposal: **no endpoint here returns a
file** — Excel/CSV/PDF export is entirely stage 11's responsibility, and the
exportable-report pattern (how a report's typed response record becomes a downloadable
file — streaming vs. buffered, which reports are exportable, whether export is a
separate endpoint per report or a generic wrapper) is an **open design decision awaiting
stage 11's own proposal**, not resolved here. Turno/caja-level reporting (Ver Cajas G2,
Caja Z G3) is also explicitly deferred to stage 11 per this stage's out-of-scope list.

---

## 9. Spec Merge Fidelity Confirmation

Four delta specs merged into `openspec/specs/`:

| Delta | Action | Target |
|---|---|---|
| `reportes-de-gestion` | NEW capability, copied verbatim | `openspec/specs/reportes-de-gestion/spec.md` |
| `rentabilidad-y-comisiones` | NEW capability, copied verbatim | `openspec/specs/rentabilidad-y-comisiones/spec.md` |
| `tablero` | NEW capability, copied verbatim except the ONE authorized amendment (§7) | `openspec/specs/tablero/spec.md` |
| `parametros-operativos` | DELTA (2 ADDED requirements), appended verbatim to the existing main spec — an `openspec/specs/parametros-operativos/spec.md` already existed (3 requirements: Parameter Scope and Fallback; tolerancia_pago/vuelto_maximo Server-Authoritative; Read Access Under OperacionDePos) | `openspec/specs/parametros-operativos/spec.md` |

Fidelity re-check performed by re-reading each written file against its source
(`openspec/changes/stage-10-agregacion-dashboard/specs/*/spec.md`) line by line after
writing:
- `reportes-de-gestion/spec.md` — character-level match against the delta source, 152
  lines, 9 requirements, 15 scenarios.
- `rentabilidad-y-comisiones/spec.md` — character-level match, 92 lines, 4 requirements,
  9 scenarios.
- `tablero/spec.md` — character-level match against the delta source EXCEPT the single
  amended scenario documented in §7; all other 5 requirements and 5 other scenarios
  (Tablero Covers Legacy G1 Parity By Default, Recharts Is Contained To
  componentes/graficos, Margin Panel Is Invisible/coverage banner ×2, Comisiones Card
  Labelled PROVISIONAL) are verbatim.
- `parametros-operativos/spec.md` — the pre-existing 3 requirements (Parameter Scope and
  Fallback; tolerancia_pago/vuelto_maximo Server-Authoritative At Checkout; Read Access
  Under OperacionDePos) are untouched; the 2 ADDED requirements from the delta (`zona_
  horaria And comision_porcentaje Are Known Parametro Keys`; `zona_horaria Is The First
  String-Typed Parametro And Must Be Stored Quoted`) are appended verbatim, in the
  delta's own order, after the existing content.

No rephrasing, translation, or improvement was applied to any block outside the single
authorized amendment in §7.
