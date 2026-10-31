# Design system Mostrador en Ways.Web

## Objective

Move the whole web app (back office, web POS, desktop POS bundle) onto the "Mostrador" design
system the owner chose on 2026-10-31: its color tokens in light and dark, its two typefaces and
the visual rules of its components, without renaming the Bootstrap classes the screens use.

## Problem / why

The UI is stock Bootstrap with an orange primary. The Artículos critique (impeccable, 24/40)
found a generic look, weak hierarchy, unreadable control borders, orange used as text, green
"save" buttons beside an orange primary and inconsistent fonts (the declared Inter is never
loaded, so everyone sees Segoe UI). The owner wants one durable visual base before polishing
screens.

## Source of truth

- Design System artifact "Ways Mostrador": https://claude.ai/artifact/NM2a19CisSaShZ7xA3YrDP
  (tokens.json, brand book, 17 components with `ws-*` reference classes, logos).
- In the repo: `src/Ways.Web/DESIGN.md` (rules + token table) and `src/Ways.Web/src/estilos/tema.css`
  (the tokens as CSS custom properties).

## Decisions

- Keep Bootstrap 5.3 and every class name the screens use; the restyle is global CSS that maps
  `--bs-*` variables onto the Mostrador tokens (`--ws-*`). No screen is edited in T1/T2.
- Fonts are self-hosted with `@fontsource-variable` packages (Bricolage Grotesque for headings,
  Instrument Sans for body), never Google Fonts: the desktop POS runs offline under a CSP.
- `brand` (#f7941d) is fill only; orange text uses `brand-ink`. `.btn-success` (used as "save")
  is mapped onto the brand primary, as the system has one primary action color.
- Tables lose stripes and vertical borders globally; `table-striped`/`table-bordered` stay in
  the markup and become no-ops visually.
- The public store (`publico.css`, `--pub-*`) keeps its own theme; it only inherits the body font.
- Print styles (`impresion.css`, `@media print`), PV stripes (`.color_x`) and ticket/label CSS
  are untouched.

## Delivery

Strategy: stacked-to-main chained PRs (owner preference). Forecast ~650 authored lines.
Slice 1 = T1, slice 2 = T2 (based on T1). Each PR passes judgment-day before opening.

## Tasks

- [x] **T1 Tokens, fonts and Bootstrap mapping** (route: delegated writer, 5+ files).
  `tema.css` rewritten on `--ws-*` tokens (light/dark) with `--bs-*` and `--ways-*` mapped onto
  them; hardcoded oranges replaced by tokens; fontsource packages imported in the four entries;
  `--ways-fuente` → Instrument Sans; headings in Bricolage; solid 2px focus ring; pre-paint
  theme script in admin.html/pos.html so a light user sees no dark flash; `DESIGN.md`; a Vitest
  test that reads `tema.css` as text and asserts every token exists in both themes and the
  contrast pairs of the system (4.5:1 text, 3:1 control borders and focus).
- [x] **T2 Global component skin** (route: delegated writer). Tables (no stripes or vertical
  borders, sunken header, hover, tabular numbers), buttons (`btn-success` → brand, secondary
  with `line-control` border), form controls, badges as pills with `-soft` tones, alerts with
  `-soft` tones, `.box` header without the accent bar, navbar active item underline, modal
  radius and shadow.

## Follow-ups (not in this feature)

- Shared components: section headings as real headings, FilterBar with visible labels, sortable
  table headers, real plurals in pagers.
- Screen-by-screen polish starting with Artículos (critique findings).
- Print from the dark theme: contextual table/alert variables are not reset in @media print (judgment-day T1, inferential).
- Desktop POS release (bump Ways.Desktop + tag `pos-v*`) so the cash registers get the new look.

## Acceptance criteria

- Every screen renders with Mostrador colors and fonts in both themes, no class renamed.
- `npm test` (Ways.Web) and `npm run build` pass; the new token test passes.
- Print output unchanged.

## Progress

- 2026-10-31: worktree `ds-mostrador`, branch `pnader/ds-mostrador-t1`; styling surface mapped.
- 2026-10-31: T1 hecha en `7bfbeb51`. RED: `tema.test.ts` 58 de 59 fallaban contra el `tema.css` anterior; GREEN: 59/59. Mutación (ink-muted claro a `#b0aea8`) mató 4 casos y se revirtió.
  `npm test` 282 archivos / 8916 tests OK; `npm run build` OK (5 woff2 emitidos como archivos, sin `data:`); `npm run lint` sin errores (solo warnings previos).
  Pre-paint: archivo externo `public/tema-inicial.js` porque la CSP de Tauri es `script-src 'self'`. Familia de fontsource = "… Variable": `publico.css` pide 'Bricolage Grotesque' a secas, se resuelve con `:root .publico { --pub-fuente-titulo }` en `tema.css` sin tocar `publico.css`.
- 2026-10-31: T2 hecha en `1bc03677`. `componentes.css` nuevo, importado tras `tema.css` en las 4 entradas; `.box` en `template.css`; las reglas contextuales de tabla (solo oscuro) pasaron a tokens para ambos temas. RED: `componentes.test.ts` fallaba por falta del archivo; GREEN 6/6; mutacion (hex y token inexistente anadidos) mato 3 casos y se revirtio.
  `npm test` 283 archivos / 8922 tests OK (una corrida previa fallo `Pos.test` autofocus; pasa solo y en la repeticion: flaky por carga); `npm run build` OK; `npm run lint` sin errores.
