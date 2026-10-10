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
- [ ] T2 — Web: selector always visible + "+" quick-create (`AltaRapidaFamilia`) for a new
  article (name travels with the alta) and for an existing non-member (single
  `POST /api/familias`). Route: delegated (writer; 4+ files).
  Checks: `npm run lint`, `npx tsc --noEmit`, `npx vitest run` on the touched suites.
- [ ] T3 — Web: join an existing familia from the edit form with preview and confirmation.
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
- T2/T3 in progress (web writer).

## Next step

T2/T3 (web), then T4.
