# Archive Report — Stage 6: Turnos de Caja, Arqueos, Tesorería y Gastos

**Archived**: 2026-08-05
**Change**: `stage-6-turnos-caja`
**Status**: COMPLETE AND CLOSED
**Verification**: PASS WITH WARNINGS (verdict 2026-08-05, main `94e9daa`)

## Executive Summary

Stage 6 is complete, verified, and archived. All 7 chained PRs merged to main (#52, #53,
#54, #55, #56, #57, #58), plus a flake-fix follow-up (#59). Delta specs have been merged
into the main spec repository. Five new capability domains created (`turnos-de-caja`,
`movimientos-de-caja`, `arqueo-de-cierre`, `gastos`, `tesoreria`); two modified via delta
merge (`comprobantes-venta`, `operacion-de-pos`). Verify closed with 0 CRITICAL, 1
WARNING (resumen D6-content gap, deferred as a documented follow-up), and 1 cosmetic
SUGGESTION.

## Artifacts Archived

| Artifact | Path | Status |
|---|---|---|
| Proposal | `proposal.md` | Complete |
| Design | `design.md` | Complete |
| Specifications | `specs/` | Complete (7 domains: 5 new, 2 delta) |
| Tasks | `tasks.md` | Complete (7 slices, all tasks checked) |
| Verification | Recorded in `state.yaml` `phases.verify.notes` (no standalone `verify-report.md` was produced this stage) | PASS WITH WARNINGS |
| State | `state.yaml` | Updated, archived |

## Specifications Merged to Main Specs

### New domains (full spec, copied directly)

| Domain | Path | Details |
|---|---|---|
| `turnos-de-caja` | `openspec/specs/turnos-de-caja/spec.md` | 4 requirements / 10 scenarios: turno schema at rest, one-open-turno-per-punto-de-venta partial unique index, apertura/cierre authorization, turno always server-resolved never client-supplied |
| `movimientos-de-caja` | `openspec/specs/movimientos-de-caja/spec.md` | 5 requirements / 11 scenarios: movimiento schema at rest, motivo required for retiro/refuerzo, apertura de cajón F12 parity, movimiento requires an open turno, movimiento authorization |
| `arqueo-de-cierre` | `openspec/specs/arqueo-de-cierre/spec.md` | 6 requirements / 12 scenarios: arqueo schema at rest, cierre payload carries only declared counts, importe esperado derivation per medio, arqueo rows only for medios with activity (never cuenta corriente), cierre as one atomic irreversible transaction, resumen parcial shares the cierre derivation |
| `gastos` | `openspec/specs/gastos/spec.md` | 5 requirements / 8 scenarios: gasto schema at rest, gasto requires an open turno, importe must be positive, no magic tipo encodes a retiro as a gasto, gasto authorization |
| `tesoreria` | `openspec/specs/tesoreria/spec.md` | 4 requirements / 7 scenarios: movimiento tesorería schema at rest, exactly one row per cierre chained from the last final, manual tesorería entries out of scope, tesorería write is part of the cierre transaction |

### Delta merges (MODIFIED/ADDED requirements folded into existing specs)

| Domain | Action | Details |
|---|---|---|
| `comprobantes-venta` | MODIFIED | "Comprobante Schema At Rest" requirement replaced in place (`id_turno_caja` now an FK, required for every new sale, resolved server-side, historical NULL-turno rows untouched; new "Every new sale carries the resolved open turno" and "Stage-5 NULL-turno comprobantes stay untouched" scenarios; the "Sale persists with id_turno_caja NULL" scenario retired). "Anulación Reverses Stock and CC, Never Restores by Editing" requirement replaced in place with the extended title "…, and Is Blocked By A Closed Turno" (adds the `409 turno_cerrado` gate and its rationale; the 4 original scenarios — stock reversal, CC contramovimiento, idempotent double-anulación, no restaurar endpoint — preserved byte-for-byte; 2 new scenarios appended: "Anulación rejected when the comprobante's turno is closed", "Stage-5 NULL-turno comprobante stays anulable"). All 7 other requirements in the file (Snapshot Immutability, Checkout Is One All-Or-Nothing Transaction, Payment Validation Rejection Order, Cuenta Corriente Payment Gating, Numeración Allocation Is Atomic, Devoluciones As NCX Comprobantes, OperacionDePos Authorization, Comprobante-Letter Resolution Stays Dormant, Tenant and Punto de Venta Isolation) preserved untouched. |
| `operacion-de-pos` | MODIFIED + ADDED | "Checkout Orchestration Contract" requirement replaced in place (adds the fail-fast open-turno precondition before pricing/oferta resolution; new "Selling with no open turno fails before any pricing work" scenario; the 3 original scenarios preserved). New "Caja Surface Lives Under OperacionDePos" requirement appended at the end of the file (apertura, cierre, movimientos, gastos, resumen all gated by the same `OperacionDePos` policy; 2 scenarios: Vendedor access, Root rejection). The other 3 requirements (OperacionDePos Policy Admits Vendedor and Admin, Explicit idPuntoVenta No Server-Side POS Session, Cart Pricing Has Exactly One Path) preserved untouched, including their prior judgment-day amendment notes. |

## Implementation Summary

### 7 Chained PRs (Merged, stacked-to-main) + 1 Follow-Up

| PR | Slice | Title | Judgment-Day Rounds | Status |
|---|---|---|---|---|
| #52 | 1 | Schema gate (`TurnosCajaYGastosEtapa6`: 5 tables, 4 enums, `ux_turnos_caja_abierto`, `comprobantes_venta` FK/index, RLS same-migration, 28 new tests) | 1 | Merged 2026-08-04 |
| #53 | 2 | Turno lifecycle (`ReglaDeTurnos`, `ReglaDeMovimientosDeCaja`, `ServicioDeTurnos.AbrirAsync`/`ResolverTurnoAbiertoAsync`/`RegistrarMovimientoAsync`, `CajaEndpoints`) | 2 | Merged 2026-08-04 |
| #54 | 3 | Gastos write path (`ServicioDeGastos`, `GastosEndpoints`) | 2 | Merged 2026-08-04 |
| #55 | 4 | Derivation + cierre — the centerpiece (`CalculadorDeArqueo`, `ResolvedorDeMedioDeCajaFisica`, 6-step close-first transaction, `FOR SHARE` guards wired into movimientos+gastos) | 2 | Merged 2026-08-04 |
| #56 | 6 | Web: caja apertura/movimientos/resumen (`api/caja.ts`, `Caja.tsx`) | 1 | Merged 2026-08-04 |
| #57 | 5 | Checkout wiring — surgical, dedicated judgment-day round (`ServicioDeVentas.EmitirAsync`/`AnularAsync`) | 1 | Merged 2026-08-04 |
| #58 | 7 | Web: cierre + `Pos.tsx` gate seam (`CierreDeCaja.tsx`, `api/arqueo.ts`, apertura self-heal) | 2 | Merged 2026-08-04 |
| #59 | — | Pre-existing full-suite-only vitest flake fix in `Caja.test.tsx` ("Calculando…" test) | — | Merged 2026-08-05 |

**Total Rounds**: 11 (1 + 2 + 2 + 2 + 1 + 1 + 2). **All Clean.**

**Delivery strategy**: chained PRs, stacked-to-main, per `protocolo-pr-solo-dev` and the
stage-3/4/5 precedent. Slice 6 (web apertura/movimientos/resumen) was sequenced ahead of
Slice 5 (checkout wiring) for review load even though its dependency graph only requires
Slice 2 + Slice 4.

### Test Results (Final Suite)

| Suite | Count | Status |
|---|---|---|
| `Ways.Domain.Tests` | 306/306 | ✓ |
| `Ways.Application.Tests` | 209/209 | ✓ |
| `Ways.IntegrationTests` (real Postgres) | 481/481 | ✓ |
| Vitest (`src/Ways.Web`) | 219/219 (flake fixed in PR #59, stable) | ✓ |
| TypeScript (`tsc -b`) / `oxlint` / `vite build` | clean | ✓ |
| EF migrations (`dotnet ef migrations has-pending-model-changes`) | clean | ✓ |

### Key Accomplishments

1. **The derivation is the centerpiece.** `CalculadorDeArqueo` (pure Domain) computes
   `importe_esperado(m) = pagos(m) − gastos(m) + [m=ancla]×(fondo + refuerzos − retiros −
   vueltosTotales)` over medios with `Comportamiento ≠ CuentaCorriente`. The resumen
   parcial and the cierre call the exact same `LectorDeMovimientosDelTurno` +
   `CalculadorDeArqueo` pair — proven byte-identical by integration test. No client
   request shape anywhere accepts a total or an expected amount — the structural answer
   to legacy bug D7.

2. **The lock-order invariant.** Every write into a turno (venta, anulación, gasto,
   movimiento de caja) opens with `SELECT … FROM turnos_caja … FOR SHARE`; the cierre's
   `UPDATE turnos_caja SET estado='cerrado' … RETURNING` is the first statement of its
   transaction and takes the exclusive lock before any derivation read. The turno is the
   first lock every path acquires — a total order, so no deadlock — and a sale can never
   commit into a turno whose arqueo has already been derived. Three genuinely racy
   surfaces proven by rendezvous tests: two aperturas, two cierres, a sale racing a
   cierre.

3. **Cierre is one atomic, irreversible transaction.** 6 steps (close-first UPDATE →
   derive → resolve cash anchor → calculate + validate declared counts → insert
   `arqueos_turno` → chain `movimientos_tesoreria`) or nothing — proven by fault-point
   injection at every step. `arqueos_turno.diferencia` is `GENERATED ALWAYS … STORED`; no
   reapertura or arqueo-edit endpoint exists.

4. **Checkout wiring stayed surgical.** `ServicioDeVentas.EmitirAsync`/`AnularAsync`
   changed by a 58/3 production diff: early `ResolverTurnoAbiertoAsync` (404 before 409,
   pricing untouched), frozen `plan.IdTurnoCaja`, `FOR SHARE` re-check as the first write
   statement, `AnularAsync` closed-turno guard. The `IdTurnoCaja = null` hardcode at
   `ServicioDeVentas.cs:459` is dead. The entire stage-5 integration suite stayed green
   except turno-precondition fixtures. Got its own dedicated full judgment-day round —
   both judges CLEAN in round 1.

5. **Gastos replaces the legacy's `tipo = 95` magic number.** Retiros de efectivo live
   exclusively in `movimientos_caja`; `categoria_gasto` has no retiro-equivalent value
   (proven by reflection test).

6. **Web: greenfield caja screens.** `Caja.tsx` (apertura, movimientos, resumen parcial)
   and `CierreDeCaja.tsx` (per-medio count inputs, irreversibility confirmation, Z
   report) plus the `Pos.tsx` gate seam (409 `turno_no_abierto` → offer to open a turno,
   never auto-resubmits the sale). Full `react-async-state` compliance across all 9
   rules, notably rule 9 (double-click issues exactly one cierre POST) and the apertura
   409 self-heal (losing tab refetches the actually-open turno instead of showing a stale
   error).

### Judgment-Day Rounds (Solo-Dev Review Protocol)

| Slice | Rounds | Key Findings | Status |
|---|---|---|---|
| 1 (schema gate) | 1 | Clean, both judges CLEAN. | APPROVED |
| 2 (turno lifecycle) | 2 | R1: read-prefix auth-guard blind spot (fixed with mutation evidence), missing cross-tenant tests (added), PLAN gap — design requires `FOR SHARE` on all four turno-writing paths but tasks only scheduled venta/anulación (movimientos+gastos guard added as tasks 4.17/4.18). R2: test-only iteration, clean (orchestrator-verified). | APPROVED |
| 3 (gastos) | 2 | R1: no majors, 3 test-only minors (enum whitelist, FK `referencia_invalida` backstops, negative 403). R2: clean. | APPROVED |
| 4 (derivation + cierre, centerpiece) | 2 | R1: both judges independently re-derived the formula (match); 2 MAJORs fixed (`conteo_duplicado` 400 instead of 500 crash; anulados-excluded scenario had no test + misleading comment) + 3 minors. R2: judge A CLEAN, judge B 1 minor (negative-esperado pin lost) restored inline. | APPROVED |
| 6 (web apertura/movimientos/resumen) | 1 | 1 MAJOR fixed with mutation evidence (generation-orphan — failed movimiento left `cargandoResumen` stuck); dead prop removed; selector-disabled tests added; 409 apertura self-heal deferred to slice 7. | APPROVED |
| 5 (checkout wiring, dedicated round) | 1 | Both judges CLEAN in round 1 — the most-guarded transaction in the project came through with no findings. | APPROVED |
| 7 (web cierre + Pos gate seam, final) | 2 | R1: 4 MAJORs total across both judges, all fixed with mutation evidence. R2: both CLEAN. | APPROVED |

### DB Change Gate Summary

Approved 2026-08-04 ("dale"), grouped by write path:

- **Write path A (Turno lifecycle)**: `turnos_caja`, `arqueos_turno`
- **Write path B (Physical cash outside the sale)**: `movimientos_caja`
- **Write path C (Gastos)**: `gastos` (without `id_comprobante_compra` — decision 1)
- **Write path D (Tesorería)**: `movimientos_tesoreria`
- **Write path E (Checkout wiring)**: `comprobantes_venta.id_turno_caja` FK + index (column
  already existed, nullable, from the stage-5 migration)
- **4 enums**: `estado_turno`, `tipo_movimiento_caja`, `tipo_movimiento_tesoreria`,
  `categoria_gasto`
- RLS (`HabilitarRlsDeTenant`) enabled on all 5 new tables in the same migration that
  creates each of them
- **Flagged tightenings presented and resolved at the gate**: (a) proposal decision 2
  (cierre role) — user kept legacy parity (Vendedor + Supervisor + Admin), no Supervisor
  tightening; (b) design decision 8 (`motivo NOT NULL length >= 5` uniformized across all
  three `tipo_movimiento_caja`, stricter than proposal decision 9's retiro/refuerzo-only
  scope) — approved as presented; (c) proposal decision 1 (`gastos` ships without the
  compra FK, deferred to stage 8, `movimientos_stock` precedent) — approved
- **Also surfaced and approved**: design decision 1 (declared deviation — cierre
  close-first + `FOR SHARE` on every turno-writing path), decision 2 (vuelto lands on the
  cash anchor as a total, not per-medio), decision 3 (hard stop on a non-unique efectivo
  medio), decision 9 (tesorería `egreso` = all gastos, legacy parity), `diferencia` as a
  `GENERATED ALWAYS` column
- Migration name: `TurnosCajaYGastosEtapa6`
- Migration clean: `dotnet ef migrations has-pending-model-changes` → no pending changes

### Deviations From Original Plan (All Documented)

1. **Cierre order inverted to close-first** (design decision 1, declared deviation from
   the proposal's indicative derive-first order) — the guarded `UPDATE turnos_caja` is
   statement #1, taking the exclusive lock before any derivation read; every writer into
   a turno opens its write transaction with `SELECT … FROM turnos_caja … FOR SHARE`. This
   closes the race where a sale resolving the turno as open commits after the arqueo was
   derived (uncounted cash) — the exact defect class legacy bug D7 belongs to.
2. **`vueltosTotales` lands on the cash anchor as a total, not per-medio** (design
   decision 2) — a strict generalization of doc 10's literal one-medio formula under the
   seeded catalog; a unit test proves the collapse.
3. **Hard stop on a non-unique efectivo medio** (design decision 3) — `409
   caja_sin_medio_efectivo_unico` on 0 or 2+ rows with `Comportamiento = Efectivo`,
   raised identically by resumen and cierre; fails closed on multi-cash-medio tenants
   until `medios_pago.es_caja_fisica` ships (deliberate, open question).
4. **`motivo` uniformized to `NOT NULL length >= 5` across all three
   `tipo_movimiento_caja`** (design decision 8) — stricter than proposal decision 9, which
   only required a non-empty motivo for retiro/refuerzo; approved at the gate.
5. **Tesorería `egreso` = all gastos over all medios** (design decision 9, legacy parity)
   — flagged as a one-line flip once bank accounts exist; approved as presented.
6. **Spec/design wording conflict resolved by the orchestrator at tasks time**: the
   repeated-cierre rejection code — `arqueo-de-cierre/spec.md`'s "Closing an
   already-closed turno is rejected" scenario was corrected from `409 turno_no_abierto`
   to `409 turno_ya_cerrado` to match design's binding transaction pseudocode (exists-but-
   closed is distinct from no-turno-at-all).
7. **Resumen parcial D6-content gap** (verify WARNING, self-disclosed at slice 6) — the
   merged `GET …/resumen` contract exposes only `{ idTurnoCaja, idMedioAncla, medios:
   [{ idMedioPago, importeEsperado }] }`, not the design/proposal's promised D6-rich
   content (áreas, tickets count, primer/último ticket, egresos por categoría). The
   binding spec requirement ("same derivation as cierre") IS met and byte-proven; this is
   a display/reporting completeness shortfall, not a derivation-correctness gap.
8. **Task 4.8 cosmetic wording** (verify SUGGESTION) — prose says "5 routes" but lists and
   ships 4 non-GET caja/gastos routes in the `SuperficieDeAutorizacionTests` allowlist,
   matching design's actual API Surface table.

### Specification Coverage

All requirements across the 5 new domain specs plus the 2 delta requirements map to
implemented behavior with passing tests:
- `turnos-de-caja`: 4 requirements / 10 scenarios
- `movimientos-de-caja`: 5 requirements / 11 scenarios
- `arqueo-de-cierre`: 6 requirements / 12 scenarios
- `gastos`: 5 requirements / 8 scenarios
- `tesoreria`: 4 requirements / 7 scenarios
- `comprobantes-venta` delta: 2 MODIFIED requirements / 8 scenarios total (5 preserved + 3 new)
- `operacion-de-pos` delta: 1 MODIFIED + 1 ADDED requirement / 6 scenarios total (3 preserved + 1 new + 2 added)

**Spec Compliance**: verified PASS WITH WARNINGS. All scenarios across the 7 spec files
map to passing runtime evidence by `sdd-verify` (0 CRITICAL). One WARNING and one cosmetic
SUGGESTION, both non-blocking (see Deviations above and Deferred/Follow-Ups below).

## Deferred / Follow-Ups

The following items are explicitly out of this stage's scope or carried forward as
documented, non-blocking backlog:

**(a) Resumen parcial D6-content enrichment** — the merged `GET …/resumen` contract
(`ServicioDeResumenDeTurno`/`ResumenDeTurno`, shipped in Slice 4) returns only per-medio
`importeEsperado` + the cash-anchor flag. The design's Data Flow diagram and the
proposal's legacy-D6-parity description promised richer content: áreas breakdown, tickets
count, primer/último ticket, and egresos por categoría de gasto + retiros. The underlying
derivation is correct and IS shared verbatim between resumen and cierre (byte-proven by
integration test) — this is a display/reporting completeness gap, not a correctness gap.
Recorded as a verify WARNING, self-disclosed by the implementation at slice 6 and
confirmed by `sdd-verify`. Candidate for a small follow-up slice extending
`ServicioDeResumenDeTurno`'s response shape.

**(b) Manual tesorería operations** — `deposito`, `gasto`, and `ajuste`
`movimientos_tesoreria` entries entered by hand, plus any tesorería reporting/UI, are
deferred (proposal decision 4). Stage 6 ships only the automatic `retiro_caja` row
written inside the cierre transaction. No endpoint exists for manual tesorería entry —
proven by an explicit spec scenario (404).

**(c) `gastos.id_comprobante_compra`** — the column and its FK are deferred to stage 8,
shipping together with the `comprobantes_compra` table (proposal decision 1, following the
`movimientos_stock.id_comprobante_compra` deferred-FK precedent from stage 5). `gastos`
currently has no reference to a purchase document.

**(d) Caja Virtual (`arqueos_recargas` / `arqueos_recargas_canales`)** — out of scope for
this stage (proposal decision 11); doc 10 §7 keeps it as a separate concern (`cajaV` of
doc 03).

**(e) Cierre-role tightening option** — proposal decision 2 was presented at the DB
Change Gate as a flagged choice: keep cierre under `Politicas.OperacionDePos` (Vendedor +
Supervisor + Admin, legacy parity) or tighten it to Supervisor + Admin only. The user kept
legacy parity at the gate (2026-08-04, "dale" with the presented defaults). The
tightening remains a one-line flip (a new policy constant + one stacked
`.RequireAuthorization(…)` on `POST …/cierre`) if ever wanted — no schema or derivation
change required.

**(f) Recargo por medio de pago** — `medios_pago.recargo_porcentaje` exists in the schema
(confirmed at the stage-5 DB Change Gate) but is still not applied anywhere. Carried
forward unchanged from stage 5; still a future product decision, not a parity detail.

### Known Carryovers (Non-Blocking, Unrelated To This Stage)

- **`ServicioDeArticulos` `articulos_empresas` replace-set concurrency gap** — spawned
  during stage-4 slice 2, still open, unrelated to caja/turnos.
- **`medios_pago.es_caja_fisica`** — the real answer to "which drawer is the drawer" when
  a tenant has more than one efectivo medio (design decision 3's deferred alternative).
  Stage 6 fails closed (`409 caja_sin_medio_efectivo_unico`) until this ships.
- **Turno spans midnight / timezone; blind arqueo flag; second cash medio; no idempotency
  key on cierre** — design.md Open Questions, none blocking, all documented at design
  time.

### Next Stage

Stage 7 (cuenta corriente management, reliquidación a precio del día F4, pagos de cuenta,
per doc 10 sequence) can start. The project now has a complete shift lifecycle: turnos
open and close atomically, every sale and expense is attached to a turno, and the close
counts physical money per medio de pago against a server-derived, trust-worthy
expectation — ready for stage 7 to layer full cuenta corriente management on top of the
narrow write-only slice stage 5 shipped.

---

**Archive completed**: 2026-08-05
**Change archived to**: `openspec/changes/archive/2026-08-05-stage-6-turnos-caja/`
**Specs merged**: 5 new domains + 2 delta-merged domains in `openspec/specs/`
**Verification**: PASS WITH WARNINGS — 0 CRITICAL; 1 WARNING (resumen D6-content gap,
deferred as documented follow-up (a) above); 1 cosmetic SUGGESTION (task 4.8 wording,
non-blocking).
**SDD Cycle**: COMPLETE
