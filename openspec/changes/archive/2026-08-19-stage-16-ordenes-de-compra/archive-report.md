# Archive Report: Stage 16 — Órdenes de compra

**Report date**: 2026-08-19
**Status**: PASS WITH WARNINGS at verify (0 CRITICAL; W1/W2/W3 all remediated pre-archive —
see `state.yaml` phase note and `tasks.md` decision 29) — **content-only phase: spec fusion +
this report. Folder move to `archive/`, `docs/11` update, and `state.yaml` closure are
performed by the orchestrator afterward with deterministic copy — not part of this phase.**

## Executive Summary

Stage 16 gives the purchase circuit an intention document that never existed, neither in the
Ways rewrite nor in the legacy: `ordenes_compra` + `items_orden_compra`, a five-value lifecycle
(`borrador | enviada | recibida_parcial | cerrada | anulada`) whose `estado` is a pure,
idempotent **projection** of the derived reception book (never a stored fact re-created by
hand) plus exactly two human decisions (manual close, anulación). Reception moves no stock,
cost or debt by itself — every physical delivery is registered as an ordinary
`comprobantes_compra` linked via a new nullable `comprobantes_compra.id_orden_compra`, so the
proven confirm/anulación engine of stages 8/12/15 is extended, never touched, in its own steps
2-6. Greenfield: legacy C3 (`alsina/facturacion.php?accion=nuevo`) never persisted
(doc-01:203-208), so every rule here is a decision, not a port. Price deviation is informational
only; the reposición report (Etapa 13, archived) keeps its exact formula, with its own
"stock en tránsito omitted" prose corrected from a since-false justification to an honest
registered deferral (T1 carryover, resolved by this phase). Six slices, PRs #140-#145, all
merged stacked-to-main 2026-08-19; one skill rule sharpened twice mid-stage
(`mutation-proof-tests` 12b/12c) from real judgment-day findings, including one production
CRITICAL (`TotalEstimado` silently extrapolating unquoted cost) caught and fixed before merge.

## Artifacts Read (traceability)

Openspec mode — filesystem retrieval only
(`openspec/changes/stage-16-ordenes-de-compra/*`), matching the convention established by the
stage-13/14/15 archives; Engram MCP is used only for this report's own persistence step.

- `openspec/changes/stage-16-ordenes-de-compra/explore.md` (6 Orchestrator Decisions, OD1-OD6,
  at the foot, formalized by the proposal)
- `openspec/changes/stage-16-ordenes-de-compra/proposal.md` (12 autonomous decisions, the
  `Modelo de datos propuesto` DB-gate contract §A-§F, capability contract, tentative slice plan)
- `openspec/changes/stage-16-ordenes-de-compra/specs/{ordenes-de-compra,comprobantes-compra,
  reposicion-de-stock}/spec.md` (1 new capability + 2 delta specs)
- `openspec/changes/stage-16-ordenes-de-compra/design.md` (16 architecture decisions, 34
  mutation targets, tensions T1-T10 in Open Questions, arbitrated by OD8)
- `openspec/changes/stage-16-ordenes-de-compra/tasks.md` (15 tasks-phase orchestrator decisions
  + a 14-entry apply-phase deviation sequence numbered 16-29 — the running numbering skips
  20-23 for slice-3's own entries and renumbers a slice-5 collision from "20" to "24" per its
  own inline note — 6 slices, all implementation checkboxes `[x]`)
- `openspec/changes/stage-16-ordenes-de-compra/verify-report.md` (PASS WITH WARNINGS,
  2026-08-19, HEAD `ae00fca`)
- `openspec/changes/stage-16-ordenes-de-compra/state.yaml` (per-phase notes, `db_gate`
  `UNA-MIGRACION-APROBADA` with independent orchestrator verification, OD1-OD9 across
  explore/spec/design/tasks, W1/W2/W3 remediation record)
- Repository `git log` (merge commits #140-#145, all 2026-08-19; HEAD `88a9963` includes
  `ae00fca` — the exact commit verify checked — plus later doc-only SDD-artifact commits, no
  further code changes) used to corroborate the PR delivery record
- `.claude/skills/mutation-proof-tests/SKILL.md` (rule 12's sub-clauses (b)/(c), each sharpened
  a second/third time this stage) and the stage-15 archive
  (`openspec/changes/archive/2026-08-18-stage-15-cc-proveedores-ledger/archive-report.md`) read
  as the structural/format precedent this report follows

## Spec Merge Summary

All three merges verified byte-identical against their delta source blocks via `diff` — every
diff below returned empty (verbatim evidence in the accompanying phase result).

| Domain | Action | Requirements | Scenarios | Fidelity evidence |
|---|---|---|---|---|
| `ordenes-de-compra` | Created (new capability, mechanical `cp` + `mv`, no Read/Write of content) | 11 | 34 (33 original + 1 Admin-gate scenario added at slice 6) | `diff -r` of the delta file against the copied file (whole ~16KB file): empty |
| `comprobantes-compra` | Updated: 3 ADDED (pure append) | +3 | +9 | `diff` of the appended block (delta lines 5-79, extracted with `tail -n +5`) against the tail of the merged file: empty. `git diff --stat`: `+76/-0`, zero minus lines |
| `reposicion-de-stock` | Updated: 1 MODIFIED (formula/text, zero acceptance-criteria change) + Purpose-prose correction (binding archive-phase carryover, T1 of the spec phase, `tasks.md` decision 14) | net 0 (1 requirement replaced in place) | +2 (from 3 to 5 — the new byte-identical-formula scenario plus the pre-existing Vendedor-403 scenario, which the delta also restates verbatim) | `diff` of the MODIFIED block (delta lines 5-53, extracted with `tail -n +5`) against the corresponding block in the merged file (main lines 125-173 post-merge): empty. `git diff`: only the Purpose paragraph (lines 13-20) and the one requirement's body/scenarios (lines 132-177) touched — `+25/-5`; zero other lines in the 320-line file touched |

Total landed in `openspec/specs/`: 11 new + 3 ADDED + 1 MODIFIED = 15 requirement units / 34 + 9
+ 5 = 48 scenarios, matching `verify-report.md`'s own measured count exactly ("ordenes-de-compra
(NEW) 11/34; comprobantes-compra (delta) 3 ADDED/9; reposicion-de-stock (delta) 1 MODIFIED/5 →
15 requirement units / 48 scenarios").

**Purpose correction (binding carryover, `state.yaml` OD7/T1 of the spec phase, restated as
`tasks.md` decision 14 and `verify-report.md`'s own closing note)**: the delta format cannot
express a Purpose-level correction, so it could not fix the now-false sentence in
`openspec/specs/reposicion-de-stock/spec.md`'s live Purpose section — *"'Stock en tránsito' is
documented here as intentionally omitted from the suggestion formula (decision 4) ... until
Etapa 16 gives orders a state and an expected arrival."* Etapa 16 now exists, so the "until"
clause was false as written. This phase rewrote it in place: the omission is no longer a
structural absence (Etapa 16 supplies `estado` + `fecha_esperada`, the derivable insumo), but it
remains a **deliberate deferral** — registered as explore's Orchestrator Decision 5 — with the
same reopen condition the MODIFIED requirement's own body now states (the first customer who
over-orders because the report ignores stock already on the way). The rest of the Purpose
paragraph (the `stock.minimo`/`stock.reposicion` semantics, the Admin-only write path, the
rotation figure, the report/tile/export enumeration) is untouched, byte-identical to its
pre-merge state — confirmed by the `git diff` above showing no other line in that paragraph
changed.

All other main specs (`articulos`, `proveedores`, `stock`, `lotes-y-vencimientos`,
`cuenta-corriente-de-proveedores`, `gastos`, `auditoria-de-operaciones`, and every capability not
named above) were left untouched, per the proposal's explicit capability contract; `git status`
on `openspec/specs/` shows exactly the two modified files plus the one new directory.

## Delivery Record — 6 Slices, PRs #140-#145, 2026-08-19

| Slice | Content | Branch | PR | Merged | Judgment-day |
|---|---|---|---|---|---|
| 1 | Migration `OrdenesDeCompraEtapa16` (1 enum/5 values, 2 tables/9 FKs/4 CHECKs/12 indexes incl. implicit AK, 1 additive ALTER on `comprobantes_compra`, RLS last on both new tables, zero data statements) + Domain entities + `ProyectorDeEstadoDeOrden` + the 6 exact-name `ManejadorDeErrores` branches (2 `23505` incl. the 3rd `_numero` ordering-trap occurrence + 4 `23514`) + doc-10 | `feat/stage16-slice1-schema` | #140 | 2026-08-19 | 1 MAJOR (judge B): a count-only index test (`ElConteoTotalDeIndicesNuevosEsExactamenteDoce`) let a column-order mutant survive — closed with `LasDefinicionesDeLosIndicesCompuestosRespetanElOrdenDeColumnasDelContrato` asserting the full `pg_indexes.indexdef`. 1 SUGGESTION (judge A): a vacuous test removed. **1 CRITICAL from judge A, REFUTED with evidence**: the reported "deletion" of the `mutation-proof-tests` rule-12c extension was an artifact of a two-dot diff against an already-advanced `main`; the three-dot `main...HEAD` diff showed only the slice's 7 real files. New process rule born from this: judgment-day diffs freeze with `main...HEAD` |
| 2 | Borrador CRUD + `enviar` (own numbering via `AsignadorDeNumeroComprobante` with `tipo_comprobante='OC'`, consumed at `enviar`, PV-pinned) with the two binding concurrency tests (distinct OCs → no 409; same OC → 200+409, number burnt) | `feat/stage16-slice2-borrador-envio` | #141 | 2026-08-19 | 2 MAJOR (judge B): (1) `mutation-proof-tests` rule 12c — the replace-set `DELETE` widened to unscoped survived 11/11 (no sibling OC seeded); closed by seeding a same-tenant sibling OC and asserting its items untouched — **2nd occurrence of the class, extends rule 12c to scoped writes, not only reads**; (2) `dto-contract-honesty` — `FechaEsperada`/`Observaciones` forced to `null` survived 11/11; closed with a persistence+update+clear round-trip test. Plus a documented WARNING (code-name drift, `orden_compra_no_enviable` deliberately more general than design's literal name — closed as a registered deviation, not a defect) |
| 3 | Ligadura (`ExigirOrdenLigableAsync`, `FOR SHARE`) + the confirm/anulación re-projection in the same transaction (lock → re-read → `UPDATE … RETURNING`, never a single self-referential `UPDATE`) + the cero-statements-extra proof (two nets: structural + behavioral with a landmine sibling OC) | `feat/stage16-slice3-ligadura` | #142 | 2026-08-19 | CLEAN |
| 4 | `cerrar`/`anular` + the four slice-4 confirm×anular races + the lock-free anulación guard | `feat/stage16-slice4-cierre-anulacion` | #143 | 2026-08-19 | 1 MAJOR (judge B, rule 12): a false coverage claim about the lock-free invariant survived after the temporary deadlock test was removed — closed with a permanent structural source-text assertion (`Assert.DoesNotContain("FOR SHARE"/"FOR UPDATE", …)`) |
| 5 | Paginated read model (`ObtenerDetalleAsync`/`ListarAsync`), per-artículo `Cobertura`, price-deviation surfacing | `feat/stage16-slice5-lectura` | #144 | 2026-08-19 | **3 CRITICAL (judge B, round 1) — rule-12b class, 3rd occurrence of the programme, sharpened the rule twice**: `Pendiente` asserted only at `0` (a fixed `0m` survived), `TotalEstimado`/`TotalReal`/`DesvioTotal` never asserted with positive values (a fixed `12345m` survived), and `IdProveedor`/`IdPuntoVenta` — two adjacent positional ints in a 17-parameter record — never read back (a swap survived 197/197). Closed with three dedicated tests, the third widened into one integral "every positional field with its own truth" fact. **Round 2 (judge A) then found 1 CRITICAL OF PRODUCTION**: `TotalEstimado` extrapolated cost from a per-artículo average across ALL ordered lines (cotizadas + non-cotizadas) instead of summing only the quoted lines — a 2-line-same-artículo POST (3 units @100 + 4 unquoted) produced `700` instead of the true `300`. Fixed at line-level (`sum(CostoUnitarioEstimado * CantidadPedida)` over quoted items only, `null` when none). Round 2 also found the listing-row analog of CRITICAL 3 (swap survived 13/13, decision 25's own claim of full listing-row coverage was itself false — corrected in place) + 1 WARNING (`ComprobantesLigados` count-only assert widened to exact-set) + 1 SUGGESTION (dead variable removed) |
| 6 | Web: `OrdenDeCompra.tsx` (list/detail/lifecycle actions), `CompraEditor.tsx` pre-load link display, `Reposicion.tsx`'s Admin-gated "Generar OC" action | `feat/stage16-slice6-web` | #145 | 2026-08-19 | B CLEAN in 10 mutation cycles ("the lessons paid off"). A: 2 WARNING — `puedeRecepcionar` didn't AND `puedeEscribir` (a non-Admin could see the button though the server still refused; fixed, gate widened) + the same-tick double-click test claimed coverage for all 4 write actions but only covered `Enviar` (fixed, replicated to `Cerrar`/`Anular`/`Guardar borrador`). **Judgment-day's own B pass then caught real flakiness** in the new `Guardar borrador` same-tick test (1/3 full-suite runs failed with 0 PUTs) — root cause: unlike the other three buttons (gated only by synchronous `ocupado`), that button also depends on `encabezadoCompleto`, hydrated from `detalle` in a later `useEffect`; under CPU load the double dispatch could race an still-disabled button. Closed with two `waitFor` guards before the double dispatch, assert strength unchanged (still exactly 1 PUT); confirmed deterministic with 3 consecutive full-suite green runs |

**Final suites, post-final-merge** (per `verify-report.md`'s own method note, cited here per the
Final-State Authority hierarchy): **Domain 526/526 · Application 291/291 · Integration
1402/1402** (re-run in isolation after 3 Testcontainers-flakiness failures under a concurrent
run — 4th occurrence of that pattern across the programme — green in isolation) **· vitest
796/796 (×3 consecutive runs)**. Gate `UNA-MIGRACION-APROBADA` held for the whole stage: exactly
one migration (`OrdenesDeCompraEtapa16`) shipped, `dotnet ef migrations
has-pending-model-changes` stayed clean, zero `migrationBuilder.Sql(` data statements, and the
final index count landed at exactly **12** (7 on `ordenes_compra` incl. the implicit AK + 4 on
`items_orden_compra` + 1 on `comprobantes_compra`) — the binding count `state.yaml`'s
`db_gate_approval` requires.

## Decisions Log

### Proposal — 12 autonomous decisions (`proposal.md`, delegated technical authority)

1. **Reception does NOT move stock by itself** — every physical reception is registered as an
   ordinary `comprobantes_compra` (borrador → confirmada) linked to the OC; the proven
   confirm/anulación engine (stages 8/12/15) stays untouched.
2. **`cantidad_recibida` is DERIVED, not stored** — no column ships; the received quantity is
   `SUM(items_comprobante_compra.cantidad)` over the OC's linked confirmed comprobantes,
   grouped by `id_articulo` on both sides.
3. **`estado` is a projection of the book plus two human decisions** (manual close,
   anulación) — every transition runs through one containment class inside the same
   transaction as confirm/anulación.
4. **Own numbering, reusing `AsignadorDeNumeroComprobante` with `tipo_comprobante='OC'`**,
   consumed at `enviar`, never at draft creation — no new sequence, no `tipos_comprobante` row.
5. **`UPDATE … RETURNING` is the sole transition authority**, the replace-set shape copied
   verbatim from `comprobantes_compra`'s own borrador editing.
6. **Lock order**: the OC row locks immediately after the comprobante header, BEFORE
   `proveedores`; the projection needs `SELECT … FOR UPDATE` before the recompute — a single
   self-referential `UPDATE` would be wrong under READ COMMITTED.
7. **Authorization mirrors `/api/compras` exactly** — no new policy; reads `OperacionDePos`,
   writes stack `GestionDeCatalogo`.
8. **Price deviation is INFORMATIONAL, costs no column** — the comprobante is the fact, the OC
   the intention; the deviation is computed on read from existing data.
9. **Anulación of an OC is governed by the book**, not the state column — zero received
   quantity AND no linked confirmable draft, expressed over the derivation so it cannot drift.
10. **Integration with stage 13 (reposición) is unidirectional** — the OC pre-loads from the
    reposición list; the stage-13 endpoint and its response shape stay unchanged.
11. **`estado_orden_compra` is a native Postgres enum**, 5 values declared in lifecycle order —
    the stage-14-decision-8 criterion (machine of states → native enum) applied.
12. **Cardinality 1 OC → N comprobantes via a composite FK** on `comprobantes_compra` —
    `comprobantes_compra.id_orden_compra integer NULL`, mirroring the `gastos.id_comprobante_
    compra` precedent, no bridge table.

Gate verdict: ONE migration (`OrdenesDeCompraEtapa16`) — purely additive: 1 new enum type, 2 new
tables (`ordenes_compra` 16 columns/PK/AK/5 FKs/2 CHECKs/7 indexes incl. implicit AK,
`items_orden_compra` 11 columns/PK/3 FKs/2 CHECKs/4 indexes), 1 additive ALTER on
`comprobantes_compra` (+`id_orden_compra NULL` + composite FK MATCH SIMPLE + support index,
metadata-only), `ManejadorDeErrores` modified (6 new exact-name branches), `Politicas`/
numeraciones/`tipos_comprobante` untouched. Total new indexes = **12**, independently verified
by the orchestrator before approval (numeraciones has no FK to `tipos_comprobante`, so the 'OC'
series costs zero schema; the ordering trap has exactly 2 precedents plus this stage's 3rd; the
child-scope criterion for items is documented verbatim; the partial-unique precedent uses
`HasFilter`).

### Design — 16 architecture decisions (`design.md`), zero new DDL

1. `EscriturasDeOrdenDeCompra` is `static`, structural copy of
   `EscriturasDeCuentaCorrienteProveedor` — exactly ONE place writes `ordenes_compra.estado`
   from the book.
2. `ProyectarEstadoAsync` = lock → short-circuit (`anulada`/manual close, unbypassable) →
   derive (separate statement) → conditional `UPDATE … RETURNING`, skipped when the projected
   estado equals the current one (idempotency observable as zero rows written).
3. The derivation groups by `id_articulo` on BOTH sides and asks two independent questions
   (`completa`, `algoRecibido` sourced from the **reception** side, so a pure-substitution
   delivery is still visible).
4. The target estado is decided by a pure Domain function,
   `ProyectorDeEstadoDeOrden.Proyectar(...)` — no database, `PoliticaDeRoles` pattern.
5. Automatic close writes `fecha_cierre`/leaves `id_empleado_cierre` NULL; a regression out of
   `cerrada` clears `fecha_cierre` in the same statement (the `ck_ordenes_compra_cierre` CHECK
   would otherwise 23514 a forgotten clause).
6. `enviar` assigns the number BEFORE its own transaction and pins the punto de venta in the
   transition `WHERE` — closes a real race the proposal didn't name (a concurrent `PUT` moving
   the draft to another PV between pre-read and lock).
7. `enviar` refuses an OC with no items (`orden_compra_sin_items`, 400) — otherwise the
   derivation's vacuous `NOT EXISTS` would read it `cerrada` on first projection.
8. The draft link is validated by `ExigirOrdenLigableAsync` (`FOR SHARE`); the BINDING guard is
   the confirm-time `FOR UPDATE`. Linkable: `enviada`/`recibida_parcial`/`cerrada`; refused:
   `borrador`/`anulada`.
9. The anulación's linked-draft guard reads `comprobantes_compra` WITHOUT any row lock — the
   one place a cycle is reachable; a plain snapshot read closes it, the TOCTOU is already
   closed on the confirm side.
10. All 6 `ManejadorDeErrores` branches ship in slice 1 with the migration, proven out-of-band
    — overrides the proposal's original slice-2 placement.
11. `ux_ordenes_compra_numero` resolves by exact name ABOVE `ClasificarUnicidad` — 3rd
    occurrence of the ordering trap.
12. The read model derives quantities per artículo and READS the estado from the column —
    never re-derives it (rule 12(a) applies literally).
13. The detail returns ordered lines AND a separate per-artículo `Cobertura` list — never a
    fabricated per-line split (grouping is per-artículo, not per-line).
14. Price deviation: weighted averages per artículo via the existing `CalculadorDeCompra`, IVA
    consistent, `null` never `0` when not comparable.
15. Listing is paginated, `fecha_emision DESC, id_orden_compra DESC` tiebreaker (the `RelojFijo`
    tie-by-construction lesson, stage-14/15 precedent).
16. No new policy — `/api/ordenes-compra` mirrors `/api/compras`'s exact gate shape; the
    consequence the proposal didn't draw (T5): `Reposicion.tsx` is Supervisor+Admin while
    creating an OC is Admin-only, so the web "Generar OC" action needs its own Admin gate or a
    Supervisor clicks into a 403.

Ten tensions (T1-T10) were flagged in design's own Open Questions for `sdd-tasks`/the
orchestrator to reconcile; all ten were arbitrated as OD8 below, all ratifying design.

### The 9 Orchestrator Decisions (OD1-OD9, `state.yaml`, spanning explore → spec → design → tasks)

| OD | Phase recorded | Subject | Verdict |
|---|---|---|---|
| OD1 | explore, formalized as proposal decision 1 | Reception does NOT move stock by itself — each reception is a linked comprobante | Ratified — preserves the confirm/anulación engine intact |
| OD2 | explore, formalized as proposal decision 12 | Cardinality 1 OC → N comprobantes via composite FK, no bridge table | Ratified — mirrors the `gastos.id_comprobante_compra` precedent |
| OD3 | explore, formalized as proposal decision 8 | Price deviation OC vs. factura: informative, never blocking | Ratified — a blocking control is the owner's policy, deferred with a reopen condition |
| OD4 | explore, formalized as proposal decision 11 | `estado_orden_compra` native Postgres enum, 5 values | Ratified — stage-14-decision-8 criterion applied |
| OD5 | explore | The reposición formula stays untouched this stage; "stock en tránsito" registered as an EXTENSION DIFERIDA with its insumo (`fecha_esperada` + pendientes) now ready | Ratified — the Purpose correction this archive phase performs cites this OD explicitly |
| OD6 | explore, formalized as proposal decision 9 | OC anulación only from `borrador`/`enviada` with zero effective receptions; a real reception CLOSES, never annuls | Ratified — verified against the estado matrix in the proposal |
| OD7 | spec phase (T1-T5) | 5 tensions arbitrated: (T1) the Purpose-prose falsity is not expressible in delta format — **binding archive-phase instruction, resolved by this phase**; (T2) the comprobantes-compra mirroring is the stage-15 pattern, not duplication; (T3) unnamed domain codes — design names them, tasks reconciles otherwise; (T4) word-budget overage — house precedent; (T5) `cuenta-corriente-de-proveedores` verified untouched | All ratified |
| OD8 | design phase (T1-T10) | 10 tensions, all ratifying design over the proposal's silence: same-OC concurrent `enviar` is 200+409 (T1); `cerrada` stays linkable (T2); `FOR SHARE` in `CrearBorradorAsync` is validation-only, not a lock (T3); citation line-number fix (T4); the web "Generar OC" gates to Admin (T5); empty-OC `enviar` refused (T6); `CompraDetalle` gains `IdOrdenCompra` (T7); `Cobertura` is a separate per-artículo list (T8); `algoRecibido` sourced from the reception side (T9); the anulación guard stays lock-free (T10) | All ratified |
| OD9 | tasks phase | The FK 2/FK 3 pre-check resolvers of `ServicioDeOrdenesDeCompra` are PRIVATE and PROPER to that class (not a shared helper) | Ratified — same criterion `ServicioDeGastos` already applies against `ServicioDeCompras`; stage-15 precedent (its own tasks.md deviation #34) |

### Tasks phase — 15 orchestrator decisions + a 14-entry apply-phase deviation sequence

`tasks.md`'s "Orchestrator Decisions Recorded This Phase" section runs decisions 1-15 (tasks
phase), then apply-phase deviations/judgment-day entries numbered 16-19, 24-29 (the file's own
inline note explains the apply run first registered the slice-5 entry as "20", colliding with
slice 3's own entry 20, and the orchestrator renumbered it to 24 — the sequence is internally
consistent once read with that note).

**Decisions 1-15** (tasks phase): (1) 6 slices stacked-to-main, merge order `1→2→3→4→5→6`,
adopted verbatim from design's ratified Slicing table; (2) DB gate `UNA-MIGRACION-APROBADA` with
the binding 12-index count; (3) pre-authorized cut points `1a/1b`, `3a/3b`, `5a/5b`, slice-6
action droppable — never degraded: lock-then-re-read-then-update, zero-extra-statements, the
`_numero` ordering trap, the manual-close short-circuit; (4) **CONFLICT #1 resolved** — the
`enviar` concurrency criterion (same-OC vs. distinct-OC), OD8/T1 authoritative; (5) **CONFLICT
#2 resolved** — ligadura state-gating domain codes, OD8/T2 ratifies "cerrada stays linkable";
(6) **CONFLICT #3 resolved** — an OC with zero items refused at `enviar`, OD8/T6 ratifies; (7)
**CONFLICT #4 resolved** — `CompraDetalle` gains `IdOrdenCompra`, OD8/T7 ratifies; (8) the 34
named mutation targets placed 1:1 across the 6 slices (1→9, 2→8, 3→13, 4→3, target 34 split
across 4/5/6 by sub-clause, none duplicated, none dropped); (9) `db-error-backstops` across
slices 1/2/3 per design decision 10; (10) `react-async-state`+`web-descriptor-tests` at slice 6
only; (11) `dto-contract-honesty` at slices 3 and 5; (12) `work-unit-commits` every slice; (13)
fixed-clock (`RelojFijo 2026-08-19T12:00:00Z`) + asymmetric-id testing convention (the stage-14
verify W2/PR #129 lesson) applied across all date-bearing and identity-bearing tests; (14) the
archive-phase Purpose-correction carryover registered so it is not read as an omission from this
phase's scope (this phase executes it, see Spec Merge Summary above); (15) the process rule
itself — every deviation `sdd-apply` takes is registered in `tasks.md`, never left to verify-time
archaeology.

**Deviations/judgment-day entries 16-19, 24-29** (apply phase, summarized by slice — full text
in the archived `tasks.md`):

- **Slice 1 (16)**: judgment-day round 1 (juez B) confirmed 1 MAJOR — a count-only index test
  let a column-order mutant survive (see Delivery Record above); closed tests-only, production
  code was correct.
- **Slice 2 (17-19)**: (17) OD9 — the FK 2/FK 3 pre-check resolvers are private and proper to
  `ServicioDeOrdenesDeCompra`; a `OrdenDeCompraBorrador` response DTO deviation (the design's
  single `OrdenDeCompraDetalle` cannot be honestly filled before the reception book exists,
  `dto-contract-honesty` rule 1); the DELETE endpoint deliberately NOT implemented (no artifact
  names it, resolved in favor of the authoritative design/tasks over a launch-prompt mismatch).
  (18) judgment-day round 1 (juez B) confirmed 2 MAJOR — rule-12c replace-set-`DELETE` scope
  gap and a `dto-contract-honesty` gap on `FechaEsperada`/`Observaciones` (see Delivery Record
  above). (19) judgment-day round 1 (juez A): 1 WARNING (code-name drift, closed as a
  registered deviation) + 1 CRITICAL REFUTED with two-dot-vs-three-dot diff evidence — the new
  process rule (judgment diffs freeze with `main...HEAD`) is registered here.
- **Slice 5 (24)**: (renumbered from "20" — collided with slice 3's own entry 20). Pre-load is
  NOT a backend method (design places the reposición→OC mapping entirely in `Reposicion.tsx`,
  resolved in favor of design over this file's own earlier drafted wording); the cobertura
  derivation is a deliberately SEPARATE LINQ query from the raw-ADO projection, cross-checked
  by a fidelity test rather than unified by code sharing (design's own testing strategy names
  two derivations); `Pendiente`/`TotalEstimado`/`TotalReal`/`DesvioTotal` formulas filled in
  where neither spec nor design pinned the exact arithmetic (documented here); test tipo
  `C-FB` (IVA-free) chosen deliberately for exact-decimal price-deviation assertions;
  judgment-day not run mid-slice (executor-boundary reason, same as every prior stage) — full
  solution suite run once end-to-end: 2216/2216 green.
- **Slice 5, judgment-day rounds (25-26)**: (25) round 1 (juez B) — **3 CRITICAL**, the rule-12b
  class 3rd occurrence, sharpening the rule twice in this stage (see Delivery Record above and
  Judgment-Day Summary below). (26) round 2 (juez A) — **1 CRITICAL of production**
  (`TotalEstimado` line-level fix) + 1 CRITICAL of tests/docs (decision 25's own listing-row
  coverage claim was itself false, corrected in place) + 1 WARNING (`ComprobantesLigados`
  exact-set assert) + 1 SUGGESTION (dead variable removed).
- **Slice 6 (27-28)**: (27) web-only slice, zero API/backend diff; `Compras.tsx` does NOT gain
  an `IdOrdenCompra` column (the link is shown where the data lives, `CompraEditor.tsx`,
  following the authoritative-DTO criterion already applied at slices 2/5); the post-write
  refetch's catch never clears `detalle` on a post-2xx failure (`react-async-state` rule 6,
  found writing the test first); the web scenario for "Generar OC" lives in
  `ordenes-de-compra/spec.md`, not `reposicion-de-stock/spec.md` (the button is an OC write,
  not a reposición-report behavior change — keeps the delta's byte-identical-formula boundary
  intact); `idAlicuotaIva` deliberately not pre-loaded (the OC has no IVA concept,
  `dto-contract-honesty`). (28) judgment-day round 1 (juez A) — **2 WARNING**, both closed (see
  Delivery Record above) plus the same-tick `Guardar borrador` flakiness judge B's pass caught
  and closed deterministically with `waitFor` guards, root-caused to a later `useEffect`
  hydration race — no assert weakened.
- **Orchestrator remediation (29)**: the three verify-report WARNINGs closed pre-archive — see
  Verify Verdict below.

## Judgment-Day Summary — 6 Confirmed CRITICALs Across 6 Slices, One Reaching Production

Every slice closed CLEAN before its PR merged, with mutation evidence (mutate → named test
fails → revert → green) recorded in `tasks.md` for every fix.

- **Slice 1 (0 CRITICAL, 1 MAJOR, 1 REFUTED CRITICAL)**: the column-order MAJOR born the
  index-DDL-assertion lesson; the CRITICAL was a false positive of the diffing method itself
  (two-dot vs. three-dot), not a real regression — closed by adopting `main...HEAD` as the
  binding freeze method for every future judgment-day round.
- **Slice 2 (0 CRITICAL, 2 MAJOR)**: rule-12c's 2nd occurrence (unscoped replace-set `DELETE`)
  and a `dto-contract-honesty` gap, both coverage-only, production correct underneath.
- **Slice 4 (0 CRITICAL, 1 MAJOR)**: a false claim of lock-free-invariant coverage after a
  temporary test was removed, closed with a permanent structural assertion.
- **Slice 5 — the richest round of the stage (4 CRITICAL across two rounds)**: judge B's 3
  CRITICALs are the rule-12b class's 3rd programme-wide occurrence — `Pendiente` asserted only
  at its zero case, three money totals never asserted with positive values, and two adjacent
  positional ints (`IdProveedor`/`IdPuntoVenta`) never read back in a 17-field record. Judge A's
  round-2 CRITICAL is **the stage's one production defect caught before merge**:
  `TotalEstimado` silently extrapolated cost from unquoted lines via a per-artículo average
  instead of summing only quoted lines — a real financial figure a purchasing operator would
  read as accurate.
- **Slice 6 (0 CRITICAL, 2 WARNING + one flaky test caught and closed deterministically)**: no
  production defect this slice; a coverage claim (same-tick guard on all 4 actions) was
  literally false for 3 of the 4 until widened, and the widening itself introduced a race the
  same judgment-day pass then found and closed without weakening any assertion.

No CRITICAL from slice 3 — clean on the first round.

### `mutation-proof-tests` rule 12: sharpened twice this stage

- **Rule 12(b) — Projection, sharpened a 3rd time** ("every positional field of a response
  record gets read back at least once with pairwise-distinct values") — a 17-parameter
  positional constructor with two adjacent ints (`IdProveedor`/`IdPuntoVenta`) was one swap away
  from shipping with rich fixtures on the "interesting" derived fields providing zero coverage
  for the identity/aggregate fields. Born from slice 5's 3 judge-B CRITICALs.
- **Rule 12(c) — Identity predicate, extended to WRITE paths ("2nd occurrence of the class")** —
  the same class that made a read-side `Where(IdProveedor)` undetectable (stage 15) now applies
  to a scoped destructive write: a replace-set `DELETE` widened to the whole table survives every
  single-entity fixture; every scoped destructive write now needs a sibling seed whose rows must
  remain intact. Born from slice 2's judge-B MAJOR.

## Verify Verdict (Final-State Authority)

Per `verify-report.md` (2026-08-19, HEAD `ae00fca`, covering all 6 slice PRs): **PASS WITH
WARNINGS**.

- **0 CRITICAL.**
- **W1 (doc-10's "Estado (Etapa 16)" annotation frozen at slice-1 scope — 4th occurrence of the
  recurring class, following stages 13/14/15's own WARNING 1)** — **remediated by the
  orchestrator, `tasks.md` decision 29**: the header now reads "COMPLETA (PRs #140-#145)" and
  states slice 6's web surface explicitly. This 4th occurrence is additionally **codified
  forward**: every future stage now carries an explicit task in its LAST slice to refresh the
  doc-10 header, closing the recurring class at the process level rather than remediating it
  again at the next archive.
- **W2 (`tasks.md` checkbox hygiene gap — slices 1/2's judgment-day/PR-merge checkboxes
  1.38/1.39/2.26/2.27 unchecked despite both PRs #140/#141 independently confirmed merged)** —
  **remediated, `tasks.md` decision 29**: all four checkboxes flipped with a closing note citing
  the PR numbers; the underlying work was never actually incomplete, only the bookkeeping.
- **W3 (design.md:309's claim that `SuperficieDeAutorizacionTests` gains the 5 new OC routes is
  stale — that file is an omission-guard allowlist, not a positive-coverage list; the 5 OC
  routes correctly stack `GestionDeCatalogo` and are caught by the existing generic check, with
  authorization coverage independently and explicitly tested elsewhere)** — **registered as
  decision 29, documentation-only, no code or test gap**.
- **SUGGESTION 1** (a short index for the tasks.md decision/deviation numbering split, same
  observation as stage-15's own SUGGESTION 1) — no action required for archive, noted for a
  future stage.
- All 15 requirement units / 48 scenarios mapped to passing tests; the migration gate (exactly
  one migration, matching the contract, 12 indexes, zero data statements) PASS; non-regression
  PASS (`VentasCheckoutTests.cs`/`src/Ways.Application/Ventas/`/`src/Ways.Application/Stock/`/
  `Politicas.cs` absent from all 6 merges' diffs; `ServicioDeCompras.cs` steps 2-6 byte-identical
  save for the insertion points); 14 of the pool's decisions sampled by verify (exceeds the
  rule-12 floor of 10), all found true against current code, no falsified deviation.

This report is the terminal record of the change per the Final-State Authority hierarchy: it
supersedes any "pending" framing for W1/W2/W3 (all three fixed pre-archive, `tasks.md` decision
29 cited above) and records no open verify-time item carried forward.

## Backlog / Deferred Extensions Registered By This Stage

- **Stock en tránsito in the reposición formula (OD5)** — the insumo (`fecha_esperada` +
  derivable pending quantity per artículo) now exists after this stage, but subtracting it from
  `sugerido` remains explicitly out of scope for the formula. Reopen condition: the first
  customer who over-orders because the report ignores stock already on the way. The Purpose
  correction this phase performed (see Spec Merge Summary) keeps this deferral honestly stated
  going forward instead of citing a since-false justification.
- **A `CompraListada` (compra-list row) column surfacing the linked OC** — `CompraListada`
  (`GET /api/compras`'s row) was never widened with `IdOrdenCompra`; only `CompraDetalle` carries
  it. Slice 6 resolved the display gap by showing the link inside `CompraEditor.tsx` instead
  (where the data lives), with zero backend change authorized for that slice. Widening the list
  row would need its own backend slice.
- **Price deviation as a blocking control** — ratified informational-only by proposal decision
  8/OD3; a blocking threshold is explicitly the owner's purchasing policy to set, deferred with
  no mechanism shipped now so nothing is paid twice if it is later requested.
- **DELETE of an OC draft** — deliberately not implemented; no artifact (design's API Surface
  table, `tasks.md`) ever named a delete route, and the launch prompt's mention was resolved in
  favor of the authoritative SDD artifacts. Flagged for a future decision if a real need arises.
- **A `recepciones_orden_compra` bridge table decoupling physical reception from the invoice** —
  refused at explore/OD1 in favor of "each reception IS a comprobante," preserving the proven
  confirm/anulación engine untouched. Would require a materially different design if reopened.
- **Printing/emailing the OC, and auditing OC transitions in `auditoria`** — both named and
  refused in writing by the proposal (Out of Scope), no reopen mechanism costed.

## Rollback Note (carried from proposal, unaffected by archival)

**Fully reversible, purely additive stage.** Slices 2-6 are additive code over an unchanged
schema — reverting one removes a write path or a read surface, leaving the two new tables intact
and consistent (nothing was ever mutated destructively by this stage's own code paths). Slice 1
(the schema): `ALTER TABLE comprobantes_compra DROP COLUMN id_orden_compra` → `DROP TABLE
items_orden_compra` → `DROP TABLE ordenes_compra` → `DROP TYPE estado_orden_compra` — no
dependent object blocks that order (no self-FK on either new table; no other column uses the
enum). Zero data statements shipped in the migration, so nothing needs repair on rollback: the
two new tables start empty and stay empty until the application writes to them.

## Next Steps (orchestrator-owned, outside this phase)

1. Move `openspec/changes/stage-16-ordenes-de-compra/` to
   `openspec/changes/archive/2026-08-19-stage-16-ordenes-de-compra/` with deterministic copy,
   matching the mechanical-move discipline this phase used for the spec merges.
2. Update `docs/11-programa-post-paridad.md`'s Etapa 16 status block with the OD1-OD9
   resolutions and the delivery record above.
3. Close `state.yaml`'s `archive` phase (`status: pending` → `done`) once the folder move is
   confirmed.

## SDD Cycle Status

The change has been fully planned (explore → propose → spec → design → tasks), implemented (6
slices, PRs #140-#145, all merged stacked-to-main), verified (PASS WITH WARNINGS, W1/W2/W3 all
remediated pre-archive per `tasks.md` decision 29), and its specs fused into `openspec/specs/`
(this phase, content complete, evidence above). Folder archival and doc-11 closure remain for the
orchestrator's deterministic-copy step.
