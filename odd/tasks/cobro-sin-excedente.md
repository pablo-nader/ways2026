# Cobro sin excedente

## Objective

A sale payment can never charge a non-cash medio above the sale total, the POS proposes payment amounts, and the admin
cambio de medios can correct an already-overpaid sale.

## Problem

A Mercado Pago reconciliation found 28 sales (2026-09-17 to 2026-10-31) whose non-cash payment exceeds the total because
the cashier typed an extra zero (sale 250: total 22200, QR 222000). Ways accepted them and the cierre credited the inflated
amount to the MercadoPago treasury account ($4.15M shown, about $343k real).

Root causes: `ValidadorDePagos` only has a lower bound; the POS leaves an unassignable excess at 0 vuelto and shows it as a
green "Vuelto"; `ReglaDeCambioDeMedios.ExigirMismoTotalYAlgunCambio` pins the new medios to the old (inflated) cobros, so
an overpaid sale can never be corrected.

## Scope

- Domain rule 9 in `ValidadorDePagos` + web mirror in `validarPagosLocal`.
- POS amount proposals (first row = total, new row = remaining).
- Cambio de medios checks the new medios against the sale total when the old cobros exceeded it.

## Decisions

- Rule 9a `importe_supera_el_total`: a row whose medio does not admit vuelto cannot exceed total + tolerance.
- Rule 9b `cobro_supera_el_total`: Σ(importe − vuelto) cannot exceed total + tolerance (cash excess must be returned).
- Rule 9 goes last so the existing observable order of rules 0–8 is unchanged.
- Offline re-send of an already-emitted sale (`reenvioDeVentaYaEmitida`) skips rule 9, like the credit limit: the
  idempotency guard answers it (returns the existing sale or 409 on different content). Without the skip, re-sending one of
  the old overpaid sales would archive it as rejected even though it exists.
- A first-time offline sync (pre-assigned number not yet emitted) is validated with rule 9: the offline total uses the
  device prices, so a current POS that passed `validarPagosLocal` never trips it. Old POS versions that still allow the
  overpayment get the sale archived as rejected with the reason until the registers get the release.
- Remito invoicing (`ServicioDeFacturacionDeRemitos`) is an online back-office flow: rule 9 applies fully.
- Cambio de medios: if the old cobros (Σ importe − vuelto) are within the total, the new ones must cobrar the same
  (unchanged). If the old cobros exceed the total, the new ones must cobrar exactly the total. The other guards (anulada,
  comprobante, CAE, conciliación, cuenta corriente, Mercado Pago) are unchanged. A closed turno is compensated per medio
  from the bruto variation, so the corrected amounts reach the arqueo and the treasury account.

No schema change.

## Tasks

- [x] T1 Domain: rule 9 + reenvío flag + cambio de medios rule, unit tests (route: inline, parent)
- [x] T2 Web: validarPagosLocal mirror, vuelto display, proposals, cambio de medios mirror, Vitest (route: delegated writer, 2+ non-trivial files)
- [x] T3 Integration tests POST /api/ventas and PUT /api/ventas/administracion/{id}/medios (route: inline)
- [ ] T4 Structural guard tests, judgment-day, PR

## Checks

- `dotnet test tests/Ways.Domain.Tests --filter ValidadorDePagos|ReglaDeCambioDeMedios`
- filtered integration tests for ventas checkout, offline re-send, remitos and cambio de medios
- `npx vitest run` on the touched web files

## Progress

- T1: domain suite 2609 passed (rule 9 tests, cambio de medios target tests).
- T2: Vitest 182 passed on touched files, full `Pos.test.tsx` 572 passed; `tsc -b` clean; oxlint clean.
- T3: `CambioDeMediosDeVentaTests` 15 passed (overpaid QR rejected, re-send exempt, admin correction compensates arqueo
  and treasury); remitos 32 passed. Mutation: forcing rule 9 on the re-send fails the re-send test.
- Existing fixtures that paid cash above the total without vuelto were fixed (`PlanDeVentaFefoTests` article price,
  `CombosVentaOfflineTests` cash = total). Filtered batch of sale-related classes: 2543/2546, the 3 failures were those
  fixtures, now green.
- `tests/Ways.Application.Tests` (structural guards): 1660 passed.
- Follow-up (stacked PR): a CC customer's non-cash excess becomes a pago a cuenta (RC) instead of being rejected.
