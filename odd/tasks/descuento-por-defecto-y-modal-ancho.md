# Descuento por defecto 0 y modal de artículo más ancho

## Objective
The article form calls the supplier discount just "Descuento", it defaults to 0 (never null), existing
null rows are backfilled to 0 with their nominal cost computed, and the article modal uses ~90% of the
viewport width instead of Bootstrap's 1140px `modal-xl` cap.

## Problem
Owner request (2026-10-11): the label "Descuento de proveedor (%)" is too long, the empty default
leaves `descuento_proveedor` NULL, and the modal wastes horizontal space.

## Evidence found during exploration
- Both calculators already treat a null discount as 0 (`CalculadorDeCostoNominal.Calcular`
  `descuentoProveedor ?? 0m`; `costoNominal.ts` `descuentoProveedor ?? 0`). A null discount does NOT
  block the nominal cost; a null `costo_nominal` with a loaded `costo_lista` has another origin.
- `modal-xl` is used only by `ModalDeArticulo.tsx`; Bootstrap sets `--bs-modal-width: 1140px` at ≥1200px.
- `descuento_proveedor numeric(5,2) NULL` (doc 10 §3). Column stays nullable in the model (the read DTO
  reuses null for roles that cannot see costs); the DB default and the write paths make it 0.

## Scope
- Backend: service coerces null → 0 on create and update; DB default 0; migration backfill
  (`SET LOCAL app.acceso = 'plataforma'`, rls-migration-backfills) null → 0 and `costo_nominal`
  computed only where it is NULL and `costo_lista` is not (never overwrite a stored nominal cost: it may
  be the effective cost of a purchase).
- Web: label "Descuento (%)", `familia.ts` label "Descuento", form default `'0'`, empty field sends 0,
  `entradasDeCosto` mirrors the server (empty → 0); `.modal-xl` at 90vw.

## Constraints
- DB gate lifted (2026-10-11): model summary recorded here, no approval prompt.
- Backfill test runs under `ways_app` with mutation evidence (remove SET LOCAL → red).
- Never run two integration suites or two vitest runs concurrently.

## Tasks
- [x] T1 — Backend: default 0, migration + backfill + nominal recompute, tests. Commit: see Progress.
      Route: delegated (writer trigger: 2+ non-trivial files).
- [x] T2 — Web: label, default 0, empty → 0 mapping, modal width, tests. Commit 4ccca146.
      Route: delegated (writer trigger: 2+ non-trivial files). Independent of T1 (disjoint files).

## Acceptance
- POST /api/articulos without `descuentoProveedor` → stored and returned as 0, `costoNominal` computed.
- PUT with `descuentoProveedor: null` → stored 0.
- Migration: rows with NULL discount become 0; rows with NULL `costo_nominal` and a usable
  `costo_lista` get the computed value; rows with a stored `costo_nominal` are untouched.
- Form: label "Descuento (%)", new article shows 0, clearing the field sends 0.
- Modal: ~90% viewport width on desktop.

## Delivery
Forecast ~350 authored lines. Strategy: ask-on-risk. Single PR after judgment-day clean round,
auto-merge (owner protocol).

## Progress
- Branch `pnader/descuento-proveedor-card-width-4af926` (worktree) from main @ cdd37b66.
- T2 done (delegated writer, sonnet): 10 web files +119/-13; vitest 172 files / 4456 tests green, lint
  and build green; mutation check on `entradasDeCosto` `?? 0` kills 3 tests. Modal rule
  `max(1140px, 90vw)` at ≥1200px. Hidden-cost edits keep sending null (existing test). Commit 4ccca146.
- T1 done (delegated writer, sonnet): service coerces null → 0 in Crear/Actualizar; `HasDefaultValue(0m)`;
  migration `20261011182842_DescuentoProveedorPorDefectoCero` (AlterColumn default 0 + `SqlDelBackfill`
  with `SET LOCAL app.acceso`, null → 0, nominal only where NULL with usable costo_lista). 6 integration
  tests; mutation evidence: SET LOCAL removed → 10 vs 0 rows; `costo_nominal IS NULL` removed → 11 vs 10;
  Bulto guard removed → 22012 division by zero; Crear coercion removed → family alta 409 vs 201;
  Actualizar coercion removed → PUT-null test red. Domain 1077/1077, Application 779/779, Integration
  (Articulos|Migracion|DescuentoProveedor) 443/443. Only Admin writes articles (`GestionDeCatalogo`),
  so the coercion never overwrites a discount the writer could not see.
