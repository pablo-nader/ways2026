# Ajustes de UI del POS

Locator: `odd/tasks/ajustes-ui-pos.md` · Engram: `odd/ajustes-ui-pos/tasks`

## Objective

Apply the owner's batch of POS adjustments (2026-10-12): single-line top bar with
dropdown menus, icon actions with tooltips, modal confirmations, cart ergonomics,
customer selector in the cart header, current-account modal improvements with payment
ticket, and payment-method totals at shift close.

## Problem / why

The POS top bar wraps in two lines, actions are text buttons that waste space, the
void confirmation renders below the table, the cart shows the newest item last, and
the current-account flow prints nothing. Treasury does not reflect the real balance
of digital accounts because platform fees and tax withholdings are not modelled.

## Constraints

- POS front changes reach the tills only with a `Ways.Desktop` bump + `pos-v*` tag.
- Desktop CSP is `img-src 'self'`: Bootstrap's `btn-close` (a `data:` SVG background)
  is blocked, which is why the modal X is invisible. Icons must be inline SVG.
- No icon library: extend `BotonIcono` (`componentes/BotonIcono.tsx`) inline SVGs.
- Exactly one payment method with `comportamiento = Efectivo` per tenant; the
  cash/digital classification already exists as `comportamiento`
  (Efectivo / Electronico / CuentaCorriente). No new column for it.
- F9/F10/Escape are already bound inside the charge confirmation; F10 "empty cart"
  must not fire while a modal is open.
- RDD is off (clone-local); each PR slice passes judgment-day (project CLAUDE.md).

## Delivery

Strategy: `auto-chain`, chain `stacked-to-main` (cached owner preference). Forecast
well above 400 authored lines → one PR per slice below.

## Tasks

- [x] **T1 — Shell and top bar** (delegated: 2+ non-trivial files)
  - Modal X visible (inline SVG, no `btn-close` data URI), also `Pos.tsx:806`.
  - Header in one line; title + "PV n — nombre · usuario" on the same line.
  - Buttons: `Vender` (F1: new sale route + focus on barcode input); `Caja` dropdown
    (Ventas del turno, Historial de turnos, Gastos, Retirar efectivo, Abrir/Cerrar
    turno by state); `Sistema` dropdown (Configuración, Sincronizar); theme; logout.
  - Sync interval fixed at 10 min; remove the input and the standalone
    "Sincronizar ahora"/"Cerrar caja"/"Retirar" buttons; keep both chips.
  - Configuración window (`Ways.Desktop/ui`) restyled to the system's dark theme.
  - Checks: `ShellPos.test.tsx`, `Pos.test.tsx`, `intervaloDeSincronizacion.test.ts`,
    `Modal.test.tsx`, `tsc -b`, `oxlint`.
- [x] **T2 — Ventas del turno and Cierres** (delegated)
  - Icon buttons with tooltips: Actualizar, Detalle, Anular (trash), Imprimir.
  - Void confirmation as a modal.
  - Cierres: detail icon (uses `GET /caja/turnos/{id}/detalle`) + printer icon.
- [x] **T3 — Cart ergonomics** (delegated)
  - Newest line first; Quitar as trash icon; focus back to barcode input after
    remove/empty; "Vaciar carrito" in the same colour as Quitar, F10 with
    confirmation "¿Está seguro que quiere vaciar el carrito?".
- [ ] **T4 — Customer in the cart header** (delegated)
  - Contains-filter select replacing the "Carrito" title; removed from
    "Datos de la venta"; "Ver detalle" icon for current account; balance chip
    (used / limit, progress style).
  - Right panel ("Datos de la venta") reworked with the freed space (owner, 2026-10-12):
    sale total first and large (at least twice the height of a current row), then the
    payment methods, then ONE line that reads "Falta" (amount in red) while the payment
    is incomplete and "Vuelto" (amount in green) otherwise, and the Cobrar button last
    as it is today.
- [ ] **T5 — Current-account modal and tickets** (delegated)
  - Max viewport height with scroll; no "Cerrar" button; "Registrar pago" on top;
    date range (default today − 7 days).
  - Payment ticket (previous balance, payment, new balance); drawer pulse when any
    cash; sale ticket for a customer shows balance before and after.
- [ ] **T6 — Totals by payment method at close** (delegated)
  - Close summary: per method, total digital, total cash, grand total (screen + ticket).
- [ ] **T7 — Fees and withholdings per payment method → treasury** — BLOCKED on an
  owner decision: treasury today is a cash-only ledger with no accounts; non-cash
  sales never reach it.
- [ ] **T8 — POS release** (`Ways.Desktop` bump + tag) once T1–T6 are in main.

## Progress

| Task | Route | Commit | PR | Checks |
|------|-------|--------|----|--------|
| T1 | delegated (writer trigger: 16 files) | 7ada332f, b9090eb3, 9cb176a8, ff59cebe | #405 (merged) | tsc clean; oxlint exit 0; vitest 208 files / 5790 passed; judgment-day: round 1 no severe (7 warnings fixed in 9cb176a8), scoped re-judgment clean, polish ff59cebe; not checked in a real Tauri window |
| T2 | delegated (writer trigger: 17 files) | f9b7f3ab, b0c0c368, 02d91938, d9909ed3, 0eb7eeb1 (merge de main) | #406 | tsc clean; oxlint exit 0; vitest 209 files / 5830 passed; judgment-day: round 1 no severe (fixes in 02d91938), scoped re-judgment no severe (polish d9909ed3); not checked in a real Tauri window |
| T3 | delegated (preparation trigger: Pos.tsx 4.6k lines) | 5f0a4264, 0d7268ab | this PR | tsc clean; oxlint exit 0; vitest 212 files / 5965 passed on the branch merged with main; judgment-day: round 1 no severe (2 confirmed warnings fixed in 0d7268ab), scoped re-judgment no severe; residual: local-snapshot scan behind the modal, unreachable through the focus trap; F10 preventDefault not checked in WebView2 |

## Next step

T4.
