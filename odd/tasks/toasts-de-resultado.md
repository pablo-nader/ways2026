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
- [x] T3 — PaginaCatalogo, Proveedores, PuntosVenta, Tenants, Usuarios, Parametros, Personal.
- [x] T4 — Payments and accounts: Pagos, PagosDelTurno, CuentaCorriente, CuentaCorrienteDeProveedor, CuentaDePersonal(+Modales), CuentasTesoreria, tesoreria/SeccionDeCuentas, cobros/SeccionesDeCobros, ModalCuentaCorrientePos.
- [ ] T5 — Documents and editors: OrdenDeCompra, Presupuesto, Remito, CompraEditor, VentasDelTurno, Vitrina, CargaSemanalDeHoras, articulos/EditorDePrecios, articulos/SeccionDeDisponibilidad, ModalDeArticulo.
- [ ] T6 — POS screen (Pos.tsx) and download errors of reports (`errorDescarga`). Needs a POS release to reach the registers.

## Follow-ups

- Persistent `aria-live` region so screen readers always announce toasts (both T1 judges, SUGGESTION).
- Ofertas: a failed list refresh after a write shows the load banner and a warning toast for the same failure (T2 judge B, SUGGESTION).
- Parametros: same double report (success toast + load banner, no warning toast) when the list refresh fails after a save (T3 judge A, SUGGESTION).
- Owner to confirm: Usuarios "profile saved, password not changed" is now a 7 s error toast instead of a persistent banner (T3, both judges noted it).
- Tests: Parametros save failure and Usuarios Desbloquear failure only cover the generic fallback text, not an `ErrorApi` message (T3 judge A, SUGGESTION).

- SeccionDeCuentas (T4): a failed refresh after an ajuste still shows the load banners (cuentas / libro, with "Reintentar") next to the success toast, no warning toast.
- ModalCuentaCorrientePos success toast is outside the dialog and reaches the registers only with a POS release; `src/pos/ShellPos.test.tsx:919` asserts the same text (passes because ShellPos mounts the Toaster) and is outside T4's edit surface.

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
- 2026-10-31: T3 done. `npx vitest run` over the 7 pages 483 passed (11 files); full `npx vitest run` 289 files / 9036 tests passed; `npm run lint` no errors (pre-existing warnings only, none in the touched files); `npm run build` ok. `alert-success` no longer appears in the 7 pages (remaining `alert-danger`: load errors and confirmation-door errors). Mutation check: Parametros `toast.exito` for "Se guardó" turned into a no-op makes the success test fail; restored. Route: delegated writer. Per page: PaginaCatalogo, Proveedores, PuntosVenta, Tenants, Personal: `aviso` removed, success -> `toast.exito`, post-write reload failure -> `toast.exito` + `toast.advertencia` (`cargar` already rethrows without the load banner when `propagar`); their rejections were already inside modals or the baja/state confirmation, so `error` stays as load banner + confirmation error. Usuarios: same, plus the `accion` (Desbloquear) failure `setError` -> `toast.error`, and the partial failure of the password POST (`errorPassword` slot, its banner and its clears removed) -> `toast.error` next to the profile success toast; `error` keeps load + baja rejection. Parametros: success -> `toast.exito`, failed save -> `toast.error`; numeric validation and the resolve-probe failure stay banners (new tests for success and failed save). Tenants: `errorDeEdicion` renders inside ModalDeTenant (stays); state-change failure shows in the confirmation (stays); ModalDeNuevoTenant untouched. Deleted tests (obsolete with self-dismissing toasts): Tenants 1 (opening the alta clears the previous notice), Usuarios 2 (password failure cleared on cancel / on search) + Usuarios.padron 1 (cleared on query change), Personal 3 (`it.each`: previous notice does not survive opening add/edit/baja). Next: T4.
- 2026-10-31: T4 done. Focused `npx vitest run` over the touched files passes (Pagos 54, PagosDelTurno 61, CuentaCorriente 60, CuentaCorrienteDeProveedor 31, CuentaDePersonal* 256, CuentasTesoreria 42, tesoreria 71, ModalCuentaCorrientePos 27, Pos + ShellPos 575); full `npx vitest run` 289 files / 9036 tests passed; `npm run lint` no errors (pre-existing warnings only, none in the touched files); `npm run build` ok. `alert-success` no longer appears in any touched screen. Mutation check: Pagos `toast.exito` for "Pago eliminado." turned into a no-op makes 2 tests fail; restored. Route: delegated writer. Per file: Pagos (alta/edición/baja/vincular) and PagosDelTurno (alta/edición): `aviso` removed, success -> `toast.exito`; post-write refresh failure -> `toast.advertencia` of the load text (`cargar`/`cargarDetalle` take `propagar` and rethrow without the load banner or clearing the list); PagosDelTurno server failure of the inline alta -> `toast.error` (client validation `errorGuardar` stays a banner; edit rejections stay in the modal). Pagos edit/baja/picker rejections stay in their modal/confirmation. CuentaCorriente, CuentaCorrienteDeProveedor: shared `aviso` removed; pago/ajuste/reliquidación/cobro (via `SeccionesDeCobros.onEscritura`) -> `toast.exito`, refetch failure -> `toast.advertencia` without the load banner (`cargarEstado`/`cargar` with `propagar`). CuentaDePersonal: `aviso` removed; `mensajeOk` -> `toast.exito` right after the write, "se amplió el rango" note -> a second `toast.exito`, failed refresh -> `toast.advertencia` ("No se pudo actualizar la vista. Recargá la pantalla.", the leading "Pero" of the old joined text dropped because it is now a separate toast); `avisosInciertos` stay banners (uncertain outcome); CuentaDePersonalModales untouched (only modal-internal warnings). CuentasTesoreria: success + refresh failure as in Categorias; baja 404 (door already closed) -> `toast.error`, other baja failures stay in the confirmation. tesoreria/SeccionDeCuentas: `confirmacion` banner (+ "Cerrar aviso") -> `toast.exito`; `avisoIncierto` stays a banner; load banners with "Reintentar" stay (see follow-ups). cobros/SeccionesDeCobros: no behaviour change (modal errors stay in the modals; its `aviso` is a state notice set while rendering, "El recibo X ya no tiene importe disponible", stays a banner); only a doc comment. ModalCuentaCorrientePos: "Pago registrado: comprobante …" inside the still-open modal -> `toast.exito`; the POS needs a release to ship it (same as T6). Deleted tests (obsolete with self-dismissing toasts): PagosDelTurno 1 (second submit clears the success banner), CuentaDePersonal 1 (previous notice does not survive opening the next modal). Next: T5.
