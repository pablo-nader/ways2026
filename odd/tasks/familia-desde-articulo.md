# familia-desde-articulo

## Objective

Let an admin pick, create and join a familia directly from the article form, instead of
only from the Familias screens.

## Problem

Familias shipped (#332…#352) with their own screens, but the article form only offers a
familia on a new article, and only when an active non-empty familia already exists. An
existing article without a familia shows nothing, and a familia cannot be created from the
article. The owner looked for the feature in the article and did not find it.

## Scope

- Familia selector always visible in the article form (new and edit), even with zero familias.
- "+" button next to it that creates a familia by name, with the article as its reference.
- Choosing an existing familia on an existing non-member article: preview, confirm, join
  (the article takes the familia's 13 shared values and fixed-list prices).
- New article + new familia is created atomically by the server.

Out of scope: ofertas targeting a familia (separate feature, needs the database gate);
familia column/filter in the article grid; empty familias (unchanged: still not selectable).

## Constraints

- No schema change. Reuses `familias` and `articulos.id_familia`.
- Lock protocol of doc 10 (membership lock first, then article rows, then price pairs).
- Admin only (`Politicas.GestionDeCatalogo`), same door as today.
- Project skills apply: dto-contract-honesty, db-error-backstops, ef-retry-safe-writes,
  single-read-under-lock, react-async-state, web-descriptor-tests, web-test-data-gates,
  claims-match-code, mutation-proof-tests.

## Configuration

- TDD: not configured in the project (no source found) → ordinary functional checks; every
  task ships its tests (project testing policy).
- Review: receipt-driven development is off (clone-local). PR gate is judgment-day (CLAUDE.md).
- Delivery strategy: ask-on-risk. Forecast ~700–900 authored lines → likely two PRs
  (backend, web), stacked-to-main per project convention.

## Tasks

- [x] T1 — Backend: atomic "new article in a new familia". `AltaArticulo` accepts a new
  familia name (mutually exclusive with `idFamilia`); the article and its familia are
  created in one transaction, the article being the reference. Route: delegated (writer;
  touches contracts, service, endpoint docs, tests, doc 10).
  Checks: `dotnet build`, unit tests, integration tests for the articles/familias endpoints.
- [x] T2 — Web: selector always visible + "+" quick-create (`AltaRapidaFamilia`) for a new
  article (name travels with the alta) and for an existing non-member (single
  `POST /api/familias`). Route: delegated (writer; 4+ files).
  Checks: `npm run lint`, `npx tsc --noEmit`, `npx vitest run` on the touched suites.
- [x] T3 — Web: join an existing familia from the edit form with preview and confirmation.
  Route: delegated (writer). Checks: as T2.
- [ ] T4 — Judgment-day on each PR slice, then PR(s). Route: delegated judges.

## Progress

Created 2026-10-10.

- T1 done — commit `4c8f8669`. Contract: `AltaArticulo.nombreFamiliaNueva` (optional). With
  `idFamilia` → 400 `familia_existente_y_nueva`; blank → 400 `nombre_requerido`; >150 → 400
  `nombre_muy_largo`; name in use → 409 `familia_nombre_duplicado` (pre-check and 23505
  backstop), nothing persisted. Observed: build 0 errors; Domain 1020 passed (writer);
  Application 755 passed (writer); integration `ArticulosAltaConFamiliaNuevaTests` 14/14;
  integration Familia*/ArticulosEndpointsTests/Estructurales 373/373 (parent run).
  Review: RDD off (clone-local), not assessed; judgment-day pending in T4.
- Judgment-day, backend slice `4c8f8669`, round 1: judge A and judge B both 0 CRITICAL,
  0 WARNING, 3 SUGGESTION each → APPROVED, no fix round. Info only: the source-text position
  tests are a weak guard (the third does not pin the `SaveChangesAsync` between the familia
  insert and the id assignment; integration tests cover it); the two messages of the same
  409 `familia_nombre_duplicado` predate this change; one very long line in doc 10. Both
  judges judged the skills by code inspection without reading each SKILL.md.
- T2 + T3 done in ONE commit `e85aca04` (hunks interleaved; could not be split cleanly):
  12 files under src/Ways.Web/src, +1815/−74, ~1300 of them tests. Observed (writer):
  `npx tsc -b` exit 0; `npx oxlint --quiet` exit 0; `npx vite build` OK; full
  `npx vitest run` 159 files / 3924 tests green on the second run. First full run had one
  failure, `NuevaFamilia.test.tsx` > "con más de 100 artículos además de la referencia avisa
  cuántos sobran y no deja previsualizar" (6.7 s against the 5 s default timeout; passes
  alone 49/49) — treated as load timeout, not investigated further. Parent spot check:
  `npx vitest run src/paginas/ArticulosFamilias.test.tsx src/paginas/articulos` → 12 files,
  515 tests passed.
- Open, self-reported by the writer: a rejected join resets the selector even on a transient
  error; success notice is lost if the reload after a join fails; focus falls to `body`
  after creating a familia from an existing article.
- Judgment-day, web slice `e85aca04`, round 1: 0 CRITICAL from either judge. Confirmed by
  both (WARNING): J1 — after choosing a familia on an existing non-member, editing and saving
  the article (or writing a price) leaves the old preview alive and re-enables confirm, so the
  join runs against a stale diff (`guardar` edit branch never drops the proposal); J2 — "+"
  with a proposal open leaves `unionElegida` behind, and after "Sacar de la familia" the
  select shows the old familia with no proposal and cannot re-propose it
  (`crearFamiliaDelArticulo` does not call `soltarFamiliaDelFormulario`). Suspect (one judge
  or self-reported): any rejected join resets the selector, incl. uncertain results; focus
  lost in several paths; two async errors without `role="alert"`; a test name that claims the
  payload while the banner assertion is what holds; duplicate-name copy says "por eso no
  aparece" even for a visible active familia; dead export `UNION_SIN_ELEGIR`; possible Tab
  trap issue in the stacked modal (not executed, likely pre-existing). Fix round 1
  authorized by the owner ("dale", 2026-10-10).
- Fix round 1: commit `71df9544` — the proposal is dropped after a successful save, after a
  price write and after "+" creates a familia; 5 tests with mutation evidence. Observed
  (fix actor): `npx tsc -b` exit 0; `npx oxlint --quiet` clean; touched suites 21 files / 692
  tests; full `npx vitest run` 159 files / 3929 tests green.
- Scoped re-judgment 1: both judges J1 RESOLVED, J2 RESOLVED, 0 CRITICAL, no fix-caused
  defect. Confirmed by both, non-severe (A: SUGGESTION, B: WARNING): test d2 "la
  previsualización tardía…" does not prove the `invalidar` generation bump (the group only
  renders while `idElegida` is set), so its name claims more than it proves — PENDING before
  the PR: add a re-propose-after-late-response test or narrow the name. One judge only: no
  notice when a price write drops the proposal; a scheduled price also drops it;
  `alEscribirPrecio` not asserted at unit level. Judges did not run tests nor load the
  SKILL.md files in this round.

## Next step

T4: judgment-day verdict on the web slice, then PRs (owner decides push/PR).
