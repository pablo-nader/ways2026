```yaml
schema: gentle-ai.verify-result/v1
evidence_revision: sha256:e8ef467bc505947a96d31ac6d6349ef3f8d9c8dd5628279f0c7f22e703d87c83
verdict: pass_with_warnings
blockers: 0
critical_findings: 0
requirements: 29/29
scenarios: 80/80
test_command: dotnet test tests/Ways.IntegrationTests --filter "FullyQualifiedName~ElConteoTotalDeIndicesNuevosEsExactamenteCatorce|FullyQualifiedName~ElConteoTotalDeIndicesNuevosAcumuladoEsExactamenteTreinta|FullyQualifiedName~LasDefinicionesDeLosIndicesCompuestos|FullyQualifiedName~UnaVentaConElTipoPreSembradoInactivoEsRechazada400SinEscribirNada|FullyQualifiedName~UnTipoDeVentaFueraDeBandaConAfectaStockFalseEsRechazadoAunqueEsteActivo|FullyQualifiedName~UnaVentaConElTipoTxrEsRechazada400SinEscribirNada|FullyQualifiedName~UnaVentaComunSigueEmitiendoDieciseisConsultasConLaRamaDelSnapshotPresente|FullyQualifiedName~UnPresupuestoConVencimientoPasadoSeReportaVencidoYElFiltroLoDiscrimina"
test_exit_code: 0
test_output_hash: sha256:14dbe01470c65dcc17e4d870f49fac094e7ca1061a042fa3df6d245505b98edb
build_command: dotnet build Ways.slnx
build_exit_code: 0
build_output_hash: sha256:9059a41b049aae6e586ec92ca0ac645c32267179b7e9ca14a29561e2e9b578fa
```

## Verification Report

**Change**: stage-17-presupuestos-y-remitos
**Version**: main @ `231db65` (8 slices merged, PRs #146-#155, plus standalone fixes #149/#154)
**Mode**: Standard (full artifact set: proposal, specs, design, tasks, state.yaml)

This verify does not re-run the full Domain/Application/Integration/vitest suites (per orchestrator
instruction -- those are already recorded in tasks.md's Work Unit Evidence tables). It re-runs
`dotnet ef migrations has-pending-model-changes`, `dotnet build`, and a targeted set of the binding
tests named by state.yaml / design.md's "Binding verify criteria" to produce first-party runtime
evidence for the criteria that are actually vinculante, plus static/source inspection of every spec
requirement, every task checkbox, and doc-10.

### Completeness

| Metric | Value |
|--------|-------|
| Tasks total (checked lines, `- [x]`) | 210 |
| Tasks complete | 210 |
| Tasks incomplete | 0 |
| Spec requirements (6 delta/new files) | 29 |
| Spec scenarios | 80 |

### Build & Tests Execution

**Build**: PASSED -- `dotnet build Ways.slnx`, 0 errors (2 pre-existing `NU1903` SSH.NET advisory
warnings, unrelated to this stage).

**EF gate**: PASSED -- `dotnet ef migrations has-pending-model-changes --project src/Ways.Infrastructure
--startup-project src/Ways.Infrastructure` returns "No changes have been made to the model since the
last migration."

**Targeted tests** (binding criteria only, see command above): 10/10 PASSED -- `ElConteoTotalDeIndicesNuevosEsExactamenteCatorce`,
`ElConteoTotalDeIndicesNuevosAcumuladoEsExactamenteTreinta`,
`LasDefinicionesDeLosIndicesCompuestosRespetanElOrdenDeColumnasDelContrato` (x2, one per schema
test file), `UnaVentaConElTipoPreSembradoInactivoEsRechazada400SinEscribirNada`,
`UnTipoDeVentaFueraDeBandaConAfectaStockFalseEsRechazadoAunqueEsteActivo`,
`UnaVentaConElTipoTxrEsRechazada400SinEscribirNada`,
`UnaVentaComunSigueEmitiendoDieciseisConsultasConLaRamaDelSnapshotPresente`,
`UnPresupuestoConVencimientoPasadoSeReportaVencidoYElFiltroLoDiscrimina` (the previously-failing
calendar-drift test, confirmed fixed by standalone PR #154 -- see Warnings).

**Full-suite numbers** (not re-run here; taken from tasks.md's recorded Work Unit Evidence, cross-checked
against the orchestrator brief): Domain **540/540**, Application **297/297**, Integration
**1580/1581 at task 8.11's snapshot -- the one deterministic failure was fixed by standalone PR #154
(commits `df6d3fb`/`6f8872d`, merged `37c0149` before PR #155)**, re-confirmed green by this verify's
own targeted re-run of that exact test. Web (vitest): tasks.md task 8.12 records **902/902** (55
files) -- see Warning W1 for the discrepancy against the orchestrator's stated 906/906.

### Binding Verify Criteria (design.md:628-654, state.yaml db_gate_approval)

| # | Criterion | Evidence | Result |
|---|---|---|---|
| 1 | Exactly two migrations PresupuestosEtapa17/RemitosEtapa17, nothing else; 30 new indexes verified by definition against pg_indexes; has-pending-model-changes clean | `src/Ways.Infrastructure/Persistencia/Migraciones/` lists exactly these two as the last two migrations (confirmed via Glob, no third). `PresupuestosSchemaTests.ElConteoTotalDeIndicesNuevosEsExactamenteCatorce` (line 451) and `RemitosSchemaTests.ElConteoTotalDeIndicesNuevosAcumuladoEsExactamenteTreinta` (line 569) both query pg_indexes directly and assert indexname-set equality + total = 14 then 30 cumulative; re-run PASSED. `dotnet ef migrations has-pending-model-changes` clean, re-run PASSED. | PASS |
| 2 | Exactly three data statements + TiposComprobanteBase change; the only ALTER TYPE ADD VALUE is motivo_stock 'remito' in RemitosEtapa17 | Confirmed by source read of both migration files (20260819195638_PresupuestosEtapa17.cs, 20260820004658_RemitosEtapa17.cs) -- ALTER TYPE ADD VALUE appears once, isolated, in RemitosEtapa17, matching db_gate_approval's registered irreversible artifact. | PASS |
| 3 | ServicioDeVentas.cs diff bounded to: one clause in ResolverTipoComprobanteAsync, the decide-phase snapshot branch + private materializer, one guarded call in EjecutarTransaccionAsync, one guarded call in EjecutarAnulacionAsync behind the widened RETURNING; zero extra statements for an ordinary sale/anulacion | Confirmed by source read: `!tipo.AfectaStock` is the sole added clause at line 1162; the snapshot branch at `if (solicitud.IdPresupuestoOrigen is { } idPresupuestoOrigen)` (line 104); the guarded call at line 987 (`if (plan.IdPresupuestoOrigen is { } ...)`); the guarded call at line 708 (`if (codigoTipoAnulado == "TXR")`). `UnaVentaComunSigueEmitiendoDieciseisConsultasConLaRamaDelSnapshotPresente` asserts exactly 16 EF commands for an ordinary sale, re-run PASSED. Slice 8's registered deviation (OD10) is the one authorized exception: `ObtenerAsync`'s read-only TXR branch, outside the transactional path, explicitly pre-authorized by state.yaml OD10 and confirmed by git diff --stat to leave EjecutarTransaccionAsync/EjecutarAnulacionAsync untouched (task 8.13 evidence). | PASS |
| 4 | Both PRE nets proven independently | `UnaVentaConElTipoPreSembradoInactivoEsRechazada400SinEscribirNada` (net 1 -- activo=false data statement) and `UnTipoDeVentaFueraDeBandaConAfectaStockFalseEsRechazadoAunqueEsteActivo` (net 2 -- the resolver's generic AfectaStock guard, proven with a synthetic active/afecta_stock=false type) both re-run PASSED independently. | PASS |
| 5 | stock spec restates four write sites naming ServicioDeRemitos, with its own concurrency test; cantidad invariant restated over nine motivos | specs/stock/spec.md:77-91 names all four sites explicitly. `ServicioDeRemitosTests.RemitirYCheckoutSobreElMismoArticuloYLoteNoDeadlockeanYAmbosCompletan` / `RemitirYRemitirSobreElMismoArticuloYLoteNoDeadlockeanYAmbosCompletan` are the fourth site's own concurrency tests. `InvarianteStockYStockLotesTests.cs` covers the cantidad invariant across motivos (existing suite, unaffected by this stage per its own non-regression filter). | PASS |
| 6 | Politicas.cs/AsignadorDeNumeroComprobante.cs unchanged; no diff under Compras/Stock/ | git log -1 on both files shows last touch 53ca8c9, pre-stage-16-close (Aug 17), before stage 17 began (Aug 19). `git diff --stat 5f03056~1..231db65 -- src/Ways.Application/Compras/ src/Ways.Application/Stock/` is empty. | PASS |
| 7 | Mutation evidence recorded in the PR body for every mutation-target row of its slice | Confirmed by sampling: tasks.md records apply-red-revert-green cycles for targets 31-35 (slice 3), 40-47 (slice 5), 48-58 (slice 6), OD10's clause (slice 8), etc, each with the exact assertion observed. Consistent with the 60-target placement table (decision 8). | PASS |
| 8 | Domain/Application/Integration/vitest green; colocated tests for every new pure helper/descriptor | Domain 540/540, Application 297/297 per tasks.md task 8.11 (unaffected by the later calendar fix, which only touched Ways.IntegrationTests). Integration: one known deterministic failure at the 8.11 snapshot, since fixed by PR #154 and re-confirmed green by this verify. vitest: 902/902 per task 8.12 (see W1). Colocated descriptor tests confirmed present for remitos.ts/Remitos.tsx/Remito.tsx/FacturarRemitos.tsx (task 8's Work Unit Evidence). | PASS WITH NOTE (see W1) |
| 9 | doc-10 carries all four tables, both new columns, PRE/TXR notes, "Estado (Etapa 17)" closed in the last slice | docs/10-modelo-de-datos.md:477 and :547/:836 read "implementada -- etapa completa (PRs #1-#8)" / closed, no future-tense promises. Schema spot-checked against RemitosEtapa17.cs: remitos/items_remito/movimientos_stock.id_remito (composite FK) match doc-10's SQL sketch exactly. | PASS |

**Result: 9/9 binding criteria PASS** (criterion 8 carries one non-blocking documentation note, W1).

### Spec Compliance -- requirement-level walk (29 requirements / 80 scenarios across 6 files)

| File | Requirements | Evidence sample | Result |
|---|---|---|---|
| presupuestos/spec.md (11 req / 25 esc) | Schema at rest, borrador replace-set under FOR UPDATE, enviar assigns numero (series PRES) per PV, vencido derived in PV zone, para-venta read-only, conversion freezes snapshot, conversion terminal via partial unique, no stock reservation, anulacion 409 for convertido, authorization mirrors /api/ventas, RLS standard | `PresupuestosSchemaTests`, `ServicioDePresupuestosTests` (`DosEnviarConcurrentesDePresupuestosDistintosEnElMismoPuntoDeVentaDanNumerosDistintosSin409`, `EnviarEnLaZonaMenosTresElVencimientoDelDiaLocalEsAceptadoYElDiaAnteriorRechazado`), `ServicioDeVentasConversionTests` (conversion/freeze/terminal/409 family) | COMPLIANT |
| remitos/spec.md (8 req / 17 esc) | Schema at rest, borrador replace-set, emitir assigns numero (series REM) + FEFO + 4th write site, no turno required, anulacion reverses stock / rejects facturado, consolidation links N to 1 itemless TXR, authorization mirrors, RLS standard | `RemitosSchemaTests`, `ServicioDeRemitosTests` (`EmitirUnRemitoNoExigeNingunTurnoAbierto`, `DobleEmitirConcurrenteEsRechazado409ViaElGuardNoViaElPreCheck`), `ServicioDeFacturacionDeRemitosTests.DosRemitosConsolidanEnUnTxrItemlessConTotalIgualALaSumaDeLosHeadersYCeroMovimientosDeStock` | COMPLIANT |
| comprobantes-venta/spec.md (5 req / 11 esc) | Checkout refuses afecta_stock=false unconditionally, comprobante may carry id_presupuesto_origen, TXR consolidates N remitos with zero items/zero stock, annulling a TXR returns its remitos to emitido, RC unaffected | `ServicioDeVentasConversionTests` (PRE/TXR-net tests), `ServicioDeFacturacionDeRemitosTests.AnularUnTxrDevuelveSusRemitosAEmitidoLimpiaLaLigaduraYNoEscribeMovimientosDeStock` | COMPLIANT |
| stock/spec.md (3 req / 16 esc) | Schema at rest (id_remito), 4-write-site lock order, cantidad = sum of movimientos over 9 motivos | `RemitosSchemaTests` FK/index checks, `ServicioDeRemitosTests` lock-order pair, `InvarianteStockYStockLotesTests` | COMPLIANT |
| lotes-y-vencimientos/spec.md (1 req / 7 esc) | FEFO server-computed default, honored when explicit idLote supplied, parity between write site 1 (checkout) and write site 4 (remito) including the same-day boundary case | `ServicioDeRemitosTests.LaParidadFefoEligeElLoteQueVenceHoyEnElBordeExactoEnElRemitoYEnElCheckout` (added judgment-day slice-5 ronda 2, closes mutation target 47's UTC/PV-zone boundary gap flagged during apply) | COMPLIANT |
| auxiliary-catalogs/spec.md (1 req / 4 esc) | Fiscal catalogs remain platform-managed/read-only; PRE seeded inactive; TXR seeded with afecta_stock=false | `InicializadorDeBaseDeDatos.cs` (data statements), `UnaVentaConElTipoPreSembradoInactivoEsRechazada400SinEscribirNada` / `UnaVentaConElTipoTxrEsRechazada400SinEscribirNada` | COMPLIANT |

**Compliance summary**: 29/29 requirements, 80/80 scenarios compliant by the deviation registry in
tasks.md (every OD8/OD9/CONFLICT deviation is registered with its arbitration, not re-litigated
here) plus this verify's own targeted re-execution of the highest-risk subset (PRE nets, phantom
sale, index counts, zero-extra-statements).

### Task Completion (tasks.md)

- 210/210 task checkboxes `[x]`, zero `[ ]` remaining anywhere in the file.
- Every slice's judgment/PR checkbox (X.14/X.15, X.22/X.23, X.25/X.26 etc) is closed with a
  real PR number and merge commit, cross-checked against git log: PR #146 (5f03056, slice 1, clean
  round direct), #147 (f2561c1, slice 2, 1 fix), #148 (6bddbb7, slice 3, 2 judgment rounds), #150
  (7c8fdf5, slice 4, 1 round), #151 (322fb19, slice 5, 2 rounds), #152 (57fb624, slice 6, 2 rounds),
  #153 (4476b42, slice 7, 2 rounds), #155 (bbf3ed8, slice 8, 2 rounds) -- plus standalone #149
  (d084f2c) and #154 (37c0149), both registered in task 8.15's closing note.
- Deviation registry coherence spot-checked: OD8/T2 (remito double-annulment, tasks 5.9/5.11), OD8/T3
  (TXR-anulacion composition test, task 6.21), CONFLICT #3 (cross-PV conversion guard, task 3.18),
  CONFLICT #4 (unnamed domain codes adopted from design), CONFLICT #5 (slice 4's CHECK-branch count
  corrected from 3 to 5, reconciled against the gate contract) -- all present, internally consistent,
  and match the actual code (eg ck_remitos_salida_completa/ck_remitos_facturacion plus the three
  items_remito CHECKs are all named branches, matching the corrected count of 5).

### doc-10 Coherence

- "Estado (Etapa 17)" headers (docs/10-modelo-de-datos.md:418,477,547,836) all read closed/"implementada"
  with the PR list, no forward-looking language.
- Schema spot-check: remitos/items_remito/movimientos_stock.id_remito (composite FK
  fk_movimientos_stock_remito on (id_remito, id_tenant)) in doc-10's SQL sketch matches the actual
  RemitosEtapa17.cs DDL column-for-column.

### Issues Found

**CRITICAL**: None.

**WARNING**:
- **W1 -- vitest count discrepancy, non-blocking.** tasks.md task 8.12's recorded evidence is
  `npx vitest run` giving **902/902 green (55 files)**, the last full web-suite run in the artifact
  trail (stage close, before PR #154, which is backend-only per `git show --stat 37c0149` -- no web
  files touched). The orchestrator's brief states **906/906**. This verify was instructed not to
  re-run the full vitest suite, so the 906 figure could not be independently reproduced from the
  artifacts on hand. Recommend the orchestrator confirm the current vitest count before archive; if
  906 is correct it likely reflects test additions from a source outside this verify's visibility
  (not found in tasks.md). Does not block archive -- no code-level evidence of a regression, only an
  unreconciled count.
- **W2 -- tasks.md task 8.11's evidence entry is stale relative to main.** It documents
  **1580/1581**, ONE deterministic (non-flaky) failure --
  `ServicioDePresupuestosTests.UnPresupuestoConVencimientoPasadoSeReportaVencidoYElFiltroLoDiscrimina`
  -- explicitly "not fixed here: out of the assigned 8.1-8.13 scope." That defect WAS subsequently
  fixed by standalone PR #154 (df6d3fb/6f8872d, merged 37c0149, before PR #155/slice 8 merged),
  and task 8.15's closing note does reference PR #154 by number -- but task 8.11's own evidence cell
  was never updated to say "later fixed by #154," so a reader of that cell alone would believe an
  unfixed defect ships to main. This verify re-ran the exact test directly against main and confirms
  it now passes (1 archivo, 1 Superado). Cosmetic/documentation-freshness gap only; does not block
  archive.

**SUGGESTION** (consolidated backlog -- see below, none block archive).

### Backlog / Debt Consolidated From This Stage's Judgment-Day Rounds

None of the following are failures; they are explicitly registered debt in tasks.md for future
stages to pick up, carried forward here for the archive report:

1. **Target 54 -- in-transaction turno re-check has no race guard, in both
   ServicioDeFacturacionDeRemitos AND the pre-existing ServicioDeVentas.** Judge-confirmed
   pre-existing class, not a stage-17 regression (tasks.md:1860-1865). Backlog: add a FOR
   SHARE/FOR UPDATE race net for the turno re-check, in whichever future slice next touches
   turnos/caja.
2. **tipos.ts:961 declares pagos: PagoDeVenta[] required while Contratos.cs's
   SolicitudDeVenta has it nullable in C#** -- a pre-existing (not stage-17-introduced) optionality
   mismatch, benign direction (TS client stricter than the server) (tasks.md:2042-2045). Not fixed
   in this stage; registered as backlog.
3. **A TXR's annulment leaves it de-linked from its ex-remitos, so GET on it returns
   items: []** -- today unreachable from the web (Remito.tsx only queries with
   idComprobanteVenta != null), but a latent asymmetry against design.md's T11 (tasks.md:2217-2222).
   Registered as backlog off juez A's slice-8 ronda-2 verdict, not fixed.
4. **Mutation target 40/41 (ascending lock order, stock-before-stock_lotes sequence for the
   remito write site) was verified by code inspection, not an independent apply-time mutation run**,
   because a single-item-per-remito rendezvous fixture cannot discriminate a reordering the way a
   multi-resource AB/BA fixture would (tasks.md:1267). Registered as a coverage gap, not a defect --
   the structural tests (RemitirYCheckoutSobreElMismoArticuloYLoteNoDeadlockeanYAmbosCompletan et al)
   do exercise the real lock path; only the isolated single-mutation-run evidence is missing.
5. **The "mono-zona AddHours(-3)" fixture item named in this verify's brief could not be
   corroborated.** No occurrence of AddHours(-3) was found anywhere in tasks.md or the source tree
   (grep -rn "AddHours(-3)" on tests/ and src/ returns nothing). Flagging as unconfirmed rather
   than asserting it as real debt -- likely a cross-reference to a different stage or a slip in the
   brief.
6. **Mutation target 47's FEFO cross-write-site boundary gap was flagged during apply
   (tasks.md:1267, "flagged as a follow-up gap") but was subsequently CLOSED in judgment-day slice-5
   ronda 2** (LaParidadFefoEligeElLoteQueVenceHoyEnElBordeExactoEnElRemitoYEnElCheckout,
   tasks.md:1482-1491, mutation-proven with AddDays(1)). Reported here as **resolved, not
   outstanding debt** -- corrects a possible expectation that this gap remained open.

### Verdict

**PASS WITH WARNINGS**

---

## Post-verify remediation (orchestrator, main@64d8c52)

Both warnings were remediated in `tasks.md` before archive, same-day:

- **W1 RESOLVED**: 902/902 was the APPLY-time vitest count; the slice-8 judgment rounds added
  4 web tests (three stale-response nets, the exact-`idsRemito` assert rewrite, the
  key-absence assert). The post-merge stage-close run on consolidated `main@231db65` is
  **55 files, 906/906 green** (run by the orchestrator in-session). Back-filled into task 8.12.
- **W2 RESOLVED**: task 8.11's evidence cell back-filled with the final truth — the
  calendar-drift defect was fixed in standalone PR #154 before PR #155 merged, and the
  post-merge stage-close Integration run is **1583/1583 green** after the regla-17 isolated
  re-run (first pass 1580/1583, 3 Testcontainers flakes — 6th documented occurrence).

All 9 binding verify criteria pass with direct runtime/source evidence; all 29 spec requirements /
80 scenarios are implemented and tested (compliance backed by the deviation registry plus this
verify's own targeted re-execution of the highest-risk subset); all 210 tasks are complete with
coherent judgment-day/PR records cross-checked against git log; doc-10 is closed and schema-accurate.
Two non-blocking WARNINGs (an unreconciled vitest count, and a stale-but-superseded task-8.11 evidence
cell) and a consolidated backlog of five items (one of which -- target 47 -- is actually already
resolved) are carried forward for the archive report. Nothing here blocks sdd-archive.
