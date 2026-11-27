# Shelf labels: articles grid parity + brand templates

## Objective
Fix two gaps in the shelf price labels screen (`/etiquetas-gondola`, PRs #651/#652) reported by the owner on 2026-11-27.

## Problem / why
- The owner asked for the article selection grid to be the same as the articles screen grid. The shipped screen
  built its own simple grid (search box only) instead of reusing `GrillaDeArticulos`, so it lacks the column
  filters, columns, sorting and paging of the articles screen.
- None of the 10 templates use the Ways brand colors (`--ws-brand` #f7941d, soft #fdebd3, ink #9a5200,
  gray #c4c4ca in `src/Ways.Web/src/estilos/tema.css`).

## Scope
- Selection grid = `GrillaDeArticulos` (same columns, filters, sorting, paging), plus a selection checkbox column
  instead of the edit/delete actions. Selection persists across pages and filters.
- "Ofertas" mode: the same grid restricted to articles with an active offer (server-side filter on the grid
  endpoint, reusing the existing offer resolution).
- The 10 templates (5 light, 5 dark) use only the Ways brand palette plus black/white/neutral grays.

## Constraints
- Articles screen behavior must not change (its existing tests stay green).
- Reuse `ServicioDeOfertas` for "active offer"; never reimplement offer math. No schema changes.

## Tasks
- [ ] T1 (delegated writer, 2+ non-trivial files) Grid parity + offers filter on the grid endpoint. Tests.
- [ ] T2 (inline, one mechanical file) Rebrand the 10 templates. Tests.
- [ ] T3 judgment-day, PR with auto-merge + Auto-fix.

## Checks
- `dotnet test` Ways.Application.Tests + integration tests for the articles grid endpoint.
- `npx vitest run` (cwd `src/Ways.Web`) for articulos + etiquetas + EtiquetasGondola; `npm run build`.

## Progress
- 2026-11-27: branch `pnader/etiquetas-gondola-grilla` from main (a010f334).
