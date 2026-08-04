# Verify Report — stage-4-ofertas

**Verdict**: PASS (0 CRITICAL, 0 WARNING, 0 SUGGESTION)
**HEAD verified**: `f3505f4`. All 4 slice PRs merged: #30 (schema), #32 (CRUD/ABM), #34 (web screen), #36 (resolution engine).
**Date**: 2026-08-04

## Completeness

42/42 tasks complete across 4 slices; every `[x]` traced to real, tested code.

## Build & Tests

| Suite | Result |
|---|---|
| Ways.Domain.Tests | 162/162 |
| Ways.Application.Tests | 207/207 |
| Ways.IntegrationTests (real Postgres) | 268/268 |
| Vitest (src/Ways.Web) | 57/57 |
| `tsc -b` / `oxlint` / `vite build` | clean |
| `dotnet ef migrations has-pending-model-changes` | clean |

## Spec Compliance

All scenarios across `ofertas`, `resolucion-de-ofertas` and the `precios` delta have a
passing covering test, spot-checked to real code. Highlights: both CHECK-exclusivity
groups (raw-SQL 23514 backstops), inclusive vigencia windows + dias_semana,
multi-lista empty=all, ADR-8/authorization, RLS on both tables, categoria
ancestor-chain matching, precedence with both tie-breaks, additive-over-original
stacking (canonical $1000/−20%/−10% → $700 proven end-to-end; 100% clamp; derivada
base $162 exact), no-writes guarantee (row-count diff over 4 tables), batch==single
parity incl. the documented Activo divergence, constant query count (≤7, identical at
N=2 vs N=20 via DbCommandInterceptor), `lineas_requeridas` contract (absent/null →
400, `[]` → 200 empty).

## Design Coherence

All 9 decisions honored (dedicated service, pure resolver with pre-decomposed local
time, in-memory lista targeting, batch path added-not-rewritten, exact-name CHECK
classifier behind the `ck_ofertas_` prefix guard with spec-pinned codes, dedicated
screen with extracted pure mappers, no-unique-index exemption). Concurrency:
`pg_advisory_xact_lock(idTenant, idOferta)` serializes ActualizarAsync and
EliminarAsync (strict 2×200 races, PUT↔DELETE ghost-edit rendezvous test).

## Deviations — Confirmed Documented

1. `ofertas_listas` junction replacing doc-10's `id_lista_precio` — DEVIATION note in
   docs/10-modelo-de-datos.md, shipped with the schema PR.
2. `ValidarVentana` added at judgment-day R1 (window CHECK now defense-in-depth).
3. Spec-pinned CHECK codes over design's draft names — confirmed in code.
4. `/resolver` Admin-gated; stage 5 MUST revisit before wiring the POS (design.md
   Open Questions — non-blocking carryover).

## Notes

- Environment note: src/Ways.Web node_modules was stale (missing vitest/jsdom) before
  verification — npm install fixed it; not a code defect.

**Next**: sdd-archive.
