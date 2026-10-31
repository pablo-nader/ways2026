# Edición de códigos de proveedor en el artículo

## Objective

Inside the article edit modal, show a "Códigos de proveedor" section with a table of the article's live supplier codes, letting an Admin add, edit and delete them.

## Problem / why

Today a supplier code can only be added (`POST /api/articulos/{id}/codigos-proveedor`, the purchase-from-compra flow or the MCP connector). A wrong code stays attached forever and blocks that code for any other article of the same supplier. There is no GET either: the edit modal cannot even show them.

## Scope

- Backend: `GET`, `PUT` and `DELETE` on `/api/articulos/{id}/codigos-proveedor[/{idCodigoProveedor}]`.
- Web: a new section in `FormularioArticulo` (edit mode only), modeled on `GestorDeCodigosBarra`, with a table (proveedor, código, acciones), inline edit, delete with confirmation, and an add row (proveedor + código) using the existing POST.

Out of scope: auditing (the articles service has none today), changing how purchase confirmation re-associates codes.

## Contract

- `GET /api/articulos/{id}/codigos-proveedor` → `200 CodigoProveedorListado[]` ordered by id; 404 when the article is missing, soft-deleted or of another tenant. Policy: group default (`OperacionDePos`), same as the barcodes GET.
- `PUT /api/articulos/{id}/codigos-proveedor/{idCodigoProveedor}` body `{ codigo }` → `200 CodigoProveedorListado`. Policy `GestionDeCatalogo`. Order: 404 first (article or code missing / deleted / other tenant / code of another article), then 400 (`codigo_requerido`, `codigo_muy_largo`), then 409 `codigo_proveedor_duplicado` when another live code of the same supplier has it (case-insensitive, citext). Same code or case-only change of itself is allowed. The 23505 backstop already maps to the same 409.
- `DELETE /api/articulos/{id}/codigos-proveedor/{idCodigoProveedor}` → `204`. Policy `GestionDeCatalogo`. Soft delete (`deleted_at`), which frees the pair under the partial unique index.

## Data model

No schema change. `codigos_proveedor` (doc 10, articulos part) already has `deleted_at`, the partial unique index `ux_codigos_proveedor_proveedor_codigo WHERE deleted_at IS NULL` and the normalization check. Purchase lines store `codigo_proveedor` as free text with no FK, so editing/deleting does not break purchases (a future purchase carrying the old code may re-associate it, by design).

## Tasks

- [x] T1 — Backend GET/PUT/DELETE + unit and integration tests. Route: delegated direct (writer trigger: service, endpoints, contracts and tests = 2+ non-trivial files). Evidence: build 0 errors; Domain.Tests 2108/2108; full IntegrationTests 5736/5736 (13 new tests, two race tests); mutation evidence for the "excluding itself" clause and the 404-first ordering. No schema change: the existing 23505 translation already covers edits.
- [x] T2 — Web section with table, add/edit/delete + Vitest tests. Route: delegated direct (writer trigger: component, client, types and tests). Evidence: `tsc -b` clean; lint clean on new files; full vitest 8880/8880; parent spot check of the 3 touched test files 46/46.

## Judgment day (2026-10-31)

Both slices `JUDGMENT: APPROVED` in round 1 (no CRITICAL from either judge). Info carried as follow-ups:

- Backend (both judges, WARNING/SUGGESTION): a concurrent PUT and DELETE on the same code are not serialized (FOR KEY SHARE only on the article), so a PUT racing a DELETE can answer 200 on a row that was just soft-deleted. Fix: lock the code row `FOR UPDATE`.
- Web (judge B, WARNING): with more than 200 suppliers the list is truncated; a supplier outside it shows "(no disponible)" and the add select can hold a habitual supplier it does not show (use `opcionesConValorActual`).
- Web (suggestions): error banner without `role="alert"`, focus not restored after a failed save, empty-state text shown when the initial load fails.

## Acceptance criteria

- An Admin sees the table in edit mode, edits a code, deletes it, and adds a new one; a Vendedor gets 403 on PUT/DELETE.
- 404-first ordering holds for PUT/DELETE (`update-404-first`).
- PUT duplicate → 409; concurrent PUT race → exactly one winner, 23505 translated (`db-error-backstops`).
- Writes use the non-retrying strategy and a single read after the lock (`ef-retry-safe-writes`, `single-read-under-lock`); structural guard tests pass on the full suite.
- Web: async reads gated, writes disabled with a sync re-entrancy ref (`react-async-state`); tests wait for data (`web-test-data-gates`).

## Checks

- `dotnet test tests/Ways.Domain.Tests`, `dotnet test tests/Ways.UnitTests` (if present), full `tests/Ways.IntegrationTests` run.
- `npm run lint`, `npx tsc -b`, `npx vitest run` in `src/Ways.Web`.

## Delivery

Forecast ~750 authored lines: over the ~400 budget, strategy `ask-on-risk` with cached chain choice `stacked-to-main`. Slice 1 = T1 (backend PR), slice 2 = T2 (web PR stacked on slice 1).

## Progress

- Exploration done (2026-10-31). Worktree `.claude/worktrees/codigos-proveedor-edicion`, branch `pnader/codigos-proveedor-edicion`.

## Next step

T1 and T2 in parallel writers.
