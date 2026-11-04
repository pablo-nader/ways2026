# Editar movimientos desde el detalle del turno

## Objective

An Admin corrects a closed shift's movements (withdrawals, sale payment methods, supplier payments)
straight from the shift detail, without hunting in other grids.

## Problem / why

The owner opened a closed shift from Caja → Histórico and found no way to edit. The three backend
corrections already work on closed shifts and recalculate the arqueo with a mark, but their entry
points live only in `/caja/retiros`, `/caja/ventas` and `/pagos`.

## Scope

- Caja → Histórico links to `CajaZ` (`/caja/turnos/:id/z`), which renders `DetalleDeTurnoListas`.
  That is the screen to extend. `ModalDetalleDeTurno` belongs to the desktop POS ("Cierres de caja")
  and stays read-only.
- Opt-in edit actions in `DetalleDeTurnoListas`: per ticket → `ModalDeCambioDeMedios` (modo
  administracion); per payment → `ModalDeEdicionDePago` with `origenEditable` (PR #632).
- `CajaZ` gains a withdrawals list for the shift (the detail endpoint carries only their total),
  each row → `ModalDeEdicionDeRetiro`.
- Enabled only for `puedeGestionarCatalogos` and only in the web app (not inside the desktop POS shell).
- After a save, the detail reloads so `InsigniaDeRecalculo` and the totals reflect the recalculation.

## Constraints

- Reuse existing admin endpoints and modals; no backend change.
- `react-async-state` (generation gating, re-entrancy refs) and `web-test-data-gates`.
- Stacked on PR #632 (branch `pnader/pago-cambio-origen-web`), PR base `main`.

## Tasks

- [x] T1 — Edit entry points in Caja Z + Vitest tests (route: delegated writer, trigger: 2+ non-trivial files)
  - Evidence: CajaZ 49/49, DetalleDeTurnoListas/Pagos/utilidades 140/140; tsc -b clean; 5 App/POS files 834/834 after npm ci; full suite 9557 passed (one known-flaky GrillaDeItemsDeCompra test once).
  - Notes: tickets gated on estado Emitido (TicketDeTurno has no medioEditable, server 409 surfaces in the modal); retiros page clamp 200 with "Se muestran X de N" + link to /caja/retiros (no URL filters there).
- [ ] T2 — judgment-day review, PR, auto-merge, Auto-fix

## Acceptance criteria

- Admin sees edit buttons for tickets, payments and withdrawals of the shift; Supervisor/Vendedor do not.
- Saving reloads the detail; the recalculation badge appears.
- Payments born from personal are not offered for editing.
- Tests cover role gating, each modal opening, and the reload after save.

## Checks

- `npx vitest run` (affected files, then full suite), `npx tsc -b`, `npm run lint` in `src/Ways.Web`.

## Progress

- Exploration done 2026-11-03.
- T1 done 2026-11-03. Next: T2 judgment-day.
