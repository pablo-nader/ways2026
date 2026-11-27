# Shelf price labels (etiquetas de góndola)

## Objective
Back-office screen to print shelf price labels on a normal printer (browser print dialog), always A4 landscape.

## Problem / why
The owner prints shelf prices by hand-made artifacts. The system already composes label data
(`POST /api/etiquetas/datos`, stage-18) and has a pure renderer (`src/Ways.Web/src/etiquetas/`), but there is
no screen, route or menu entry, and quantity-threshold offers ("3x $2.700") are not reported.

## Scope (owner decisions, 2026-11-27)
- Pick articles from a grid with search (like the articles grid), multi-select; each selected article prints once.
- Price list selector; default = the list with `esDefault` (seeded as "General").
- 10 templates: 5 light, 5 dark. Labels per sheet: 2, 4, 8 or 16. Always A4 landscape.
- Label content: Ways logo, article name (max 2 lines, font auto-fitted), price of the chosen list. Nothing else.
- "Ofertas" checkbox: only articles with an active offer; same 10 templates with a minimal offer variant that adds
  the offer text (e.g. "3x $2.700" or "-15%"); without the checkbox offers are ignored (plain list price).

## Constraints
- Reuse `ServicioDeOfertas` resolution; never reimplement offer math. No cost fields in label DTOs.
- New SPA route registered in App.tsx, menu.ts and RutasDeLaSpa.cs.
- No schema changes.

## Delivery
Strategy `ask-on-risk` → owner pre-authorized everything; chain `stacked-to-main`:
- PR A: backend (quantity offers in label data).
- PR B: web screen + templates (stacked on A).

## Tasks
- [ ] T1 (delegated writer) Backend: `FilaDeEtiqueta` reports quantity-threshold offers (min qty > 1) with the
  unit price at that quantity, resolved through `ServicioDeOfertas`; `SoloConOfertaVigente` includes them. Tests.
- [ ] T2 (delegated writer) Web: api client, 4 landscape formats × 10 themes + offer variant, name auto-fit,
  selection screen, route/menu/SPA allowlist. Vitest tests.
- [ ] T3 judgment-day on each slice, PRs with auto-merge + Auto-fix.

## Acceptance criteria
- Printing N selected articles yields ceil(N / perSheet) landscape A4 pages with the chosen theme.
- Names up to 2 lines never overflow; font shrinks for long names.
- Price equals the chosen list price; offer mode shows offer text and only offer articles.

## Checks
- `dotnet test` on Ways.Application.Tests (Etiquetas) + integration Etiquetas/Authorization surface.
- `npm run test` (vitest) + `npm run build` in src/Ways.Web.

## Progress
- Exploration done (2026-11-27). Route per task: T1/T2 delegated (2+ non-trivial files).
