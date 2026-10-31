# Toasts de resultado

## Objective

Replace the banners that report the result of a user action ("Pago eliminado.", "No se pudo eliminar…") with toasts that dismiss themselves.

## Problem

Action results are page-level `aviso` / `error` states rendered as `alert alert-success|danger|warning`. They stay on screen until the next action, push the layout down and look the same as persistent warnings.

## Scope (owner decision, 2026-10-31)

- IN: success messages after a write (save, delete, cancel, emit, send…) and error messages from a failed write action.
- OUT (stay as banners): form validation (`ErrorDeFormulario` and modals), persistent state warnings (no open shift, duplicate number, POS update, uncertain outcome `avisoIncierto`), screen load errors, empty states, result panels that show data (Transferencias, FacturarRemitos, ConteoDeInventario, ModalDeNuevoTenant).
- Online store and landing: no toaster (no action-result banners there).

## Design

- Module-level store (`src/toasts/`) with `toast.exito|error|advertencia(texto)`; `<Toaster />` subscribes with `useSyncExternalStore`. No provider required, callable from any handler.
- Auto-dismiss: success/warning ~4 s, error ~7 s; pause on hover/focus; close button.
- a11y: success/warning in a `role="status"` live region, errors in `role="alert"`.
- Styling with Mostrador tokens (`--ws-*`, `.alert-*` restyle in `estilos/componentes.css`), fixed bottom-right, z-index above modals.
- Mounted once in `App.tsx` (admin + web POS) and in `pos/ShellPos.tsx` (desktop POS).
- Tests: `src/test/setup.ts` resets the store after each test; page tests render `<Toaster />` next to the page when they assert result text.
- Variables shared by a load error and an action error (`error && !baja` pattern) are split: the load error stays as a banner, the action error becomes a toast.

## Tasks

Route: every task delegated to one writer (2+ non-trivial files each). Delivery: `stacked-to-main`, one PR per task.

- [x] T1 — Toast store + `Toaster` + CSS + unit tests; mount in `App.tsx` and `ShellPos.tsx`; store reset in test setup.
- [x] T2 — Catalog ABM pages: Categorias, Clientes, Combos, Articulos, Dominios, Empresas, EquiposPos, Familia, Familias, Ofertas.
- [ ] T3 — PaginaCatalogo, Proveedores, PuntosVenta, Tenants, Usuarios, Parametros, Personal.
- [ ] T4 — Payments and accounts: Pagos, PagosDelTurno, CuentaCorriente, CuentaCorrienteDeProveedor, CuentaDePersonal(+Modales), CuentasTesoreria, tesoreria/SeccionDeCuentas, cobros/SeccionesDeCobros, ModalCuentaCorrientePos.
- [ ] T5 — Documents and editors: OrdenDeCompra, Presupuesto, Remito, CompraEditor, VentasDelTurno, Vitrina, CargaSemanalDeHoras, articulos/EditorDePrecios, articulos/SeccionDeDisponibilidad, ModalDeArticulo.
- [ ] T6 — POS screen (Pos.tsx) and download errors of reports (`errorDescarga`). Needs a POS release to reach the registers.

## Acceptance criteria

- No `alert-success` remains for an action result in the migrated files.
- Load errors, validation and persistent warnings are still banners.
- Every migrated page's tests assert the toast text; `npm run test`, `npm run lint` and `npm run build` pass in Ways.Web.

## Checks

- `npx vitest run` (Ways.Web), `npm run lint`, `npm run build`.

## Progress

- 2026-10-31: exploration done; feature document created. Next: T1.
- 2026-10-31: T1 done. `npx vitest run src/toasts` 16 passed; full `npx vitest run` 287 files / 9036 tests passed; `npm run lint` no errors (pre-existing warnings only); `npm run build` ok. Reset in setup.ts mutation-checked (3 tests fail without it). Toaster mounted in App.tsx and ShellPos.tsx (desktop POS entry is AppPos, never App: no double mount). Route: delegated writer. Next: T2.
- 2026-10-31: T2 done. `npx vitest run` over the 10 pages passes; full `npx vitest run` 287 files / 9021 tests passed (15 fewer than T1: tests of "a new action clears the previous success banner", which no longer exists with self-dismissing toasts: Dominios 3, Familias 3, Familia 9); `npm run lint` no errors (pre-existing warnings only, none in the touched files); `npm run build` ok. `alert-success` no longer appears in the 10 pages. Mutation check: Ofertas `toast.exito` for "creada" turned into a no-op makes 9 tests fail; restored. Route: delegated writer. Per page: success -> `toast.exito` everywhere; post-write reload failure -> `toast.advertencia` of the failure text (success text emitted separately); Categorias, Clientes, Articulos, EquiposPos, Empresas, Ofertas: no action error became a toast (rejections already live in the modal or the confirmation; Ofertas `error` keeps load + open-for-edit read failures as banner). Combos: `error` split into `errorDeBaja` (confirmation) + `toast.error` for a combo that no longer exists on save/delete; alta confirmed by re-read -> `toast.advertencia`. Dominios: `errorDeAccion` split, verificar failure -> `toast.error`, `errorDeEliminacion` stays in the confirmation. Familias: `error` -> `errorDeDisolucion` (confirmation) + `toast.error` on 404. Familia: `errorDeEscritura` -> `errorDeSalida` (confirmation) + `toast.error` when the exit closes the door after `familia_cambio`/404; arrival notice (`state.aviso`) is shown as a toast and the history state is still consumed; `alEmpezar` prop of the add panel removed (nothing left to clear). Empresas `zonaDeAvisosRef` kept as fixed focus target. Next: T3.
