# Archive Report: Stage 13 — Stock inteligente (mínimos, alertas y reposición)

**Archived**: 2026-08-16
**Status**: PASS WITH WARNINGS at verify (W1/W2 remediated pre-archive, W3 formally
acknowledged in this report) — archived as **intentional-with-warnings**, not clean.

## Executive Summary

Stage 13 turns the two dormant `stock.minimo`/`stock.reposicion` columns (nullable
since the Etapa 5 migration, never read or written) into a full replenishment
circuit: an Admin-only reorder-point write path with a no-movement guarantee, a
`bajo`/`sin_minimo`/`ok` classification reused identically by `existencias` and by
the new `reposicion-de-stock` report, a pull alert grouped by proveedor habitual
that never drops unassigned rows (`"Sin proveedor"`), a rotation figure (advisory
only, never gating the alert) that feeds a suggested minimum and a suggested
purchase quantity (`null`, never `0`, when `reposicion` is unset), and a Tablero
tile that reuses the report method rather than running a second aggregation query.
The stage shipped **zero migrations** — every new field lived on columns that
already existed since Etapa 5 — and touched no checkout write path; the
`VentasCheckoutTests` round-trip-count guards (`16`/`17`) stayed byte-for-byte
untouched, proving the blinded-checkout-budget claim by test rather than by
inspection.

## Artifacts Read (traceability)

Openspec/hybrid mode — Engram MCP tools were not exposed in this execution
environment, so retrieval was filesystem-only (`openspec/changes/{change}/*`); no
`sdd/{change}/*` observation IDs were read or produced. This is recorded as a
deviation from the skill's default engram-retrieval step, not a silent omission.

- `openspec/changes/stage-13-stock-inteligente/proposal.md` (8 autonomous decisions,
  gate verdict, success criteria, question round)
- `openspec/changes/stage-13-stock-inteligente/specs/{stock,reposicion-de-stock,
  parametros-operativos,reportes-de-gestion}/spec.md` (4 delta specs)
- `openspec/changes/stage-13-stock-inteligente/design.md` (17 architecture decisions)
- `openspec/changes/stage-13-stock-inteligente/tasks.md` (12 orchestrator decisions,
  7 slices, 106/106 tasks checked)
- `openspec/changes/stage-13-stock-inteligente/state.yaml` (phase notes for explore,
  propose, spec, design, tasks, apply, verify — the verify phase's note is the only
  available account of `sdd-verify`'s findings; no standalone `verify-report.md`
  file was ever committed to this change folder, confirmed via `git log --all` on
  that path — recorded as a gap, not silently backfilled)
- Repository `git log` (PR merge commits #115-#122, dates, file-level diffs) used to
  corroborate/date the delivery record independently of the narrative artifacts

## Spec Merge Summary

All four merges verified byte-identical against their delta source blocks via
`diff` (see the accompanying phase result for verbatim mechanical-copy evidence).

| Domain | Action | Requirements moved | Scenarios moved |
|---|---|---|---|
| `stock` | Updated (ADDED requirement appended) | 1 | 2 |
| `reposicion-de-stock` | Created (new capability, mechanical `cp`) | 8 | 29 |
| `parametros-operativos` | Updated (ADDED requirement appended) | 1 | 4 |
| `reportes-de-gestion` | Updated (MODIFIED requirement replaced in place) | 1 (2→6 scenarios) | 7 (2 preserved + 5 new) |

Total landed in `openspec/specs/`: 11 requirements / 42 scenarios, matching the
spec-phase note's own final count in `state.yaml` (8 reposicion-de-stock + 1 stock +
1 reportes-de-gestion + 1 parametros-operativos; 29+2+7+4). All other main specs
(`exportacion-de-reportes`, `articulos`, `conteo-de-inventario`,
`lotes-y-vencimientos`, every write-path spec) were left untouched, per the
proposal's explicit "Not modified" list.

## Delivery Record — 7 Slices, PRs #115-#121, 2026-08-14..16

| Slice | Content | Branch | PR | Merged | Judgment-day |
|---|---|---|---|---|---|
| 1 | `ReglaDeReposicion` (pure Domain rule), 2 `ParametroConocido` keys, `PUT /api/stock/minimos`, doc-11 backlog re-registration | `feat/stage13-slice1-minimos-api` | #115 | 2026-08-14 | CLEAN round 1 (Judge B two-pass: static + live mutation, 11 mutations/0 survivors; Judge A fresh: 0 findings) |
| 2 | `minimo`/`reposicion`/`estado` on `/existencias` + 3 export columns | `feat/stage13-slice2-existencias-minimos` | #116 | 2026-08-14 | CLEAN round 2 (round 1: 1 MAJOR — surviving header-label mutant — + 1 WARNING, both fixed; re-judged clean) |
| 3 | `Existencias.tsx` editor grid, inline edit, add-row, descriptor + component tests | `feat/stage13-slice3-web-minimos` | #119 | 2026-08-14 | **The longest judgment-day cycle of the program**: Judge B round 1 (2 MAJOR + 4 WARNING), 2 scoped Judge-B re-judgments (round 2 caught a fix-caused MAJOR live), and **3 Judge-A rounds** chasing the ghost-row (`filaFantasma`) mechanism — closed structurally with a `useState` mirror of the pending-add ref, because a `ref` mutation never triggers a render and any render-time guard has to read committed state. Final verdict APPROVED at HEAD `678f1c2` |
| 4 | Reposición read model, `/reposicion` + `/export` sibling (no rotation fields yet) | `feat/stage13-slice4-reposicion` | #117 | 2026-08-14 | CLEAN round 2 (round 1: 2 MAJOR — header-label mutant, `ExigirVentanaValida` deletable with 0 coverage — + 1 WARNING resolved as Orchestrator Decision #12, soft-deleted-proveedor ordering) |
| 5 | Rotation (`LeerConsumoAsync`, window wiring), rotation columns, `GET /rotacion` | `feat/stage13-slice5-rotacion` | #118 | 2026-08-14 | CLEAN round 1 |
| 6 | `Reposicion.tsx` grouped by proveedor + download + nav | `feat/stage13-slice6-web-reposicion` | #120 | 2026-08-15 | CLEAN round 2 (round 1: 4 findings — 1 MAJOR non-clicking download-button test, 3 WARNING — all closed; round 2 Judge A: 1 WARNING closed, `sugerido = 0` genuine-zero coverage gap) |
| 7 | Tablero tile (`/resumen`) + `Sugerido` column on the editor — designated droppable slice, degradation **not exercised** | `feat/stage13-slice7-tile-y-sugerencia` | #121 | 2026-08-16 | CLEAN round 2 (round 1 Judge B: 3 MAJOR/WARNING closed, including a symmetric-cardinality test fix; round 1 Judge A: 1 WARNING + 1 SUGGESTION closed test-only) |

**Bonus work, this session (not part of the 7-slice plan)**: PR #122
(`test/headers-exports-preexistentes`, merged 2026-08-16) extended the header-row
assertion pattern — born from the two header-label mutants that survived slices 2
and 4 above — to the remaining 11 pre-existing JSON/XLSX export-equality tests
repo-wide, and is the origin of `mutation-proof-tests` rule 8 (`docs(skills):
mutation-proof-tests regla 8`, commit `bbdc855`): an export-equality test MUST
assert the header row, not only data cells, or a header-label swap ships
undetected. Recorded here as delivered work adjacent to, but outside, this change's
own scope.

**Final suites, post-final-merge (per `tasks.md`'s apply-phase note and `state.yaml`
verify note)**: Domain 452 · Application 257 · Integration 1119 · vitest 660. Gate
`SIN-CAMBIOS-DE-SCHEMA-RATIFICADO` held for the whole stage — every slice's gate
guard task confirmed `dotnet ef migrations has-pending-model-changes` clean and zero
files under `Migraciones/`.

## Decisions Log

### Proposal — 8 autonomous decisions (`proposal.md`, delegated technical authority)

1. **The reorder point is a fixed value the owner sets**, with a rotation-computed
   `minimoSugerido` shown beside it, never persisted automatically. `minimo IS NULL`
   = unmanaged; the boundary is `cantidad <= minimo`, not `<`; `reposicion` is the
   restock target, not a second threshold.
2. **Alerts stay pull.** The second use case (vencimientos + bajo stock) makes the
   Tablero "the alert tray" rather than justifying push infrastructure. Three named
   tripwires recorded to reopen the question.
3. **The purchase suggestion is a listing grouped by proveedor**, never a compra
   borrador or an OC. Rows with no `id_proveedor_habitual` land under `"Sin
   proveedor"`, never omitted; `sugerido` is `null`, never `0`, when `reposicion` is
   unset.
4. **"Stock en tránsito" is omitted from the formula**, not hard-coded to `0` — no
   order-with-state entity exists until Etapa 16.
5. **The full conteo snapshot/freeze/variance workflow is carved out** of this
   stage and re-registered in `docs/11-programa-post-paridad.md` (backlog row 367,
   new working name `stage-13b-conteo-por-planilla`) — completed as task 1.6, inside
   slice 1, not a closing sweep.
6. **The minimum is edited in the Existencias grid**, which becomes the per-PV
   stock screen, not in the tenant-wide articulo editor.
7. **Rotation is computed in LINQ inside `ServicioDeReportesDeStock`** — no new
   raw-SQL file, `LectorDeSerieTemporal` untouched. Consumption = `venta` +
   (`anulacion` where `id_comprobante_compra IS NULL`); window resolved in the PV's
   own `zona_horaria`; rotation is advisory-only and never gates the alert.
8. **No synchronous low-stock warning at the POS.** The checkout budget stays
   blinded; verified by an unchanged round-trip-count test, not promised.

### Design — 17 architecture decisions (`design.md`)

1. `ReglaDeReposicion` as one pure static Domain class (no `IWaysDbContext`).
2. Two report shapes over one rule: existencias classifies every row
   (`SinMinimo`/`Bajo`/`Ok`); reposición returns only `Bajo` rows.
3. LEFT JOIN to `proveedores`; a soft-deleted or missing proveedor survives as
   `null` rather than vanishing the row.
4. Ordering by ids (`id_proveedor_habitual` NULLS LAST, then `id_articulo`), never
   by name — locale-free and deterministic.
5. Rotation as one private primitive, `LeerConsumoAsync`, with two callers (the
   reposición report and `GET /rotacion`) — one filter, one negation, one window.
6. Consumption definition and its `-SUM(cantidad)` negation, verified against the
   ledger to avoid netting purchase reversals into sales.
7. The rotation window as a pure Domain function returning UTC instants — the one
   expression that carries the zone, making the mutation surgical.
8. The tile calls the full report method, rotation included — no "light" variant
   that would make `null` mean two different things.
9. The tile's third metric, **resolved by the orchestrator during `sdd-spec` as
   `sinProveedor`, not design.md's stale `sinSugerencia`** — `sinSugerencia`
   conflates two distinct causes (no proveedor vs. no `reposicion` configured)
   behind one number; `sinProveedor` is the actionable one (load a proveedor). This
   is the conflict Orchestrator Decision #5 in `tasks.md` re-confirmed and enforced
   at apply time, so no slice-7 task silently followed the older design text.
10. The write path is one `INSERT ... ON CONFLICT DO UPDATE` whose `SET` list
    names only `minimo`/`reposicion` — `cantidad` is in `VALUES` (create-at-zero)
    and absent from `SET` (provably cannot perturb a balance).
11. `PUT /api/stock/minimos` full-replaces both fields; `null` clears (unmanage).
    Response echoes the persisted row from `RETURNING`, enabling the no-refetch UI.
12. The rotation read is skipped entirely when the bajo-mínimo set is empty, and
    bounded to those articulos when it is not — costing exactly one query on a PV
    with zero minimums configured.
13. The reposición export is the aggregate cap shape (like existencias), sharing
    one method with the JSON endpoint — no `ObtenerReposicionParaExportacionAsync`
    twin, making export/JSON equality structural.
14. `minimoSugerido` lives on its own endpoint (`GET /rotacion`), not as fields on
    `FilaExistencia` — a clean non-delivery path for the pre-approved degradation,
    and absence honestly encodes "no history".
15. Existencias edits inline, one row at a time; opening another row while a write
    is outstanding is BLOCKED, not token-reconciled (supersede-during-write class).
16. No post-write refetch of the report — the row is patched from the write's own
    authoritative `RETURNING` response.
17. No file of any write path (`Ventas`/`Compras`) is opened by this stage — the
    checkout budget is protected structurally, proven by the unchanged
    `VentasCheckoutTests` constants (`16`/`17`).

### Orchestrator decisions recorded at apply (`tasks.md`, 12 total)

1. 7 slices/7 PRs stacked-to-main, per design.md's re-scoped breakdown (not the
   proposal's tentative one).
2. DB gate `SIN-CAMBIOS-DE-SCHEMA-RATIFICADO` — every slice (including the 3
   web-only ones) carries a gate-guard task; any DDL would have reopened the gate.
3. No `size:exception` anticipated on any slice at planning time (later reopened
   for slice 3 — see W3 below).
4. `judgment-day` runs once per slice, 7 independent rounds.
5. **Conflict found and resolved — the tile's third metric name.** design.md's
   decision 9 and its Contratos section still read `sinSugerencia`; the ratified
   `specs/reposicion-de-stock/spec.md` and `state.yaml`'s own spec-phase note both
   name it `sinProveedor`. **The ratified spec is authoritative** — flagged
   explicitly so `sdd-apply` would not silently follow the stale design text.
6. `db-error-backstops` declared explicitly not applicable — zero new constraints
   ship this stage.
7. `mutation-proof-tests` compliance: the **thirteen** named mutation targets in
   design.md's table each placed in exactly one slice (1.9-1.12, 2.4, 4.6-4.8,
   5.6-5.9, 7.7) — count corrected at verify (W1 below; earlier artifacts said
   "eleven"/"twelve" in different places, the table's actual row count is 13, all
   placed and evidenced, 4.6 disproven with recorded evidence).
8. `dto-contract-honesty` applies at every `Contratos.cs` edit (slices 1, 2, 4, 5,
   7).
9. `react-async-state` + `web-descriptor-tests` apply to slices 3, 6, 7 — the only
   web-touching slices.
10. Test dates fixed, never wall-clock-relative — the rotation-window boundary
    test pins the clock at `2026-08-14T12:00:00Z`.
11. Doc-11 backlog re-registration (proposal decision 5) is a slice-1 task, not a
    closing sweep.
12. **`judgment-day` round 1, slice 4 — Judge B's WARNING on the soft-deleted-
    proveedor ordering, RESOLVED.** The pinned task-4.1 snippet was a draft, not a
    locked contract: a soft-deleted proveedor's row disagreed between its ORDER KEY
    (raw FK) and its DISPLAY GROUP ("Sin proveedor"), which would have split one
    logical group into two on screen once slice 6's fold ran over it. Resolution:
    project `IdProveedor := p == null ? null : (int?)a.IdProveedorHabitual` and
    order `orderby (p == null), a.IdProveedorHabitual, a.Id` — every row with no
    EFFECTIVE proveedor falls into one trailing bucket, and slice 6's
    `agruparPorProveedor` inherits a trivially single-bucket fold as a result.

## Judgment-Day Summary — ~20 Confirmed Findings, One Structural Fix

Across the 7 slices, judgment-day confirmed roughly 20 findings (MAJOR + WARNING +
1 CRITICAL + 1 SUGGESTION), every one closed with recorded mutation evidence
(mutate → named test fails → revert → green) before its slice's PR merged:

- **Slices 1, 5**: clean on round 1 — no confirmed findings.
- **Slice 2**: 1 MAJOR (surviving header-label mutant in `ColumnasExistencias`) + 1
  WARNING, closed round 2.
- **Slice 3 — the longest cycle of the program.** Judge B ran two rounds (round 1:
  2 MAJOR + 4 WARNING; round 2: 1 fix-caused MAJOR proved live — a stale-ref
  corruption from round 1's own fix). Judge A required **three separate rounds**
  chasing successive variants of the same defect class around the add-row "ghost"
  row (`agregarFila`): an unconditional ref clear orphaning the ghost, then a
  render gate conflating "no row in edit" with "no unsaved ghost exists" as two
  different predicates. Closed **structurally**, not symptomatically, with
  `filaFantasma` — a `useState` mirror of the ghost-tracking ref, on the reasoning
  that a `ref` mutation never triggers React's render pass, so any render-time
  condition must read committed state, not a ref. Final verdict APPROVED at HEAD
  `678f1c2`.
- **Slice 4**: 2 MAJOR (a second surviving header-label mutant in
  `ColumnasReposicion`; `ExigirVentanaValida` deletable with zero test coverage
  since no existing test sent `?dias=`) + 1 WARNING (resolved as Orchestrator
  Decision #12 above), closed round 2. **The two header-label mutants found here
  and in slice 2 are what produced `mutation-proof-tests` rule 8** (commit
  `bbdc855`), later swept repo-wide in the bonus PR #122.
- **Slice 6**: 4 findings round 1 (1 MAJOR non-clicking download-button test, 3
  WARNING) + 1 WARNING round 2 (a `sugerido = 0` genuine-zero case had no
  discriminating test against `null`), all closed.
- **Slice 7**: 3 findings round 1 Judge B (1 MAJOR — a symmetric-cardinality test
  where mutating the `sinProveedor` fold to the `sinSugerido` fold survived — + 2
  WARNING) + 2 round 1 Judge A (1 WARNING, 1 SUGGESTION), all closed test-only.
- **The one REFUTED finding (task 4.6, trivalent logic)**: a proposed mutation
  target — deleting `s.Minimo != null` from the reposición query — was tested and
  **disproven**, not silently skipped. Deleting the clause left the query
  unchanged in every seeded case: `s.cantidad <= s.minimo` already excludes every
  `minimo`-NULL row through SQL's own three-valued logic (`x <= NULL` is always
  `UNKNOWN`), confirmed via `.ToQueryString()` on both versions. The clause stays
  in the code for documentary intent; the scenario it names is still covered as
  ordinary spec coverage, just not as mutation-proof evidence — recorded honestly
  per `mutation-proof-tests` rule 3 ("kill the confounds, exhaust the search before
  declaring a target undiscriminating").

## W3 (MANDATORY) — Slice 3 Size:Exception, Formally Recognized At Archive

**`tasks.md` recorded the fact at apply time but the review-workload guard was
never formally exercised for it; this archive report is the first artifact to
close that gap.** Slice 3 (PR #119, `feat/stage13-slice3-web-minimos`) shipped as
**644 insertions(+), 23 deletions(-) = 667 authored changed lines**
(`git diff --shortstat main..HEAD -- src/Ways.Web`), above both its ~380-line
estimate and the 400-line review-workload budget guard (`sdd-phase-common.md`
Section E) — and per `sdd-verify`'s W3 finding, the true delta including the full
judgment-day remediation history reached **1450 additions**.

- **The pre-identified cut point was NOT exercised, and this was a deliberate,
  recorded decision, not a silent overrun.** Task 3.4 (the articulo add-row) was
  the designated droppable unit if the slice overflowed. By the time the overflow
  became measurable, tasks 3.1-3.11 were already implemented as one cohesive,
  fully green, fully committed unit (35 test files, 629/629 vitest green at that
  point; `dotnet build` clean; gate guard clean) — un-shipping a working, tested
  add-row would have discarded verified work rather than avoided writing it.
- **A meaningful share of the overflow is test depth, not scope creep** — the
  program-wide pattern named in every prior stage's Review Workload Forecast.
  `Existencias.test.tsx` alone grew +171/-13 from the mutation-target evidence
  work (task 3.6) and the five judgment-day rounds' worth of dedicated regression
  tests for the ghost-row mechanism (round 1 Findings 2/3/6a/6b/7, round 2 Finding
  1, round A Findings 1/2/3, round A-3's `filaFantasma` closure and its own new
  test) — every one of them load-bearing evidence for a CRITICAL-class defect
  class, not incidental bulk.
- **Authority for this exception**: recognized under delegated technical
  authority (the same `execution_mode: automatic-autonomous` mandate that
  ratified the stage's DB gate), on the same reasoning `sdd-verify`'s W3 recorded
  — judgment-day's test-depth requirement on a genuinely hard defect class (a
  three-round structural fix on a stateful ghost-row mechanism) is exactly the
  kind of overrun the review-workload guard's own forecast text anticipates
  ("overflow is expected to come from test depth, not scope") and pre-approves in
  spirit, even though no `size:exception` was formally declared in `tasks.md`'s
  Orchestrator Decision #3 at planning time.
- **Why this is recorded explicitly rather than left implicit**: the
  review-workload guard (Section E) exists to protect reviewer cognitive load,
  and a size overrun that is never named is a guard that silently stopped
  applying. This report is the formal record that slice 3's 1450-addition PR is a
  **de facto size:exception**, backed by judgment-day's own depth of scrutiny (the
  longest cycle of the program, 3 Judge-A rounds, ~7 confirmed findings across all
  rounds) as the substitute for the line-count discipline the guard would
  otherwise have required. No future slice in this program should treat this as a
  precedent for skipping the guard's decision point at planning time — the
  overrun should have been declared via Orchestrator Decision #3's revision once
  it became measurable, not only reconciled after the fact at verify and archive.

## Backlog Registered By This Stage

- **Nav visibility is invisible to the suite (suite-wide limitation, not a
  regression of this stage).** Slice 6's judgment-day round 1 registered — and
  every prior sibling report screen (`Vencimientos.tsx` etc.) shares — the fact
  that role-gated route tests mount `RutaProtegida` directly inside a test-owned
  `MemoryRouter`, never through `App.tsx`, so `Layout.tsx`'s nav-entry visibility
  for a role without access has zero unit coverage across the whole reportes-de-
  gestión test suite. Not added for `Reposicion.tsx` because no sibling has it
  either — recorded as a suite-wide gap, not a new one.
- **Calibrate line-count estimates against judgment-day depth, not raw scope**
  (per `sdd-verify`'s S1 finding, carried here for continuity). Slices 3 and 4 both
  overran their estimates primarily from test-evidence volume (mutation proofs,
  discriminating-seed integration tests, multi-round regression coverage), not
  from added product scope — the same pattern the program has now observed across
  stages 11-13. A future stage's Review Workload Forecast should weight "how many
  judgment-day rounds this slice's defect class is likely to need" alongside raw
  line estimates.
- **Owner-reserved pendings — untouched, as scoped.** Real commission formula,
  Supervisor margin, `OperacionDePos` read model, cierre de caja por rol, export
  branding — all explicitly out of scope per the proposal's "Out of Scope" section
  and confirmed untouched by this stage's diff.
- **`CrearBorradorAsync` transaction chip** — task `task_bc8c3429`, carried
  forward unresolved; not intersected by this stage's write-path constraints
  (stage 13 opened no file under `ServicioDeCompras`).
- **Carried, still open, not touched by this stage** (per `state.yaml`'s carryover
  note): the `articulos_empresas` replace-set concurrency gap and the importe CHECK
  micro-gate (both from stage 8); the containment/import-boundary lint rule
  (stage-10/11 carryover); stage-12's own backlog (the `id_lote` `ThenBy` mutation
  target, the decomiso `ExigirObservaciones` wording, missing 404 tests on lotes
  endpoints); `ways_owner` as a testcontainer superuser (repo-wide, not relevant to
  a zero-migration/zero-new-RLS-policy stage).
- **Re-registered, not dropped**: the full conteo snapshot/freeze/variance
  workflow, carved out of this stage (proposal decision 5) and re-registered in
  `docs/11-programa-post-paridad.md` backlog row 367 under the new working name
  `stage-13b-conteo-por-planilla` (task 1.6, completed inside slice 1).

## Verify Verdict (Final-State Authority)

Per `state.yaml`'s verify-phase note (2026-08-16, HEAD `7683654`, the highest-
ranked available account since no standalone `verify-report.md` file exists on
disk or in `git log --all` for this change — see the Artifacts Read section
above): **PASS WITH WARNINGS**.

- **0 CRITICAL.**
- **W1 (mutation-target count drift)** — remediated pre-archive. Different
  artifacts said "eleven"/"twelve" mutation targets in different places; the
  design.md table's actual row count is **13**, all 13 confirmed placed in exactly
  one slice each and evidenced (4.6 disproven with recorded evidence, not
  silently dropped). Corrected in `tasks.md` and `state.yaml` before this archive.
- **W2 (stale phase statuses in `state.yaml`)** — remediated pre-archive, fixed in
  the same verify-phase commit (`7abc44c`).
- **W3 (slice-3 de facto size:exception)** — **formally recognized in this report**
  per the section above; not merely acknowledged in passing.
- All 11 requirements / 42 scenarios mapped to passing tests; gate 2 (sin-schema)
  and gate 3 (checkout protection — `VentasCheckoutTests` 16/17 byte-identical)
  PASS; `LectorDeSerieTemporal` confirmed still the only raw-SQL reader (design
  decision 7 honored, no second raw-SQL file opened).

This report is the terminal record of the change per the Final-State Authority
hierarchy: it supersedes any "pending"/"open" framing in `verify-report`-equivalent
snapshots for facts recorded above (W1/W2 fixed, W3 recognized here), and treats
`state.yaml`'s per-phase notes as the correct rank-3/4 source given the missing
verify-report file.

## Rollback Note (carried from proposal, unaffected by archival)

Per slice: additive code over an unchanged schema — reverting any slice leaves the
two `stock` columns dormant exactly as since Etapa 5. Runtime: setting every
`minimo` back to `NULL` silences the entire feature without touching a single
quantity, movement, or other row. Whole stage: revert the code — there is no
migration to roll back and no irreversible artifact of any kind, unlike stage 12's
`motivo_stock` enum values.

## SDD Cycle Complete

The change has been fully planned (explore → propose → spec → design), implemented
(7 slices, PRs #115-#121, plus adjacent bonus work in PR #122), verified (PASS WITH
WARNINGS, W1/W2 remediated), and archived (this report, W3 formally recognized).
Ready for the next change.
