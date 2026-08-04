# Archive Report — Stage 4: Ofertas

**Archived**: 2026-08-04
**Change**: `stage-4-ofertas`
**Status**: COMPLETE AND CLOSED
**Verification**: PASS (verdict 2026-08-04, HEAD `f3505f4`)

## Executive Summary

Stage 4 is complete, verified, and archived. All 4 chained PRs have merged to main
(#30, #32, #34, #36). Delta specs have been merged into the main spec repository.
Two new capability domains created (`ofertas`, `resolucion-de-ofertas`); one
modified (`precios`, ADDED-only delta).

## Artifacts Archived

| Artifact | Path | Status |
|---|---|---|
| Proposal | `proposal.md` | Complete |
| Design | `design.md` | Complete |
| Specifications | `specs/` | Complete (3 domains: 2 new, 1 delta) |
| Tasks | `tasks.md` | Complete (4 slices, 42/42 tasks) |
| Verification Report | `verify-report.md` | PASS |
| State | `state.yaml` | Updated, archived |

## Specifications Merged to Main Specs

| Domain | Status | Details |
|---|---|---|
| `ofertas` | **NEW** → `openspec/specs/ofertas/spec.md` | Full spec, 6 requirements / 21 scenarios (schema + dual exclusivity CHECKs, vigencia windows, cantidad_minima trigger, multi-lista targeting via ofertas_listas, ABM lifecycle/authorization, tenant isolation) |
| `resolucion-de-ofertas` | **NEW** → `openspec/specs/resolucion-de-ofertas/spec.md` | Full spec, 5 requirements / 16 scenarios (batch input shape, candidate matching incl. categoria ancestor-chain, base selection + tie-breaks, additive-over-original stacking, applied-ofertas reporting with no-writes guarantee) |
| `precios` | **MODIFIED (delta ADDED-only)** → `openspec/specs/precios/spec.md` | Merged 1 new requirement, "Batch Current-Price Resolution" (3 scenarios), inserted before the existing "Tenant Isolation for precios" requirement — same insertion convention used for stage-3's listas-precio-minimal delta merge. Every pre-existing requirement (Price History Never Overwrites, Programmable Future Prices, Current-Price Query Semantics By Date, Derived List Price Resolution At Read Time, Margin-Based Price Suggestion, Tenant Isolation for precios) is untouched. |

## Implementation Summary

### 4 Chained PRs (Merged, stacked-to-main)

| PR | Slice | Title | Tests (D/A/I) | Status |
|---|---|---|---|---|
| #30 | 1 | Schema + domain foundation (`ofertas`/`ofertas_listas`, `ReglaDeOfertas`, `ClasificarCheck` backstop classifier) | 121/190/232 | Merged 2026-08-03 |
| #32 | 2 | CRUD/ABM service + API (`ServicioDeOfertas`, `ofertas_listas` replace-set, race protection) | 121/204/256 | Merged 2026-08-03 |
| #34 | 4 | Web: dedicated Ofertas screen (`Ofertas.tsx`, `api/ofertas.ts` mappers, ~1.65k lines) | (57/57 vitest) | Merged 2026-08-04 (parallel with PR #36) |
| #36 | 3 | Resolution engine (`ResolvedorDeOfertas`, batch price path, `/resolver` endpoint) | 162/207/268 | Merged 2026-08-04 (parallel with PR #34) |

**Delivery strategy**: chained PRs, stacked-to-main, per `protocolo-pr-solo-dev` and the
stage-3 precedent. Slices 3 and 4 built in parallel on isolated worktrees, both depending
only on Slice 2's CRUD API (the Ofertas form never calls `/resolver`).

### Test Results (Final Suite)

| Suite | Count | Status |
|---|---|---|
| `Ways.Domain.Tests` | 162/162 | ✓ |
| `Ways.Application.Tests` | 207/207 | ✓ |
| `Ways.IntegrationTests` (real Postgres) | 268/268 | ✓ |
| Vitest (`src/Ways.Web`) | 57/57 | ✓ |
| TypeScript (`tsc -b`) / `oxlint` / `vite build` | clean | ✓ |
| EF migrations (`dotnet ef migrations has-pending-model-changes`) | clean | ✓ |

### Key Accomplishments

1. **`ofertas` (catálogo-scoped, `id_empresa NULL` = tenant-wide)**:
   - Dual exclusive-group schema: scope (articulo | grupo | categoria) and benefit
     (precio_unitario | porcentaje | importe_fijo), each backed by a `num_nonnulls = 1`
     CHECK plus a pure Domain guard (`ReglaDeOfertas`) that makes the CHECKs
     defense-in-depth only.
   - Vigencia windows (fecha/hora/dias_semana, inclusive both ends), `cantidad_minima`
     trigger, `prioridad`/`acumulable`/`activo`.
   - `ofertas_listas` junction **deviates from doc 10** (single `id_lista_precio NULL`
     column replaced by a multi-lista junction) — the deviation is documented inline in
     `docs/10-modelo-de-datos.md`, shipped in the same PR as the migration.
   - Concurrency: `pg_advisory_xact_lock(idTenant, idOferta)` serializes
     `ActualizarAsync`/`EliminarAsync` — strict 2×200 races, PUT↔DELETE ghost-edit
     rendezvous test, `DbUpdateConcurrencyException` → 409 `edicion_concurrente` as
     generic defense in depth.

2. **`resolucion-de-ofertas` (pure, DB-free rule engine + batch service)**:
   - `ResolvedorDeOfertas` — pure static resolver: base = highest-`prioridad`
     non-acumulable candidate (tie-break: greater discount, then lower `id_oferta`);
     every matching `acumulable = true` candidate stacks **additively over the original
     resolved price**; combined discount clamped to `[0, original]`.
   - Categoria scope matching walks the ancestor chain (depth ≤ 3), so an oferta on a
     parent categoria reaches articulos in its subcategorias.
   - Batch-first: **7 constant queries per resolution call**, independent of N articles
     × M listas — proven via `DbCommandInterceptor` (identical count at N=2 and N=20).
   - `POST /api/ofertas/resolver`, query-only, no-writes guarantee proven by row-count
     diff across all affected tables.
   - Canonical arithmetic proven end-to-end: $1000 base −20% + acumulable −10% → $700;
     100% clamp; derivada-lista-as-original-base ($180 base → $162 final, never
     recomputed against the underlying $200 base-lista price).

3. **`precios` batch current-price path (additive, stage-3 semantics untouched)**:
   - `ServicioDePrecios.PreciosVigentesEnLoteAsync` — new method, never a rewrite of
     `PrecioVigenteAsync`/`PreciosVigentesAsync`, which keep their exact signatures,
     semantics, and documented `Activo` divergence.
   - Closes the deliberate N+1 flagged in `PreciosVigentesAsync`'s INFO doc-comment,
     ahead of stage 5's POS depending on it.
   - Parity proven: batch == single for the same inputs (fija, derivada, missing price,
     inactive lista), asserted per pair in one test.

4. **Web: dedicated Ofertas screen**:
   - `src/Ways.Web/src/paginas/Ofertas.tsx` + pure mapping helpers in
     `src/api/ofertas.ts` (design decision 9 — keeps `web-descriptor-tests` applicable).
   - First stage-4 slice built `react-async-state`-compliant from day one (rule 9:
     block supersede-during-write; rule 5: per-entity busy flags) — nearly clean at
     judgment-day R1.
   - Descriptor/component unit tests shipped per `web-descriptor-tests` (vitest infra
     landed in PR #28; the stage-3 smoke-only waiver no longer applied to this stage).

### Judgment-Day Rounds (Solo-Dev Review Protocol)

| Slice | Rounds | Key Findings | Status |
|---|---|---|---|
| 1 (schema) | 2 | R1: 1 WARNING (window-order validation missing from `ReglaDeOfertas`) → `ValidarVentana` added, 9 unit tests, error-code parity. R2: B clean, A docs-only. | APPROVED |
| 2 (CRUD/ABM) | 3 | R1 CRITICAL (unlocked replace-set: lost update / untranslated 500) → advisory lock + generic concurrency arm. R2 CRITICAL (PUT↔DELETE ghost edit) → lock+tx in `EliminarAsync`, post-lock liveness re-check, rendezvous test. R3 clean (B caught an uncommitted fix from R2, landed before PR). | APPROVED |
| 3 (resolution engine) | 3 | R1: null-`Lineas` NRE→500 + untested paths → fixed. R2: judge A disproved the fix with an isolated repro (`SetsRequiredMembers` skips STJ required-checks) → explicit `lineas_requeridas` contract added. R3 clean ×2. | APPROVED |
| 4 (web screen) | 1 + fix | R1: B clean; A 1 real WARNING (rule-9 supersede path untested) → test added, orchestrator-verified inline (stage-3 test-only precedent). | APPROVED |

**Total Rounds**: 9 (2 + 3 + 3 + 1). **All Clean.**

### DB Change Gate Summary

One combined migration `OfertasEtapa4`:
- 2 new tables: `ofertas` (catálogo scope, `id_tenant` NOT NULL, `id_empresa NULL`),
  `ofertas_listas` (junction, tenant-scoped, PK-only, no soft-delete)
- 4 CHECKs: `ck_ofertas_alcance_exclusivo`, `ck_ofertas_beneficio_exclusivo`,
  `ck_ofertas_ventana_valida`, `ck_ofertas_dias_semana`
- Composite FKs hand-named in snake_case per doc 10; `pk_ofertas_listas` named
  explicitly (fixes the `PK_articulos_empresas` naming inconsistency)
- RLS enabled on both tables in the same migration
- **DEVIATION documented**: `ofertas_listas` replaces doc-10's single
  `id_lista_precio NULL` column — `docs/10-modelo-de-datos.md` updated in the same PR
- No backfill/seed needed (all additive)
- Migration clean: `dotnet ef migrations has-pending-model-changes` → no pending changes

### Deviations from Original Plan (All Documented)

1. **`ofertas_listas` junction replacing `id_lista_precio`** (user decision 4,
   pre-approved at proposal time) — `docs/10-modelo-de-datos.md` updated in the schema PR.
2. **`ValidarVentana` added to `ReglaDeOfertas`** at judgment-day R1 (Slice 1) — the
   window-order CHECK is now genuinely defense-in-depth, closing a gap in the original
   "all four CHECKs unreachable" claim.
3. **Spec-pinned CHECK error codes win over design's draft names** — `tasks.md`'s
   Orchestrator Decision 1 resolved `oferta_alcance_invalido`/`oferta_beneficio_invalido`
   (spec) over `alcance_de_oferta_invalido`/`beneficio_de_oferta_invalido` (design draft);
   confirmed in shipped code.
4. **`POST /api/ofertas/resolver` ships Admin-gated** (`GestionDeCatalogo`) — no
   POS-facing policy exists yet. **Non-blocking carryover for stage 5**: the POS flow
   (likely `Vendedor`) needs access before checkout can call this endpoint. Recorded in
   `design.md` Open Questions to avoid rediscovery.

### Specification Coverage

All requirements across the 2 new domain specs plus the 1 delta requirement map to
implemented behavior with passing tests:
- `ofertas`: 6 requirements / 21 scenarios (schema, vigencia, cantidad_minima,
  multi-lista targeting, ABM lifecycle/authorization, tenant isolation)
- `resolucion-de-ofertas`: 5 requirements / 16 scenarios (batch input shape, candidate
  matching, base selection/tie-break, additive-over-original stacking, no-writes
  guarantee)
- `precios` delta: 1 requirement / 3 scenarios (batch current-price resolution,
  existing single-articulo methods unaffected)

**Spec Compliance**: 100%. Every scenario has a passing covering test, spot-checked
against real code by `sdd-verify` (0 CRITICAL, 0 WARNING, 0 SUGGESTION).

### Known Carryovers (Non-Blocking, Flagged for Stage 5)

- **`/resolver` Admin-gated authorization** — stage 5 MUST revisit this policy before
  wiring the POS checkout flow, or requests will hit 403s (design.md Open Questions).
- **Timezone for `hora_desde/hasta`/`dias_semana` matching** — v1 uses server-local
  time; no tenant timezone modeled anywhere yet (`ParametroConocido` is the natural
  future home). Flagged, not blocking.

### Next Stage

Stage 5 (`comprobantes`/pagos/stock, per doc 10 sequence) can start. The project now
has a complete pricing + offers engine ready for the selling/POS flow: `precios`
resolves the base price, `ofertas` resolves the final selling price, both batch-first
and constant-query-count.

---

**Archive completed**: 2026-08-04
**Change archived to**: `openspec/changes/archive/2026-08-04-stage-4-ofertas/`
**Specs merged**: 2 new domains + 1 delta-merged domain in `openspec/specs/`
**Verification**: PASS — no CRITICAL issues, no blockers.
**SDD Cycle**: COMPLETE
