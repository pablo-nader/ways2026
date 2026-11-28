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
- Owner added mid-flight: ink-only templates (no backgrounds), preview in a modal, quantity offer large + normal
  price small, and 10 genuinely different designs (not recolors). All done.
- T1 done (a5559c7e): grid reuse + server-side active-offer filter + preview modal. Backend 242/242, integration
  597/597, web 996 passed (writer report); parent re-ran grid tests 88/88.
- T2 done (4c0a246a…3743746d): 10 distinct ink-only models (modelosDeGondola.ts, region-based fitting with measured
  glyph widths). Reviewed in the browser at 4 and 16 per sheet; labels+screen tests 469/469.
- T3 judgment-day: no CRITICAL from either judge → APPROVED. Info / follow-ups: Ofertas grid rejects above 5000
  candidates and re-resolves offers on every page (both judges); grid Precio column uses the default list; first
  print may miss logo/fonts if never previewed; article with both unit offer and qty promo shows the discounted
  price as the small line; no debounce on selection-driven label data requests. Print not tested on a real printer.
