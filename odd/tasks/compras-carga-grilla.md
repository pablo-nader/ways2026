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

Slice 2 — supplier code on the purchase line (backend, DB gate approved 2026-10-10):

- [ ] T5 `items_comprobante_compra.codigo_proveedor` (`citext` NULL, max 50, CHECK normalized; no FK, no unique index, no backfill): entity, configuration, migration, doc 10; request/response contracts; persisted on draft create/update for article and concepto lines; at confirm, article lines associate the code in `codigos_proveedor` (a code owned by another article of that supplier does not block: it stays on the line only). Route: delegated.

Slice 3 — items grid (web):

- [ ] T6 Grid with columns Código, Detalle, UM, Cantidad, Importe, Descuento, IVA, Total; tab navigation; always one empty trailing row; remove "Agregar línea", "Agregar concepto", "Cargar por total". Lote/vencimiento cells only on rows whose article controls lote; "Act. costo" always on for article lines; UM Bulto = quantity × the article's unidades por bulto.
- [ ] T7 Código lookup on blur against the supplier's codes: prefill Detalle (read-only), UM = Unidad (option Bulto), Importe = article cost, Descuento = article default, IVA = article's or 21%. Unknown code stays on the line (T5).
- [ ] T8 Detalle as keyboard-navigable search (arrow keys, Tab to accept); free text with no match = concepto.
- [ ] T9 "+" button next to Detalle opening the full article creation form (extract the form state out of `paginas/Articulos.tsx` so it can be embedded).
- [ ] T10 Actions: "Guardar" (create + confirm chained from the web; on a failed confirm the user lands on the draft with the error), "Guardar borrador", "Cancelar" (with confirmation, back to the list).

## Constraints

- Database change gate: no schema change without explicit owner approval.
- Tests ship with every task (Vitest + RTL; skills `web-descriptor-tests`, `web-test-data-gates`, `react-async-state`).
- Never run two Vitest suites concurrently.
- TDD mode: not configured (source: no project/session setting); ordinary functional checks. Runner: `npm test` in `src/Ways.Web`.

## Decisions

- 2026-10-10 (owner): unmatched supplier code is persisted on the purchase line (T5 model approved through the database gate).
- 2026-10-10: a half-filled invoice number is never sent; saving and confirming refuse until both halves are filled or both are empty.
- 2026-10-10: date field emits only complete valid dates while typing and commits on blur/Enter; two-digit years expand to the nearest of the next 20 years, otherwise the previous century.

## Review

- Slice 1: judgment-day round 1 (main..15ed8859) no severe findings, 4 warnings confirmed by both judges, fixed in d70a0d40; scoped re-judgment of the delta clean. Native picker on the `inert` hidden input verified in Chromium. JUDGMENT: APPROVED.
- Receipt-driven development: off (clone-local); no native review.

## Follow-ups

- `articulos/EditorDePrecios.tsx` still uses `datetime-local` (browser locale format).
- Read-only printed date ranges (e.g. CuentaCorriente) still show ISO.
- `CampoNumeroDeComprobante`: the "left the group" flag never resets, so a re-edited half-filled number is flagged while typing.

## Progress

- 2026-10-10: exploration done; slice 1 (T1-T4) implemented and reviewed; full web suite 3931 passed, tsc and oxlint clean.

## Next step

Deliver slice 1 as a PR, then T5.
