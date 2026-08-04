# Archive Report — Stage 5: POS y Ventas

**Archived**: 2026-08-04
**Change**: `stage-5-pos-ventas`
**Status**: COMPLETE AND CLOSED
**Verification**: PASS (verdict 2026-08-04, HEAD `4c73e3e`)

## Executive Summary

Stage 5 is complete, verified, and archived. All 7 chained PRs have merged to main
(#38, #40, #42, #44, #46, #48, #50). Delta specs have been merged into the main spec
repository. Four new capability domains created (`comprobantes-venta`, `stock`,
`consumo-cuenta-corriente`, `operacion-de-pos`); five modified via delta merge
(`codigos-barra`, `clientes`, `articulos`, `parametros-operativos`,
`resolucion-de-ofertas`).

## Artifacts Archived

| Artifact | Path | Status |
|---|---|---|
| Proposal | `proposal.md` | Complete |
| Design | `design.md` | Complete |
| Specifications | `specs/` | Complete (9 domains: 4 new, 5 delta) |
| Tasks | `tasks.md` | Complete (7 slices, all tasks checked) |
| Verification Report | `verify-report.md` | PASS |
| State | `state.yaml` | Updated, archived |

## Specifications Merged to Main Specs

### New domains (full spec, copied directly)

| Domain | Path | Details |
|---|---|---|
| `comprobantes-venta` | `openspec/specs/comprobantes-venta/spec.md` | 9 requirements / 24 scenarios: comprobante schema at rest, snapshot immutability of items, all-or-nothing checkout transaction, payment validation rejection order (legacy B6 parity), cuenta corriente payment gating, atomic numeración allocation, devoluciones as NCX, anulación with inverse movements, dormant comprobante-letter resolution, tenant/PV isolation |
| `stock` | `openspec/specs/stock/spec.md` | 6 requirements / 12 scenarios: stock schema at rest, sale decrement inside the checkout transaction, admin-only manual ajuste, anulación inverse movement, cantidad-as-sum-of-movimientos invariant, POS read access |
| `consumo-cuenta-corriente` | `openspec/specs/consumo-cuenta-corriente/spec.md` | 6 requirements / 8 scenarios: movimiento schema at rest, consumo written inside the sale transaction, credit-limit evaluation, anulación contramovimiento, saldo-as-maintained-cache invariant, explicit no-reliquidación/no-CC-management/no-pagos-de-cuenta scope guard |
| `operacion-de-pos` | `openspec/specs/operacion-de-pos/spec.md` | 4 requirements / 10 scenarios: OperacionDePos policy (Vendedor + Supervisor + Admin, amended at JD), explicit idPuntoVenta with no server-side POS session, single cart-pricing path via ServicioDeOfertas batch resolution, checkout orchestration contract |

### Delta merges (MODIFIED/ADDED requirements folded into existing specs)

| Domain | Action | Details |
|---|---|---|
| `codigos-barra` | MODIFIED + ADDED | "Barcode Add/Remove/List Management" requirement replaced (listing now also reachable under `OperacionDePos`, two Vendedor scenarios split into blocked-write vs can-list); new "Scan Resolution Rule" requirement appended before "Tenant Isolation for codigos_barra" (6 scenarios: short/long code, `N*codigo`, re-scan sums, inactive articulo, unknown code) |
| `clientes` | MODIFIED | "Cliente Schema At Rest" requirement replaced (saldo is no longer frozen — moves inside sale/anulación transactions; new "Saldo moves only through a cuenta corriente sale or its anulación" scenario added); "Cliente ABM Lifecycle and Authorization" requirement replaced (listing/search opened to `OperacionDePos`; new "Vendedor can search clientes for checkout" scenario added) |
| `articulos` | MODIFIED | "Articulo ABM Lifecycle and Authorization" requirement replaced (listing/lookup opened to `OperacionDePos` for cart pricing; new "Vendedor can list/lookup articulos for the POS cart" scenario added) |
| `parametros-operativos` | ADDED | Two new requirements appended at end of file (no existing Tenant Isolation requirement to insert before): "tolerancia_pago and vuelto_maximo Are Server-Authoritative At Checkout" (2 scenarios) and "Read Access Under OperacionDePos For UI Preview" (2 scenarios) |
| `resolucion-de-ofertas` | ADDED | One new requirement appended at end of file: "OperacionDePos Authorization For POST /api/ofertas/resolver" (2 scenarios), closing the stage-4 verify carryover — resolution semantics unchanged |

## Implementation Summary

### 7 Chained PRs (Merged, stacked-to-main)

| PR | Slice | Title | Judgment-Day Rounds | Status |
|---|---|---|---|---|
| #38 | 1 | Auth policy & POS read-surface (`OperacionDePos`, re-gating 5 endpoint groups) | 2 | Merged 2026-08-04 |
| #40 | 2 | Numeración schema, atomic allocator & escaneo (`NumeracionDeComprobantesEtapa5`, `ParserDeEscaneo`, `ServicioDeEscaneo`) | 1 | Merged 2026-08-04 |
| #42 | 3 | Schema gate: comprobantes/stock/CC (`VentasStockYCuentaCorrienteEtapa5`, 6 tables, 3 enums, 4 pure Domain rule classes) | 1 | Merged 2026-08-04 |
| #44 | 4 | Checkout write path (`ServicioDeVentas.EmitirAsync`, decide-then-commit, `POST /api/ventas`) | 2 | Merged 2026-08-04 |
| #46 | 6 | Web: cart & scan (`Pos.tsx` skeleton, `carrito.ts` reducer) | 3 | Merged 2026-08-04 |
| #48 | 5 | Anulación & ajuste manual de stock (`ServicioDeVentas.AnularAsync`, `ServicioDeStock.AjustarAsync`) | 2 | Merged 2026-08-04 |
| #50 | 7 | Web: payment, checkout & ticket (`pagos.ts`, full `Pos.tsx` payment panel) | 4 | Merged 2026-08-04 |

**Delivery strategy**: chained PRs, stacked-to-main, per `protocolo-pr-solo-dev` and the
stage-3/4 precedent. Slices 1∥2 and 5∥6∥7 (structurally) ran with genuine independence
per the tasks.md dependency graph — Slice 6 depended only on Slices 1+2, not on the
backend Slices 3-5.

### Test Results (Final Suite)

| Suite | Count | Status |
|---|---|---|
| `Ways.Domain.Tests` | 271/271 | ✓ |
| `Ways.Application.Tests` | 209/209 | ✓ |
| `Ways.IntegrationTests` (real Postgres) | 391/391 (one transient Testcontainers flake, clean re-run green) | ✓ |
| Vitest (`src/Ways.Web`) | 165/165 | ✓ |
| TypeScript (`tsc -b`) / `oxlint` / `vite build` | clean | ✓ |
| EF migrations (`dotnet ef migrations has-pending-model-changes`) | clean | ✓ |

### Key Accomplishments

1. **`comprobantes-venta` / `stock` / `consumo-cuenta-corriente` (the checkout engine)**:
   - Decide-then-commit architecture: pricing, oferta resolution, parámetros, and
     payment validation resolved as pure reads/rules OUTSIDE the transaction against a
     pinned `momento`; the transaction itself only writes a pre-computed, immutable
     `PlanDeVenta` in a fixed statement order (numeración → comprobante → items → pagos
     → stock ascending `id_articulo` → CC).
   - No advisory locks (deviation from the original proposal, approved at the DB Change
     Gate) — every mutable row is written with a single atomic `UPDATE ... RETURNING` /
     `INSERT ... ON CONFLICT DO UPDATE ... RETURNING` statement that both takes its own
     row lock and returns the post-state.
   - `stock.cantidad` and `Cliente.Saldo` proven by test to always equal the sum of
     their respective ledgers (`movimientos_stock`, `movimientos_cuenta_corriente`).
   - Anulación reverses stock and CC in one transaction; no `restaurar` endpoint exists,
     killing the legacy's stock-inflation bug by design.

2. **`operacion-de-pos` (authorization + orchestration)**:
   - New `Politicas.OperacionDePos` policy, amended at judgment-day R1 of Slice 1 to
     include `Supervisor` (legacy parity `tipoUser IN (2,3,4)`) alongside Vendedor and
     Admin; Root excluded.
   - Group-gate-becomes-`OperacionDePos` pattern with `GestionDeCatalogo` stacked on
     writes (AND composition) across 5 endpoint groups; a `SuperficieDeAutorizacionTests`
     omission guard walks `EndpointDataSource` so a future write endpoint added without
     the stacked policy fails the test instead of silently shipping open.
   - Explicit `idPuntoVenta` per request — no server-side POS session state.

3. **Delta merges to 5 existing domains**: `codigos-barra` gained the scan-resolution
   rule as specified behaviour; `clientes` activated the previously-frozen `saldo`
   write path; `articulos` opened lookup to the POS policy; `parametros-operativos`
   made `tolerancia_pago`/`vuelto_maximo` server-authoritative; `resolucion-de-ofertas`
   closed the stage-4 verify carryover by relaxing `/resolver`'s authorization.

4. **Web: POS screen (`Pos.tsx`)**:
   - Split across two PRs on the same file (Slice 6: cart + scan; Slice 7: payment +
     ticket + full checkout wiring) per the launch prompt's pre-authorized split.
   - Full `react-async-state` compliance across all 9 rules, notably rule 9
     (block every superseding action during the outstanding checkout POST, plus a
     first-line `if (cobrando) return` re-entrancy guard) — proven by a
     double-click-issues-exactly-one-POST component test.
   - Pure reducer `carrito.ts` and pure math module `pagos.ts`, both with colocated
     unit tests per `web-descriptor-tests`.

### Judgment-Day Rounds (Solo-Dev Review Protocol)

| Slice | Rounds | Key Findings | Status |
|---|---|---|---|
| 1 (auth policy) | 2 | R1 CRITICAL (catalogos-fiscales ungated, triple doc/code disagreement) + Supervisor added by orchestrator decision + Root-403/401 tests. R2 CRITICAL (shipped React front still routed Root into the now-403 fiscales page → permanent error; front gates narrowed to Admin). | APPROVED |
| 2 (numeración + escaneo) | 1 | Clean of code CRITICALs. Real WARNING (merge order — Slice 1 must land first) resolved structurally; suggestions closed (comment placement, dead code dropped, cross-tenant scan test). | APPROVED |
| 3 (schema gate) | 1 | Merged into PR #42 alongside Slice 4's checkout work; no CRITICALs recorded against the schema/domain/rules layer. | APPROVED |
| 4 (checkout write path) | 2 | Merged as PR #44. Biggest slice of the stage; atomicity/concurrency/budget/snapshot/parity tests all green. | APPROVED |
| 5 (anulación + ajuste) | 2 | R1 CRITICAL (ajuste double-apply under ambiguous-commit retry) → shared `EstrategiaSinReintento` no-retry posture for ajuste+anulación, EF reference pre-checks closing raw-ADO 500s. R2 dedup to Abstracciones + anti-regression test. | APPROVED |
| 6 (web cart + scan) | 3 | Merged as PR #46, parallel to Slices 3-5. | APPROVED |
| 7 (web payment + ticket) | 4 | R1 2 CRITICALs (Cobrar clickable in the resolver window; duplicate-medio Map collapse misattributing vuelto). R2 availability-contract reconciliation (failed preview allows checkout, server authoritative, Reintentar action). R3 failed-preview math against synthetic total 0 suppressed. R4 clean with proofs. | APPROVED |

**Total Rounds**: 15 (2 + 1 + 1 + 2 + 2 + 3 + 4). **All Clean.**

### DB Change Gate Summary

**The largest gate of the project so far** — approved 2026-08-04 ("dale nomas"), presenting
BOTH migrations together for ONE approval, grouped by write path:

- **Write path A (Emisión)**: `numeraciones_comprobante` (Slice 2's migration,
  `NumeracionDeComprobantesEtapa5`) + `comprobantes_venta` / `items_comprobante_venta` /
  `pagos_comprobante` (Slice 3's migration, `VentasStockYCuentaCorrienteEtapa5`)
- **Write path B (Stock)**: `stock` + `movimientos_stock`
- **Write path C (Cuenta corriente)**: `movimientos_cuenta_corriente`
- **3 enums** (correction to the proposal's count of 2): `estado_comprobante`,
  `motivo_stock`, `tipo_movimiento_cc`
- RLS (`HabilitarRlsDeTenant`) enabled on all 7 tables in the same migration that
  creates each of them
- **8 bundled assumptions confirmed at the gate**: sales without turno de caja
  (`id_turno_caja` always NULL); `OperacionDePos` AND-composition with the two
  inverted Vendedor tests; explicit `idPuntoVenta` with no `asignaciones_empleado`
  table; the narrow CC slice with the `LimiteCredito`/`CreditoIlimitado` model;
  anular-never-restaurar with TX/NCX only and optional `id_comprobante_asociado`;
  admin-only manual ajuste; recargo por medio de pago staying dormant; and the
  no-advisory-locks decide-then-commit deviation with the pinned statement order.
- Migration clean: `dotnet ef migrations has-pending-model-changes` → no pending changes
- `movimientos_stock.id_comprobante_compra` deferred to stage 8 (deviation recorded
  in `docs/10-modelo-de-datos.md` in the Slice 3 PR — the FK target table does not
  exist yet)

### Deviations From Original Plan (All Documented)

1. **No advisory locks in the sale** (design decision 1) — every mutable row uses a
   single atomic `UPDATE ... RETURNING` / `INSERT ... ON CONFLICT DO UPDATE ...
   RETURNING` statement instead of `pg_advisory_xact_lock`. Declared and approved at
   the DB Change Gate.
2. **`Supervisor` added to `OperacionDePos`** (judgment-day R1, Slice 1) — legacy
   parity (`tipoUser IN (2,3,4)`); catalog writes remain Admin-only.
3. **`id_empleado` uses a simple FK to `usuarios.id_usuario`**, not the composite FK
   design originally specified — the alternate key on `Usuario` would have forced
   `IdTenant NOT NULL`, corrupting the platform-staff NULL sentinel (verified by
   scaffold at judgment-day R1 of Slice 3).
4. **`EstrategiaSinReintento` no-retry posture** for anulación/ajuste (judgment-day
   R1, Slice 5) — closes an ambiguous-commit double-apply CRITICAL found under
   execution-strategy retry.
5. **Availability contract reconciled** (judgment-day R2, Slice 7) — a failed
   client-side preview still allows checkout to proceed; the server remains the
   sole price authority, with a `Reintentar` UI action and a dedicated
   `generacionCobroRef` generation guard.
6. **`movimientos_stock.id_comprobante_compra` deferred to stage 8** — the FK target
   table (`comprobantes_compra`) does not exist yet; the column is created without
   its FK, documented as a declared deviation from doc 10 §6.
7. **Numeración committed in its own prior transaction, not inside the sale
   transaction** (reconciled into design.md at verify, originating from
   judgment-day R1 of Slice 4) — a failed sale therefore leaves a real número gap,
   and the pre-committed número serves as the ambiguous-commit idempotency key.
8. **Rendezvous-deviation doc-comment** and the stock spec's advisory-lock wording
   were stale relative to shipped code — both reconciled into the spec/design before
   archive (see verify-report Compliance section).

### Specification Coverage

All requirements across the 4 new domain specs plus the 5 delta requirements map to
implemented behavior with passing tests:
- `comprobantes-venta`: 9 requirements / 24 scenarios
- `stock`: 6 requirements / 12 scenarios
- `consumo-cuenta-corriente`: 6 requirements / 8 scenarios
- `operacion-de-pos`: 4 requirements / 10 scenarios
- `codigos-barra` delta: 1 MODIFIED + 1 ADDED requirement / 11 scenarios total
- `clientes` delta: 2 MODIFIED requirements / 7 scenarios total
- `articulos` delta: 1 MODIFIED requirement / 3 scenarios
- `parametros-operativos` delta: 2 ADDED requirements / 4 scenarios
- `resolucion-de-ofertas` delta: 1 ADDED requirement / 2 scenarios

**Spec Compliance**: verified PASS. All scenarios traced to passing runtime evidence
by `sdd-verify` (0 CRITICAL). One explicit pending item, non-blocking (see below).

### Pending Gate Item (Explicit, Non-Blocking, Carried Forward)

`ck_pagos_comprobante_importe_no_negativo` — a schema defense-in-depth CHECK constraint
awaiting the user's DB micro-gate. Domain rules 0/0b in `ValidadorDePagos` already close
every reachable path today; a TODO is recorded in `ValidadorDePagos.cs`. Not blocking
for this stage's archive; the next database-touching change should surface this for the
user's explicit decision.

### Known Carryovers (Non-Blocking, Flagged for Later Stages)

- **`ck_pagos_comprobante_importe_no_negativo` micro-gate** (above) — needs an explicit
  user yes/no at the next DB Change Gate opportunity.
- **Turnos de caja requirement wiring** (stage 6) — `comprobantes_venta.id_turno_caja`
  is nullable and currently always written NULL; stage 6 (turnos/arqueos/tesorería)
  will tighten this into a real requirement. No schema change needed — the column was
  already nullable in doc 10.
- **Cuenta corriente management, reliquidación a precio del día (F4), pagos de cuenta,
  CC UI/reporting** (stage 7) — stage 5 shipped only the narrow write-only slice
  (`consumo` + `contramovimiento`); full CC management remains stage 7's scope.
- **Comprobantes de compra + `movimientos_stock.id_comprobante_compra` FK** (stage 8) —
  the column exists without its FK; stage 8 must add both the `comprobantes_compra`
  table and the deferred FK together.
- **Recargo por medio de pago stays dormant** — `medios_pago.recargo_porcentaje`
  exists in the schema (confirmed at the DB Change Gate) but is not applied anywhere;
  applying it is a future product decision, not a parity detail.
- **`PPPP` in the visible numero has no real business number** — `puntos_venta` has no
  `numero` column yet; stage 5 zero-pads the global `id_punto_venta`, which will
  exceed four digits with enough tenants. Harmless while TX/NCX stay non-fiscal;
  fiscal invoicing will need a real per-empresa `puntos_venta.numero` plus a backfill.
  Flagged in design.md Open Questions, not built.
- **Carried from stage 4, now resolved**: the `/resolver` Admin-gated authorization
  carryover is closed by this stage's `resolucion-de-ofertas` delta (decision 2).
  Still OPEN and unrelated to this stage: the `ServicioDeArticulos`
  `articulos_empresas` replace-set concurrency gap recorded as a spawned follow-up
  during stage-4 slice 2 — remains a candidate for a future dedicated fix.

### Process Notes (Solo-Dev Review Protocol, Notable Patterns This Stage)

- **No-retry posture adopted for ajuste/anulación**: `EstrategiaSinReintento` was
  introduced specifically because EF Core's `CreateExecutionStrategy` retry mechanics
  interact badly with raw-ADO conditional writes on ambiguous commit — a pattern now
  established for any future write path with the same shape (conditional UPDATE +
  side-effecting inserts, no idempotency key).
- **Gap semantics honesty**: the spec's "a rolled-back sale leaves an accepted gap"
  scenario only holds true via the SALE transaction's execution-strategy retry
  mechanics, not via a plain single-statement rollback of the numeración allocator in
  isolation (a clean pre-commit rollback of the allocator alone REUSES the number).
  This distinction, first surfaced honestly during Slice 2's judgment-day, shaped
  Slice 4's atomicity test design and was later reconciled fully into design.md at
  verify: numeración is now committed in its own prior transaction, and the
  pre-committed número doubles as the ambiguous-commit idempotency key.
- **Decide-then-commit as the stage's central architectural discipline**: every
  money-relevant decision (pricing, oferta resolution, parámetros, payment
  validation, CC gating) runs as pure reads/rules OUTSIDE the retryable transaction
  lambda, against a pinned `momento`, producing an immutable `PlanDeVenta` that the
  transaction only writes. This is what makes the atomicity tests meaningful and
  keeps a retry from ever charging a customer an amount nobody validated.
- **Availability-contract reconciliation** (Slice 7, judgment-day R2-R3): a genuinely
  new pattern for this stage — a client-side price preview is allowed to fail
  without blocking checkout, because the server remains the sole authority; this
  required a dedicated generation-ref (`generacionCobroRef`) distinct from the
  cart's resolution generation ref, to avoid conflating "preview failed" with
  "checkout in flight."

### Next Stage

Stage 6 (turnos de caja / arqueos / movimientos de caja / tesorería / gastos, per
doc 10 sequence) can start. The project now has a complete, auditable sale-and-return
engine: checkout, anulación, and manual stock adjustment all write through an
append-only ledger with proven cache invariants, ready for stage 6 to layer turno
enforcement, cash-drawer reconciliation, and treasury movements on top.

---

**Archive completed**: 2026-08-04
**Change archived to**: `openspec/changes/archive/2026-08-04-stage-5-pos-ventas/`
**Specs merged**: 4 new domains + 5 delta-merged domains in `openspec/specs/`
**Verification**: PASS — 0 CRITICAL issues; 1 explicit non-blocking pending gate item
carried forward (`ck_pagos_comprobante_importe_no_negativo`).
**SDD Cycle**: COMPLETE
