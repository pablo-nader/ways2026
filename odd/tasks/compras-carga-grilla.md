# compras-carga-grilla

## Objective

Make purchase entry (`/compras/nueva`, `/compras/:id`) fast for bulk loading: fix the header
ergonomics and replace the per-row item editor with a spreadsheet-like grid.

## Problem / why

The current editor (`src/Ways.Web/src/paginas/CompraEditor.tsx`) forces a button click per line,
shows codes instead of names, is unreadable in dark mode on incomplete rows, and native date
inputs render MM/DD/YYYY depending on the browser locale.

## Scope

Slice 1 — header and cross-cutting fixes (no backend change):

- [x] T1 Dark mode: `.table-warning` / `.table-danger` rows follow the theme (fix in `estilos/tema.css`, applies to every screen using them). Route: delegated. Commit 9c05a8d7.
- [x] T2 Header selectors: proveedor label = nombre de fantasía (fallback razón social); tipo label = nombre instead of code; single punto de venta preselected on a new purchase. Route: delegated. Commit 3cf0ad61.
- [x] T3 Número de comprobante split in two inputs (punto de venta 4 digits, número 8 digits), zero-padded on blur; wire value stays `PPPP-NNNNNNNN`. Route: delegated. Commit c5053bb1.
- [x] T4 Shared date field rendering DD/MM/YYYY, value contract stays `YYYY-MM-DD`; replace every `type="date"` in Ways.Web (32 usages). Route: delegated. Commit 15ed8859.

Slice 2 — items grid (pending product decisions, see below):

- [ ] T5 Grid with columns Código, Detalle, UM, Cantidad, Importe, Descuento, IVA, Total; tab navigation; always one empty trailing row; remove "Agregar línea", "Agregar concepto", "Cargar por total".
- [ ] T6 Código lookup on blur against the supplier's codes: prefill Detalle (read-only), UM = Unidad (option Bulto), Importe = article cost, Descuento = article default, IVA = article's or 21%.
- [ ] T7 Detalle as keyboard-navigable search (arrow keys, Tab to accept); free text with no match = concepto.
- [ ] T8 "+" button next to Detalle opening the full article creation form (extract the form state out of `paginas/Articulos.tsx` so it can be embedded).
- [ ] T9 Actions: "Guardar" (create + confirm), "Guardar borrador", "Cancelar" (with confirmation, back to the list).

## Constraints

- Database change gate: no schema change without explicit owner approval.
- Tests ship with every task (Vitest + RTL; skills `web-descriptor-tests`, `web-test-data-gates`, `react-async-state`).
- Never run two Vitest suites concurrently.
- TDD mode: not configured (source: no project/session setting); ordinary functional checks. Runner: `npm test` in `src/Ways.Web`.

## Open decisions

- Unmatched supplier code "to be associated later": persist on the purchase line (new column, DB gate) or keep it client-side only.
- Assumptions pending confirmation: lote/vencimiento cells shown only for articles that control lote; "Act. costo" always on for article lines; UM Bulto sends bultos × the article's unidades por bulto.

## Progress

- 2026-10-10: exploration done; slice 1 started.
- 2026-10-10: slice 1 (T1-T4) implemented; full web suite 3904 passed, tsc clean.

## Next step

Slice 2 (T5-T9) once the open decisions are answered.
