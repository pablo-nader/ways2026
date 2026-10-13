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
- [x] **T4 — Customer in the cart header** (delegated)
  - Contains-filter select replacing the "Carrito" title; removed from
    "Datos de la venta"; "Ver detalle" icon for current account; balance chip
    (used / limit, progress style).
  - Right panel ("Datos de la venta") reworked with the freed space (owner, 2026-10-12):
    sale total first and large (at least twice the height of a current row), then the
    payment methods, then ONE line that reads "Falta" (amount in red) while the payment
    is incomplete and "Vuelto" (amount in green) otherwise, and the Cobrar button last
    as it is today.
- [x] **T5 — Current-account modal and tickets** (delegated)
  - Max viewport height with scroll; no "Cerrar" button; "Registrar pago" on top;
    date range (default today − 7 days).
  - Payment ticket (previous balance, payment, new balance); drawer pulse when any
    cash; sale ticket for a customer shows balance before and after.
- [x] **T6 — Totals by payment method at close** (delegated)
  - Close summary: per method, total digital, total cash, grand total (screen + ticket).
- [ ] **T7 — Fees and withholdings per payment method → treasury** — BLOCKED on an
  owner decision: treasury today is a cash-only ledger with no accounts; non-cash
  sales never reach it.
- [ ] **T8 — POS release** (`Ways.Desktop` bump + tag) once T1–T6 are in main.

## Progress

| Task | Route | Commit | PR | Checks |
|------|-------|--------|----|--------|
| T1 | delegated (writer trigger: 16 files) | 7ada332f, b9090eb3, 9cb176a8, ff59cebe | #405 (merged) | tsc clean; oxlint exit 0; vitest 208 files / 5790 passed; judgment-day: round 1 no severe (7 warnings fixed in 9cb176a8), scoped re-judgment clean, polish ff59cebe; not checked in a real Tauri window |
| T2 | delegated (writer trigger: 17 files) | f9b7f3ab, b0c0c368, 02d91938, d9909ed3, 0eb7eeb1 (merge de main) | #406 (merged) | tsc clean; oxlint exit 0; vitest 209 files / 5830 passed; judgment-day: round 1 no severe (fixes in 02d91938), scoped re-judgment no severe (polish d9909ed3); not checked in a real Tauri window |
| T3 | delegated (preparation trigger: Pos.tsx 4.6k lines) | 5f0a4264, 0d7268ab | #409 (merged) | tsc clean; oxlint exit 0; vitest 212 files / 5965 passed on the branch merged with main; judgment-day: round 1 no severe (2 confirmed warnings fixed in 0d7268ab), scoped re-judgment no severe; residual: local-snapshot scan behind the modal, unreachable through the focus trap; F10 preventDefault not checked in WebView2 |
| T4 | delegated (writer trigger: 13 files) | ac9bd2a9, 606a2a0f, 794e02cb, 84c49383, 395cfc0e, e6f18d5c | #410 (merged) | tsc clean; oxlint exit 0; vitest on the branch merged with main (see PR); judgment-day: round 1 no severe (fixes in 395cfc0e), scoped re-judgment no severe; carried to T5: truncated-list announcement, clear remote cache when the text is emptied; not checked in a real browser (1366x768 header, chip, popup) |
| T5 | delegated (writer trigger: 12 files) | 969460d3, 169ef31e, 3a50542b, fcef1ff5, bad215ae, d5a2ff27 | #416 | tsc clean; oxlint exit 0; vitest 216 files / 6148 passed (merged with main); judgment-day: round 1 found one severe (balances ignored queued offline sales) fixed in fcef1ff5; round 2 fixes d5a2ff27; final re-judgment no severe. Open follow-ups: a customer re-read started before a sale can land after it and re-confirm a stale balance when the customer stays selected (presupuesto mode / no Consumidor Final); one re-read per drained sale (no debounce); a failed re-read after a drain is not retried; not checked on a real printer |
| T6 | delegated (writer trigger: server + web, 18 files) | 800fe93d, 17437782, ed37e4ed, d18b921c | this PR | build 0 errors; Domain.Tests 1467 passed (after merging main); Application.Tests 871 passed; integration only filtered Caja classes (21 passed) — full integration suite left to CI; tsc clean; oxlint exit 0; vitest 217 files / 6161 passed (before the last merge of main). judgment-day: round 1 no severe but a double count in the grand total (credit sale + its cash receipt) fixed in ed37e4ed/d18b921c; scoped re-judgment no severe. Decision: closing totals mean money collected (digitales + efectivo = general); credit sales reported apart as "Fiado en cuenta corriente". Open follow-ups: a cash anchor later reclassified to CuentaCorriente shows its cash as fiado on reprint; "Sin ventas en el turno" wording when there is only fiado; no unit test for credit notes |

## Next step

T7 once the owner decides (accounts with their own balance vs. report only); then T8 release.
