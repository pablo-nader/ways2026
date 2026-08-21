# Archive Report: Stage 17 — Presupuestos y remitos

**Change**: `stage-17-presupuestos-y-remitos`
**Archived**: 2026-08-21
**Final state**: `main` @ `231db65` (8 slice PRs #146-#155 merged, plus 2 standalone fixes #149/#154), post-verify remediation on `main` @ `64d8c52`
**Verdict carried from `sdd-verify`**: PASS WITH WARNINGS (0 CRITICAL, 0 blockers) — both warnings (W1, W2) remediated same-day, before this archive, per the Final-State Authority rule below.

---

## 1. Executive Summary

Stage 17 ships the **sale side of the document circuit** doc-11:307-324 asked for: a **presupuesto**
(quote) that commits neither stock nor cash, carries a `date` expiry resolved in the punto de venta's
own timezone, and is convertible into a sale that **replays its own frozen price snapshot** instead of
re-resolving today's prices; and a **remito** (delivery note) that moves stock at delivery time — as the
system's **fourth formal stock write site**, honoring the existing "identical lock order at every write
site" guarantee — and is later **consolidated** by an itemless, non-fiscal `TXR` comprobante that links
N remitos to one invoice and writes **zero** additional stock movements.

The stage is purely additive to the existing sale engine: `ServicioDeVentas.EjecutarTransaccionAsync`'s
pinned statement order is untouched, and an ordinary counter sale still emits zero extra statements.
Along the way, the stage closed a **latent phantom-sale defect already living in `main`**: the seeded
`PRE` `tipos_comprobante` row was active and `afecta_stock = false` but nothing in the write path ever
read `afecta_stock`, so a `POST /api/ventas` with `codigoTipoComprobante = "PRE"` would have decremented
stock and consumed cuenta corriente exactly like an ordinary sale. Two independent nets close it: the
catalog row is deactivated (data statement + seed change, so a fresh install never reopens the hole),
and the checkout resolver now refuses **any** `afecta_stock = false` type unconditionally, proven by
mutation tests that show either net alone still catches the phantom sale.

Delivered: 4 new tables (`presupuestos`/`items_presupuesto`, `remitos`/`items_remito`), 2 new enums
(`estado_presupuesto`, `estado_remito`), 1 irreversible `ALTER TYPE motivo_stock ADD VALUE 'remito'`
(ships with its writer in the same stage), 2 additive ALTERs (`comprobantes_venta.id_presupuesto_origen`,
`movimientos_stock.id_remito`), 30 new indexes, 2 new capability specs (`presupuestos`, `remitos`), and
5 delta specs (`comprobantes-venta`, `stock`, `lotes-y-vencimientos`, `auxiliary-catalogs`). All 210
`tasks.md` checkboxes are complete; all 9 binding verify criteria pass; the change is closed with no
open CRITICAL or blocking issue.

---

## 2. Pull Requests

10 PRs total: 8 slice PRs (#146-#155, skipping the two standalone numbers) + 2 standalone fixes.

| PR | Branch | Content | Rounds to clean |
|---|---|---|---|
| **#146** | `feat/stage17-slice1-schema-presupuestos` | Schema: `presupuestos`/`items_presupuesto`, migration `PresupuestosEtapa17`, both `PRE` nets' schema half (data statement + seed), RLS/CHECK/index tests | Clean, direct |
| **#147** | `feat/stage17-slice2-presupuestos-abm` | Full `borrador→enviado→anulado` ABM, own numbering series `'PRES'`, list/detail | 1 fix |
| **#148** | `feat/stage17-slice3-conversion` | The resolver's `afecta_stock` guard (net 2 of PRE), `GET /para-venta`, conversion with frozen-price snapshot, `convertido` terminal state | 2 rounds (1 CRITICAL + 3 MAJOR ronda 1; 1 MAJOR + 1 WARNING ronda 2) |
| **#149** (standalone) | `fix/reposicion-hoy-zona-del-pv` | Pins a fixed clock at the UTC-day boundary for the reposición "hoy" shape test — a calendar-drift fix unrelated to slice content, delivered out-of-band | Standalone, own clean judgment |
| **#150** | `feat/stage17-slice4-schema-remitos` | Schema: `remitos`/`items_remito`, migration `RemitosEtapa17` (isolated `ALTER TYPE ADD VALUE 'remito'`), the `TXR` catalog row, RLS/CHECK/index tests | 1 round |
| **#151** | `feat/stage17-slice5-remito-write-site` | `ServicioDeRemitos.EmitirAsync` — the fourth stock write site, FEFO resolution, its own independently-implemented lock order and rendezvous concurrency test | 2 rounds (4 MAJOR ronda 1; 3 WARNINGs ronda 2) |
| **#152** | `feat/stage17-slice6-consolidacion` | `POST /api/remitos/facturacion` — itemless `TXR` consolidation, zero stock writes, TXR-annulment un-link path | 2 rounds (2 MAJOR + 1 SUGGESTION ronda 1; 1 MAJOR ronda 2) |
| **#153** | `feat/stage17-slice7-web-presupuestos` | Web: presupuesto screens, POS conversion entry point/banner | 2 rounds (1 MAJOR + 2 WARNINGs ronda 1; 1 WARNING ronda 2) |
| **#154** (standalone) | `fix/seed-de-precios-inmune-al-calendario` | Fixes the deterministic calendar-drift failure in `ServicioDePresupuestosTests` (a real wall-clock seed racing a hardcoded fixed test clock) found during slice 8's stage-close full-suite run; merged before #155 | Standalone, own clean dual judgment |
| **#155** | `feat/stage17-slice8-web-remitos` | Web: remito screens, consolidation UI, `doc-10`'s "Estado (Etapa 17)" header closed; OD10's items_remito read-model join | 2 rounds (3 MAJOR + 1 MINOR ronda 1; 1 WARNING + 2 SUGGESTIONs ronda 2) |

---

## 3. Orchestrator Decisions (10 total, running numbering across the whole change)

1. **Presupuesto = its own table** (`presupuestos`/`items_presupuesto`), mirroring stage 16's OC shape — keeps the checkout byte-identical by construction and gives the `PRE` phantom-sale finding no writer to reach.
2. **The `PRE` finding is closed in this stage with two independent nets** — catalog deactivation (data statement + seed) and an unconditional `afecta_stock` guard in the resolver — each proven by its own mutation test.
3. **`estado_presupuesto` has four values; `vencido` is a derived function, never stored** — no scheduler exists in the repo, so expiry is computed from `vencimiento date` compared against "hoy" in the punto de venta's own timezone.
4. **The presupuesto's own snapshot replaces the pricing engine as the price authority at conversion** — the quoted price is frozen and replayed, never re-resolved; the converted lines are immutable.
5. **A presupuesto reserves no stock** — a quote is a price commitment, not a hold; deferred with an explicit reopen condition.
6. **Remito = its own table, `ServicioDeRemitos` as the fourth formal stock write site** — the `stock` capability's "identical lock order at every write site" guarantee is extended to four, implemented independently with its own concurrency test; the new `motivo_stock` value `'remito'` is accepted as the stage's one irreversible artifact because it ships with its writer.
7. **Consolidated invoicing ships now, non-fiscal, as an itemless `TXR` comprobante that writes zero stock** — itemless-by-construction (the `RC` precedent) makes the double-decrement and phantom-restock traps unreachable, not merely avoided; the fiscal type is deferred to Etapa 19.
8. **(Spec-phase tensions, `state.yaml`)** Four tensions arbitrated in favor of `design`: the remito double-annulment scenario (missing from spec, added via tasks), the TXR-annulment composition discriminant test (required, not "plausible by construction"), a `punto_venta` cross-check on conversion (named by design, absent from spec prose, resolved in favor of design), and several unnamed domain error codes adopted verbatim from design.
9. **(Design-phase tensions, `state.yaml`)** All twelve design tensions (T1-T12) ratified in favor of `design` — including one `id_lista_precio` per quote as a service invariant, the `vencido` filter's mandatory `idPuntoVenta`, and the conversion race loser burning a `TX` series number (accepted with registry, not fiscal).
10. **(Apply-phase gap, judgment-day slice 6, juez A, WARNING de contrato — full story below.)**

### OD10 in detail

The `comprobantes-venta` delta spec and `design.md`'s tension T11 both required that a `TXR`
consolidation comprobante's **printed detail** be sourced by joining `items_remito` — the comprobante
itself is itemless by construction, so its read model has to reach into its linked remitos' own frozen
lines for anything to be printable. No task in `tasks.md`'s original slices implemented this: a
requirement existed with no task behind it, a pure planning gap. It surfaced during **slice 6's**
judgment-day round, flagged by juez A as a WARNING against contract fidelity, not as a functional
defect (slice 6 shipped the consolidation write path; the read-model join was simply never scheduled).
Because slice 6's two-round fix budget was already exhausted by that point, the orchestrator resolved
it by registering a **new task, 8.10b, in slice 8** rather than reopening slice 6 — alongside two
SUGGESTIONs from the same verdict (naming `N = 1` as a deliberate edge case, and testing TXR annulment
under a closed turno independently). Slice 8 implemented it as `ServicioDeVentas.ObtenerAsync`'s new
`TXR` branch, a private `ObtenerItemsDeTxrAsync` helper, and a `Proyectar` overload — confirmed by
`git diff --stat` to leave `EjecutarTransaccionAsync`/`EjecutarAnulacionAsync` untouched (state.yaml's
own pre-authorization named this "the ONLY backend exception of the slice"), and proven by a mutation
cycle: the join clause `if (tipo.Codigo == "TXR")` mutated to a never-true literal produced a RED
result (`Expected: 5, Actual: 0`) against
`ElDetalleDeUnTxrQueLigaDosRemitosMuestraLasCincoLineasCombinadasSourceadasDeItemsRemito`, then
reverted to green.

---

## 4. Judgment-Day Log by Slice

| Slice / PR | Judge(s) / rounds | Confirmed findings | Resolution |
|---|---|---|---|
| 1 / #146 | Clean round, direct | None | — |
| 2 / #147 | 1 round | 1 fix (unspecified minor scope, see `tasks.md:588` deviation note on `SuperficieDeAutorizacionTests` allowlist) | Fixed, clean re-round |
| 3 / #148 | 2 rounds — ronda 1 juez B (1 CRITICAL + 3 MAJOR + 1 WARNING); ronda 2 juez A (1 MAJOR + 1 WARNING) | Ronda 1: the guarded `UPDATE`'s three `WHERE` clauses (`estado`, `vencimiento`, `id_punto_venta`) were eclipsed by the service's own pre-check and had no discriminating coverage (targets 31-33, CRITICAL/MAJOR); a doc-comment overclaimed that statement *position* (not transaction atomicity + the DB's partial unique index) was what prevented a double-conversion race (target 35); the "16 queries" counter was documented as proving "zero extra statements," but it is blind to raw-ADO calls (target 34). Ronda 2: `EmitirAsync` resolved `cliente` unconditionally before the presupuesto-snapshot branch. | All fixed: direct-connection tests isolate each `WHERE` clause from the pre-check, a TOCTOU race test proves the DB guard is the real net, doc-comments corrected to their true mechanism, `cliente` resolution moved behind the snapshot branch |
| 4 / #150 | 1 round, juez B | 1 WARNING + 1 SUGGESTION | Fixed |
| 5 / #151 | 2 rounds — ronda 1 juez B (4 MAJOR); ronda 2 juez A (APPROVE, 3 WARNINGs) | Ronda 1: the same pre-check-eclipses-guard class recurred for `EmitirHeaderAsync`'s guarded `UPDATE`; mutation targets 40/41 (ascending lock order, remito write site) had no discriminating coverage — single-resource rendezvous fixtures cannot observe a reordering. Ronda 2: FEFO honoring an explicit `idLote`, anulación of a `borrador` remito writing nothing, and route-prefix collisions in the regression guard were test-only gaps. | All fixed. This recurrence (2nd occurrence of the pre-check-eclipse class, plus two genuinely new classes) is what grew `mutation-proof-tests` to **v1.1** (rule 3 reinforced, rules 13/14 added) |
| 6 / #152 | 2 rounds — ronda 1 juez B (2 MAJOR + 1 SUGGESTION); ronda 2 juez B (1 MAJOR) | Ronda 1: coverage gaps in the consolidation path; the turno re-check's race-guard absence was confirmed pre-existing (not a stage-17 regression) and registered as backlog (target 54). Ronda 2: the `pg_locks` probe used to observe real lock ordering needed a bound/retry — the first attempt's lightweight probe under-waited. | Fixed |
| 7 / #153 | 2 rounds — ronda 1 juez B (1 MAJOR + 2 WARNINGs); ronda 2 juez A (1 WARNING + 1 pre-existing SUGGESTION) | Ronda 1: `Pos()`'s `key={idPresupuesto ?? 'libre'}` remount branch had no coverage; two test doc-comments overclaimed coverage of a token guard that **React 19** made unobservable (`setState` post-unmount became a silent no-op, no longer emitting the `console.error` React ≤18 used to raise — see Lessons). | Remount test added; doc-comments corrected to state honestly what each test actually proves, confirmed by real (not reasoned) mutation runs |
| 8 / #155 | 2 rounds — ronda 1 juez B (3 MAJOR + 1 MINOR, all test-only); ronda 2 juez A (APPROVE, 1 WARNING + 2 SUGGESTIONs) | Ronda 1: the OD10 join lacked per-comprobante discrimination (2nd occurrence of the same class found in slice 6); the consolidation POST's `idsRemito` payload wasn't proven to carry the exact selected subset rather than the full list; the stale-response guard pattern from `Pos.test.tsx`/`SelectorDeLote` had never been replicated across `Remitos.tsx`/`Remito.tsx`/`FacturarRemitos.tsx`; one assertion didn't discriminate an explicit `null`. Ronda 2: 1 WARNING + 1 SUGGESTION fixed, 1 SUGGESTION deferred to backlog (TXR de-link asymmetry, see §6). | All test-only findings fixed with real mutation cycles (mutate → RED → revert → green) |

Standalone PRs #149 and #154 each ran their own independent clean dual-judgment round outside the slice sequence (see §2).

---

## 5. Final Test Suites

Per the **Final-State Authority** hierarchy, these are the numbers confirmed by the orchestrator's
post-verify remediation on consolidated `main` — not the intermediate apply-time/verify-time snapshots,
which are superseded below.

| Suite | Final count | Note |
|---|---|---|
| Domain | **540/540** | Unaffected by the later calendar fix (backend-only, `Ways.IntegrationTests` scope) |
| Application | **297/297** | Same |
| Integration | **1583/1583** | First pass on consolidated `main@231db65` was 1580/1583 (3 failures); regla-17 isolated re-run gave 1583/1583 green — the **6th documented Testcontainers-flakiness occurrence** in the programme, trx `stage17-close-rerun.trx` |
| vitest (web) | **906/906** (55 files) | `tasks.md`'s apply-time snapshot recorded 902/902; the slice-8 judgment rounds added 4 tests (three stale-response nets + the exact-`idsRemito` assert + the key-absence rewrite) between apply and merge — the 906 figure is the post-merge run on `main@231db65`, back-filled by the orchestrator (verify's W1) |

**Regla 17 note**: the Integration suite's first post-merge pass showed 3 failures in a small batch,
consistent with the programme's known Testcontainers flakiness pattern (non-reproducible, infra correct
by design — never run concurrent suites against the same Docker daemon). The isolated re-run protocol
was followed and produced a clean 1583/1583, confirming the failures were transient infra noise, not a
regression.

Superseded intermediate numbers (recorded here only for traceability, not as current fact): `tasks.md`
task 8.11's own evidence cell recorded Integration 1580/1581 with one **deterministic** (non-flaky)
failure, `ServicioDePresupuestosTests.UnPresupuestoConVencimientoPasadoSeReportaVencidoYElFiltroLoDiscrimina`,
confirmed pre-existing and unrelated to this stage's diff at apply time; it was fixed by standalone PR
#154 before PR #155 merged (see §6, "the calendar bomb").

---

## 6. Lessons

1. **`mutation-proof-tests` grew to v1.1 during slice 5** (rule 3 reinforced, rules 13/14 added), driven
   by a recurring class of false-negative coverage: a service's own pre-check (e.g. `EmitirAsync`'s
   pre-check before `EmitirHeaderAsync`'s guarded `UPDATE`) sequentially eclipses the guarded statement
   it is supposed to protect, so a test that only exercises the ordinary sequential path never proves
   the guard is what actually rejects a race — it proves the pre-check does, leaving the real database
   guard's clauses free to be deleted without failing any test. This same class recurred a second time
   in slice 3 (the `MarcarConvertidoAsync` guarded `UPDATE`'s three `WHERE` clauses) and a third time in
   slice 8 (the OD10 join filter). The fix pattern established by v1.1: route the test **below the
   confound** — call the guarded statement directly against a raw connection, or force a genuine
   concurrent race with a pause interceptor, never through the outer service's happy path alone.
2. **The calendar bomb**: `ServicioDePresupuestosTests`'s vencido-filter test pinned `RelojFijo` at
   `2026-08-19T12:00:00Z` in its assertions, but its own test-data helper seeded a `Precio.VigenteDesde`
   from the **real wall clock** (`DateTimeOffset.UtcNow.AddDays(-1)`) rather than from the fixed clock.
   The test passed every day until the real calendar caught up with and then passed the hardcoded date,
   at which point the seeded price's vigencia window landed *after* the frozen clock and the test began
   failing deterministically, every day, forever — a defect that "worked" in CI for weeks and then
   silently became permanent. It was pre-existing (introduced in slice 2, untouched by this stage's own
   diff) but only surfaced during slice 8's full-suite stage-close run. Standalone PR #154 fixed it by
   making test-data seeds immune to the calendar — using the fixed clock everywhere a date-bearing seed
   is built, never the real one. PR #149, delivered earlier and unrelated in content, fixed the same
   *class* of defect in a different test (`reposicion`'s "hoy" shape at the UTC-day boundary) — both are
   grouped here as the stage's calendar-drift lesson: **any test that pins a fixed clock for its
   assertions must also pin it for every seed the test itself builds**, or the two clocks will
   eventually disagree.
3. **The React 19 lesson (slice 7)**: a test asserted `expect(errorSpy).not.toHaveBeenCalled()` to prove
   a stale-response token guard was doing its job. Under React ≤18 this was a real assertion — a
   `setState` call after unmount used to emit a `console.error` warning the guard was meant to
   suppress. Under **React 19**, a post-unmount `setState` became a silent no-op with no warning at
   all, so the assertion passed identically whether the guard existed or not — a test that had quietly
   stopped testing anything. Confirmed empirically (mutation run: guard removed, suite still 54/54
   green) rather than reasoned about, and the annotation was corrected to state honestly what the test
   actually proves (that a late resolve doesn't crash the process) rather than what it no longer can
   (that the token guard specifically fires).
4. **The `pg_locks` probe (slice 6)**: proving the fourth write site's lock order (`stock` before
   `stock_lotes`, ascending `id_articulo`/`id_punto_venta`/`id_lote`) required observing real Postgres
   lock state directly via `pg_locks`, because `ServicioDeRemitos`'s raw statements bypass EF Core's
   `DbCommandInterceptor` pipeline the existing checkout-side lock-order tests rely on. The first
   lightweight probe attempt under-waited and needed a second judgment round to add a proper bound/retry
   before it reliably observed the lock — a reminder that a raw-connection concurrency probe against
   `pg_locks` needs the same polling discipline as any other eventually-consistent assertion, not a
   single-shot read.

---

## 7. Backlog (consolidated)

Carried forward from `verify-report.md`'s consolidated backlog (post both W1/W2 remediations) plus
`tasks.md`'s own deviation registry. None of the following block archive; they are registered debt for
a future stage to pick up.

1. **Target 54 — the in-transaction turno re-check has no race guard**, in both
   `ServicioDeFacturacionDeRemitos` and the pre-existing `ServicioDeVentas`. Judge-confirmed
   pre-existing class, not a stage-17 regression. Add a `FOR SHARE`/`FOR UPDATE` race net for the turno
   re-check in whichever future stage next touches turnos/caja.
2. **`tipos.ts:961` declares `pagos: PagoDeVenta[]` required while `Contratos.cs`'s `SolicitudDeVenta`
   has it nullable in C#** — a pre-existing (not stage-17-introduced) optionality mismatch, benign
   direction (the TS client is stricter than the server). Not fixed this stage.
3. **A `TXR`'s annulment leaves it de-linked from its ex-remitos, so `GET` on it returns `items: []`.**
   Today unreachable from the web (`Remito.tsx` only queries with `idComprobanteVenta != null`), but a
   latent asymmetry against `design.md`'s T11. Registered off juez A's slice-8 ronda-2 verdict, not
   fixed.
4. **Mutation targets 40/41 (ascending lock order, stock-before-stock_lotes for the remito write site)
   were verified by code inspection, not an independent apply-time mutation run**, because a
   single-item-per-remito rendezvous fixture cannot discriminate a reordering the way a multi-resource
   AB/BA fixture would. A coverage gap, not a defect — the structural rendezvous tests do exercise the
   real lock path; only the isolated single-mutation-run evidence is missing.
5. A brief-referenced "mono-zona `AddHours(-3)`" fixture item could not be corroborated anywhere in
   `tasks.md` or the source tree — flagged as unconfirmed rather than asserted as real debt, likely a
   cross-reference to a different stage or a drafting slip.

**Already resolved, reported here only to prevent it being mistaken for open debt**: mutation target
47's FEFO cross-write-site boundary gap, flagged during apply, was closed in judgment-day slice-5
ronda 2 (`LaParidadFefoEligeElLoteQueVenceHoyEnElBordeExactoEnElRemitoYEnElCheckout`, mutation-proven
with `AddDays(1)`).

**Out of scope, carried from the proposal, not stage-17 debt**: stock reservation by a presupuesto
(deferred with a stated reopen condition), repricing at conversion, editing a converted quote's lines,
the fiscal consolidation type (Etapa 19), direct presupuesto→remito conversion, partial conversion of a
presupuesto, rentabilidad/reportes de gestión over the remito circuit, a new authorization policy,
auditing presupuesto/remito transitions, printing/emailing a quote or delivery note — plus every
pre-existing carryover already tracked before this stage (`importe` CHECK micro-gate, the
`articulos_empresas` replace-set gap, `ways_owner` superuser, stage-13b conteo por planilla).

---

## 8. Artifacts Read for This Report

- `openspec/changes/stage-17-presupuestos-y-remitos/proposal.md`
- `openspec/changes/stage-17-presupuestos-y-remitos/state.yaml`
- `openspec/changes/stage-17-presupuestos-y-remitos/tasks.md`
- `openspec/changes/stage-17-presupuestos-y-remitos/verify-report.md`
- `openspec/changes/stage-17-presupuestos-y-remitos/specs/{presupuestos,remitos,comprobantes-venta,stock,lotes-y-vencimientos,auxiliary-catalogs}/spec.md`
- `git log` (merge commits for PRs #146-#155, #149, #154)

## 9. Spec Merge Traceability

See §"Specs Synced" in the return envelope for the full per-domain requirement count and the byte-exact
verification method (programmatic block extraction + diff against the delta source, not manual retyping).

## 10. Note on `state.yaml` Phase-Status Drift (not corrected by this archive)

`state.yaml`'s `propose` and `tasks` phase entries still read `status: pending` despite both artifacts
(`proposal.md`, `tasks.md`) being complete and consumed by every later phase — a pre-existing recording
gap from those phases, not introduced or corrected here. This archive updates exactly the phases the
orchestrator's instructions named (`apply`, `verify`, `archive`); the `propose`/`tasks` drift is flagged
here for awareness rather than silently fixed outside the given scope.
