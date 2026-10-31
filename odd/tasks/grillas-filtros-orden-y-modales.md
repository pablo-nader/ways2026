# Grids with filters and sorting, confirmations and forms in modals

## Objective

Apply four UI rules across Ways.Web (owner request, 2026-10-28):

1. Every padrón (master-data ABM screen) has filters in its grid.
2. Every grid is sortable by every column.
3. Every confirmation is shown in a modal, never embedded in the page and never `window.confirm`.
4. Every create/edit form opens in a modal.

## Problem / why

An audit on 2026-10-28 found: no shared table component and no column sorting anywhere;
`componentes/ConfirmacionDeBaja.tsx` (18 hosts) renders an inline alert, not a modal;
`window.confirm` in Articulos, Clientes, Ofertas and AltaDeArticuloDesdeCompra; Anular in
Presupuesto, Remito and OrdenDeCompra runs with no confirmation at all; several padrones edit
inline or on a separate page; most padrones have no filter or only a search box.

## Decisions (owner, 2026-10-28)

- Document editors with line grids (compras, presupuestos, remitos, órdenes de compra) stay as
  pages. Their listings still get sorting.
- Line grids being edited (items of a document, transferencias) keep their entry order: no
  sorting there.
- Grouped grids sort within each group (Reposición by proveedor). The heatmap of sales by hour
  is not a table and stays as is.
- "Filters" means per-column filters like the Artículos grid, not a single search box.
- Parámetros and Vitrina are settings forms, not record create/edit: they stay as pages.
- A server-paginated grid (Artículos) sorts on the server, never just the visible page.
- Shipped autonomously; the owner is told once everything is merged.

## Delivery strategy

`auto-chain`, `stacked-to-main`: one PR per slice, opened against main only when its base is
already in main. Each slice passes judgment-day, then PR + auto-merge + Auto-fix.
Independent slices run in parallel in isolated worktrees. This document is updated in the
coordinator worktree and shipped in the last PR; the Engram mirror tracks progress meanwhile.
Forecast: ~4,000 authored lines over 12 slices.

## Tasks

Wave 1 (from main, in parallel):

- [x] S1 (#490) — `ConfirmacionDeBaja` renders inside `Modal`; all 18 hosts keep working. Route: delegated.
- [x] S2 (#488) — Shared sorting (hook + sortable header with `aria-sort`) and shared per-column filter
  row; applied to `PaginaCatalogo` (Áreas, Marcas, Grupos, Medios de pago, Listas de precio).
  Route: delegated.
- [x] S4a (#489) — Create/edit in a modal: Clientes, Usuarios, Dominios, Gastos (alta). Route: delegated.

Wave 2:

- [x] S3 (#498, #494) — Remaining confirmations to modal: `window.confirm` (Articulos, Clientes, Ofertas,
  AltaDeArticuloDesdeCompra), inline confirmations in EditorDePrecios and SeccionDeFamilia,
  CompraEditor anular panel, POS offline-sale discard, and new confirmation for Anular in
  Presupuesto, Remito and OrdenDeCompra. Depends on S1, S4a.
- [x] S4b (#495) — Empresas and Puntos de venta create/edit in a modal.
- [x] S4c (#496) — Familias (rename + nueva) and Tenants (rename + nuevo) in a modal.

Wave 3 (after S2 and the S4 slice that touches the same screen):

- [x] S5a (#497) — Filters + sorting: Categorías, Clientes, Proveedores, Personal, Usuarios.
- [x] S5b (#499) — Filters + sorting: Empresas, Puntos de venta (filters for every role), Familias,
  Combos, Ofertas, Cuentas de tesorería, Tenants, Dominios, Equipos POS, Catálogos fiscales.
- [x] S5c (#493) — Artículos: server-side sorting by every column (backend + grid).
- [x] S6a (#491) — Sorting in reports: Existencias, Reporte de artículos, Ventas por artículo, Libro IVA,
  Vencimientos, Auditoría, Tablero, Tesorería, Reposición (within group).
- [x] S6b (#492) — Sorting in caja, cuentas corrientes, document listings and table modals.
- [x] S5d (#528) — Server-side per-column filters and sorting for Clientes, Proveedores, Personal and
  Usuarios. S5a found that their endpoints return only the first 25 rows (`tamanio ?? 25`) and the
  web has no pager, so client-side filtering would lie. After S6c (reuses its `OrdenDeListado`).
- [x] S5e (#540) — Web side of S5d for Personal and Usuarios (the S5d backend is already done):
  - Personal: a pager in place of "Se muestran N de M", and ranges on valor hora and saldo.
  - Usuarios: a server-side tenant filter.
  It reuses the shared pieces under `componentes/padron/`.
- [x] S6c (#503) — Server-side sorting for the server-paginated reports found by S6a: Reporte de
  artículos, Auditoría, Tesorería. Built on S5c's pattern.
- [x] S6d (#527) — Server-side sorting for the server-paginated listings found by S6b: Compras,
  Presupuestos, Remitos, Órdenes de compra, Histórico de cajas, Cierres de caja, Cuenta corriente
  de proveedor, Cuenta de personal, and the POS article search modal (its endpoint returns the
  first 25 results). After S6c.
- [x] S6e (#539) — Remainder of S6d: server-side sorting for Histórico de cajas, Cierres de caja, Cuenta
  corriente de proveedor, Cuenta de personal, and a decision on the POS article search modal.
  S6d covers only the four document listings.
- [x] S5b2 (#502) — Filters and sorting for Empresas, Puntos de venta, Familias, Tenants and Ofertas,
  once S4b, S4c and S3a are in main.
- [x] S7 (this PR) — Close this document with evidence.

## Checks

- `npm run lint`, `npx tsc -b` and `npx vitest run` in `src/Ways.Web` per slice.
- S5c also runs the backend unit and integration suites for the touched endpoint.
- POS web changes (S3 offline discard) reach the registers only with a `pos-v*` release, which
  needs the owner's OK.

## Progress

- 2026-10-28: audit done, owner confirmed scope.
- 2026-10-28: judgment-day APPROVED, one fix round each:
  - S2: PR #488. Fixed the filter-row cell count.
  - S4a: PR #489. Fixed focus after save, the Gastos alta getting stuck busy, and the repeated error.
  - S1: PR #490. Fixed the stacked Tab trap, the error behind the backdrop, aria-describedby and stale test comments.
  - S6a: branch pushed; its PR opens once #488 is in main. Fixed unstable columns in Tablero and kept the new Existencias row last.
  - S5c: branch pushed, suggestions only. Its integration tests did not run locally because Docker was down; CI runs them.
  - Auto-fix could be enabled only on #488. #489 and #490 are checked by hand.
- 2026-10-28: S6a found ReporteDeArticulos, Auditoría and Tesorería are server-paginated, which opened S6c. S3a, S3b, S4b, S4c and S6c are in progress.
- 2026-10-28:
  - Merged: #488 (S2) and #489 (S4a).
  - PRs open: #490 (S1), #491 (S6a), #492 (S6b), #493 (S5c).
  - Approved and pushed, waiting for their base to merge: S3b, S4b and S4c (base S1), and S6c (base S5c).
  - In fix rounds: S3a and S5a.
  - S5a found the same 25-row cap in the Clientes, Proveedores, Personal and Usuarios listings, which opened S5d.
- Stale-comment follow-up: the `react-async-state` skill cites `NuevaFamilia.crear`, a file S4c removed.
- Skill follow-up: `react-async-state` rule 17 still cites `conservarError` in `Usuarios.tsx`, which S4a removed.
- 2026-10-30: closing.
  - Merged: #488–#499, #502, #503, #527 and #528.
  - Open with auto-merge: #539 (S6e) and #540 (S5e). Both are judgment-day APPROVED.
  - Merging main into them needed three changes:
    - The Gasto→Pago rename: the column is now "Comprobante/Pago" and the field is `IdPago`.
    - A label for the new `Redondeo` movement in the server-side "Tipo" sort, caught by `TodoValorDeCadaEnumTieneEtiqueta`.
    - `horarioHabitual` in the Personal padrón test fixture.
  - Verification: local unit, typecheck and full web suites are green on every slice. Integration tests ran only in CI, because Docker was unavailable on this machine.

## Outcome

- Every padrón filters per column. Every grid sorts by every column. Server-paginated grids sort and filter on the server, which also fixed the 25-row cap in Clientes, Proveedores, Personal and Usuarios.
- Confirmations always open in a modal. Create/edit opens in a modal, except the document editors (Compras, Presupuestos, Remitos, OC). Those stay as pages by the owner's decision (2026-10-28).

## Limitations

- The POS article search modal stays unsorted.
- In S6e, supplier names in the cuenta corriente sort are visible only to Admin.
- POS web changes, such as the offline discard confirmation, reach the registers only with a `pos-v*` release, which needs the owner's OK.

## Follow-ups

- The `react-async-state` skill still cites `NuevaFamilia.crear` (removed in S4c) and `conservarError` in `Usuarios.tsx` (removed in S4a).
