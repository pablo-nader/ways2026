# ofertas-por-familia

## Objective

An oferta can target a familia de artículos, in addition to an article, a grupo or a categoría.

## Problem

Ofertas are scoped by exactly one of `id_articulo`, `id_grupo`, `id_categoria`. Familias group
the articles that share price and attributes, which is the natural unit for a promotion, but
there is no way to target one. Requested by the owner on 2026-10-10.

## Approved model (database gate passed 2026-10-10, owner: "aprobado el modelo de ofertas")

One migration, table `ofertas` only, mirroring how grupo is modelled:

- `id_familia integer NULL`.
- `ck_ofertas_alcance_exclusivo` becomes
  `num_nonnulls(id_articulo, id_grupo, id_categoria, id_familia) = 1` (same name).
- `fk_ofertas_familia (id_familia, id_tenant)` → `familias (id_familia, id_tenant)`, Restrict.
- `ix_ofertas_familia (id_familia, id_tenant)`.
- Tenancy/RLS unchanged. No backfill.

Behaviour stated to the owner with the model and not objected:

- Precedence unchanged: only `prioridad` / `acumulable`; no automatic artículo > familia > grupo.
- Dissolving a familia referenced by any oferta is blocked with 409, like a grupo in use.
- Membership is dynamic: the oferta follows `articulos.id_familia` at resolution time; issued
  sales do not change.

## Scope

Backend (domain, application, infrastructure, API, docs, tests) and the Ofertas web screen
(radio "Familia" + familia select). Out of scope: POS bundle/desktop (the POS receives
resolved prices; no release needed), precedence by specificity, `activo = false` familias
(not checked, same as grupos today).

## Configuration

- TDD: not configured → ordinary functional checks; every task ships its tests.
- Review: receipt-driven development off (clone-local). PR gate: judgment-day.
- Delivery: stacked on `pnader/familias-carga-asociacion-a5ad83`; ask-on-risk.

## Tasks

- [ ] O1 — Backend: migration + entity/config, domain rule and resolver, service (create,
  update, existence check, candidate query, projection), dissolve guard, error text, doc 10
  and specs, tests (domain, application, integration incl. CHECK/FK backstops by SQLSTATE and
  the missing DB-level resolution test for grupo and familia). Route: delegated (writer).
- [ ] O2 — Web: Ofertas screen and mappers (`AlcanceOferta`, `aAltaOferta`, `aValoresOferta`),
  types, tests. Route: delegated (writer).
- [ ] O3 — Judgment-day per slice, then PRs. Route: delegated judges.

## Progress

Created 2026-10-10. Branch `pnader/ofertas-por-familia`.

- O1 implemented — commit `87ec0041` (28 files; +966/−52 outside the generated migration
  files). Migration `20261010191731_OfertasPorFamilia`, exactly the approved model. Contract:
  `idFamilia` on `OfertaListado`/`AltaOferta`/`EdicionOferta`; 400 `oferta_alcance_invalido`
  ("…exactamente un artículo, grupo, categoría o familia."); 400 `referencia_invalida` for a
  missing/soft-deleted/other-tenant familia; `DELETE /api/familias/{id}` → 409 `familia_en_uso`
  when any oferta (live or soft-deleted) references it. Observed (writer): build 0 errors /
  0 warnings; Domain 1032; Application 767; integration filtered 505; FULL integration
  3095/3095. Parent spot check: Domain 1032/1032; `FamiliasDisolverConOfertasTests` +
  `OfertasCheckBackstopTests` 17/17. Migration `Down` never executed (no migration tests).
  Not checked off: judgment findings below are open.
- Judgment-day, O1, round 1: 0 CRITICAL from either judge. Confirmed by both:
  K1 — `openspec/specs/ofertas/spec.md:52-58` claims a soft-deleted familia ends in 23503; false,
  the row exists and the FK passes, only the service pre-check rejects it (both WARNING).
  K2 — unlocked familia pre-check in `ServicioDeOfertas` vs a concurrent dissolve can leave a
  live oferta on a dissolved familia (inert); same residual as grupos;
  `BloquearSiEstaVivaAsync` exists and is unused; doc 10 says "bloqueado" without the residual
  (A WARNING, B SUGGESTION). K3 — `Down` aborts with 23514 once familia ofertas exist (both
  SUGGESTION). K4 — the structural-net lookbehind exempts every field for a receiver named
  `oferta`, not only `IdFamilia` (both SUGGESTION). One judge only: the guard's position after
  the FOR UPDATE is not pinned by `ServicioDeFamiliasPosicionDeLocksTests`; `Contratos.cs`
  summary still says "las tres columnas". Judges ran no tests and read few SKILL.md files.
  Fix round: awaiting the owner.
- O2 in progress (web writer; also carries the pending d2 test fix of familia-desde-articulo).

## Next step

Owner decision on the O1 fix round; then O2 verdict and PRs.
