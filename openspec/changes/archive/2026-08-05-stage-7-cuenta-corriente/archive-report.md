# Archive Report — Stage 7: Cuenta Corriente y Reliquidación a Precio del Día

**Archived**: 2026-08-05
**Change**: `stage-7-cuenta-corriente`
**Status**: COMPLETE AND CLOSED
**Verification**: PASS WITH WARNINGS (verdict 2026-08-05, main `bf60af8`)

## Executive Summary

Stage 7 is complete, verified, and archived. All 6 chained PR slices plus 1 micro-fix follow-up
merged to main (#61–#67). Delta specs have been merged into the main spec repository. Four new
capability domains created (`pagos-a-cuenta`, `reliquidacion-a-precio-del-dia`,
`ajustes-de-cuenta-corriente`, `estado-de-cuenta`); four modified via delta merge
(`consumo-cuenta-corriente`, `comprobantes-venta`, `arqueo-de-cierre`, `operacion-de-pos`).
Verify closed with 0 CRITICAL and 5 WARNINGS, all adjudicated/reconciled — none blocking.

The change ran under the user's explicit **autonomous delegation** (2026-08-05,
"aprobalas vos... maneja todo autonomo"), including the DB Change Gate. This report carries the
orchestrator's full decision log for the user's audit.

## Artifacts Archived

| Artifact | Path | Status |
|---|---|---|
| Proposal | `proposal.md` | Complete — 11 orchestrator-resolved decisions, DB Change Gate evaluation |
| Design | `design.md` | Complete — 9 architecture decisions, transaction pseudocode, 6 open questions |
| Specifications | `specs/` | Complete (8 domains: 4 new, 4 delta) |
| Tasks | `tasks.md` | Complete (6 slices, all tasks checked) |
| Verification | Recorded in `state.yaml` `phases.verify.notes` (no standalone `verify-report.md` was produced this stage, same posture as stage 6) | PASS WITH WARNINGS |
| State | `state.yaml` | Updated (`phase: archive`, `status: done`), archived |

## Specifications Merged to Main Specs

### New domains (full spec, copied directly)

| Domain | Path | Details |
|---|---|---|
| `pagos-a-cuenta` | `openspec/specs/pagos-a-cuenta/spec.md` | 7 requirements / 15 scenarios: RC idempotent seed, zero-items shape, open-turno requirement, forbidden CC medios + CF, one negative Pago movement atomically, overpayment→saldo a favor, anulación symmetry, independent numeración series |
| `reliquidacion-a-precio-del-dia` | `openspec/specs/reliquidacion-a-precio-del-dia/spec.md` | 8 requirements / 12 scenarios: eligibility scan, current-lista re-pricing, offer-discount reversion (with the worked example), one movement per run, atomic marker+saldo, no-op case, irreversibility, concurrent-sale serialization |
| `ajustes-de-cuenta-corriente` | `openspec/specs/ajustes-de-cuenta-corriente/spec.md` | 4 requirements / 9 scenarios: required detalle, structural distinction from the anulación contramovimiento, Supervisor+Admin authorization, atomic saldo_resultante snapshot |
| `estado-de-cuenta` | `openspec/specs/estado-de-cuenta/spec.md` | 5 requirements / 9 scenarios: server-side disponibilidad, running-balance movement list (DESC), default last-month + desde/hasta + histórico, tenant/cliente scoping, empty state |

### Delta merges (ADDED/MODIFIED requirements folded into existing specs)

| Domain | Action | Details |
|---|---|---|
| `consumo-cuenta-corriente` | MODIFIED + REMOVED | "Movimiento Schema At Rest" replaced in place (all four `tipo_movimiento_cc` values now have write paths; new "Pago snapshots the resulting saldo" scenario). "Anulación Produces A Contramovimiento" replaced in place (extends beyond CC-sale consumo to an RC's `pago` row; new "Anulación reverses an RC's Pago movement" scenario, prior scenario preserved). "Saldo Is The Maintained Cache Of The Ledger" replaced in place (invariant now spans all four movement types; new "mixed sequence" scenario, prior scenario preserved). "No Reliquidación, No CC Management, No Pagos De Cuenta" requirement REMOVED (reason + migration recorded — stage 7 implements exactly what it excluded). The two untouched requirements ("Consumo Is Written Inside The Sale Transaction", "Credit-Limit Evaluation") preserved byte-for-byte. |
| `comprobantes-venta` | ADDED + MODIFIED | New "RC Joins The POS-Emittable Tipos, Non-Fiscal" requirement inserted after "Snapshot Immutability of Items" (1 scenario). "Checkout Is One All-Or-Nothing Transaction" replaced in place (covers the itemless RC path explicitly; new "RC checkout commits with zero items and one Pago movement" scenario, 2 prior scenarios preserved). "Numeración Allocation Is Atomic" replaced in place (RC named as an independent series; new "RC and TX numerar independently" scenario, 2 prior scenarios preserved). "Anulación Reverses Stock and CC…" replaced in place (contramovimiento clause extended from CC-sale-consumo-only to `consumo` OR `pago`; new "RC anulación is blocked by a closed turno" scenario appended, all 6 prior scenarios preserved). The 6 other requirements in the file (Comprobante Schema At Rest, Payment Validation Rejection Order, Cuenta Corriente Payment Gating, Devoluciones As NCX Comprobantes, OperacionDePos Authorization, Comprobante-Letter Resolution Stays Dormant, Tenant and Punto de Venta Isolation) preserved untouched. |
| `arqueo-de-cierre` | MODIFIED | "Importe Esperado Derivation Per Medio" replaced in place (clarifies RC pagos are ordinary `pagos(m)`, no new term; new "An RC pago counts toward efectivo esperado like any other pago" scenario appended, all 3 prior scenarios preserved). All 4 other requirements (Arqueo Schema At Rest, Cierre Payload Carries Only Declared Counts, Arqueo Rows Only For Medios With Activity, Cierre Is One Atomic Irreversible Transaction, Resumen Parcial Uses The Same Derivation As Cierre) preserved untouched. |
| `operacion-de-pos` | ADDED | New "SupervisionDeCuentaCorriente Policy Gates Reliquidación And Ajuste Manual" requirement appended (3 scenarios: Supervisor, Admin, Vendedor-rejected). New "Pago A Cuenta And Estado De Cuenta Reads Live Under OperacionDePos" requirement appended (2 scenarios). All 5 pre-existing requirements (OperacionDePos Policy Admits Vendedor and Admin, Explicit idPuntoVenta, Cart Pricing Has Exactly One Path, Checkout Orchestration Contract, Caja Surface Lives Under OperacionDePos) preserved untouched, including their prior judgment-day amendment notes. |

## Implementation Summary

### 6 Chained PR Slices (Merged, stacked-to-main) + 1 Micro Follow-Up

| PR | Slice | Title | Judgment-Day Rounds | Status |
|---|---|---|---|---|
| #61 | 1 | Schema gate (`CuentaCorrienteEtapa7`: marker column + AK + composite self-FK + partial eligibility index + idempotent RC dual-path seed, doc-10 §1 note) | 1 | Merged 2026-08-05, main `e503825` |
| #62 | 2 | Pago a cuenta write path (`EscriturasDeCuentaCorriente` extraction, `ServicioDeCuentaCorriente` lean RC path, `ValidadorDePagoACuenta`, minimal `AnularAsync` widening) — **own dedicated judgment-day round** | 2 | Merged, main `b2468e6` |
| #63 | 3 | Reliquidación engine — the centerpiece (`ReliquidadorDeConsumos` pure, 8-step transaction, preview==execute identity, TOCTOU closure) | 2 | Merged, main `c6152e9` |
| #64 | 4 | Ajuste manual + estado de cuenta API (`ReglaDeAjusteDeCuenta`, `CalculadorDeEstadoDeCuenta`, mixed-sequence saldo invariant closed) | 2 | Merged, main `5ab0eef` |
| #65 | 5 | Web: estado de cuenta screen + pago modal | 3 | Merged, main `ac5b46a` |
| #66 | 6 | Web: ajuste + reliquidación modals + doc-10 §8 close-out (final) | 3 | Merged, main `afa8076` |
| #67 | — | Micro follow-up: `PreviewAsync` zero-delta normalization (preview/commit no-op contract symmetry, judge A slice-6 finding) | — | Merged |

**Total judgment-day rounds**: 13 (1 + 2 + 2 + 2 + 3 + 3). Slice 5 ran **three rounds — the
longest loop of the project, by design** (round 1: four MAJORs — timezone, state-dependent
cliente identity/CF gate, invisible window; round 2 caught two BLOCKERs *inside* the round-1
fixes — month-end overflow in `rangoUltimoMes`, CF gate failing OPEN on fetch error — plus a
circular offset test; round 3: all prescribed fixes with double mutation evidence). Slices 2 and
3 carried dedicated rigor for every `ServicioDeVentas` touch (Slice 2's own full judgment-day
round on `AnularAsync`, the project's most-guarded transaction; Slice 3's TOCTOU closure on the
anulación×reliquidación race).

**Delivery strategy**: chained PRs, stacked-to-main, per `protocolo-pr-solo-dev` and the
stage-3/4/5/6 precedent, `auto-chain` cached decision.

### Test Results (Final Suite)

| Suite | Count | Status |
|---|---|---|
| `Ways.Domain.Tests` | 356/356 | ✓ |
| `Ways.Application.Tests` | 212/212 | ✓ |
| `Ways.IntegrationTests` (real Postgres) | 556/556 | ✓ |
| Vitest (`src/Ways.Web`) | 322/322 | ✓ |
| TypeScript (`tsc -b`) / `oxlint` / `vite build` | clean | ✓ |
| EF migrations (`dotnet ef migrations has-pending-model-changes`) | clean | ✓ |

Baselines re-checked at Slice 1 branch-cut per Orchestrator Decision 5 (Domain 306 / Application
209 / Integration 481 / vitest 219 predated the in-flight D6 resumen-parcial follow-up PR #60,
which landed mid-stage and shifted the Slice 1 counts to 306/209/488/221).

### Key Accomplishments

1. **Reliquidación shipped pure-Domain first, the stage's centerpiece.** `ReliquidadorDeConsumos`
   is a DB-free re-pricer; the preview endpoint and the commit transaction call the **same**
   object with the same inputs — proven byte-identical by integration test (the "never two
   formulas" contract). The worked example from doc-01:398 (sold 900, current 1500, delta 600 =
   500 re-pricing + 100 annulled discount) is pinned numerically in the unit suite.

2. **The `clientes` row is the serialization point of every balance-touching path**, taken with
   the same discipline stage 6 applied to the turno: lock first, derive under the lock, commit
   once. Total lock order (`turnos_caja` → `clientes` → ledger INSERT) stays deadlock-free across
   all 5 CC write paths (consumo, pago, ajuste, reliquidación, anulación).

3. **Pago a cuenta reuses the sale machinery instead of a parallel one.** The `RC` comprobante
   inherits numeración, the turno guard, the `FOR SHARE` lock order, the arqueo derivation
   (`pagos(m)`, no new term), and the anulación path — verified end-to-end with zero code change
   to `CalculadorDeArqueo`.

4. **One movement per business act, enforced structurally.** Reliquidación writes exactly ONE
   `ActualizacionPrecios` row per run (500-consumo cap, deterministic `(fecha, id)` ordering
   proven end-to-end with 501 rows). The financed-fraction proration (`factor = min(1,
   consumo.importe / comprobante.total)`) is a declared, tested deviation from the legacy's
   whole-ticket re-pricing that collapses to the legacy formula under full financing.

5. **The anulación×reliquidación TOCTOU was found and closed inside the stage** (judgment-day
   slice-2 finding, judge A; closed as task 3.13 in slice 3): the `consumo_reliquidado` guard's
   unlocked read is re-checked under the cliente-row lock, failing closed if a reliquidación
   flips the marker mid-flight. Rendezvous race test proves exactly one of anulación/reliquidación
   wins, never both.

6. **Web: greenfield estado de cuenta screen + 3 action modals**, `react-async-state` compliant
   across the obligations that carry weight here (rule 8 `key={idCliente}`, rule 9 re-entrancy
   guards on pago/reliquidación, rule 6 never-report-2xx-as-failure, rule 10 sibling-surface
   replication — evidence-based deviation for ajuste/reliquidación since `turno_no_abierto` is
   structurally irreproducible on those endpoints, confirmed by grep and re-verified by both
   judges).

### DB Change Gate Summary

**Approved by the orchestrator under the user's explicit autonomous mandate** (2026-08-05,
"aprobalas vos... maneja todo autonomo"), grouped by write path:

- **Pago a cuenta**: no new table. ONE new row in the global `tipos_comprobante` (`RC`, clase
  `venta`, letra `NULL`, signo `+1`, `discrimina_iva false`, `es_fiscal false`,
  `afecta_stock false`), shipped as an idempotent `INSERT ... WHERE NOT EXISTS` inside the
  migration **and** appended to the fresh-database seed list — condition: proven on a
  stage-6-migrated database (met; a critical bug was caught in apply where, without the `AND
  EXISTS` fresh-DB guard, fresh deployments got RC pre-seeder and lost the other 10 catalog rows).
- **Reliquidación**: `movimientos_cuenta_corriente.id_movimiento_actualizacion integer NULL` — a
  self-FK to the `ActualizacionPrecios` row (design decision 2, chosen over a plain boolean for
  per-consumo audit traceability), additive + nullable + indexed for the eligibility scan
  (partial index `WHERE tipo='consumo' AND id_movimiento_actualizacion IS NULL`) + RLS-covered
  (inherited, no policy change) + backstopped (generic `fk_` → `400 referencia_invalida`).
- **Ajuste / estado de cuenta**: no schema change — `movimientos_cuenta_corriente` already had
  `tipo`, `importe`, `saldo_resultante`, `detalle`.
- **Conformity**: strict doc-10 §8 shape; zero new enum values (`pago` and
  `actualizacion_precios` already existed in `tipo_movimiento_cc`); operativa scoping and RLS
  unchanged; zero new `ManejadorDeErrores` branches.
- Migration name: `CuentaCorrienteEtapa7`.
- Migration clean: `dotnet ef migrations has-pending-model-changes` → no pending changes.

## The Autonomous Decision Log (for the user's audit)

Per `state.yaml`'s `gate` field and the per-slice apply notes — reproduced here for
traceability, since this is the first fully autonomous SDD change in the project.

### DB Gate self-approval and its conditions

The orchestrator exercised the DB Change Gate itself (the gate **step** still ran — model
summary presented, evaluation recorded in `proposal.md` under "DB Change Gate — orchestrator
evaluation" — it simply did not wait for the user, per the explicit delegation). Evaluation:
**APPROVED with strict doc-10 conformity**, conditional on (a) the RC insert being idempotent
and proven on a stage-6 database, and (b) any marker column being additive + nullable +
RLS-covered. Both conditions were met and verified in apply (Slice 1) and verify.

### The 11 proposal decisions (all orchestrator-resolved, autonomous mode)

1. **Pago a cuenta is a real `comprobante_venta`, tipo `RC`** — flows through the existing
   `ServicioDeVentas` machinery with physical medios, one negative `Pago` movement per checkout.
2. **No FIFO / no invoice-level imputación** — one signed movement per pago against the running
   saldo.
3. **Reliquidación is batched, re-prices against the client's current `id_lista_precio`**,
   annuls oferta discounts (verified correction to the incoming brief: doc-01:398 says offer
   lines are re-priced at the full current price with the discount annulled, not excluded).
4. **Ajuste manual is a first-class action** with a required detalle, distinguished from the
   anulación contramovimiento structurally (`id_comprobante_venta IS NULL` ⇒ manual).
5. **Authorization split — the stage's one deliberate departure from legacy parity.** Pago a
   cuenta and estado de cuenta reads stay under `OperacionDePos` (Vendedor + Supervisor + Admin,
   legacy parity). Reliquidación and ajuste manual sit under a **new** `SupervisionDeCuentaCorriente`
   policy (Supervisor + Admin only) — the legacy has no role gate on cuenta corriente at all.
6. **No interest, recargos or punitorios on CC balances** — reliquidación already is the
   business's inflation mechanism; interest on top would double-charge.
7. **Anulación symmetry, with one exception** — an RC's anulación reverses its `Pago` movement;
   reliquidación movements are NOT anulable (irreversible by design, correction is a compensating
   `Ajuste`).
8. **Web**: estado de cuenta screen (header + running-balance movement list + filters) with the
   three actions gated per decision 5.
9. **Overpayment produces saldo a favor** (negative saldo), never rejected.
10. **Credit-limit rules do not apply to a payment** — RC forbids `cuenta_corriente` medios
    entirely (a debt cannot pay a debt).
11. **RC gets its own numeración series** via the existing `(IdPuntoVenta, TipoComprobante)`
    counter — no new mechanism.

### The `SupervisionDeCuentaCorriente` tightening (the stage's only parity departure)

Flagged explicitly per decision 5: the legacy has zero role gating on cuenta corriente
operations. Stage 7 introduces `Politicas.SupervisionDeCuentaCorriente` (Supervisor + Admin) to
gate reliquidación and ajuste manual — both mutate balances in bulk or at discretion and are
irreversible in practice, a materially different risk class from taking a payment (which stays
under the legacy-parity `OperacionDePos` tier, open to Vendedor).

### Orchestrator triages (spec/design conflicts resolved during tasks/apply/verify)

- **Policy-name conflict** (tasks Orchestrator Decision 2): `design.md`'s API Surface table
  named the policy `Politicas.SupervisionDeOperacion`; every spec file named it
  `Politicas.SupervisionDeCuentaCorriente`. Tasks bound to the spec name (testable acceptance
  scenarios are the source of truth); `sdd-verify` confirmed the implementation already matched
  the spec name by grep — the design-doc mismatch was cosmetic, not a code defect. Same posture
  as the stage-6 `turno_ya_cerrado`-style spec-binding precedent.
- **Domain-code naming conflict** (tasks Orchestrator Decision 3): `design.md`'s Backstop Map
  used `medio_no_admite_pago_a_cuenta` / `detalle_requerido`; specs used
  `pago_a_cuenta_sin_medios_fisicos` / `ajuste_detalle_requerido`. Tasks bound to the spec codes;
  implementation matches; design flagged for a wording-only correction.
- **Estado de cuenta ordering** (verify): the spec's movement-list ordering was tightened to
  `fecha` **DESCENDING** (newest-first, legacy F4 parity) after an orchestrator triage over an
  ambiguity between spec and design at apply time (Slice 4).
- **Financed-fraction proration** (design decision, flagged as a declared deviation from strict
  legacy parity, `state.yaml` gate note): a comprobante that is part cash, part fiado only
  re-indexes the financed fraction (`factor = min(1, consumo.importe / comprobante.total)`).
  Collapses to the legacy's whole-ticket formula under full financing (the normal case),
  asserted by a unit test.
- **Skip-unpriceable lines, never abort** (design, flagged in the gate note): a line with
  `id_articulo IS NULL` or no vigente price in the client's lista is skipped with a motivo in the
  audit detail rather than aborting the whole run or (the legacy's defect) crediting the line
  against a NULL price.
- **DESC/preview-noop micro-fix** (PR #67): `ServicioDeReliquidacion.PreviewAsync`'s zero-delta
  response did not match `EjecutarAsync`'s no-op contract exactly (judge A, slice-6 finding) —
  fixed as a dedicated micro follow-up so the preview↔commit identity holds even in the
  zero-eligible-consumos / zero-delta path.

## Verification (2026-08-05, main `bf60af8`)

**PASS WITH WARNINGS**: 0 CRITICAL; 5 WARNINGS, all adjudicated/reconciled — none blocking:

(a) **Vendedor UI reachability** — the only web entry point (`/clientes`) is Admin-only while the
estado de cuenta screen itself is `OperacionDePos`-gated (Vendedor+Supervisor+Admin); a Vendedor
can reach the screen only by direct URL. Product decision **deferred to the user** (see Deferred
below).

(b/c) **Design/spec doc staleness** — a pagination phantom in design's API table was removed; the
estado-de-cuenta ordering scenario was tightened to DESCENDING in the spec; the policy/code-name
claim (see Orchestrator Triages above) was a verify false positive — already aligned at tasks
time, confirmed by grep against the shipped code.

(d) **Estado sin pagination** — confirmed internally consistent (one `GET` returns header + full
filtered page, per design decision 9; no pagination contract exists anywhere in the surface).

(e) **Two missing ajuste scenarios ADDED to the spec** — `ajuste_importe_invalido` (zero-importe
rejection) and the Consumidor Final rejection scenario were present in the implementation but
missing from the original spec text; added during verify to close the coverage gap.

(f) **Stored detalle JSON PascalCase quirk documented** — the reliquidación `detalle` column
stores real PascalCase keys because the backend's raw serializer bypasses the camelCase policy;
the web parser is dual-case tolerant with a defensive fallback. Backend-side normalization is an
optional follow-up (see Deferred below).

All 12 proposal Success Criteria evidenced; lock order across the 5 CC write paths verified
cycle-free; both TOCTOU closures verified; the preview==commit no-op contract closed by #67.

## Specification Coverage

All requirements across the 4 new domain specs plus the 4 delta requirements map to implemented
behavior with passing tests:
- `pagos-a-cuenta`: 7 requirements / 15 scenarios
- `reliquidacion-a-precio-del-dia`: 8 requirements / 12 scenarios
- `ajustes-de-cuenta-corriente`: 4 requirements / 9 scenarios
- `estado-de-cuenta`: 5 requirements / 9 scenarios
- `consumo-cuenta-corriente` delta: 3 MODIFIED + 1 REMOVED requirement / 8 scenarios total (5
  preserved + 3 new, plus the 1 removed requirement's reason+migration recorded)
- `comprobantes-venta` delta: 1 ADDED + 3 MODIFIED requirements / 15 scenarios total (11
  preserved + 4 new)
- `arqueo-de-cierre` delta: 1 MODIFIED requirement / 4 scenarios total (3 preserved + 1 new)
- `operacion-de-pos` delta: 2 ADDED requirements / 5 scenarios (all new)

**Spec Compliance**: verified PASS WITH WARNINGS. All scenarios across the 8 spec files map to
passing runtime evidence by `sdd-verify` (0 CRITICAL). 5 WARNINGS, all non-blocking (see
Verification above).

## Deferred / Follow-Ups

The following items are explicitly out of this stage's scope or carried forward as documented,
non-blocking backlog:

**(a) Vendedor UI reachability for the estado de cuenta screen** — the only navigation entry
point (`Clientes.tsx`'s per-row action, reached from `/clientes`) is Admin-only in the current
web routing, while the estado de cuenta screen itself and the pago action are `OperacionDePos`
(open to Vendedor). A Vendedor can reach the screen only by typing the URL directly
(`/clientes/:id/cuenta-corriente`); the backend authorization is correct and was verified, but
the navigation path is incomplete for that role. **This is a product decision pending the user's
input** — whether to add a Vendedor-reachable entry point (e.g. from the POS checkout screen or
a dedicated cliente-lookup surface) or to accept the current admin-mediated flow as intentional.

**(b) Backend detalle JSON serializes PascalCase** — the reliquidación `ActualizacionPrecios`
movement's `detalle` column is written by a raw serializer that bypasses the API's camelCase
policy, so the stored JSON has real PascalCase keys (`Comprobante`, `Item`, `PrecioAnterior`,
etc.). The web parser is dual-case tolerant with a defensive fallback (verified working, both
judges re-verified in slice 6). Normalizing the backend writer to emit camelCase consistently
with every other API response is an **optional follow-up**, not a functional gap.

**(c) Cuenta corriente de proveedores** — deferred to stage 8, per proposal decision/scope.

**(d) Invoice-level imputación (FIFO / partial allocation)** — explicitly out of scope (proposal
decision 2); a pago stays one signed movement against the running saldo, legacy parity.

**(e) Interest, recargos or punitorios on CC balances** — explicitly out of scope (proposal
decision 6); no legacy equivalent exists, and reliquidación already serves as the business's
inflation mechanism.

**(f) Recargo por medio de pago** — still dormant, carried forward unchanged since stage 5; a
future product decision, not a parity detail.

**(g) `ServicioDeArticulos` `articulos_empresas` replace-set concurrency gap** — still open,
carried forward unchanged since stage 4, unrelated to cuenta corriente.

### Known Carryovers (Non-Blocking, Unrelated To This Stage)

- **Reversing a reliquidación** — irreversible by design (proposal decision 7); the correction
  path is always a compensating `Ajuste`. Not a gap — a deliberate, documented invariant.
- **The reliquidación `detalle` is JSON in a `text` column** (design Open Questions) — if it ever
  needs querying, `jsonb` is a later, separate decision.
- **500 consumos per run cap** (design Open Questions) — a client who ever hits it needs two
  runs, each writing its own movement; correct, but visible to the operator.
- **`id_punto_venta` on reliquidación/ajuste is provenance, not authority** (design Open
  Questions) — validated tenant-scoped per ADR-8, since neither operation has a turno to derive
  it from.

### Next Stage

Stage 8 (cuenta corriente de proveedores, comprobantes de compra, transferencias, inventario,
per doc 10 sequence) can start. The project now has a complete money-in/money-out cycle for
client accounts: debt accumulates through consumo (stage 5), is repaid through pago a cuenta,
corrected through ajuste, and re-indexed to the price of the day through reliquidación — all
through the same audited, one-derivation machinery stages 5–6 established.

---

**Archive completed**: 2026-08-05
**Change archived to**: `openspec/changes/archive/2026-08-05-stage-7-cuenta-corriente/`
**Specs merged**: 4 new domains + 4 delta-merged domains in `openspec/specs/`
**Verification**: PASS WITH WARNINGS — 0 CRITICAL; 5 WARNINGS, all adjudicated/reconciled, none
blocking (see Verification above; item (a) — Vendedor UI reachability — is the one item carried
forward as a pending product decision, not a code defect).
**SDD Cycle**: COMPLETE
