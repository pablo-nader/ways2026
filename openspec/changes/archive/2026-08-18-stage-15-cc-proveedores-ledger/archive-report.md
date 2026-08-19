# Archive Report: Stage 15 — Cuenta corriente de proveedores (ledger)

**Report date**: 2026-08-18
**Status**: PASS WITH WARNINGS at verify (0 CRITICAL; W1 remediated pre-archive by the
orchestrator on `468e533`; W2 informational/process-limitation, no action needed) —
content-only phase: spec fusion + this report. **Folder move to `archive/`, docs/11 update,
and `state.yaml` closure are performed by the orchestrator afterward with deterministic
copy (`shutil`/`filecmp`) — not part of this phase.**

## Executive Summary

Stage 15 promotes the proveedor saldo from a two-aggregate derived read
(`ServicioDeSaldoDeProveedor`, no lock, no history) into an append-only ledger,
`movimientos_cuenta_corriente_proveedor`, mirroring Etapa 7's design applied to money owed
to suppliers instead of money owed by clients. `proveedores.saldo` becomes a maintained
cache with exactly ONE write authority (`EscriturasDeCuentaCorrienteProveedor`, a structural
copy of the stage-7 class); four movement tipos (`apertura | compra | pago | ajuste`) cover
the opening backfill, compra confirmation, gasto-as-payment, and manual/contramovimiento
ajustes. This is **the first non-additive migration of the post-parity programme**: one new
enum, one new table (12 columns, 6 FKs, 6 indexes, 1 CHECK), two ALTERs over existing tables
(`proveedores` gets a cached `saldo` column, `gastos` gets an alternate key it was verified
to be missing), and two idempotent data statements backfilling one `apertura` movement per
proveedor with non-zero derived saldo — computed with the EXACT formula of the
`saldo-de-proveedor` spec being retired, so the migration is provable, not claimed, over a
fixture mixing borrador/confirmada/anulada, linked/unlinked, and soft-deleted rows on both
sides. Reliquidación, retenciones/notas de crédito, FIFO imputación, and a dedicated payment
comprobante are all explicitly out of scope with reopen conditions named. Six slices, PRs
#134-#139, all merged stacked-to-main 2026-08-17/18; two skill rules (`mutation-proof-tests`
11 and 12) were born mid-stage from real judgment-day findings; the per-compra payment-status
formula was arbitrated by the orchestrator (OD7) rejecting BOTH the proposal's and the
design's competing formulas, each of which silently mis-scored a pre-cutover compra.

## Artifacts Read (traceability)

Openspec mode — filesystem retrieval only
(`openspec/changes/stage-15-cc-proveedores-ledger/*`), matching the convention established
by the stage-13/14 archives; Engram MCP is used only for this report's own persistence step.

- `openspec/changes/stage-15-cc-proveedores-ledger/explore.md` (5 Orchestrator Decisions at
  the foot, OD1-OD5, to be formalized by the proposal)
- `openspec/changes/stage-15-cc-proveedores-ledger/proposal.md` (9 autonomous decisions, the
  `Modelo de datos propuesto` DB-gate contract §A-§F, capability contract, 6-slice tentative
  plan, 5 refutations/refinements to the explore's Orchestrator Decisions)
- `openspec/changes/stage-15-cc-proveedores-ledger/specs/{cuenta-corriente-de-proveedores,
  saldo-de-proveedor,gastos,comprobantes-compra,operacion-de-pos}/spec.md` (1 new capability +
  4 delta specs)
- `openspec/changes/stage-15-cc-proveedores-ledger/design.md` (16 architecture decisions, 28
  mutation targets, 3 tensions flagged in Open Questions for `sdd-tasks` to reconcile)
- `openspec/changes/stage-15-cc-proveedores-ledger/tasks.md` (14 tasks-phase orchestrator
  decisions + 44-entry running sequence — decisions 1-14, apply-phase deviations 15-44 — 6
  slices, all implementation checkboxes `[x]`)
- `openspec/changes/stage-15-cc-proveedores-ledger/verify-report.md` (PASS WITH WARNINGS,
  2026-08-18, HEAD `8c03226`)
- `openspec/changes/stage-15-cc-proveedores-ledger/state.yaml` (per-phase notes, `db_gate`
  `UNA-MIGRACION-APROBADA` with independent orchestrator verification, OD6-OD9 arbitrations,
  the post-tasks spec amendment record)
- Repository `git log` (merge commits #134-#139, all 2026-08-18) used to corroborate the PR
  delivery record
- `.claude/skills/mutation-proof-tests/SKILL.md` (rules 11-12, added mid-stage) and the
  stage-14 archive (`openspec/changes/archive/2026-08-17-stage-14-auditoria-trazabilidad/
  archive-report.md`) read as the structural/format precedent this report follows

## Spec Merge Summary

All five merges verified byte-identical against their delta source blocks via `diff` — every
diff below returned empty (verbatim evidence in the accompanying phase result).

| Domain | Action | Requirements | Scenarios | Fidelity evidence |
|---|---|---|---|---|
| `cuenta-corriente-de-proveedores` | Created (new capability, mechanical `cp`) | 11 | 27 | `diff` of the whole 300-line file against the delta: empty |
| `saldo-de-proveedor` | Updated: 1 MODIFIED + 2 REMOVED | net −1 (4 → 3 survive: "Per-Compra Payment Status", now rewritten, plus the unmodified "Authorization And Scoping"; "Saldo Is A Derived Read, Never Persisted" and "Saldo Is An Approximation, Not An Invariant" fully retired, no residue — the exact form stage 7 used for `consumo-cuenta-corriente`'s removed requirement) | MODIFIED block: 3 (was 2) | `diff` of the MODIFIED block (delta lines 5-39) against the replacement text in the merged file: empty. `diff` of the untouched Purpose header (lines 1-12) and the untouched "Authorization And Scoping" requirement (delta-adjacent, not in the delta at all): both empty. `git diff` shows only the two REMOVED blocks deleted and the one MODIFIED block's body/scenarios replaced — zero other lines touched |
| `gastos` | Updated: 1 ADDED (pure append) | +1 | +3 | `diff` of the appended block (delta lines 5-33) against the tail of the merged file: empty. `git diff --stat`: `+30/−0`, zero minus lines |
| `comprobantes-compra` | Updated: 2 ADDED (pure append) | +2 | +2 | `diff` of the appended block against the delta tail: empty. `git diff --stat`: `+29/−0`, zero minus lines |
| `operacion-de-pos` | Updated: 2 ADDED (pure append) | +2 | +3 | `diff` of the appended block against the delta tail: empty. `git diff --stat`: `+29/−0`, zero minus lines |

Total landed in `openspec/specs/`: 17 requirement deltas / 38 scenarios, matching
`verify-report.md`'s own measured count exactly ("cuenta-corriente-de-proveedores 11/27;
saldo-de-proveedor 1 MODIFIED + 2 REMOVED / 3; gastos 1 ADDED / 3; comprobantes-compra 2
ADDED / 2; operacion-de-pos 2 ADDED / 3 → 17 requirement deltas / 38 scenarios"). A combined
`git diff` across all five files confirms: the three pure-ADDED merges are pure trailing
additions (zero pre-existing lines touched); the `saldo-de-proveedor` merge touches only the
two removed requirement blocks and the one modified requirement's body/scenarios, leaving
the Purpose section and the "Authorization And Scoping" requirement byte-identical to their
pre-merge state. All other main specs (`proveedores`, `arqueo-de-cierre`,
`auditoria-de-operaciones`, `consumo-cuenta-corriente`, `pagos-a-cuenta`, `estado-de-cuenta`,
`reliquidacion-a-precio-del-dia`, `ajustes-de-cuenta-corriente`, `turnos-de-caja`, `stock`,
`lotes-y-vencimientos`) were left untouched, per the proposal's explicit capability contract.

**Method note on the removal form**: per the skill's requirement that a REMOVED requirement
carry `(Reason: ...)`/`(Migration: ...)` notes in the delta before deletion — both are present
in `specs/saldo-de-proveedor/spec.md`'s `## REMOVED Requirements` section — the notes justify
the deletion but are NOT carried into the merged main spec, matching the precedent set by
stage 7's removal of `consumo-cuenta-corriente`'s "No Reliquidación, No CC Management, No
Pagos De Cuenta" requirement (verified: `openspec/specs/consumo-cuenta-corriente/spec.md`
carries no residue of that title, reason, or migration note today). The historical record of
*why* lives in the archived delta file, not in the ongoing main spec.

## Delivery Record — 6 Slices, PRs #134-#139, 2026-08-17/18

| Slice | Content | Branch | PR | Merged | Judgment-day |
|---|---|---|---|---|---|
| 1 | Migration `CuentaCorrienteDeProveedoresEtapa15` (enum, table, 6 FKs, 6 indexes, CHECK, both ALTERs, both idempotent data statements, RLS last) + Domain entity/enum + EF config + `MapEnum` in both builders + cloned tenant filter + doc-10 | `feat/stage15-slice1-ledger-schema` | #134 | 2026-08-18 | CLEAN. 5 mutation targets required a source-text (golden) escalation after runtime attempts were exhausted per `mutation-proof-tests` rule 3 (targets #4, #11 — both provably-equivalent mutants under the real fixture/superuser carryover); 2 additional equivalent mutants verified and registered without further action. All 11 slice-1 targets closed with recorded mutation evidence |
| 2 | `EscriturasDeCuentaCorrienteProveedor` (both statements + validator) + the `compra` movement on confirm + the reversing `ajuste` on anulación (with the OD8 pre-cutover fallback) + both widened `RETURNING`s + the pinned lock order | `feat/stage15-slice2-escrituras-y-deuda` | #135 | 2026-08-18 | **1 CRITICAL** (judge B, `mutation-proof-tests` rule 11's origin finding): the only `SaldoResultante` assertion ran against a FRESH proveedor, where `saldo_resultante == total` by pure arithmetic coincidence — a value-substitution mutant (`nuevoSaldoProveedor` → `encabezado.Total`) passed 9/9. Closed by seeding a real, discriminating prior debt (≠0, ≠ the operation's own importe) and widening the anulación tests the same way; two mutation cycles (confirm-path, anulación-path) both failed as expected, reverted, 10/10 green |
| 3 | `pago` movement inside `InsertarGastoAsync` + imputación + the predicate scenarios + `pago × pago`/`anulación × pago` races | `feat/stage15-slice3-pago-por-gasto` | #136 | 2026-08-18 | CLEAN on the first round. One test-authoring bug found and fixed pre-judgment (wrong `tipo_comprobante` seed, `C-FA` instead of `C-FB`, broke 6/12 hardcoded totals — zero production impact). The slice-2 CRITICAL's lesson (discriminating prior debt) applied proactively to every new saldo/`saldo_resultante` assertion |
| 4 | Estado de cuenta (paginated, OD9) + `ServicioDeSaldoDeProveedor` re-sourced with the OD7 binding formula | `feat/stage15-slice4-estado-de-cuenta` | #137 | 2026-08-18 | **3 CRITICALs (judge B) + 1 CRITICAL (judge A) — the richest round of the stage**, and the origin of `mutation-proof-tests` rule 12. Judge B: (1) no test discriminated the cache (`proveedores.saldo`) from the retired re-derived aggregate — closed by desyncing them on purpose via a raw `UPDATE ... = 777.77` and asserting the endpoint returns the sentinel; (2) no test asserted `SaldoResultante` on returned estado-de-cuenta items — closed with pairwise-distinct accumulated values across 3 movements; (3) `Where(m => m.IdProveedor == idProveedor)` was untested — closed by seeding a second proveedor in the same tenant and asserting zero cross-contamination. All three: production code was correct, the gap was coverage. Judge A: the regla-10 offset boundary test sent `historico=true` alongside `desde`/`hasta`, so the real filter never ran and the assertion passed unconditionally — fixed by dropping `historico=true` and redesigning the dataset so inclusion depends on the OFFSET (1m59s inside / 5m01s outside a `-03:00` boundary), not the bare date |
| 5 | `SupervisionDeCuentaDeProveedor` policy + `POST /ajustes` (top-level) | `feat/stage15-slice5-ajuste-manual` | #138 | 2026-08-18 | CLEAN on the first round |
| 6 | `CuentaCorrienteDeProveedor.tsx` + ajuste modal + `ResumenSaldoDeProveedor.tsx` re-pointed | `feat/stage15-slice6-web` | #139 | 2026-08-18 | **1 CRITICAL (judge B) + 1 CRITICAL — PRODUCTION defect (judge A, round 2)**. Judge B: the double-click guard test was overdetermined — `disabled` alone (not the `registrandoRef` guard the comment claimed was "the real defense") already blocked the second click because React re-rendered between the two `await`ed clicks; fixed by dispatching both clicks inside one `act()` with no `await` between them, isolating the ref guard as the only defense in that window. Judge A (round 2) — **the headline finding of the stage**: `ResumenSaldoDeProveedor.tsx`'s `<Link>`, the ONLY real entry point to the proveedor ledger screen, never passed `state`, so EVERY real navigation (not only the direct-URL/bookmark corner case deviation #37 originally and falsely claimed was the only affected path) landed with `location.state.proveedor` null — and `puedeAjustar` (gated on `errorProveedor === ''`) silently disabled the ajuste button for a Supervisor whenever the Admin-only name-lookup fallback 403'd, which is exactly what happens on every real click for a non-Admin role. Fixed in two layers: `puedeAjustar` no longer depends on `errorProveedor`/`proveedorInfo` at all (role + puntos de venta only — the ajuste endpoint doesn't need the name), and `ResumenSaldoDeProveedor` gained an optional `proveedor` prop so its `<Link>` passes `state={{ proveedor }}` when the caller has it, mirroring `Clientes.tsx`'s own pattern |

**Final suites, post-final-merge** (per `verify-report.md`'s own method note, cited here per
the Final-State Authority hierarchy): **Domain 492/492 · Application 286/286 · Integration
1297/1297 (re-run in isolation after 20 flaky failures under a concurrent Testcontainers run —
the third occurrence of this pattern across the programme — green in isolation) · vitest
730/730**. Gate `UNA-MIGRACION-APROBADA` held for the whole stage: exactly one migration
(`CuentaCorrienteDeProveedoresEtapa15`) shipped, `dotnet ef migrations
has-pending-model-changes` stayed clean across every subsequent slice's gate-guard task, and
the final index count landed at exactly 7 (6 named on the new table + 1 implicit from the
`gastos` alternate key) — the binding count `state.yaml`'s `db_gate_approval` requires.

## Decisions Log

### Proposal — 9 autonomous decisions (`proposal.md`, delegated technical authority)

1. **Migration = one `apertura` movement per proveedor**, computed with the retired
   `saldo-de-proveedor` formula — not a synthetic replay. Zero-derived-saldo proveedores get
   NO row; the opening movement has NO `id_punto_venta`/`id_empleado` (both NULL,
   CHECK-enforced); it is its OWN enum value, not an `ajuste` (an `ajuste` requires an actor).
2. **The payment stays a `gasto`** — already turno-scoped, already an arqueo egress term,
   already has the compra-link TOCTOU guard. **Verified correction**: the `(Id, IdTenant)`
   alternate key on `gastos` does NOT exist (`GastoConfiguration.cs` has no
   `HasAlternateKey`) — added as a second ALTER inside the gate contract, not assumed.
3. **Reliquidación OUT of scope** — no economic symmetry (a payable does not lose value to
   inflation for us the way a receivable does), doc-11 does not ask for it, `proveedores` has
   no `id_lista_precio` to key off. The enum reserves no speculative value for it.
4. **Native enum `tipo_movimiento_cc_proveedor`, four values, no speculative ones** — NOT
   `text` + CHECK, the opposite of stage 14's `auditoria.accion` call, argued explicitly: a
   CLOSED 4-value discriminator with one writer each (vs. `auditoria`'s OPEN, ever-growing
   catalog). `apertura` is refined INTO the launch set (not speculative — its writer is this
   stage's own migration); retenciones/NC deferred with a reopen condition.
5. **Contramovimiento on anulación: IN scope.** A ledger diverging from truth on the first
   anulación is born broken. Gastos are still NOT reversed ("sin motor de reversión de
   gastos" survives verbatim); a fully-paid-then-annulled compra leaves a negative saldo
   ("saldo a favor"), surfaced, never clamped to zero.
6. **One write authority**: `EscriturasDeCuentaCorrienteProveedor`, a structural copy of the
   stage-7 class — exactly one raw `UPDATE ... RETURNING`, exactly one ledger `INSERT`, never
   a tracked `proveedor.Saldo +=` (a `CreateExecutionStrategy` retry would double-count).
7. **Imputación is explicit and per movement**, one `id_comprobante_compra` column with
   per-tipo meaning — no FIFO, no allocation table. The retired formula's observable outcomes
   (an unimputed payment reduces the total without settling any compra) are preserved.
8. **Ajuste manual gets a NEW policy**, `SupervisionDeCuentaDeProveedor` (Supervisor + Admin)
   — `SupervisionDeCuentaCorriente`'s generic name is already promised to a future cierre-de-
   caja tightening, reuse would be a semantics stretch.
9. **Pinned total lock order**: `turnos_caja → comprobantes_compra → lotes →
   stock/stock_lotes → proveedores → ledger INSERT` — `proveedores` is ALWAYS the last row
   lock, verified against all three real call sites plus the checkout precedent (the explore's
   candidate order was incomplete: it omitted the `lotes`/`stock` locks already present).

Gate verdict: ONE migration (`CuentaCorrienteDeProveedoresEtapa15`) — **the first
non-additive migration of the post-parity programme**: 1 new enum type, 1 new table (12
columns, 6 FKs, 1 CHECK, 6 indexes, standard RLS, no AK), 2 ALTERs over existing tables
(`proveedores` + `saldo` cache column, metadata-only; `gastos` + the verified-absent
`ak_gastos_id_gasto_id_tenant`), 2 idempotent data statements, ZERO `ALTER TYPE` anywhere.
`db-error-backstops` resolved: no new `23505` family (the `gastos` AK is structurally
unviolable); every FK exemption documented; `ManejadorDeErrores.cs` untouched.

### Design — 16 architecture decisions (`design.md`), zero new DDL

1. `EscriturasDeCuentaCorrienteProveedor` is `static`, not DI-registered — exactly two
   statements and one validator, the same containment argument as its stage-7 original.
2. Raw `UPDATE ... RETURNING`, never a tracked `Saldo +=` — copied for the reason, not the
   shape: `FabricaDeEstrategiaSinReintento` exists precisely to make a replay-double-count
   impossible in the first place.
3. Every raw-ADO parameter through `ParametrosDeComando.Agregar`/`AgregarNulo` (the settled
   outcome of PR #129, which needed a fifth call site after four were already patched) — a
   sixteenth private `AgregarParametro` clone would re-open a closed defect class.
4. The two authoritative `RETURNING`s widen (`ConfirmarHeaderAsync`,
   `MarcarAnuladaAsync`) instead of a second `SELECT`/pre-lectura read — zero extra round
   trips, the stage-14 decision-8 criterion applied verbatim.
5. The `compra` movement is written at step 5 of `EjecutarConfirmarAsync`, after the costo
   loop and immediately before commit — NOT at "step 1.5" where stage 14 put its audit row,
   because the proveedor lock must be the LAST one taken for update.
6. The anulación contramovimiento reads the ledger, with a named pre-cutover fallback
   (`−total` from the widened `RETURNING` when no own `compra` movement exists) — refusing to
   annul a pre-cutover compra would be a regression of a shipped operation.
7. The `pago` movement is written inside `InsertarGastoAsync`, after `SaveChangesAsync`
   (`id_gasto` is generated there) and before commit, gated by the retired formula's own
   predicate verbatim — no new derivation, no new arqueo term.
8. Per-compra payment status formula (design's own shape) — **later REJECTED by OD7**, see
   below; design's own Open Questions flagged the disagreement with the proposal/spec without
   resolving it.
9. `ServicioDeSaldoDeProveedor.ObtenerAsync` keeps its signature and DTOs byte-identical; only
   its two queries change (`Saldo` from the cache, `pagadoPorCompra` from an indexed ledger
   aggregation) — `dto-contract-honesty`, a published contract consumed by two web screens.
10. Estado de cuenta is PAGINATED (`OFFSET`, `fecha DESC, id_movimiento DESC` tiebreaker) —
    **a reconciliation against the spec's unpaginated prose, later ratified as OD9**; the
    ledger grows unboundedly and a tied-`fecha` `RelojFijo` fixture ties by construction.
11. The running balance is the stored `saldo_resultante`, never re-derived — stage-7
    decision-9 verbatim: a backward-running balance is wrong under a filter, the snapshot is
    right under any filter and any page.
12. `POST /ajustes` mapped TOP-LEVEL, not stacked under `OperacionDePos` — proposal decision
    8's rejection of an AND-composition; precedented by `GET /saldo`'s own top-level mapping
    in the same area.
13. The manual ajuste reuses `ReglaDeAjusteDeCuenta.Validar` unchanged (client-agnostic
    already) — cloning it would create two thresholds that drift over time.
14. The ajuste takes NO turno — it moves no physical money and contributes no arqueo term;
    requiring one would be theatre (stage-7 decision-4 precedent).
15. `apertura` is refused at three layers and reachable from none — the API DTO has no `tipo`
    field, the writer's validator throws, the CHECK backs both.
16. `MovimientoCuentaCorrienteProveedorConfiguration` declares all six support indexes by
    hand — `ForeignKeyIndexConvention` re-adds a support index for any uncovered FK even when
    removed by hand (the exact stage-14 gate-amendment-1 lesson).

Two conflicts were flagged in design's own Open Questions and left for `sdd-tasks`/the
orchestrator to reconcile (decision 8's payment-status formula, decision 10's pagination vs.
the spec's unpaginated prose); both were arbitrated as OD7/OD9 below.

### The 9 Orchestrator Decisions (OD1-OD9, `state.yaml`, spanning explore → spec → design)

| OD | Phase recorded | Subject | Verdict |
|---|---|---|---|
| OD1 | explore, formalized as proposal decision 1 | Opening asiento per proveedor, exact retired formula, no synthetic replay | Ratified, refined into its own `apertura` tipo |
| OD2 | explore, formalized as proposal decision 2 | Payment stays `gastos`; verify/add the `(Id, IdTenant)` AK on `gastos` | Ratified — the AK was verified absent, not assumed |
| OD3 | explore, formalized as proposal decision 3 | Reliquidación out of scope | Ratified, with `proveedores`'s missing `id_lista_precio` as added evidence |
| OD4 | explore, formalized as proposal decision 4 | Native enum, launch set, retenciones/NC deferred | Ratified — enum choice argued against stage 14's opposite `text`+CHECK call; value set refined to 4 (`apertura` included, not speculative) |
| OD5 | explore, formalized as proposal decision 5 | Contramovimiento on anulación in scope | Ratified — tipo pinned to `ajuste`, saldo-a-favor consequence made explicit |
| OD6 | spec phase (T1) | The `/export` sibling of the estado de cuenta | OUT of scope — the proposal is the contract and excludes it with a "cheapest first extension" rationale; a spec-brief misalignment, not a proposal gap |
| OD7 | design phase (T1, the stage's central arbitration) | Per-compra payment-status formula: proposal's `SUM(importe) WHERE id_comprobante_compra=X ... <=0⇒pagada` vs. design's `−Σ importe WHERE tipo<>'compra'` | **Both REJECTED.** The proposal's formula reads a pre-cutover compra as `pagada` (its debt lives in the `apertura`, no own `compra` row); the design's formula loses a pre-cutover PARTIAL payment (it never queries `gastos`). **Binding formula**: `pagado(X) = Σ gastos.importe linked to X` (the retired mechanism, valid for all time — a payment is still a gasto) `+ Σ(−importe)` of `ajuste`-tipo movements imputed to X; `pago`-tipo movements are NEVER counted (would double-count the gastos sum) |
| OD8 | design phase (T2) | Anulación pre-cutover fallback: `importe = −total` from the widened `RETURNING` when no own `compra` movement exists | Ratified — not a conflict, but registered so its absence from `spec.md`'s literal anulación requirement is not read as an omission |
| OD9 | design phase (T3) | Estado de cuenta paginated vs. stage-7's unpaginated shape | **Paginated is authoritative** — `OFFSET` + `id_movimiento DESC` tiebreak, the stage-13/14 unbounded-growth criterion applied |

### The post-tasks spec amendment (orchestrator, 2026-08-18)

A judgment-day SUGGESTION from judge A on the slice-4 round surfaced that BOTH spec deltas
(`cuenta-corriente-de-proveedores`'s "Per-Compra Payment Status Is Derived From Imputed
Movements" and `saldo-de-proveedor`'s MODIFIED requirement) still transcribed formulas OD7
had already REJECTED — `spec.md` ran in parallel with `design.md` during planning and nobody
reconciled the spec text when OD7 arbitrated. The implementation (slice 4, task 4.5) was
already OD7-conformant; the drift was documentation-only. The orchestrator rewrote both
deltas to the binding OD7 formula (gastos ligados + imputed ajustes, `pago` movements
excluded) and added a new pre-cutover-partial-payment discriminating scenario to each,
commit `b4a00b0`. **This report's spec-merge section above reflects the amended, OD7-conformant
delta text** — the version actually fused into `openspec/specs/`.

### Tasks phase — 14 orchestrator decisions + 30 apply-phase deviations (44-entry sequence)

`tasks.md` runs one continuous numbered sequence: entries 1-14 are tasks-phase decisions,
15-44 are deviations registered during `sdd-apply`/judgment-day, chronological by slice
(15-18 → slice 1, 19-24 → slice 2, 25-26 → slice 3, 27-32 → slice 4, 33-35 → slice 5, 36-44 →
slice 6) — SUGGESTION 1 of `verify-report.md` asked for exactly this index, applied by the
orchestrator inside `tasks.md` itself.

**Decisions 1-14** (tasks phase): (1) 6 slices/6 PRs stacked-to-main, merge order
`1→2→3→4→5→6`; (2) DB gate `UNA-MIGRACION-APROBADA` with the binding 7-index count; (3)
pre-authorized cut points `1a/1b`, `2a/2b`, `4a/4b`, slice-6 modal droppable — never degraded:
backfill fidelity, single-write-authority containment, the contramovimiento, the pre-cutover
`impaga`/`parcial` cases; (4) **CONFLICT #1 resolved** — the OD7 binding formula, target #24
redefined; (5) **CONFLICT #2 resolved** — OD9 pagination, tasks 4.2-4.3/4.8 build and prove
it; (6) the OD8 anulación fallback registered (not a conflict); (7) the confirm×pago
rendezvous sequencing clarified (slice 2 races the writer class directly, slice 3 repeats
through the real `ServicioDeGastos` call site); (8) `mutation-proof-tests` 28-target placement
confirmed 1:11/2:9/3:3/4:3/5:1/6:1; (9) `db-error-backstops` at slices 1/3/5; (10)
`react-async-state`+`web-descriptor-tests` at slice 6 only; (11) `dto-contract-honesty` at
slices 4/5; (12) `work-unit-commits` every slice; (13) test dates fixed at `RelojFijo
(2026-08-17T12:00:00Z)`, with the migration's `now()`-stamped `apertura.fecha` as the one
documented exception (no `IRelojDelSistema` in migration context); (14) the deviation-
registration process rule itself (stage-12 discipline).

**Deviations 15-44** (apply phase, summarized by slice — full text in `tasks.md`):

- **Slice 1 (15-18)**: (15) the `gastos` ALTER's migration-file position corrected from the
  task's literal order to the topologically-required one (Postgres `42830` reproduced
  empirically when tried the other way) — the gate's actual invariants (RLS last, backfill
  before RLS, 7-index total) unaffected; (16)-(17) mutation targets #4 and #11 escalated to
  source-text assertions after being proven equivalent mutants at runtime (`ways_owner`
  superuser bypasses RLS regardless of order; the backfill's `id_proveedor IS NOT NULL`
  predicate is unreachable under real NOT NULL join semantics), per `mutation-proof-tests`
  rule 3; (18) the `fk_..._tenant` exemption test asserts SQLSTATE + prefix instead of the
  exact constraint name — Postgres reports only one violated constraint on a composite key.
- **Slice 2 (19-24)**: (19) `MarcarAnuladaAsync`'s `RETURNING` corrected to include `total`
  (needed by the OD8 fallback, omitted from design's literal column list but required by its
  own Transactions section); (20) three pre-existing test files missing
  `MapEnum<TipoMovimientoCcProveedor>` fixed (a gap opened by slice 1, surfaced only by the
  first code path that actually writes the enum); (21)-(22) mutation targets #17/#19
  escalated to source-text after runtime attempts were exhausted (the retry-isolation class
  makes a real double-count structurally unforceable; a real deadlock is structurally
  impossible with only one shared lockable resource); (23) the slice-2 "payment" in rendezvous
  tests calls the writer class directly, not `ServicioDeGastos` (doesn't exist until slice 3);
  (24) **the slice's CRITICAL** — see Delivery Record above, origin of `mutation-proof-tests`
  rule 11.
- **Slice 3 (25-26)**: (25) a test-authoring bug (`C-FA` instead of `C-FB` as the seeded
  `tipo_comprobante`) broke 6/12 hardcoded totals, zero production impact; (26) the slice-2
  CRITICAL's discriminating-prior-debt lesson applied proactively to every new saldo
  assertion in this slice.
- **Slice 4 (27-32)**: (27) the slice-1 backfill fidelity test's pre-migration saldo capture
  inlined the retired formula into its own test-owned helper (re-sourcing `ObtenerAsync` broke
  it, since the ledger table doesn't exist yet at that point in the fixture); (28) another
  `MapEnum` gap, same class as deviation 20, opened one slice later; (29) mutation target #25
  escalated to source-text after two runtime attempts (a `DbCommandInterceptor` and a
  physical-TID-decoupling `UPDATE`) both failed to force the tie-order mutant through
  Postgres's plan-dependent tiebreak resolution; (30) mutation evidence for target #24
  exercised BOTH rejected OD7 formulas beyond the design table's literal "widen one filter"
  mutation, confirming exactly the failure modes OD7's arbitration predicted; (31)-(32) **the
  4 CRITICALs** — see Delivery Record above, origin of `mutation-proof-tests` rule 12.
- **Slice 5 (33-35)**: (33) the actual diff (~466 lines) exceeded the ~260-line forecast and
  the 400-line reviewer budget — registered non-blocking, no smaller deliverable boundary
  existed, the overage is entirely integration-test depth mirroring the client-side
  `AjustesDeCuentaCorrienteTests.cs`; (34) the 404 pre-check reuses the existing private
  `ResolverSaldoDeProveedorAsync` rather than adding a second parallel method (design's
  phrasing named the pattern, not a literal cross-class call target); (35) the request DTO
  carries no `idComprobanteCompra` field, confirmed against design decision 15 and the spec's
  own binding text, not a broader "optional imputación" reading.
- **Slice 6 (36-44)**: (36) one shared entry point (`ResumenSaldoDeProveedor`'s own link)
  realizes the "two entry points" task rather than two duplicated buttons; (37) the Admin-only
  `clienteDeProveedores.obtener` gate registered as a corner-case gap, **later found by
  judgment-day (#44) to be the real path on every navigation, not a corner case** — the
  original framing was corrected, not just supplemented; (38) the ajuste modal ships with no
  confirmation checkbox (not named as a requirement by design, read as literal scope); (39)
  the react-async-state rule-10 sibling sweep found nothing to replicate (no turno-recovery
  path exists for a turno-less write); (40) the "movement mapper" task realized as two
  granular named helpers instead of one combined mapper, matching the per-column assertable
  unit `mutation-proof-tests`/`web-descriptor-tests` reward; (41) judgment-day/branch-PR-merge
  tasks explicitly out of `sdd-apply`'s scope (orchestrator-owned, per the solo-dev PR gate);
  (42) the actual diff (~1449 lines) exceeded the ~380-line forecast — non-blocking, the
  pre-authorized modal-drop degradation was NOT exercised because the full scope fit with
  green tests; (43)-(44) **the 2 CRITICALs** — see Delivery Record above.

## Judgment-Day Summary — 7 Confirmed CRITICALs Across 6 Slices

Every slice closed CLEAN before its PR merged, with mutation evidence (mutate → named test
fails → revert → green) recorded in `tasks.md` for every fix. Seven CRITICALs, all real,
none dismissed as noise:

- **Slice 2 (1 CRITICAL, judge B)**: a value-substitution mutant on `saldo_resultante`
  survived because the only assertion ran against a fresh proveedor where the correct value
  and a wrong one coincide by arithmetic accident. Closed with real discriminating prior debt.
  Directly produced `mutation-proof-tests` rule 11.
- **Slice 4 (4 CRITICALs — the richest round of the stage)**: 3 from judge B, all coverage
  gaps in the read layer with correct production code underneath — the cache-vs-derivation
  source of truth was never desynced on purpose, `SaldoResultante` on returned items was never
  asserted, and the `IdProveedor` filter was never tested against a second proveedor in the
  same tenant. Directly produced `mutation-proof-tests` rule 12 (three read-layer mutant
  classes that write-side coverage kills none of). Plus 1 from judge A: the regla-10 offset
  boundary test was self-defeating — it sent `historico=true`, which made the code path under
  test never execute, so the assertion passed for the wrong reason.
- **Slice 6 (2 CRITICALs)**: judge B found an overdetermined double-click test (the assertion
  wasn't wrong, but it wasn't testing what its own comment claimed). Judge A (round 2) found
  the stage's **only production defect that shipped and was caught before merge**: the real
  navigation link never propagated `state`, so an Admin-only 403 on the name lookup silently
  disabled the ajuste button for every Supervisor on every real click — not the narrow
  direct-URL corner case deviation #37 originally claimed.

No CRITICAL from slices 1, 3, or 5 — both closed clean on the first round.

### mutation-proof-tests: 2 new rules born this stage

- **Rule 11** ("a ledger assert needs prior state that discriminates") — a fresh-entity seed
  makes the correct value and several wrong ones arithmetically indistinguishable; every
  `saldo_resultante`/cached-`saldo`/derived-estado assertion must seed real, non-trivial prior
  state. Born from slice 2's CRITICAL.
- **Rule 12** ("a read layer has its own three mutant classes — write-side coverage kills none
  of them") — source-of-truth (cache vs. re-derivation), projection (every returned field
  actually asserted), and identity predicate (a second entity in the same scope must be
  seeded to prove a filter). Born from slice 4's three judge-B CRITICALs in one round.

## Verify Verdict (Final-State Authority)

Per `verify-report.md` (2026-08-18, HEAD `8c03226`, covering all 6 slice PRs): **PASS WITH
WARNINGS**.

- **0 CRITICAL.**
- **W1 (doc-10's "Estado (Etapa 15)" annotation frozen at slice 1's scope — the THIRD
  occurrence of this exact class, following stages 13 and 14's own WARNING 1)** —
  **remediated pre-archive by the orchestrator on commit `468e533`**. The annotation described
  only "Slice 1 — schema + backfill: implementada" with the remaining slices in future tense
  ("llegan en Slice X") even though all 6 were merged; it now states the whole stage is
  implemented end to end.
- **W2 (informational, process-evidence limitation, no code defect)** — deviation #30's
  mutation-evidence narrative (testing both rejected OD7 formulas against the fixtures, then
  reverting) is not independently replayable by verify, by construction, since the mutations
  were reverted. The end-state code was independently confirmed correct against the binding
  OD7 formula. Non-blocking; no remediation performed or needed.
- **SUGGESTION 1** (a short index for the 1-44 sequence's decision/deviation split) —
  **applied**: the index note now opens `tasks.md`'s "Orchestrator Decisions Recorded This
  Phase" section.
- **SUGGESTION 2** (task 4.6's file-creation reconciliation exists only as inline prose) —
  registered, no action taken; correct as documented.
- All 17 requirement deltas / 38 scenarios mapped to passing tests; the migration gate
  (exactly one migration, matching the contract, 7 indexes, backfill fidelity by data) PASS;
  non-regression PASS (`VentasCheckoutTests.cs`/`src/Ways.Application/Ventas/`/
  `ManejadorDeErrores.cs` absent from all 6 merges' diffs; the pre-existing stage-8
  `SaldoDeProveedorTests.cs`'s 16 tests stay byte-identical in their asserts — only one
  `MapEnum` line added); OD7/OD8/OD9 verified directly against the shipped code, not assumed
  from the plan; 10 of 44 deviations sampled by verify, all found true against current code
  (rule 12 of the review discipline, distinct from the mutation-proof-tests rule 12 above).

This report is the terminal record of the change per the Final-State Authority hierarchy: it
supersedes any "pending" framing for W1 (fixed pre-archive, commit cited above) and records W2
as an informational process limitation, not an open item carried forward.

## Backlog / Deferred Extensions Registered By This Stage

- **The `/export` sibling of the estado de cuenta (OD6)** — explicitly out of scope, recorded
  as the "cheapest first extension": the read model (`ConstruirQuery`) is already
  export-ready and shared, needing only the route + column set over the existing etapa-11
  export infrastructure (`ExportacionDeListados`, `ContextoDeExportacionHttp`,
  `NombreDeArchivo`, `GuardaDeTope.Exigir` twice) — the exact shape
  `CuentaCorrienteEndpoints.cs` already uses for the client-side ledger. Not designed in, not
  costed in any slice; the orchestrator would need to decide whether it enters a future stage
  or a standalone chip PR.
- **Retenciones y notas de crédito de proveedor** — deferred from proposal decision 4 with an
  explicit reopen condition: the first real retention or supplier credit note a customer
  needs. Costs one `ALTER TYPE ... ADD VALUE` (irreversible, in its own migration) plus a
  write path; no speculative enum value was shipped now, so nothing is paid twice.
- **The Admin-only `GET /proveedores/{id}` gate on the name-lookup fallback (deviations
  #37/#44)** — pre-existing API surface, unmodified by this stage, that leaves a Supervisor
  without the proveedor's display name whenever the navigation state is unavailable (direct
  URL, bookmark, or — as judgment-day found and fixed — a link that failed to propagate
  `state`). The functional gap (Supervisor CANNOT operate) was closed in this stage
  (`puedeAjustar` no longer depends on the name fetch); the COSMETIC gap (Supervisor sees
  "Proveedor #N" instead of the real name when the fallback 403s) remains, by design, because
  fixing the underlying policy is out of this stage's scope. `Compras.tsx`'s own pre-existing
  `GET /proveedores?tamanio=200` listing fetch carries the identical gate — a pre-existing
  condition this stage did not introduce and does not fix.
- **Reliquidación a precio del día for proveedores (proposal decision 3)** — refused on
  economic-symmetry grounds (a payable does not lose value to inflation for us the way a
  receivable does) and structural grounds (`proveedores` has no `id_lista_precio`). Not a
  "cheapest first extension" the way `/export` is — would require a new mechanism, not a route.
- **Blocking soft-delete of a proveedor with a non-zero saldo** — explicitly left as an open
  product question (proposal Out of Scope), not silently tightened. Today's ABM allows it; the
  ledger is append-only so nothing corrupts.
- **`EscriturasDeCuentaCorriente.cs`'s own un-migrated private `AgregarParametro`** (design
  Open Questions) — the client-side ledger's writer still carries its own private copy while
  this stage's new writer uses the already-`main`-merged `ParametrosDeComando`. Explicitly out
  of this stage's scope; not smuggled into any slice.
- **An open chip to unify duplicated raw-ADO parameter helpers** — carried forward from stage
  14's own backlog (PR #129), unrelated to this stage's own writer (which was built against
  the already-unified `ParametrosDeComando` from day one), registered here only for
  continuity since this stage's design explicitly declined to touch the old one.

## Rollback Note (carried from proposal, unaffected by archival)

**The first non-additive stage of the programme is nevertheless fully reversible.** Per
slice: slices 2-6 are additive code over an unchanged schema — reverting one removes a write
path or a read surface and leaves the table intact and consistent (append-only, nothing needs
repair). Slice 1 (the schema): `DROP TABLE movimientos_cuenta_corriente_proveedor` → `ALTER
TABLE proveedores DROP COLUMN saldo` → `ALTER TABLE gastos DROP CONSTRAINT
ak_gastos_id_gasto_id_tenant` → `DROP TYPE tipo_movimiento_cc_proveedor` — no dependent
object in that order (no self-FK, gate §B; no other column uses the type). The backfill
destroys nothing: it writes only new rows and one new column, never rewrites an existing row
in `comprobantes_compra` or `gastos` — the retired `saldo-de-proveedor` formula still computes
the identical number from the identical data, so the derived read can be restored bit-for-bit
by reverting the code alone. No `ALTER TYPE ... ADD VALUE` shipped anywhere in this stage —
nothing irreversible.

## Next Steps (orchestrator-owned, outside this phase)

1. Move `openspec/changes/stage-15-cc-proveedores-ledger/` to
   `openspec/changes/archive/2026-08-18-stage-15-cc-proveedores-ledger/` with deterministic
   copy (`shutil.copytree` + `filecmp.dircmp` readback), matching the mechanical-move
   discipline this phase used for the spec merges.
2. Update `docs/11-programa-post-paridad.md`'s Etapa 15 status block with the OD6/OD7/OD8/OD9
   resolutions and the post-tasks spec amendment.
3. Close `state.yaml`'s `archive` phase (`status: pending` → `done`) once the folder move is
   confirmed.

## SDD Cycle Status

The change has been fully planned (explore → propose → spec → design → tasks), implemented (6
slices, PRs #134-#139, all merged stacked-to-main), verified (PASS WITH WARNINGS, W1
remediated, W2 informational), and its specs fused into `openspec/specs/` (this phase, content
complete, evidence above). Folder archival and doc-11 closure remain for the orchestrator's
deterministic-copy step.
