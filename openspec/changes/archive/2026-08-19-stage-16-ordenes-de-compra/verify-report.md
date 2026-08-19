# Verification Report — stage-16-ordenes-de-compra

**Change**: Stage 16 — Órdenes de compra
**HEAD verified**: `ae00fca` (6 slices merged: PRs #140-#145, stacked-to-main)
**Date**: 2026-08-19
**Verdict: PASS WITH WARNINGS** (0 CRITICAL, 3 WARNING, 1 SUGGESTION, all remediable pre-archive)

## Method note

The orchestrator's full suites (Domain 526/526, Application 291/291, Integration 1402/1402 -
re-run in isolation after 3 Testcontainers-flakiness failures, 4th occurrence of that pattern -
vitest 796/796 x3 consecutive, 2026-08-19) were accepted as evidence and not re-run in full.
Verify ran its own filtered spot-checks plus source inspection against every binding artifact:

| Check | Result |
|---|---|
| dotnet ef migrations has-pending-model-changes (--startup-project src/Ways.Infrastructure, the documented workaround for the Ways.Api EF-Design-package gap) | clean - "No changes have been made to the model since the last migration." |
| Migration file read in full (20260819042145_OrdenesDeCompraEtapa16.cs) | matches gate section A-D verbatim; ordering CREATE TYPE then ordenes_compra+indexes then items_orden_compra+indexes then ALTER comprobantes_compra then RLS on both new tables last |
| Index count by direct read of the migration's CreateIndex calls | 6 named + 1 implicit AK on ordenes_compra (7) + 4 named on items_orden_compra (4) + 1 named on comprobantes_compra (1) = 12, matches the binding count exactly |
| ManejadorDeErrores.cs read in full around the OC branches | exactly 6 exact-name branches: 2 23505 (ux_ordenes_compra_numero placed ABOVE ClasificarUnicidad's generic call - 3rd ordering-trap occurrence, confirmed by line order; ux_items_orden_compra_orden) + 4 23514 dispatched through a StartsWith-guarded switch |
| git diff --stat across all 6 merges (feed161..ae00fca) for src/Ways.Application/Ventas/, src/Ways.Application/Stock/, src/Ways.Api/Seguridad/Politicas.cs, tests/Ways.IntegrationTests/VentasCheckoutTests.cs | all four empty - zero touches |
| git diff feed161..ae00fca -- src/Ways.Application/Compras/ServicioDeCompras.cs read in full | confirms insertion-only diff: two widened RETURNINGs (+id_orden_compra), two guarded call sites (position 1.b/1.6, both behind if IdOrdenCompra is idOc), one new private ExigirOrdenLigableAsync method, one new positional field on the CompraDetalle mapper - steps 2-6 of confirm/anular untouched, byte-identical |
| Migration grep -c "migrationBuilder.Sql(" | 0 - zero data statements |
| OrdenesDeCompraEndpoints.cs read in full | exactly 7 routes (GET /, GET /{id}, POST /, PUT /{id}, POST /{id}/enviar, POST /{id}/cerrar, POST /{id}/anular) - no DELETE, matches decision 17's registered non-implementation; all 5 writes stack Politicas.GestionDeCatalogo |
| Test-file spot reads across all 6 slices' integration/application/web test files | all binding tests named by the contract exist and match their described shape (detail below) |

## 1. Gate (binding, state.yaml db_gate_approval)

| Criterion | Result |
|---|---|
| Exactly one new migration named OrdenesDeCompraEtapa16 | PASS - one .cs + one .Designer.cs; the only August-19 migration in the tree; git log shows a single migration commit (1088a37) for this stage |
| dotnet ef migrations has-pending-model-changes clean | PASS (verified empirically above) |
| Index count = 12 | PASS - verified by direct read of the migration's CreateIndex calls (7+4+1), matching ElConteoTotalDeIndicesNuevosEsExactamenteDoce's asserted count and LasDefinicionesDeLosIndicesCompuestosRespetanElOrdenDeColumnasDelContrato's column-order proof (added in slice-1 judgment-day, decision 16, after a name-only test let a column-swap mutant survive) |
| ManejadorDeErrores.cs - EXACTLY 6 branches, ordering trap resolved above ClasificarUnicidad | PASS - confirmed by direct source read; ux_ordenes_compra_numero sits at lines 159-161, ClasificarUnicidad's generic dispatch at 203-205 |
| Zero DDL outside proposal sections A-D, zero data statements | PASS - migration body is exactly: enum annotation, 2 CreateTable (+ FKs/CHECKs/indexes), 1 AddColumn + AddForeignKey + CreateIndex on comprobantes_compra, HabilitarRlsDeTenant x2 last. No other table touched, no migrationBuilder.Sql( |
| Cero-statements-extra proof for a confirm without OC, TWO nets | PASS - EscriturasDeOrdenDeCompraLockOrderTests (structural: asserts the literal if IdOrdenCompra guard and that the call sits behind it, mutation target #29) + ServicioDeComprasLigaduraTests.UnConfirmSinOrdenLigadaNoTocaNingunaOrdenDeCompraExistente (behavioral: a sibling OC of the same proveedor/PV seeded with a landmine - an FK-real confirmed reception that would flip the sibling's projection if any statement touched it - and both Estado/UpdatedAt assert byte-unchanged). Both present, matching the judgment-day-round-2 finding (decision 21) that neither net alone is sufficient |
| Two concurrent enviar on distinct OCs, one PV - no 409 | PASS - DosEnviarConcurrentesDeOrdenesDistintasEnElMismoPuntoDeVentaDanNumerosDistintosSin409 present |
| Two concurrent enviar on the SAME OC - 200+409, number burnt | PASS - DosEnviarConcurrentesDeLaMismaOrdenDanUn200YUn409ConNumeroQuemado present |

## 2. Traceability (spec to implementation to test)

Actual counts (measured against the retrieved spec files, not estimated):

| Spec | Requirements | Scenarios |
|---|---|---|
| ordenes-de-compra (NEW) | 11 | 34 (33 original + 1 Admin-gate scenario added in slice 6, "The Generar OC action is Admin-gated...", line 288) |
| comprobantes-compra (delta) | 3 ADDED | 9 |
| reposicion-de-stock (delta) | 1 MODIFIED | 5 |
| Total | 15 requirement units | 48 scenarios |

Sampled trace-to-test, confirmed by direct file/test-name inspection:

- Schema at rest / both CHECKs both directions / both item CHECKs -> OrdenesCompraSchemaTests.cs
  (UnNumeroSinFechaDeEnvioViolaLaCheckDeEnvioCompleto, UnaFechaDeEnvioSinNumeroViolaLaCheckDeEnvioCompleto,
  UnaFechaDeCierreConEstadoNoCerradaViolaLaCheckDeCierre, UnCierreManualSinFechaDeCierreViolaLaCheckDeCierre,
  UnaCantidadPedidaNoPositivaViolaLaCheck, UnCostoUnitarioEstimadoNegativoViolaLaCheck) - the CHECK x4
  criterion is satisfied exactly (2 CHECKs, both directions each).
- RLS on both new tables -> UnaSesionDeOtroTenantNoVeLasOrdenesDeCompraPorSelect,
  UnaSesionDeOtroTenantNoVeLosItemsDeOrdenDeCompraPorSelect, UnInsertConIdTenantAjenoEnOrdenesCompraSeRechaza.
- The _numero ordering trap -> UnNumeroDuplicadoEnElMismoPuntoDeVentaResuelveAlConstraintExactoDeOrdenesDeCompra
  (asserts the translated code, not the SQLSTATE alone) + the two binding concurrency tests (section 1).
- Estado projection (recibida_parcial/cerrada, regression on annulment, duplicate lines, over-delivery,
  confirm x confirm race) -> ServicioDeComprasLigaduraTests.cs (multiple facts) + Domain unit
  ProyectorDeEstadoDeOrden truth table.
- Manual close immutability -> UnaOrdenCerradaManualmenteNoSeReabreAlAnularSuRecepcion
  (OrdenesCompraCierreYAnulacionTests.cs).
- Anulacion governed by the book, defense-in-depth on confirm -> UnaOrdenCuyaUnicaRecepcionFueAnuladaPuedeAnularseElla,
  UnaOrdenConRecepcionEfectivaNoPuedeAnularse409, UnaOrdenConBorradorLigadoConfirmableNoPuedeAnularse409,
  ConfirmarUnaRecepcionLigadaAUnaOrdenRealmenteAnuladaPorElEndpointEsRechazada409.
- The 4 slice-4 races -> AnularPierdeCuandoConfirmarComitePrimeroMientrasAnularEstaPausada,
  AnularPierdeCuandoIntentaPrimeroMientrasConfirmarEstaPausada, AnularGanaLaCarreraCuandoElPutQueLigaEstaPausado,
  ElPutQueLigaGanaLaCarreraCuandoAnularEstaPausada - all four present, confirmed non-bidirectional finding
  registered honestly in decision 22 rather than reshaped.
- Lock-free anulacion guard (statements 2/3, no FOR SHARE/FOR UPDATE) -> ServicioDeOrdenesDeCompraLockFreeGuardsTests
  - source-text assertion confirmed present with Assert.DoesNotContain("FOR SHARE"/"FOR UPDATE", ...).
- TotalEstimado line-level formula (round-2 production CRITICAL fix) -> TotalEstimadoSumaSoloLasLineasCotizadasSinExtrapolarAlPromedioDelArticulo
  present in OrdenesCompraLecturaTests.cs.
- Web Admin gate on "Generar OC" -> Reposicion.tsx lines 48,194 (esAdmin = usuario rolId === ROL.Admin,
  grupo.idProveedor !== null && esAdmin) + Reposicion.test.tsx gating tests (6.9/6.15/6.16).
- doc-10 update -> present (section detail below).

All sampled scenarios trace to a passing test; no untested binding scenario found in the sample.

## 3. Decisions (proposal's 12 + design's 16 + 9 Orchestrator Decisions + 28 tasks.md entries)

Sampled 14 of the pool (exceeds the rule-12 floor of 10), weighted toward the ones the launch
prompt named explicitly and the ones later corrected:

| Decision | Verified against code |
|---|---|
| Proposal 2 (cantidad_recibida NOT a column, derived) | PASS - no such column in the migration; EscriturasDeOrdenDeCompra's statement 2 is a SUM ... GROUP BY id_articulo CTE |
| Proposal 4 (own numbering, reuse AsignadorDeNumeroComprobante with 'OC', no seed) | PASS - AsignadorDeNumeroComprobante.cs absent from the stage's diff (git diff --stat empty); ServicioDeOrdenesDeCompra.EnviarAsync calls it with "OC" |
| Proposal 11 (native 5-value enum, declaration order = C# order) | PASS - migration annotation borrador,enviada,recibida_parcial,cerrada,anulada matches EstadoOrdenCompra member order |
| Design decision 6 (assigner outside the tx, PV-pinned UPDATE) | PASS - EnviarAsync calls the assigner before BEGIN; the transition UPDATE carries AND id_punto_venta = pv |
| Design decision 9 (anulacion's linked-draft guard is LOCK-FREE) | PASS - ServicioDeOrdenesDeCompraLockFreeGuardsTests (section 1) |
| Design decision 16 / OD8-T5 (web Admin gate on "Generar OC") | PASS - Reposicion.tsx (section 2) |
| OD9 (FK 2/FK 3 pre-check resolvers are PRIVATE to ServicioDeOrdenesDeCompra, not shared) | PASS - ResolverProveedorAsync/ResolverPuntoVentaAsync/ExigirArticulosExistentesAsync are private in ServicioDeOrdenesDeCompra.cs, no shared helper class introduced |
| tasks.md decision 16 (judgment-day slice-1 MAJOR - index column-order test) | PASS - LasDefinicionesDeLosIndicesCompuestosRespetanElOrdenDeColumnasDelContrato present, asserts full indexdef |
| tasks.md decision 19 (code-name drift: orden_compra_no_enviable deliberately more general than design's orden_compra_ya_enviada) | TRUE - orden_compra_no_enviable is the code shipped, orden_compra_ya_enviada does not appear anywhere in src/ |
| tasks.md decision 22/23 (lock-free invariant - decision 22's own interim claim about interceptor-test coverage was FALSE, corrected by decision 23's permanent structural test) | TRUE, both halves - the interceptor tests pause at BeginTransactionAsync (before any statement, cannot see a statement-level lock omission) and the actual regression is the structural test confirmed above |
| tasks.md decision 24 (slice-5 apply registered itself as "20", colliding with slice-3's "20" - orchestrator renumbered to 24) | TRUE - the file's numbering runs 1-19, then 20(slice3)/21(judgment)/22(slice4)/23(judgment)/24(renumbered slice5)/25/26/27/28, internally consistent with the note |
| tasks.md decision 26 (production CRITICAL: TotalEstimado was extrapolating from a per-articulo average instead of summing only quoted lines) | TRUE - the fix and its discriminating test (TotalEstimadoSumaSoloLasLineasCotizadasSinExtrapolarAlPromedioDelArticulo) are both present and match the described before/after values (700 wrong vs 300 correct) |
| tasks.md decision 28 (web WARNING: puedeRecepcionar didn't AND puedeEscribir, fixed) | TRUE - grep of OrdenDeCompra.tsx confirms puedeEscribir gates "Registrar recepcion"; the widened Vendedor test asserts its absence |
| tasks.md decision 17 (DELETE endpoint deliberately NOT implemented, launch-prompt/design mismatch resolved in favor of design) | TRUE - OrdenesDeCompraEndpoints.cs has exactly 7 routes, no MapDelete |

No falsified deviation found in the sample - every documented claim checks out against the
current codebase (rule 12 satisfied).

## 4. Non-regression

| Check | Result |
|---|---|
| VentasCheckoutTests.cs absent from the stage's diff | PASS |
| No file under src/Ways.Application/Ventas/ or src/Ways.Application/Stock/ in the diff | PASS |
| Politicas.cs untouched | PASS |
| ServicioDeCompras.cs steps 2-6 (items, lotes, stock, costo, proveedores ledger) byte-identical | PASS - full diff read confirms only 2 widened RETURNINGs, 2 guarded call insertions, 1 new private method, 1 mapper field addition; no line inside steps 2-6 changed |
| ManejadorDeErrores.cs - only the 6 new OC branches added, no pre-existing branch touched | PASS - confirmed by source read; the pre-existing ordering-trap branches (ux_comprobantes_venta_numero, ux_comprobantes_compra_numero_externo) are unchanged in position and content |

## 5. DB-10

docs/10-modelo-de-datos.md section 5-adjacent carries both new tables with a DDL block matching the
shipped migration column-for-column, the comprobantes_compra.id_orden_compra annotation with
its own DDL fragment, and the "Estado (Etapa 16)" note describing EntidadBase: SI, the
MapEnum registration, and the 6 ManejadorDeErrores branches.

The annotation is NOT fully congelada at slice 1 in the narrow textual sense the header literally
reads ("Estado (Etapa 16, Slice 1 - schema + backstops...)") - but unlike stages 13/14/15's W1
pattern (which left the annotation in pure future tense, "llega en Slice X"), this one closes
with a one-line forward reference: "El proyector puro... y el motor de escritura... son slice 3;
ServicioDeOrdenesDeCompra (draft CRUD, enviar, cerrar, anular, lectura) es slices 2-5." That
sentence is factually accurate (all of it shipped) but it is terse relative to stage 15's closing
annotation style ("implementada - etapa completa (PRs #134-#139)") and it never mentions slice 6
(web) at all, nor the PR range #140-#145. Flagged as WARNING 1 below - the same recurring class
flagged in stages 13, 14 and 15's own verify reports, present again a fourth time.

## Warnings

WARNING 1 (doc-10 annotation incompleteness - 4th occurrence of the recurring class) -
docs/10-modelo-de-datos.md's "Estado (Etapa 16)" header still reads "Slice 1 - schema +
backstops" and never states the whole stage (6 slices, PRs #140-#145) is complete, nor mentions
slice 6's web surface at all. Unlike stages 13/14's fully-frozen instances, this one partially
self-corrects with a one-line slice-2-5 forward reference, but it is still incomplete and
inconsistent with stage 15's closing-annotation convention. Remediable pre-archive: update the
header to "COMPLETA (PRs #140-#145)" and add one clause for slice 6 (web screens, the Admin-gated
"Generar OC" action), mirroring how stage 15's own WARNING 1 was closed at archive time.

WARNING 2 (tasks.md checkbox hygiene gap - slices 1 and 2) - tasks.md tasks 1.38/1.39
(judgment-day / branch-PR-merge for slice 1) and 2.26/2.27 (same for slice 2) remain unchecked
("- [ ]"), even though the underlying work is independently verified as done: PR #140 (slice 1)
and PR #141 (slice 2) are both merged into main (confirmed by git log), and both slices'
judgment-day rounds are documented with fix evidence (decisions 16/18/19 for slice 1's index-order
MAJOR and slice 2's two MAJORs plus the refuted CRITICAL; decision 19 for slice 2's judgment-day
juez A findings). Every later slice (3 through 6) closed its equivalent checkboxes explicitly with
a dedicated "docs(sdd): cierra la tarea N.NN" commit; slices 1 and 2 never got that closing commit.
Non-blocking (the work itself is verified complete by independent evidence), but it is a real gap
in the "process rule: every deviation registered in tasks.md" discipline the stage's own decision
15 claims to enforce. Remediable pre-archive: flip the four checkboxes with a short closing note
citing PR #140/#141.

WARNING 3 (design.md claim about SuperficieDeAutorizacionTests does not match what shipped,
undocumented as a deviation) - design.md's API Surface section states "The stage-5
SuperficieDeAutorizacionTests allowlist gains the five new non-GET routes" (design.md:309).
git diff --stat across all 6 merges shows zero touches to that file. On inspection, the claim was
imprecise rather than a missed obligation: SuperficieDeAutorizacionTests is an omission-guard
allowlist - it only needs an entry for a write route that does NOT stack GestionDeCatalogo; the 5
new OC write routes correctly DO stack it (OrdenesDeCompraEndpoints.cs, confirmed by direct read),
so they are caught and pass by the existing generic check without needing an allowlist entry. The
equivalent authorization matrix is independently and explicitly tested in
OrdenesCompraCierreYAnulacionTests.cs (VendedorEsRechazadoEnLasCincoRutasDeEscrituraDeOrdenesDeCompra,
SupervisorEsRechazadoEnLasCincoRutasDeEscrituraDeOrdenesDeCompra,
AdminEjerceElCicloCompletoDeEscrituraDeOrdenesDeCompra), so authorization coverage is complete -
only the design-document claim about where that coverage lives is stale, and it was never
registered as a task-level deviation despite decision 15's "every deviation is registered"
discipline. Non-blocking, documentation-only.

## Suggestions

SUGGESTION 1 - tasks.md's "Orchestrator Decisions Recorded This Phase" numbered list mixes
three different sources under one running sequence (tasks-phase decisions 1-15, apply-phase
deviations 16-28, with one mid-sequence renumbering registered inline at 24) across 1773 lines.
Same observation stage-15's verify report made (its own SUGGESTION 1) about the equivalent list -
a short index at the top would save a future auditor the full-file read this verify pass required.
No action needed for archive.

## Compliance

mutation-proof-tests (34/34 named targets placed and evidenced across the 6 slices, per-slice
mutation cycles recorded with FAIL then revert then green evidence in tasks.md); db-error-backstops
(all FK/CHECK/AK exemptions documented, the 3rd ordering-trap occurrence resolved and tested);
dto-contract-honesty (CompraDetalle.IdOrdenCompra round-trip tested, OrdenDeCompraBorrador
deviation correctly scoped to avoid filler fields, TotalEstimado's line-level fix is itself a
dto-contract-honesty correction); react-async-state / web-descriptor-tests (slice 6 only,
verified: descriptor tests, same-tick double-click guards on all 4 write actions, stale-response
discard, flakiness closed deterministically); work-unit-commits; judgment-day (clean rounds
recorded per slice in tasks.md's apply notes, with every confirmed finding closed by a fix plus
mutation evidence, including one refuted CRITICAL with git-diff-freezing-method evidence).

Next recommended: sdd-archive, after remediating WARNING 1 (doc-10 annotation refresh) and
WARNING 2 (checkbox hygiene) - both non-blocking, both remediable in the same style stages 14/15
closed their equivalents. Recall the binding archive-phase carryover already registered in
tasks.md decision 14: the LIVE openspec/specs/reposicion-de-stock/spec.md Purpose prose must be
updated together with this stage's delta fusion (T1 of the spec phase) - that is sdd-archive's
job, not a verify gap.
Risks: none blocking. No CRITICAL found.
