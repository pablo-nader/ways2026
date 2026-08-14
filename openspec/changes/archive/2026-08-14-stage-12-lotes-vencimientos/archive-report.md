# Archive Report: Stage 12 — Lotes y vencimientos (FEFO)

**Change**: `stage-12-lotes-vencimientos`
**Archived**: 2026-08-14
**Final state**: `main` @ `b998486`, 15/15 slices merged (PRs #99–#111, #113, #114) plus 1 cross-cutting follow-up fix (PR #112)
**Verdict**: PASS WITH WARNINGS (0 CRITICAL, 2 WARNING — 1 remediated, 1 accepted-and-documented, 6 SUGGESTION — backlog)

---

## 1. Shipment Map — 15 Slices + 1 Follow-Up Fix, PRs #99–#114, Stacked-to-Main

| # | Slice | Branch | PR | Content |
|---|---|---|---|---|
| 1 | Esquema | `feat/stage12-slice1-esquema` | #99 | `lotes`/`stock_lotes` tables, 6 additive columns, 2 `motivo_stock` enum values, RLS, EF configs, `ManejadorDeErrores` mappings. **Declared `size:exception`** (~430 lines, design decision 21 — a single migration cannot be split across merged PRs). No writer touches these columns yet. |
| 2 | Activación | `feat/stage12-slice2-activacion` | #100 | 2 `ParametroConocido` keys, `ReglaDeLotes` (pure Domain rule), batched parametro read (`ServicioDeVentas`, 2→1 queries) |
| 3 | Servicio de Lotes | `feat/stage12-slice3-servicio-de-lotes` | #101 | `ServicioDeLotes` get-or-create, sin-identificar, bounded saldos read, `GET/POST /api/stock/lotes` |
| 4 | Reconciliación | `feat/stage12-slice4-reconciliacion` | #102 | Net-zero `reclasificacion` pair, both activation-trigger hooks, admin re-run endpoint |
| 5 | Recepción | `feat/stage12-slice5-recepcion` | #103 | Compra draft lot input, get-or-create at confirm, per-lot movement + balance |
| 6 | Compra Anulación | `feat/stage12-slice6-compra-anulacion` | #104 | Per-lot anulación refusal (aggregate + lot, both mandatory) |
| 7 | Venta Plan FEFO | `feat/stage12-slice7-venta-plan-fefo` | #105 | FEFO planning in the decide phase, per-line `idLote`, plan carries the resolved lot |
| 8 | Venta Escritura | `feat/stage12-slice8-venta-escritura` | #107 | Per-lot writes in the pinned lock order, item snapshot, exact anulación |
| 9 | NCX | `feat/stage12-slice9-ncx` | #108 | NCX lot rules, `loteVencido` warning contract |
| 10 | Transferencias | `feat/stage12-slice10-transferencias` | #109 | Lot travels, `≥2N`-key lock order, per-lot sufficiency + expired refusal, joint checkout-vs-transfer deadlock proof |
| 11 | Ajuste + Decomiso | `feat/stage12-slice11-ajuste-decomiso` | #110 | Lot-aware ajuste, `POST /api/stock/decomiso` (first-class, Admin-only, never-negative) |
| 12 | Conteo | `feat/stage12-slice12-conteo` | #111 | Per-lot conteo, cross-cutting stock/stock_lotes invariant suite (all 8 motivos) |
| 13 | Vencimientos | `feat/stage12-slice13-vencimientos` | #106 | Report (4-state classification incl. `sin_fecha`) + `/export` sibling + `/resumen` tile feed |
| — | *(cross-cutting fix, outside the 15-slice plan)* | `fix/manejador-errores-raw-ado` | #112 | Closed a repo-wide `ManejadorDeErrores` gap for raw-ADO exceptions, flagged at slice-12 judgment-day, delivered in the same window |
| 14 | Web Operación | `feat/stage12-slice14-web-operacion` | #113 | POS lot picker (FEFO pre-selected), CompraEditor lot input |
| 15 | Web Back-Office | `feat/stage12-slice15-web-backoffice` | #114 | Vencimientos screen, `controlaLote` toggle, 2 parametro toggles, lot columns on transfers/conteo |

**Merge order** (confirmed via `git log --oneline --merges`): `1 → 2 → 3 → {4, 5→6, 7→8→9, 10→11→12, 13 in any order that respects the arrows} → 14 → 15`, with `#112`'s fix landing inside that same window. Everything after slice 3 forked into four parallel fronts living in disjoint files (`ServicioDeLotes`, `ServicioDeCompras`, `ServicioDeVentas`, `ServicioDeStock`); the one cross-front coupling — task 10.12's joint checkout-vs-transfer deadlock proof, which needs slice 8's per-lot write to exist first — was satisfied by construction (slice 10 is downstream of slice 8 in every valid merge order), not by luck.

**Task completion gate**: `tasks.md` — 204/204 tasks checked (`- [x]`), 0 unchecked. Verified directly by reading the persisted `tasks.md` before this archive; no stale-checkbox reconciliation was needed.

---

## 2. Verify Verdict And Remediations

**Verdict**: PASS WITH WARNINGS (`verify-report.md`, verified against `main` @ `d5d223c`, PRs #99–#111/#113/#114 + follow-up #112).

- 34/34 requirements, 136/136 scenarios across the 7 delta specs COMPLIANT (post-judgment-day amended counts — comprobantes-venta +1, conteo-de-inventario +2 versus the tasks-phase snapshot of 133).
- All 13 declared Success Criteria satisfied at the evidence level available to the verify pass.
- The DB gate contract is matched byte-for-byte by the shipped migration (see §4).
- Domain 420/420, Application 257/257, vitest 612/612 independently re-run green in the verify pass; `dotnet build`, `npx tsc -b`, and `dotnet ef migrations has-pending-model-changes` all clean.
- The full `Ways.IntegrationTests` suite was not re-run by the verify pass itself (a concurrent orchestrator-run testcontainers session was observed live via `docker ps`, and running a second suite against the same Docker daemon is the documented flakiness class in project memory) — Integration-layer scenarios were instead confirmed by direct source inspection (test files exist, load-bearing assertions match the amended spec text) plus the orchestrator's own concurrently-running full suite.
- 0 CRITICAL findings.

**WARNING-1 — doc 10 §6 closing status note stale ("Slice 1 — esquema, sin escritor").** The verify pass found the note still described the stage as schema-only even though all 15 slices with real writers had merged. **REMEDIATED in this archive window, commit `b998486`**: the note now reads "Etapa 12 — COMPLETA", names which service writes `reclasificacion`/`decomiso`, and states the three invariants are proven end to end. This is a higher-ranked, later fact than the verify-report's original "Open" status for this item — the verify-report's own consolidated-debts table (§ below, item 1) is superseded by this remediation, not restated as still-pending.

**WARNING-2 — `id_lote` NULLS-FIRST `ThenBy` mutation target not independently killable by a live concurrency test.** The transfer side's `ConstruirClavesOrdenadas` ordering clause (design's named mutation target) cannot be discriminated by the transfer-vs-reverse-transfer or checkout-vs-transfer joint tests, because both write sites convoy-lock on the shared aggregate `stock` row for the same articulo/PV before either transaction reaches lot granularity — a structural property of the system, not a test-writing gap. This was investigated and **honestly documented** (tasks.md 10.4, 10.12) rather than hidden or faked with fabricated mutation evidence. The ordering itself is still correct and covered by a single-transaction test (task 8.7). **ACCEPTED** as a negative finding, carried to backlog (§7) — this is the honest-negative-finding pattern this report was asked to preserve, not a defect to paper over.

---

## 3. The Judgment-Day Season

Fifteen independent judgment-day rounds ran, one per slice, per `protocolo-pr-solo-dev` (dual blind review, iterate to a clean round before merge). Several slices needed 2–3 rounds; none needed a fourth.

**Real BLOCKERs / CRITICALs found and fixed pre-merge (the substantive findings, not the cosmetic ones):**

1. **Slice 1 — double REJECT, round 1.** Judge A: BLOCKER, a duplicate `HasIndex(IdTenant)` metadata slot silently dropped `ix_articulos_tenant` from the EF model. Judge B: BLOCKER, `Down()` threw `NotSupportedException` on enum-label removal (proven live on postgres:17) — the migration's rollback path was fabricated, not real — plus MAJOR, the two EF-filter tests were vacuous under the RLS confound (proven by mutation: the tests would pass even if the filter did nothing, because RLS independently blocked the rows). Fixed in `bf45913`/`d0be19c`/`b7aa957` with per-fix mutation evidence; round 2 double APPROVE, including a fresh snapshot-drift mutation probe.
2. **Slice 4 (Reconciliación) — round 1 CRITICAL, resolved as a false alarm.** Judge A observed a live mutation (a `Reclasificacion` row becoming `Ajuste`) that turned out to be judge B's own authorized probe running concurrently in the same worktree — a documented class of judgment-day cross-contamination (two review rounds mutating the same worktree can observe each other). Round 2 was serialized B→A specifically to remove that race window. Real finding underneath: both `ReconciliarAsync` scope filters survived deletion because the trigger tests never seeded negative cases — fixed with 3 new tests (negative-scope, multi-pair exact counts, idPuntoVenta-only branch).
3. **Slice 5 (Recepción) — CRITICAL.** `codigo_lote` without `fecha_vencimiento` via the raw API returned an unmapped `500` (the CHECK constraint had no `ManejadorDeErrores` translation and no app-level guard). Fixed with a two-layer `400 lote_input_incompleto` (app guard + backstop, each independently proven). Also found: the anulación-of-a-lot-tracked-compra hole (aggregate reversed correctly, `stock_lotes` silently left inflated, `200 OK`) — closed with an interim guard, later replaced in full by slice 6.
4. **Slice 7 (Venta Plan FEFO) — CRITICAL, the FEFO-selects-the-expired-lot bug.** `ElegirFefo` chose the expired lot over a non-expired one with equal claim under pure fecha-ascending order, contradicting the spec's binding requirement that FEFO prefer a non-expired lot whenever one exists. **This is decision 15** (§5) — the selection now partitions non-expired-first, preserving "never blocks" (an all-expired set still resolves). This round also produced a **process failure and its fix**: the apply run had reported this exact deviation to the orchestrator in a chat message, but never wrote it into `tasks.md` — judge B found it only by grepping the repo. New binding rule from this point forward: **every deviation is recorded in `tasks.md`, in addition to being communicated to the orchestrator.**
5. **Slice 8 (Venta Escritura) — 3 rounds, the hottest writer's real bugs.** Round 1: a mutation that hoisted the FIRST line's `IdLote` onto every reversal movement survived 176 existing tests, because every prior anulación test sold exactly one line — the multi-line ledger's per-movement lot attribution was silently corruptible. Fixed with multi-line and mixed anulación tests killing the mutation from both angles (`7b4b98a`). Round 2: nothing proved the aggregate ACCUMULATES across two lines of the same articulo — a group-by-articulo "optimization" using only the last delta survived undetected. Fixed with a seeded-aggregate, discriminant-deltas test asserting the exact final stock value (`a600c52`). Round 3: clean.
6. **Slice 9 (NCX) — MAJOR, a real product bug.** The snapshot-suggested lot vanished from the picker the moment its balance hit zero — exactly the mainline devolución case (a lot fully sold out, then returned) — because `idLoteSugerido` resolved after `LeerSaldosAsync` with an empty candidate list. Fixed (`c09db6f`) by threading the snapshot lot through as a pre-saldos `idsLotePedidos` candidate, mirroring the write path's own design (decision 6).
7. **Slice 10 (Transferencias) — MAJOR, silent contract drift.** `LineaTransferida` had silently dropped the design-mandated `IdLote` field, and the per-articulo response aggregation collapsed two lines of the same articulo with different explicit lots into one row — the caller could never learn which lot the FEFO default actually picked. Fixed (`5703a29`) with a per-`(articulo, lote)` response shape and a 3-row field-by-field test. Also the origin of the honest negative finding recorded as verify WARNING-2 above (task 10.4's mutation evidence).
8. **Slice 12 (Conteo) — BLOCKER, invariant 3 broken in silence.** `ContarAsync` never checked `ReglaDeLotes.ControlEfectivo` before accepting an aggregate `Contada`: a `POST` with only an aggregate total against a lot-effective articulo returned `200`, moved `stock.cantidad`, and left `stock_lotes` untouched — proven empirically (40→50 agregado, lotes stayed at 40). The pre-approved `409 conteo_lote_no_soportado` degradation (decision 11) did not cover this case, because per-lot conteo WAS implemented — this was a missing guard on the aggregate path, not a missing feature. Fixed (`7b88759`) with `ExigirFormaDeConteoCoincideConControlDeLote` (`400 conteo_requiere_lotes` / `400 conteo_no_aplica_lotes`, spec amended) plus SELECT-first lot validation.
9. **Slice 14 (Web Operación) — 2 rounds, 5 confirmed findings**, the sharpest being a dead re-entrancy guard (`cargandoRef` never actually fired because the native `disabled` attribute won the double-click race first — the removed test's own comment claiming otherwise was empirically false) and zero coverage for the `loteVencido: true` warning render path.
10. **Slice 15 (Web Back-Office) — 3 rounds, the CRITICAL dead-end.** Round 3, judge A: `ConteoDeInventario.tsx` derived `esLoteEfectivo` from `controlaLote` alone, ignoring `lotes_habilitado` — a broken mirror of `ReglaDeLotes.ControlEfectivo`, which is the AND of both flags. With the module OFF and the articulo flagged, the per-lot grid rendered anyway, `GET /api/stock/lotes` returned zero lots (no reconciliation had ever run), and `puedeContar` required ≥1 complete line — a **permanent dead-end**: the operator could never count that articulo. Fixed with a token-gated `lotes_habilitado` resolution defaulting safely to aggregate when unresolved. Round 1 also found the transfer-line dedupe key blocking a legal multi-lot depot transfer (dedupe was `idArticulo`-only, contradicting decision 11) and a stock/tile metric-swap gap in `Tablero.test.tsx`.

**Findings NOT counted as REJECTs but load-bearing:** the empirically-discovered spec inaccuracies at decisions 13 (expiry boundary) and 14 (the get-or-create race never raises `23505` on its own conflict target — the spec's mechanism claim was simply wrong, and the code was correct) — both resolved by amending the SPEC, not the code, with an audit trail in each affected scenario.

**Skill/process growth**: the tasks.md-deviation-logging rule born at slice 7's judgment-day (§ above) is now permanent house process for this project, independent of this stage.

---

## 4. DB Gate — Formal Approval With Three Amendments

The **DB CHANGE GATE** (mandatory per `CLAUDE.md`) was evaluated **before spec/design**, against `proposal.md`'s "Modelo de datos propuesto" section, and approved 2026-08-12 under delegated autonomous mandate. Verdict: **APPROVED-WITH-AMENDMENTS**.

- **Naming**: OK — Spanish snake_case plural, `id_lote` PK, explicit `ck_`/`fk_`/`ix_`/`ux_` names overriding EF's PascalCase default.
- **Scoping — AMENDMENT 1 (nominal only)**: `lotes` is **tenant-wide** (`id_tenant`, no `id_empresa`, follows the articulo like `precios`), corrected from an initial "Catálogo" mislabel to doc-09's actual category name — the physical model was already correct; only the classification label in `proposal.md` was fixed.
- **`stock_lotes` as an operativa PK-only cache**: OK, matching `stock`'s precedent exactly.
- **CHECKs**: OK — `ck_lotes_vencimiento_segun_tipo` (XOR correct), `ck_lotes_codigo_no_vacio`, `ck_items_comprobante_compra_lote_input`; deliberate absence of a CHECK on `stock_lotes.cantidad` (parity with `stock` — negative balance is legal at the counter).
- **Indexes**: OK — partial indexes with `WHERE deleted_at IS NULL`, `ux_lotes_sin_identificar` as a unique partial, `ix_lotes_vencimiento` for the report, explicit FK-support indexes throughout.
- **RLS**: OK — both new tables via `HabilitarRlsDeTenant`, FORCE, identical to the pattern used by 5 prior migrations; `stock_lotes` additionally gets the hand-rolled tenant filter (the `Stock` precedent, since it has no audit columns to hang `EntidadTenant`'s global filter off of).
- **AMENDMENT 2 (binding, applied to proposal.md §F/§G)**: the FKs of `id_lote` in `items_comprobante_venta`/`items_comprobante_compra` target the **3-column composite** `(id_lote, id_articulo, id_tenant)` against the existing alternate key `ux_lotes_id_articulo_tenant`, instead of a 2-column `(id_lote, id_tenant)` — this reuses the single existing AK (avoiding EF creating a second AK on `lotes`) and pushes lot/articulo coherence to the DB level too, with `MATCH SIMPLE` correctly covering the nullable `id_articulo` case on venta's free-concept lines.
- **AMENDMENT 3 (binding note for design/apply)**: the get-or-create against `ux_lotes_articulo_codigo` and the sin-identificar partial unique index require their `db-error-backstops` translation and a live SQLSTATE `23505` race test; the `stock_lotes` upsert inherits the `RETURNING` discipline. The PG17 migration note (`ALTER TYPE ... ADD VALUE` cannot be referenced in the same transaction that added it) was carried as a binding verify criterion.

**Verify's fidelity check** (verify-report.md §"DB Gate Fidelity") compared the shipped migration (`20260813003414_LotesYVencimientosEtapa12.cs`) line by line against the amended gate contract: **every element matched exactly** — table shapes, all names, both enum values with zero `Sql()` reference to the new literals in the same migration, both RLS calls, and the `stock`/`parametros` tables receiving zero schema change. `dotnet ef migrations has-pending-model-changes` confirmed zero drift between the EF model and the shipped migration. **Zero scope violations found.** The `Down()` method's honesty (BLOCKER fixed at slice-1 judgment-day, see §3.1) means the migration's rollback path is real, not fabricated — it correctly throws rather than silently claiming a capability Postgres does not have (enum-label removal).

---

## 5. Autonomous Decisions — 15 Total, With Fundamento

### Proposal-level (1–12, delegated technical authority, `proposal.md`)

1. **Lot control is a per-articulo boolean, mandatory where on.** Rejects a three-way enum — "opcional" is the one option with no honest invariant; `SUM(stock_lotes) = stock.cantidad` becomes unassertable the moment the dimension is sometimes-filled.
2. **The module switch is an empresa parametro, ANDed with the tenant-wide flag, and rides free inside a query the sale already makes.** Closes the tenant-wide-articulo vs. empresa-scoped-parametro mismatch at the write path itself, not just the UI, and the batched parametro read turns "the module costs nothing when off" into a literal, test-asserted integer (17→16).
3. **Pre-existing stock gets a "sin identificar" lot via a net-zero `reclasificacion` movement pair, not a migration backfill.** Activation is per-empresa/per-articulo/on-demand, which a deploy-time migration cannot express; the mirrored-pair shape is the same one transfers already use, applied over the lot axis instead of the PV axis.
4. **FEFO is a server-computed default, never an imposition; one lot per line; the counter never blocks.** The operator holding the physical package outranks a computed suggestion; forcing a different lot would make the system's own data a lie.
5. **The ledger gains a nullable `id_lote`; the lot balance lives in a new, parallel `stock_lotes` cache; `stock` does not change.** Re-keying `stock` breaks the hottest table's PK and its spec-asserted invariant; deriving lot balances from the ledger on read kills the row-to-lock, destroying the concurrency primitive FEFO needs.
6. **The lock order extends to a single lexicographic order over `(id_articulo, id_punto_venta, id_lote NULLS FIRST)`, identical at all three write sites, duplicated on purpose.** This is the stage's highest-risk mechanical item (a deadlock is production-only and intermittent); the codebase's stated position against unifying its most concurrency-sensitive code holds, so the rule is stated once in the spec and tested three times.
7. **In a transfer, the lot travels; sufficiency is refused per lot.** A lot is an identity of the merchandise, not the location — re-bucketing into a destination sin-identificar lot would destroy exactly the expiry information the stage exists to preserve.
8. **Anulación is exact by snapshot; NCX carries an explicit lot; `id_comprobante_asociado` stays optional.** Making the association mandatory would retroactively un-return goods sold before the module existed; the snapshot field on the item is what makes reversal trivially exact with no lookup.
9. **`decomiso` is a first-class motivo, never negative.** Merma por vencimiento is a P&L number the owner wants reportable without parsing free-text `observaciones` — the exact `tipo=95` disease doc-10 principle 4 exists to prevent. This is the stage's **one genuinely irreversible artifact** (Postgres cannot drop an enum value), stated honestly rather than glossed.
10. **Alerts are pull: report + export sibling + Tablero tile. No push infrastructure. This is the alert channel stage 13 inherits.** Zero notification infrastructure exists anywhere in `src` (verified by grep in `explore.md`); building push here would be an entire stage of infrastructure decided blind for zero current users.
11. **Conteo of a lot-effective articulo counts per lot; "never a delta" is unchanged.** Leaving it aggregate-only is NOT neutral — it silently breaks invariant 2; the pre-approved degradation (a clean `409`) is the one honest fallback if the slice overflows budget.
12. **An expired lot blocks the back office and warns the counter.** Verbatim application of the codebase's existing counter-never-blocks / back-office-does asymmetry, already established by `transferencias-de-stock`.

### Judgment-day amendments (13–15, resolved live during apply, recorded in `state.yaml`)

13. **Expiry boundary is inclusive: `vencido` = `fecha_vencimiento < hoy` strict.** A lot expiring today is still sellable today. Resolved at slice-2 judgment-day after judge A caught the spec scenario's THEN contradicting both `design.md`'s own boundary table and the scenario's stated zone-resolution intent — the SPEC was amended, not the code, on retail-semantics/conservative-bias grounds.
14. **The get-or-create race never raises `23505` on its own conflict target.** The `ON CONFLICT ... DO UPDATE ... RETURNING` on `ux_lotes_articulo_codigo` serializes the race entirely inside Postgres — no exception surfaces to either concurrent confirm. The spec's original wording (promising a `23505` + backstop here) was empirically wrong; the real `23505` backstop lives on the admin alta path (`POST /api/stock/lotes`, a plain `INSERT`). SPEC amended, code and its empirical test left as-is — same pattern as decision 13: the implementation had it right, the spec overstated the mechanism.
15. **FEFO selection prefers non-expired lots.** `ElegirFefo`'s selection is partitioned — among lots with positive balance, non-expired lots (including the sin-identificar lot, which counts as non-expired per decision 4) are tried first, expired lots second; within each partition the base FEFO order still applies. "Never blocks" stays intact: an all-expired positive-balance set still resolves to the expired lot, flagged with a warning. `OrdenarFefo` (the picker's LISTING order) is unchanged; only `ElegirFefo` (the server's selection) gained the partition.

---

## 6. Suites At Close

| Suite | Result |
|---|---|
| Domain | 420 passed / 0 failed / 0 skipped |
| Application | 257 passed / 0 failed / 0 skipped |
| Integration | 1065 passed / 0 failed / 0 skipped |
| vitest (Ways.Web) | 612 passed / 0 failed, 35/35 test files |

All four suites reported as passing on **first attempt** at the point they were run in this delivery window; the verify pass independently re-executed Domain/Application/vitest live (420/257/612 confirmed) and left the full Integration run to the orchestrator's own concurrently-running suite per the flakiness-avoidance policy in project memory (never run two testcontainer suites against the same Docker daemon concurrently).

**The pre-stage-9 seed regression, caught at the merge gate.** During this delivery window a regression in the pre-existing (stage-9-era) seed surfaced and was caught by the merge gate rather than shipping — hotfixed at commit `c7fbd4e`. This is recorded here as evidence the gate discipline (build/test before merge, every slice) worked as designed, not as an open issue; nothing about it is carried to the backlog below.

---

## 7. Backlog (Carried Forward, Not Blocking)

Consolidated from `verify-report.md` §"Consolidated Debts", cross-checked against `tasks.md`'s judgment-day notes, with WARNING-1 removed (remediated this window, §2):

1. **The `id_lote` NULLS-FIRST `ThenBy` mutation target is not independently killable by a live concurrency test** (convoy-masking on the shared aggregate `stock` row — verify WARNING-2, §2). Accepted; the ordering is still correct and covered by a single-transaction test.
2. **No dedicated 404 tests for the lotes endpoints' `referencia_invalida` paths** (slice 3 JD debt) — pattern already covered by sibling stock endpoint suites, low risk.
3. **No dedicated mixed-compra (lot + non-lot items) end-to-end anulación test** (slice 6 JD debt) — structurally the union of two already-covered paths (per-line independent loop).
4. **Decomiso's `ExigirObservaciones` error message says "ajuste"** even on the `/decomiso` path (slice 11 JD debt, pre-existing wording since stage 5) — cosmetic, follow-up ticket already noted by the judges.
5. **The per-lot conteo path lacks the aggregate path's defense-in-depth `final != contada` loud check** (slice 12 JD debt) — very low risk, consistency suggestion only.
6. **`articulos_empresas` replace-set concurrency gap** and the **importe CHECK micro-gate** — both carried over from stage 8, still open, correctly untouched by this stage.
7. **`ways_owner` is a testcontainer superuser**, so the migration fixture cannot prove RLS end-to-end repo-wide — mitigated specifically for `lotes`/`stock_lotes` by running the RLS assertions over the `ways_app` connection (slice 1); the repo-wide weakness stays open, unchanged from prior stages.
8. **No CI lint rule enforces the containment/import-boundary discipline** (stage-10/11 carryover) — `lotes-y-vencimientos` introduces no new cross-boundary risk, does not reopen the gap.
9. **Web dedupe / UI debts closed during this stage, listed for completeness, NOT carried forward**: `Transferencias.tsx`'s `key={l.idArticulo}` React-key collision (fixed slice 15), `ConteoDeInventario.tsx`'s `esLoteEfectivo` missing the `lotes_habilitado` AND (fixed slice 15 round 3, was the CRITICAL dead-end), the transfer dedupe key blocking legal multi-lot transfers (fixed slice 15 round 1), and the systemic `ManejadorDeErrores` raw-ADO gap (fixed PR #112).
10. **Doc-comment nit**: one test's doc-comment says `Assert.Contains`, actual code uses `Assert.Single` — cosmetic, slice 9 judgment-day round 2.

---

## 8. Handoff — Stage 12 → Stage 13 (Stock Inteligente)

- **The alert channel this stage establishes is PULL** (decision 10) — a report, an `/export` sibling, and a Tablero tile, with zero push infrastructure (no email sender, no scheduler, no notification table exists anywhere in `src`). Per doc-11:180-183, stage 13 inherits this shared channel; if push is wanted, stage 13 owns that decision with a second real use case in hand, not decided blind here.
- **The stage-11 report/export infrastructure is proven out a second time**: the vencimientos report + `/export` sibling cost exactly "one mapping and one route line" as promised, following the `TablaExportable`/`IExportadorDeTabla` house standard verbatim.
- **`movimientos_stock` now carries a lot dimension for rotation analysis.** `id_lote` on the ledger (nullable, additive) is exactly the join key stage 13's stock-intelligence work needs for rotation/velocity-by-lot analysis, without any further schema change.
- **The reconciliation primitive (net-zero `reclasificacion` pairs) is reusable** for any future stage that needs to retrofit a new dimension onto pre-existing aggregate stock without moving the aggregate — the mirrored-pair shape generalizes beyond lots.
- **The lock-order discipline (`id_articulo, id_punto_venta, id_lote NULLS FIRST`, three independent write sites, deliberately un-unified)** is the pattern any future stage adding a fourth stock dimension should extend, not replace — the codebase's stated position against unifying its most concurrency-sensitive code held for this entire stage.
- **`ReglaDeLotes` in `Ways.Domain/Stock/` is the reference shape for future pure Domain rules** consumed by multiple raw-SQL writers — FEFO ordering, effective-control, and expiry classification are each exercised by every writer's own unit suite for free, with zero DB dependency.

---

## Traceability

- Proposal: `openspec/changes/stage-12-lotes-vencimientos/proposal.md`
- Exploration: `openspec/changes/stage-12-lotes-vencimientos/explore.md`
- Delta specs (merged, source retained under the change folder pre-archive):
  `specs/lotes-y-vencimientos/spec.md` (new, 10 req/40 esc),
  `specs/stock/spec.md`, `specs/transferencias-de-stock/spec.md`,
  `specs/comprobantes-venta/spec.md`, `specs/comprobantes-compra/spec.md`,
  `specs/conteo-de-inventario/spec.md`, `specs/parametros-operativos/spec.md`
- Design: `openspec/changes/stage-12-lotes-vencimientos/design.md` (21 numbered decisions)
- Tasks: `openspec/changes/stage-12-lotes-vencimientos/tasks.md` (204/204 complete)
- Verify report: `openspec/changes/stage-12-lotes-vencimientos/verify-report.md`
- State: `openspec/changes/stage-12-lotes-vencimientos/state.yaml` (15 autonomous decisions, DB gate approval text)
- Doc-10 remediation commit: `b998486` (docs/10-modelo-de-datos.md §6 closing note, W1)
- Doc-10 initial documentation commit: `c5943c7` (slice 1)
- Merge commits: `8fcb947` (#99) `fc4dd79` (#100) `b200af7` (#101) `03ca464` (#102) `1953228` (#103) `bb829f8` (#104) `8355242` (#105) `985d265` (#107) `00d59a8` (#106, vencimientos) `fcedc8b` (#108) `ee9ac09` (#109) `6f658d1` (#110) `851111e` (#111) `a8bb293` (#112, fix) `0364370` (#113) `d5d223c` (#114)
- Merged main specs (updated by this archive):
  - `openspec/specs/lotes-y-vencimientos/spec.md` (new, mechanical copy)
  - `openspec/specs/stock/spec.md` (delta merged — 3 requirements modified, 1 added)
  - `openspec/specs/transferencias-de-stock/spec.md` (delta merged — 2 requirements modified, 3 added)
  - `openspec/specs/comprobantes-venta/spec.md` (delta merged — 3 requirements modified, 2 added)
  - `openspec/specs/comprobantes-compra/spec.md` (delta merged — 3 requirements modified, 1 added)
  - `openspec/specs/conteo-de-inventario/spec.md` (delta merged — 1 requirement renamed+modified, 2 modified, 1 added)
  - `openspec/specs/parametros-operativos/spec.md` (delta merged — 2 requirements added)
