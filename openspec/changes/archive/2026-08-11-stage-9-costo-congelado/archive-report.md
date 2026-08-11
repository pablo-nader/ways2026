# Archive Report: stage-9-costo-congelado

**Archived**: 2026-08-11
**Verdict at close**: PASS WITH WARNINGS (warning remediated before archive)
**Main @**: `eff30a2` (PR #75 merged)

## Change Summary

Stage 9 of the post-parity program (`docs/11-programa-post-paridad.md`) freezes the article
cost on every sale line at emission time, closing the one gap in `items_comprobante_venta`'s
snapshot model — the previous frozen list already carried `descripcion`, `codigo_barra`,
`id_area`, `precio_unitario`, `id_lista_precio`, `id_oferta`, `id_alicuota_iva`/`porcentaje_iva`,
but never a cost. `articulos.costo_nominal` is a single mutable value with no history, overwritten
by every confirmed compra, so without this stage the margin of a sale becomes permanently
unreconstructible the moment the next purchase of that article is confirmed.

What shipped, single slice / single PR:

- **Migration** `CostoCongeladoEnVentaEtapa9` — two additive columns on `items_comprobante_venta`
  (`costo_unitario numeric(14,2) NULL`, `costo_es_estimado boolean NOT NULL DEFAULT false`), two
  CHECK constraints (`ck_items_comprobante_venta_costo_no_negativo`,
  `ck_items_comprobante_venta_estimado_con_costo`), and a one-shot idempotent backfill running
  under `SET LOCAL app.acceso = 'plataforma'` (required because every tenant table is `FORCE ROW
  LEVEL SECURITY` and the app role has no `BYPASSRLS` — a plain `UPDATE` would silently match zero
  rows). Verified verbatim against the DB CHANGE GATE model the owner approved in-session
  (2026-08-11); zero deviations found by `sdd-verify`.
- **Capture** in `ServicioDeVentas` — `LineaDelPlan` gains `CostoUnitario`, set from the
  already-materialized `articuloPorId` dictionary in `MaterializarItems` (zero new queries),
  written onto `ItemComprobanteVenta` in `EjecutarTransaccionAsync`'s non-retryable half. NCX
  lines need no dedicated code: the same unsigned-per-unit capture applies, and the sign lives in
  `cantidad` per the pre-existing convention.
- **Error mapping** — both CHECKs mapped by exact constraint name in `ManejadorDeErrores`
  (`ClasificarCheckDeVentas`) to 400 responses.
- **doc-10 §4** — schema note for both columns, same trailing-blockquote convention as stages 5-8.
- **13 new tests** in `tests/Ways.IntegrationTests/CostoCongeladoTests.cs` plus backstop additions
  covering: emission freeze, NULL-vs-zero cost distinction, reprint immutability, NCX self-freeze
  with sign reversal, no-exposure on `ItemEmitido`/`ComprobanteEmitido` and the raw JSON response
  body, multi-tenant backfill, and the RLS-false-green-vs-honest statement-level backfill proof
  (`ways_owner` naive test + `ways_app` statement-level test asserting 0 rows without `SET LOCAL`
  and idempotent re-run).
- **No changes** to `src/Ways.Web`, to `ItemEmitido`/`ComprobanteEmitido`, or to the sale
  transaction's shape, numbering, stock, cuenta corriente, anulación, or authorization.

Delivered as **PR #75**, branch `feat/stage9-slice1-costo-congelado`, merged stacked-to-main.

## Verify Verdict And Remediation

`sdd-verify` returned **PASS WITH WARNINGS**:

- **CRITICAL**: 0.
- **WARNING** (1): `tasks.md` left boxes 1.17 (judgment-day) and 1.18 (branch/PR) unchecked even
  though both were independently proven complete via `state.yaml`'s apply-phase notes (clean
  judgment-day round, PR #75 merged at `eff30a2`) — a documentation drift with no functional
  impact. **Remediated**: both boxes were checked before archive; `tasks.md` now shows 18/18
  tasks complete, matching the proven state. This satisfies the Task Completion Gate — no
  unchecked implementation tasks remain in the persisted artifact.
- **SUGGESTION**: 0.

Spec compliance: 8/8 new-scenario obligations compliant (100%), no UNTESTED/FAILING/PARTIAL
scenarios. All six binding design decisions and all six proposal decisions confirmed followed
with zero deviation by direct source read.

## Judgment-Day Record

Clean round on the **first pass** — 2/2 APPROVE:

- **Judge A** (production correctness): verified migration fidelity, non-retryable capture
  placement, exact-name CHECK dispatch (no `Contains`/prefix ambiguity), and RLS backfill
  semantics. **1 MINOR fixed**: `design.md`'s deploy-path wording was corrected — app-boot
  auto-migrate *does* run with the platform interceptor; `SET LOCAL` is kept specifically for the
  CLI (`dotnet ef database update`) path, which has none.
- **Judge B** (test honesty): ran the full suite plus a **live mutation experiment** — deleted
  `SET LOCAL` from the migration and confirmed the naive multi-tenant test is the predicted false
  green while the `ways_app` statement-level test is the honest compensation that catches it.
  Full spec-scenario-to-test mapping found with no orphans. **1 MINOR informational, no action
  taken**: the backfill SQL is hand-duplicated in the test file, following the existing
  `ComprasTipoSeedTests` house precedent, with paired doc-comments.

No second round was required — per the tasks-phase decision, judgment-day applied once on the
whole slice diff before the PR (this change touches no new state machine, unlike stage-8 Slice 2).

## Autonomous Decisions (Overnight Mandate)

- **`size:exception` for the single PR**: the slice landed at ~1050 hand-authored lines against a
  ~250-400 line forecast in `tasks.md`'s Review Workload Forecast. The overage is entirely
  test-harness depth (13 new integration tests covering the three-state cost model, NCX sign
  reversal, no-exposure, and the RLS false-green/honest-test pair) — splitting would have
  separated tests from the code they verify, which was judged worse than a single oversized,
  cleanly-scoped PR. Recorded under the project's overnight/autonomous mandate; no chained PRs
  were used, `chain_strategy: stacked-to-main` stayed the nominal setting though only one slice
  existed.
- Tasks 1.17 (judgment-day) and 1.18 (branch/PR) were executed by the orchestrator directly
  rather than the apply sub-agent, consistent with how those steps are structured for
  single-slice changes; this is the source of the checkbox drift the verify WARNING flagged and
  that archive remediated.

## Suites At Close

| Suite | Result |
|---|---|
| Domain | 378 / 378 (no drift from baseline) |
| Application | 212 / 212 (no drift from baseline) |
| Integration | 704 / 704 (baseline 691 + 13 new `CostoCongeladoTests`/backstop tests) |
| vitest (`src/Ways.Web`) | Untouched — zero changes in the slice diff to any `Ways.Web` path, confirmed by `git diff --stat`; not re-run, matching the design's Testing Strategy row |

All three .NET suites were run directly against `main` at `eff30a2` by `sdd-verify` and passed
with 0 failed / 0 skipped.

## Stage-9 → Stage-10 Handoff

Binding for whoever plans stage 10 (margin aggregation/dashboards):

1. **Margin formula is IVA-included on both sides, no conversion**: `margen = total −
   costo_unitario × cantidad`. `costo_unitario` freezes `articulos.costo_nominal` exactly
   as-is — per unit, unsigned, IVA-included (verified: `CalculadorDeCompra.cs:86-95`,
   doc-10:438-441) — and the POS only emits TX/NCX (`discrimina_iva = false`), so
   `precio_unitario` is likewise the final IVA-included price. Both stored values already share
   the same base; do not normalize either side. Because purchase and sale use the same
   `articulo.IdAlicuotaIva`, the gross margin is the net margin scaled by `(1 + iva)`, and
   `porcentaje_iva` is already frozen on the line — so a future fiscal (IVA-discriminating)
   stage can derive the net margin later with no schema change.
2. **Cost is a three-state value per line**, not a plain number:
   - `(NOT NULL, false)` = real snapshot taken at emission — trustworthy.
   - `(NOT NULL, true)` = backfilled approximation from the one-shot migration, using
     *today's* cost at backfill time — under inflation this is typically **higher** than the
     true cost at sale time, so it is a **pessimistic lower bound**, not noise. Stage 10 MUST
     exclude these lines by default, with an explicit opt-in, and must state coverage rather
     than silently averaging over the gap.
   - `(NULL, false)` = cost unknown (no `costo_nominal` on the article, or a free-concept line
     with no `id_articulo`). Never coerced to `0` — `0` is a distinct, legitimately stated cost
     of zero (e.g. a bonificación).
   An NCX line freezes its **own** cost at its own emission (it does not copy the original
   line's cost — no line-to-line link exists in the schema); `costo_unitario` is stored unsigned
   per unit exactly like `precio_unitario`, so `costo_unitario × cantidad` reverses sign on its
   own via `cantidad`, with no dedicated NCX branch anywhere in the capture code.
3. **Role-gating expectation**: `costo_unitario`/`costo_es_estimado` never leave the server
   through the sale surface — `ItemEmitido`, `ComprobanteEmitido`, and every `src/Ways.Web`
   payload stay byte-unchanged by this stage (verified as a CRITERION, not an assumption, in both
   verify and judgment-day). Stage 10 is the first place cost/margin becomes visible to any
   client, and it MUST do so through its own aggregated, role-gated endpoints — a Vendedor has no
   business reading purchase cost through the ticket/checkout surface; margin visibility is an
   Admin-tier (or narrower) concern to be scoped explicitly in stage 10's own proposal.
4. Out of scope for stage 9, still open for a future stage: real historical cost reconstruction
   from `items_comprobante_compra`'s per-purchase `costo_unitario numeric(14,4)` history (usable
   to refine exactly the rows marked `costo_es_estimado = true`); weighted-average/FIFO/LIFO
   valuation; cost per punto de venta; multi-currency costs.
5. Carryover, unrelated to stage 9 (inherited from the stage-8 archive, still open): the
   `ServicioDeArticulos` `articulos_empresas` replace-set concurrency gap, and the pending
   micro-gate for the `importe` CHECK.

## Artifacts

- `openspec/specs/comprobantes-venta/spec.md` — merged: 2 requirements modified in place
  (`Comprobante Schema At Rest`, `Snapshot Immutability of Items`), 2 requirements appended
  (`Cost Snapshot Semantics, NCX Freeze, And No-Exposure`, `One-Shot Backfill Marks Pre-Existing
  Rows As Estimated`). All 12 original requirements preserved unchanged.
- `openspec/changes/stage-9-costo-congelado/proposal.md`
- `openspec/changes/stage-9-costo-congelado/design.md`
- `openspec/changes/stage-9-costo-congelado/specs/comprobantes-venta/spec.md` (delta, source of
  the merge above)
- `openspec/changes/stage-9-costo-congelado/tasks.md` (18/18 tasks complete)
- `openspec/changes/stage-9-costo-congelado/verify-report.md`
- `openspec/changes/stage-9-costo-congelado/state.yaml`
- `openspec/changes/stage-9-costo-congelado/archive-report.md` (this file)

Folder move to `openspec/changes/archive/2026-08-11-stage-9-costo-congelado/` is performed by the
orchestrator's deterministic script, not by this report.
