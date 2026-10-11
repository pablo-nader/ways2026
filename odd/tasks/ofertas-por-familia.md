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

- [x] O1 — Backend: migration + entity/config, domain rule and resolver, service (create,
  update, existence check, candidate query, projection), dissolve guard, error text, doc 10
  and specs, tests (domain, application, integration incl. CHECK/FK backstops by SQLSTATE and
  the missing DB-level resolution test for grupo and familia). Route: delegated (writer).
- [x] O2 — Web: Ofertas screen and mappers (`AlcanceOferta`, `aAltaOferta`, `aValoresOferta`),
  types, tests. Route: delegated (writer).
- [ ] O3 — Judgment-day per slice, then PRs. Route: delegated judges.

## Progress

Created 2026-10-10. Branch `pnader/ofertas-por-familia`.

- O1 implemented — commit `87ec0041` (28 files; +966/−52 outside the generated migration
  files). Migration `20261011024117_OfertasPorFamilia`, exactly the approved model. Contract:
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
- O2 implemented — commit `bcdddd8c` (6 files, +355/−56): radio "Familia", select "Familia
  objetivo", mappers, option builder labelling inactive/empty familias, listing text, load
  failure notice, pin test for the dissolve 409. Observed (writer): `npx tsc -b` OK;
  `npx oxlint --quiet` exit 0; touched suites 103 passed; full `npx vitest run` 159 files /
  3949 tests. Parent spot check: ofertas + Ofertas + Familias + ArticulosFamilias suites
  261/261. Not checked off: findings below are open.
- Also on this branch, belonging to familia-desde-articulo: `3cc69c64` (test-only) — real
  mutation showed no product hole; d2's comment narrowed, a full re-propose sequence test
  added; the `invalidar` bump is killed by the hook test in `usePrevisualizacion.test.ts`.
- Judgment-day, O2, round 1: 0 CRITICAL from either judge. Confirmed by both (non-severe):
  L1 — the `Familias.test.tsx` pin test is named for `familia_en_uso` but proves only "the
  server message of a rejected DELETE is shown"; L2 — editing a familia oferta with the
  familias list failed shows "Elegir…" while the value is kept (payload correct, display
  misleading; the notice copy overstates). One judge only: L3 — `idFamilia !== null` is true
  for `undefined`, so a web deployed before the backend would open categoría ofertas as
  "Familia" and list "Familia: undefined" (deploy-skew; one-line fix); no test of precedence
  with two non-null scopes; no `GUIA_POR_CODIGO` hint for `familia_en_uso`; radios without
  fieldset/legend (pre-existing). Fix round: awaiting the owner.
- Owner decision 2026-10-10: K2 race stays documented as a follow-up ("dejala documentada").
- Fix round 1: `9665edc6` (backend/docs/tests: K1, K2 doc, K3 note, K4 regex narrowed, K5
  guard position pinned, K6) and `c80af516` (web: L1 rename, L2 fallback option "Familia #N
  (no disponible)" + truthful notice, L3 `!= null` + precedence tests). Observed (fix actor):
  build 0/0; Domain 1032; Application 770; integration filtered 417; `npx tsc -b` and
  `npx oxlint --quiet` clean; full `npx vitest run` 159 files / 3952 tests.
- Scoped re-judgment 1: both judges — every item RESOLVED, 0 CRITICAL, no fix-caused code
  defect. Confirmed by both: stale "tres columnas" comment left in `api/tipos.ts` (K6
  residue). One judge: doc 10 said the residual is pending "para todos los catálogos" while
  article writers already close it. Both corrected by the parent as wording-only commits
  `7cd7888c` and `03a3bd6e` (`npx tsc -b` + `npx oxlint --quiet` clean); verified by readback
  against `GuardaDeReferencias.cs:46-52`, NOT re-judged. Left as info: "(no disponible)"
  flashes while familias are still loading; `textoAlcance` fix has no test of its own; grupo
  and categoría selects have the same orphan-option gap (pre-existing).
- Verdict: APPROVED for both slices, pending only the owner's delivery decision.
- Delivery authorized by the owner 2026-10-10 ("si, dale y autocompletalos todos"): four PRs
  stacked to main with auto-merge. main had moved (#357, #358, #359), so the work was rebuilt
  by cherry-pick onto `a8be9491` as `pnader/familia-desde-articulo-backend`,
  `pnader/familia-desde-articulo-web`, `pnader/ofertas-por-familia-backend`,
  `pnader/ofertas-por-familia-web`. Changes relative to the judged code: `AltaArticulo` keeps
  main's `CostoListaPor`/`CostoListaIncluyeIva` and then `NombreFamiliaNueva` last; the test
  helper in `ArticulosAltaConFamiliaNuevaTests.cs` adapted to #359 (no `CostoNominal` on the
  alta); migration regenerated on main's model as `20261011024117_OfertasPorFamilia` with
  identical Up/Down (`has-pending-model-changes`: none). NOT re-judged after the rebase.
  Observed on the final tip (writer): build 0 errors on all four tips; Domain 1077;
  Application 779; FULL integration 3142/3142; `npx oxlint --quiet` clean; full
  `npx vitest run` 163 files / 4039 tests.
- Known after the rebase: #359 made the backend compare 15 shared familia fields while the
  web still models 13 ("trece campos" copy, incl. the new join warning); main's own web
  slice has to close that.
- Product note raised to the owner: with OD4 strict, a familia that ever had an oferta
  (even a soft-deleted one) can never be dissolved.

## Next step

O3: PRs (owner decides push/PR/merge). Four slices: familia backend, familia web, ofertas backend, ofertas web.
