# Verify Report — stage-3-articulos-y-precios

**Verdict**: PASS
**HEAD verified**: `0a80fb0` (merge of PR #26, slice 5). All 6 slice PRs merged: #16, #18, #20, #22, #24, #26.
**Date**: 2026-08-03

## Completeness

All 6 slices complete; every tasks.md item checked and backed by real code/tests.

## Build & Tests

| Suite | Result |
|---|---|
| Ways.Domain.Tests | 86/86 |
| Ways.Application.Tests | 190/190 |
| Ways.IntegrationTests | 218/218 (real Postgres) |
| `npx tsc -b` | clean |
| `npx oxlint` | 1 pre-existing unrelated warning (AuthContext.tsx, predates this stage) |
| `npx vite build` | clean |
| `dotnet ef migrations has-pending-model-changes` | clean ("No changes have been made to the model since the last migration"), run with `--project src/Ways.Infrastructure --startup-project src/Ways.Infrastructure` (hosts the design-time factory) |

## Spec Compliance

All requirements across the four spec files (`articulos`, `codigos-barra`, `precios`,
`listas-precio-minimal`) map to implemented behavior with a passing covering test.
Representative evidence:

| Requirement | Evidence |
|---|---|
| articulos / codigo_interno autogen under concurrency | `AsignadorDeCodigoInternoArticuloConcurrenciaTests` |
| articulos / availability restriction | `ReglaDeArticulosTests` (5 cases) |
| codigos-barra / uniqueness race | 1×201+1×409 SQLSTATE-asserted integration test |
| codigos-barra / Add/Remove/List | `ArticulosEndpoints` GET + `ListarCodigosBarraAsync` + 4 integration tests (added in slice 5 by judgment-day demand; spec extended in the same slice) |
| precios / never overwrites, one pending, derived read-time resolution | `AbrirNuevoPrecioAsync` single write path, `ux_precios_vigente`, `ResolvedorDePrecios` pure fn |
| precios / margin suggestion precedence | `SugeridorDePrecioTests` |
| listas-precio / mode-switch, deactivation-while-base, derivada validation, es_default swap | `ServicioDeListasPrecio` guards + 18 unit cases + endpoint suite |
| Tenant isolation (all domains) | `ArticulosYPreciosRlsTests` + ADR-8 cross-tenant tests |

## Design Coherence

Dedicated `ServicioDeArticulos`/`ServicioDePrecios` vs `ServicioDeListasPrecio`
extending the generic catalog machine — as designed. Close-and-open history with
advisory-lock pair serialization. Backstop map (`_codigo_interno`, `codigos_barra`,
`_vigente` before the generic `_codigo` branch) verified directly in
`ManejadorDeErrores.cs`. One combined migration (`ArticulosYPreciosEtapa3`) plus the
gate-approved `PreciosVentanaValida` CHECK from judgment-day.

## Deviations (all confirmed documented)

1. Single `ListaPrecioAlta` contract (ADR-11) — tasks.md 4.1/4.2 + `Contratos.cs` doc-comment.
2. Margin suggestion folded into slice 2 — tasks.md "Slice Cut Rationale".
3. No dedicated `listasPrecio.ts` client — tasks.md 6.2.
4. Slice-5 `GET /api/articulos/{id}/codigos-barra` + spec extension — tasks.md 5.3, spec requirement renamed Add/Remove/List.
5. `listas-precio-minimal` superseded read-resolution clause dropped — inline spec note.

## Issues

- CRITICAL: none. WARNING: none blocking.
- SUGGESTION: `Ways.Web` still lacks unit-test infrastructure — recorded as spawned
  follow-up (vitest + descriptor/mapping tests + scope the orphan-option fallback),
  waived this stage per tasks.md's explicit smoke-verify criterion and stage-2 precedent.

**Next**: sdd-archive.
