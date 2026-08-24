# Verify Report: stage-19-fiscal-arca (sub-etapa 19a)

**Change**: `stage-19-fiscal-arca` (scope: 19a) · **Mode**: openspec (file-based artifacts) ·
**HEAD verified**: `21c3294` (main) · **PRs verified against `git log`**: #159, #160, #161, #162, #163
(all five merge commits confirmed present on `main`, in order, matching `tasks.md`'s claims byte
for byte — `b5b3b35`, `3fd2d79`, `757acc4`, `3c30f21`, `7606aab`).

## Verdict: **PASS WITH WARNINGS**

0 CRITICAL · 3 WARNING (all non-blocking, documentation-only) · 0 SUGGESTION (2 items in the backlog below are informational, already closed, and carry no severity).
No CRITICAL issue was found against any binding verify criterion, any of the 8 delta specs' 32
requirements, or the 159-task ledger. All warnings are pre-existing textual drift, already
partially registered by the change's own Reconciliaciones, that a follow-up should finish
correcting. None of them require touching shipped code or re-opening the DB gate.

---

## 1. Binding verify criteria (design.md:583-610 + state.yaml gate) — evidence-cited

| # | Criterion | Verdict | Evidence |
|---|---|---|---|
| 1 | Exactly one migration `FiscalArcaEtapa19a`, last of the sub-stage, `has-pending-model-changes` clean | PASS | `src/Ways.Infrastructure/Persistencia/Migraciones/20260822002214_FiscalArcaEtapa19a.cs` is the only fiscal migration and the highest-timestamped file in the directory (`20260820004658_RemitosEtapa17.cs` is the only earlier one; nothing follows it). tasks.md:315 records the CLI confirmation, re-verified at slices 2/3/4 |
| 2 | Zero ALTER TYPE ADD VALUE anywhere in the migration | PASS | grep for "ALTER TYPE" against the migration file returns only a comment line (:19); no executable ALTER TYPE statement exists |
| 3 | New index count = 8, by definition | PASS | 8 CreateIndex calls in the migration (lines 101, 121, 126, 170, 226, 231, 238, 243, 281, 286 span 8 distinct CreateIndex blocks), matching design.md's numbered table |
| 4 | New CHECK count = 8, each mutation-tested | PASS | 3 AddCheckConstraint calls (lines 116, 158, 165) plus 5 inline CheckConstraint entries in CreateTable blocks (209, 210, 211, 265, 266) equal 8. All 8 exact names present in ManejadorDeErrores.ClasificarCheckDeFiscal, each with its own 23514 mutation test |
| 5 | RLS ENABLE+FORCEd on both new tables, cross-tenant read+write pair | PASS | HabilitarRlsDeTenant on certificados_fiscales and numeraciones_fiscales are the last two statements of Up() (lines 333-334), matching design's stated order |
| 6 | Non-fiscal sale byte-identical to main; POST /api/ventas fiscal code still 400 | PASS | ServicioDeVentas.cs never appears in any slice diff; ContadorDeComandos equality target 74; live 400 test target 73 |
| 7 | Zero PEM/PFX/private-key material under src/ or tests/ | PASS | SinMaterialDeClaveTests.cs exists and is exercised with a live-planted-marker mutation cycle in slice 1 and slice 2's ronda-2 fix verification |
| 8 | No real ARCA hostname as a configuration default | PASS | Repository-wide scan of src/ for wswhomo/servicios1.afip returns zero matches |
| 9 | Down() is a true inverse; Up-Down-Up clean; codigo_afip reverted with no other column moved | PASS | Down() contains the 3 doubly-guarded UPDATE reverts, DropTable x2, and 3 DropCheckConstraint calls for the 3 ALTER-added CHECKs; inline CreateTable CHECKs drop automatically with DropTable |
| 10 | SobreSoap.cs is the only file under src/ naming SOAP or a SOAP namespace | PASS | Scan for soapenv/schemas.xmlsoap.org/ServiceModel across src/ returns exactly one file: SobreSoap.cs |
| 11 | Politicas.cs gains exactly 1 public const (11-to-12); ManejadorDeErrores.cs gains exactly 10 branches | PASS | grep -c "public const string" Politicas.cs equals 12; AdministracionFiscal present. ManejadorDeErrores.cs: 2 exact-name 23505 arms plus 8 exact-name 23514 arms equals 10 |
| 12 | D1 lock proof, both halves (pg_locks shows numeraciones_fiscales held, turnos_caja/stock/stock_lotes/clientes absent) | PASS | AsignadorDeNumeroFiscalTests.cs contains a live pg_locks poll from a second connection (target 55) |
| 13 | Mutation evidence recorded per-slice; Domain/Application/Integration green | PASS (see section 5 for a suite-count caveat) | All 76 mutation targets are logged with mutation-RED-revert-green cycles or, for structural rows, the equivalent |

**Additional gate-level facts (state.yaml, propose phase) re-confirmed**: `codigo_afip smallint NULL`
pre-existing on the 3 catalogues (migration uses `WHERE codigo_afip IS NULL` guards, not `ALTER`);
`FA/FB/FC` seeded active pre-existing (referenced defensively in `ServicioDeFacturacionFiscal.cs`
gate comments); the `EsFiscal` guard at `ServicioDeVentas.cs:1162` untouched (task 5.9).

### The six 409 gates vs. the design's six named gates

Verified directly in `src/Ways.Application/Fiscal/ServicioDeFacturacionFiscal.cs`: six named,
sequential, pre-transaction 409s exist in the code — `empresa_sin_condicion_fiscal` (Gate 1, :69-74),
`punto_venta_sin_numero_fiscal` (Gate 2, :76-81), `tipo_fiscal_invalido` (Gate 3, :83-85, via
`ResolverTipoFiscal`), `condicion_fiscal_receptor_no_mapeada` (Gate 4, :97-119, the `NO_RESP` check
by `Codigo`, never `CodigoAfip`), `certificado_fiscal_ausente` (Gate 5, :121-129), and
`tipo_fiscal_letra_no_coincide` (Gate D10, :140-155, added in judgment-day Slice 5 ronda 1 — the
letter cross-check). This matches design.md's data-flow diagram exactly as amended by
Reconciliacion 8 ("SEIS GATES (D10)", design.md:327-333). All six issue zero network calls before
the transaction opens (I4), consistent with target 64's assertion.

---

## 2. Requirements walkthrough — 8 delta specs

All 8 spec deltas were read in full (specs/fiscal-arca, comprobante-fiscal, certificados-fiscales,
numeracion-fiscal, comprobantes-venta, auxiliary-catalogs, operacion-de-pos, tenant-organization).
Every requirement maps to a shipped mutation target and a passing test, per the per-slice evidence
tables cited in section 1. No requirement lacks a covering test. Two items are flagged below as
WARNING because they are spec-text drift, not implementation gaps — the runtime behavior in both
cases exceeds what the spec text literally states, never falls short of it.

| Capability | Requirements | Status |
|---|---|---|
| fiscal-arca | 7/7 covered (BCL-only signing, no key material, TA cache+IRelojDelSistema, SOAP isolation, WSFE operation surface, fixture suite pinned, error taxonomy, no real hostname) | PASS |
| comprobante-fiscal | 8/8 covered (3-state machine, I1-I4, totals composition, QR, NO_RESP rejection) | PASS, see WARNING-1 below (gate count text) |
| certificados-fiscales | 4/4 covered (AdministracionFiscal gate, AES-GCM+AAD, one-active-per-empresa+ambiente, DTO exposure clause) | PASS. Reconciliacion 7's amendment (versioned ClaveMaestraActual/ClavesMaestras shape) confirmed present in the spec text as shipped |
| numeracion-fiscal | 5/5 covered (inside-transaction assignment, serialization, injective PV map, reconciliation) | PASS |
| comprobantes-venta (delta) | 5/5 covered (additive nullable columns+CHECK, separate write site, byte-identical checkout, letter resolver's first caller) | PASS |
| auxiliary-catalogs (delta) | 2/2 covered (double-net codigo_afip, Exento/No gravado stay NULL) | PASS |
| operacion-de-pos (delta) | 2/2 covered (AdministracionFiscal new policy, fiscal emission stays OperacionDePos) | WARNING-2, see below |
| tenant-organization (delta) | 2/2 covered (empresas.id_condicion_fiscal no-default, puntos_venta.numero_fiscal unique) | PASS |

WARNING-1 / WARNING-2 — residual "four gates" text in two spec files, not corrected by
Reconciliacion 8. design.md's own data-flow diagram was corrected in place by Reconciliacion 8
("CUATRO GATES" to "SEIS GATES", tasks.md:79-87), but that reconciliation's scope was design.md
only. Two spec requirement titles/bodies still say "four": specs/comprobante-fiscal/spec.md:64
("The Fiscal Emission Use Case Has Four Independent Named Gates, Including Invariant I4") and
specs/operacion-de-pos/spec.md:23 ("A Vendedor can attempt a fiscal emission (subject to the four
gates)"). The shipped code implements six gates (section 1 above), a strict superset of the four
the spec text names — no scenario in either spec is violated, so this is not a CRITICAL spec-vs-code
gap, but the requirement titles are stale and should be corrected to "six" in a follow-up. The same
root cause is also present, unaddressed, in proposal.md:724,749,835 and design.md:377,537,563 (the
design's own file-changes/mutation-target/slicing tables never updated "four gates" to "six" outside
the one data-flow diagram line Reconciliacion 8 touched) — this is exactly the residual the
requester's brief asked to have logged, and it is carried forward into section 6 below.

Spec amendment count — precision note. The brief asked whether the specs were amended twice.
Verified: exactly one of the 8 spec files was textually amended with a registered reconciliation —
certificados-fiscales/spec.md (Reconciliacion 7, the versioned master-key config shape).
Reconciliacion 8 amended design.md (not a spec file) for the gate-count drift. So there is one spec
amendment and one design amendment, both registered; not two spec amendments.

---

## 3. Task completion — tasks.md (159 tasks, 5 slices)

- All checkboxes: zero unchecked top-level "- [ ]" tasks found across the full 1442-line file. The
  nested "[ ] Open PR #N" / "[ ] judgment-day round" sub-markers inside already-[x]-marked lines
  (1.51/1.52, 2.26/2.27, 3.23/3.24, 4.24/4.25, 5.30/5.31) are the project's documented honesty
  convention (regla 18, tasks.md:496-501): a fix agent cannot self-declare a judgment round or PR
  merge closed — only the orchestrator can, and each one carries an explicit "DONE by the
  orchestrator" line with the real PR number and merge SHA. All five were independently confirmed
  against git log in this verify.
- PR mapping (tasks.md claim vs. git log reality, exact match):
  - Slice 1: feat/stage19a-slice1-schema-fiscal -> PR #159, merge b5b3b35 -> confirmed
  - Slice 2: feat/stage19a-slice2-wsaa -> PR #160, merge 3fd2d79 -> confirmed
  - Slice 3: feat/stage19a-slice3-wsfe-y-cae -> PR #161, merge 757acc4 -> confirmed
  - Slice 4: feat/stage19a-slice4-numeracion-y-certificados -> PR #162, merge 3c30f21 -> confirmed
  - Slice 5: feat/stage19a-slice5-emision-y-qr (real branch name feat/stage19a-slice5-emision,
    Deviation 7, cosmetic) -> PR #163, merge 7606aab -> confirmed
- Judgment rounds: all 5 slices show a clean round recorded by the orchestrator, with real fix
  commits cited (ef5871c/49b6d05 slice 2; 30de47c/4bdcfd3 slice 3; 48a4ed8/fb70eec/2dddb53 slice 4;
  ab185d5/5bea411 slice 5). Findings ranged CRITICAL to SUGGESTION and every CRITICAL/MAJOR is
  traced to a concrete before/after mutation cycle (RED then revert then GREEN), not narrative
  assertion.
- 76 mutation targets: all 76 accounted for across the 5 slices' evidence tables (23+13+15+12+13),
  matching Reconciliacion 2's arithmetic.
- ~10 deviations: 9 inline "DEVIATION (registered)" tags plus 2 slice-closing "Deviations
  registered" lists (4 items slice 1, 7 items slice 5 — one struck through and corrected in place
  after judgment). All are individually justified against a cited design/proposal line, none silent.

No CRITICAL found. Task-completion dimension: PASS.

---

## 4. Docs 09/10/11

- doc 09 (docs/09-multi-tenancy.md:78): "Estado (Etapa 19a - CERRADA, ...)" — present, honest,
  cites the scope closed.
- doc 10 (docs/10-modelo-de-datos.md:95,451,511): three "Etapa 19a - CERRADA" status blocks
  (catalogos, comprobantes_venta, new tables), each naming what shipped.
- doc 11 (docs/11-programa-post-paridad.md:426-459): the fullest status block. Explicitly states
  19a is implemented, explicitly lists what 19a "deliberately does NOT do" (D12/T1 gap named, no
  real ARCA bytes, zero UI, zero CAEA), names 19b's exact blocking reason (WSASS/Clave Fiscal, "No
  se pide, se documenta"), names 19c's scope, and closes with "Esta nota no declara la Etapa 19
  completa - solo su primera sub-etapa" — matching the binding honesty requirement verbatim.
- Schema spot-check: doc 10 (empresas.id_condicion_fiscal integer NULL REFERENCES
  condiciones_fiscales, :62) and puntos_venta.numero_fiscal integer NULL (:72) match the migration's
  AddColumn statements exactly (types, nullability, FK target). certificados_fiscales and
  numeraciones_fiscales table blocks in doc 10 match the migration's CreateTable column lists (18
  and 6 columns respectively) on inspection.

Docs dimension: PASS.

---

## 5. Suites — verified against tasks.md's own records (NOT re-run, per skill instruction)

| Suite | tasks.md's own last-stated total | Where cited |
|---|---|---|
| Ways.Domain.Tests | 545/545 | tasks.md:1383 (ronda-2 hygiene note, slice 5 close) |
| Ways.Application.Tests | 373/373 | tasks.md:1382 (370 pre-ronda-2 + 3 new InvalidarAsync tests) |
| Ways.IntegrationTests | 1715/1715 explicitly stated (tasks.md:1401, task 5.25, pre-ronda-2-fixes) | — |

WARNING-3 (non-blocking, traceability only). The orchestrator's stated final Integration total of
1725/1725 is not written verbatim anywhere in tasks.md. The last explicit "Ways.IntegrationTests:
X/X" line in the file is 1715/1715 (task 5.25, before judgment-day ronda 2's fixes). Ronda 2 added
tests to ServicioDeFacturacionFiscalTests (an Ways.IntegrationTests class: went from 9 to 14 in
ronda 1, then 14 to 19 in ronda 2 — net +10 integration tests across both rounds atop 5.25's 1715
baseline), which arithmetically reconciles to 1715+10 = 1725, matching the orchestrator's number —
but this reconciliation is an inference from delta counts scattered across the ronda-1/ronda-2
hygiene notes, not a single restated total. Recommend the archive phase (or a one-line tasks.md
edit) add the explicit final "Ways.IntegrationTests: 1725/1725" line so a future reader does not
have to reconstruct it. This does not affect the verdict — the arithmetic is internally consistent
and no test regression is implied.

Targeted checks run in this verify (not full-suite re-runs, per the skill's guidance to only run
tests a binding criterion requires): the has-pending-model-changes-equivalent state was confirmed
statically (single migration file, no dangling model diff markers); the key-material scan was
confirmed statically (no PEM/PFX/PRIVATE KEY markers under src/); the indexdef/CHECK-name-by-
definition counts were confirmed statically against the migration source (section 1). These static
confirmations corroborate, but do not replace, the Testcontainer-backed runtime evidence already
recorded in tasks.md's per-slice tables (RLS cross-tenant pairs, pg_locks poll, mutation RED/GREEN
cycles) — that runtime evidence is the actual proof for criteria 5/12/13 in section 1.

---

## 6. Backlog — explicit deferred debt

### For 19b (blocked, WSASS pending owner — documented, never requested)

1. int.Parse on the WSAA fault code (ClienteWsaa.cs:71, MapearFalla) assumes the numeric fault
   codes 500/501/502/600/601/602 the proposal names; if the real wire emits a symbolic fault
   (ns1:cms.sign.invalid etc.) this throws FormatException instead of mapping to a domain error.
   19b must confirm against the real wire and decide whether a defensive TryParse plus
   wsaa_error_no_mapeado fallback is needed (T3, tasks.md:356-365).
2. Confirm NO_RESP maps to CodigoAfip 15 against FEParamGetCondicionIvaReceptor — currently a
   provisional seed (RG 5616 "IVA No Alcanzado") never consumed by any runtime decision (the
   rejection gate checks Codigo, never CodigoAfip), but the seeded value itself is unconfirmed
   (decision 11, tasks.md:322, Slice 1's binding note to Slice 5).
3. WSFE fixtures have lower pedigree than WSAA's — the WSAA fixtures were checked directly against
   the WSAA spec PDF; the WSFE fixtures were reconciled against explore.md's in-repo transcription
   of the manual (which itself caught a real ImpIVA/ImpTrib order defect in judgment-day Slice 3
   ronda 2), not against manual-desarrollador-ARCA-COMPG-v4-0.pdf directly. 19b's first task
   (fixture-vs-reality diff, T4) must treat the WSFE set with lower confidence
   (tasks.md:626-630,712-717).
4. Confirm the WSAA fault taxonomy's exact wire strings (T3) — the proposal's numbering is
   unverified against the spec's symbolic fault codes.
5. tickets_acceso_fiscal table — registered 19b gate item (decision 10): persisting the Access
   Ticket is deferred because no real TA is obtainable until 19b; the in-memory cache plus
   single-flight port is the interim shape.
6. Real fixture-vs-manual diff as 19b's literal first task (T4, both WSAA and WSFE fixture sets).

### For 19c (future, independent of 19b except for a printed real CAE)

1. T1 (BINDING) — the fiscal write plan is comprobante + items ONLY (D12); 19c must add
   movimientos_stock/pagos_comprobante/movimientos_cuenta_corriente/turno-guard writes together
   with the screen that supplies them, and target 75's zero-rows test must go RED as the trip-wire
   proving the gap was closed on purpose (FA/FB/FC carry afecta_stock = true in the catalogue
   today — a named, safe-only-because-of-I4 inconsistency).
2. T2 — I1's operator-release path (releasing a bound-but-unresolved fiscal number by explicit
   operator action) is not shipped; 19a ships only the enforceable half. Registered for 19c
   alongside the durable offline queue.
3. The display-only letter drift on retry — ReintentarAsync recomputes
   ResolvedorDeLetraComprobante.Resolver on every attempt; if the emisor/receptor's condicion
   fiscal changed between the original emission and a retry, the recomputed letter can diverge
   from the letter the comprobante was originally emitted with. Today this is purely
   display/informational (never validated against a persisted catalogue value on retry) —
   registered as a 19c UI-facing note (tasks.md:1372-1377).
4. UI, fiscal printing with QR, certificate/PV/condicion-fiscal configuration screens — 19a ships
   only the API/payload; nobody can press a button yet.
5. Operational contingency: the durable offline queue and CAEA as last resort, with their own enum
   values and writers (June-2026 rule: CAEA is contingency-only, capped 5%/month).
6. The fiscal consolidation type for remitos (ServicioDeFacturacionDeRemitos parameterization, TXR
   stays valid history) — ships together with its writer, per the stage-17 rule.
7. Libro IVA ventas/compras.
8. Residual "four gates" text — proposal.md:724,749,835, design.md:377,537,563, and both spec
   files named in section 2's WARNING-1/2 (specs/comprobante-fiscal/spec.md:64,
   specs/operacion-de-pos/spec.md:23) still say "four" where the shipped gate count is six.
   Reconciliacion 8 only corrected the one data-flow-diagram line judgment-day flagged; the rest
   is cosmetic historical-table drift the requester specifically asked to have logged here.
   Recommend a small documentation-only pass (no code change) before or alongside 19c's spec work.
9. DI port tension noted-but-resolved during 19a (informational, already closed): slice 2's
   registered tension (RepositorioEnMemoriaDeTicketDeAcceso registered both as its own concrete
   singleton and via the port) was resolved in slice 5 — ObtenerOFirmarAsync was elevated to
   IRepositorioDeTicketDeAcceso, and the concrete-type registration was retired. No action needed;
   recorded here only because the brief asked for the full backlog trail.

### Traceability suggestion (not blocking archive)

- Add an explicit final "Ways.IntegrationTests: 1725/1725" restatement to tasks.md (section 5,
  WARNING-3).

---

## 7. Summary table

| Dimension | Verdict |
|---|---|
| Binding verify criteria (13 + gate's 8) | PASS — all evidence-cited against real migration/code |
| Six named 409 gates vs. design | PASS — code matches design's amended "SEIS GATES" |
| 8 delta specs / 32 requirements | PASS, 2 WARNING (stale "four gates" text in 2 spec files) |
| 159 tasks / 5 slices / 76 mutation targets | PASS — all closed, all PRs confirmed against git log |
| Docs 09/10/11 | PASS — honest, scoped, Etapa 19 never declared complete |
| Suites | PASS, 1 WARNING (final Integration total not verbatim-restated, but arithmetically reconciles) |
| Backlog for 19b/19c | Logged above, all pre-existing and named by the change's own artifacts |

Recommendation: proceed to sdd-archive. No CRITICAL blocks archival. The three WARNINGs are
documentation-only and do not require unwinding any shipped code, re-opening the DB gate, or
touching ServicioDeVentas.cs/RLS/the migration.
