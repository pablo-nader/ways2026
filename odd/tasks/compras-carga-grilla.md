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

- [x] T5 `items_comprobante_compra.codigo_proveedor` (`citext` NULL, max 50, CHECK normalized; no FK, no unique index, no backfill): entity, configuration, migration, doc 10; request/response contracts; persisted on draft create/update for article and concepto lines; at confirm, article lines associate the code in `codigos_proveedor` (a code owned by another article of that supplier does not block: it stays on the line only). Route: delegated. Commits 4d40dfef (backend, migration `ComprasCodigoProveedorEnLinea`, doc 10), fb4cb1eb (web types and mapper).

Slice 3 — items grid (web):

- [x] T6 Grid with columns Código, Detalle, UM, Cantidad, Importe, Descuento, IVA, Total; tab navigation; always one empty trailing row; remove "Agregar línea", "Agregar concepto", "Cargar por total". Lote/vencimiento cells only on rows whose article controls lote; "Act. costo" always on for article lines; UM Bulto = quantity × the article's unidades por bulto. Route: delegated (single writer). Commit fd000f47.
- [x] T7 Código lookup on blur against the supplier's codes: prefill Detalle (read-only), UM = Unidad (option Bulto), Importe = article cost, Descuento = article default, IVA = article's or 21%. Unknown code stays on the line (T5). Commit fd000f47.
- [x] T8 Detalle as keyboard-navigable search (arrow keys, Tab to accept); free text with no match = concepto. The "+" opens the existing quick-create modal (the integration point T9 swaps). Commit fd000f47.
- [ ] T9 "+" button next to Detalle opening the full article creation form (extract the form state out of `paginas/Articulos.tsx` so it can be embedded).
- [x] T10 Actions: "Guardar" (create + confirm chained from the web; on a failed confirm the user lands on the draft with the error), "Guardar borrador", "Cancelar" (with confirmation, back to the list). Commit fd000f47 (same commit as T6-T8: the four tasks rewrite the same hunks of `CompraEditor.tsx`).

## Constraints

- Database change gate: no schema change without explicit owner approval.
- Tests ship with every task (Vitest + RTL; skills `web-descriptor-tests`, `web-test-data-gates`, `react-async-state`).
- Never run two Vitest suites concurrently.
- TDD mode: not configured (source: no project/session setting); ordinary functional checks. Runner: `npm test` in `src/Ways.Web`.

## Decisions

- 2026-10-10 (owner): unmatched supplier code is persisted on the purchase line (T5 model approved through the database gate).
- 2026-10-10: a half-filled invoice number is never sent; saving and confirming refuse until both halves are filled or both are empty.
- 2026-10-10: date field emits only complete valid dates while typing and commits on blur/Enter; two-digit years expand to the nearest of the next 20 years, otherwise the previous century.
- 2026-10-10 (T6-T10): `descuento_proveedor` is a PERCENTAGE (`numeric(5,2)`, `SugeridorDePrecio`: `costoLista * (1 - descuento/100)`), so the Descuento column is a percent of the line gross and the API receives the amount `bruto * % / 100` (exact decimal, half away from zero).
- 2026-10-10 (T7): the code lookup prefills with `costoLista` plus the article's `descuentoProveedor`; an article without list cost falls back to `costoNominal` (replacement cost, already net of discount) with no discount.
- 2026-10-10 (T10): the irreversibility checkbox panel was replaced by a note next to "Guardar" (stock enters, article cost updates, no way back to draft); the note drops the stock sentence when the purchase only has conceptos.
- 2026-10-10 (T6): an untouched saved line is resent with its stored numbers (percent and price-per-bulto shown are rounded derivations); a saved line mixing loose units and bultos opens as UM Unidad with the total quantity and is resent as stored until quantity, UM, importe or descuento is touched.
- 2026-10-10 (slice 3): the Descuento column is a percentage of the line gross; the API still receives the amount.
- 2026-10-10 (slice 3): UM Bulto takes the price per bulto, with a visible warning when the 4-decimal unit cost does not divide exactly.
- 2026-10-10 (slice 3): article costs are IVA-inclusive, so the prefilled Importe is netted when the receipt discriminates IVA and prices are net.
- 2026-10-10 (slice 3): a loaded line keeps its stored `actualizaCosto`.
- 2026-10-10 (owner): on a supplier change, rows resolved by code are cleared (article, Importe, Descuento, IVA) and looked up again from scratch after an explicit confirmation, replacing the per-cell snapshot (`prefillDeCodigo`). Rows chosen by search or created with "+", loaded-draft rows and conceptos are untouched; with no code-resolved row there is no prompt.
- 2026-10-11 (backend #359): the prefilled Importe is derived from the list cost normalized to per unit and IVA-inclusive using `costoListaPor` and `costoListaIncluyeIva` (list cost is stored as loaded), then netted when the receipt discriminates IVA with net prices; the quick-create modal declares its cost per unit, without IVA when the Importe is a net price.
- 2026-10-11: switching a row's UM converts the Importe so the per-unit cost is preserved (Unidad to Bulto multiplies by `unidadesPorBulto`, Bulto to Unidad divides, 4 decimals); Cantidad is not converted. A row takes the alícuota of the article created from it; the typed Importe and Descuento stay.

## Review

- Slice 1: judgment-day round 1 (main..15ed8859) no severe findings, 4 warnings confirmed by both judges, fixed in d70a0d40; scoped re-judgment of the delta clean. Native picker on the `inert` hidden input verified in Chromium. JUDGMENT: APPROVED.
- Slice 2 (T5): judgment-day (a29232a3..fb4cb1eb) no severe findings. Both judges suspected a race between confirm and the article soft delete; it was disproved (the stock movement FK already takes FOR KEY SHARE on the article before the association) and pinned with a deterministic test plus mutation evidence in 04878b3c. JUDGMENT: APPROVED.
- Slice 3: judgment-day round 1 on fd000f47 found five defects confirmed by both judges, fixed in 05d2162d; scoped re-judgment found no critical defect and four confirmed warnings, fixed in d8de8057; the second scoped re-judgment (d8de8057) left two issues confirmed by both judges (wrong price carried across a supplier change, missing association hint), the judgment was escalated, and the owner authorized a third fix round applied in 880c06a7.
- Slice 3, third re-judgment (880c06a7): left two warnings confirmed by both judges in the supplier-change path; the owner chose to replace the snapshot design with the simple clear-and-look-up-again rule, applied in 97608511.
- Slice 3, re-judgment of 97608511: no critical defect; the simple rule holds. Three minor adjustments (price mode, quantity in bultos, conceptos with a pending code) applied in 0cbef200.
- Cost normalization (be1aa4f6): judged by both judges with no critical finding; the UM conversion and the modal alícuota were applied in f95214e1.
- Receipt-driven development: off (clone-local); no native review.

## Follow-ups

- The "+" button of the Detalle is outside the Tab order (`tabIndex=-1`, reachable with Ctrl+Enter) to keep bulk entry fast; confirm with the owner.
- A saved article line whose `actualizaCosto` was false (old editor checkbox) is resent as true, as the grid has no such column.
- IVA of a concepto row is not defaulted: the operator picks it (the article rows take the article's alícuota or the 21 %).

- Owner question open: annulling a confirmed purchase keeps the supplier code association made at confirm.
- A code left unassociated because it belongs to another article gives no signal beyond the code on the line.

- Untested path: lookup in flight when the supplier prompt opens, then Cancel (the row stays unresolved until the next blur).
- After a supplier change, rows with an article and a code show "Se asociará al artículo al confirmar." (intended: the association goes to the new supplier).

- `articulos/EditorDePrecios.tsx` still uses `datetime-local` (browser locale format).
- Read-only printed date ranges (e.g. CuentaCorriente) still show ISO.
- `CampoNumeroDeComprobante`: the "left the group" flag never resets, so a re-edited half-filled number is flagged while typing.
- The quick-create modal stores `costoLista` with 2 decimals (column `numeric(14,2)`) while the row keeps 4.
- When a list cost exists, the prefill prefers it over a more recent `costoNominal` left by a purchase.
- `FormularioArticulo` does not yet expose `costoListaPor` / `costoListaIncluyeIva`.

## Progress

- 2026-10-10: exploration done; slice 1 (T1-T4) implemented and reviewed; full web suite 3931 passed, tsc and oxlint clean.
- 2026-10-10: T5 done: integration 3085, application 752, domain 1028, web 3936 passed; tsc and oxlint clean.
- 2026-10-10: T6-T8 and T10 done (fd000f47): web 4061 passed (167 files), tsc and oxlint clean. Mutation evidence recorded for the lookup generation gate, the untouched-amount round trip, the incomplete-row block and the no-second-POST retry.

## Next step

Deliver slice 3 as a PR, then T9 (embed the full article form in place of `AltaRapidaArticuloDeCompra`).
