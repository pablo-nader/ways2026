# Corrección de pagos de personal

## Objective

Let an Admin correct salary payments that belong to the staff ledger: edit the medio/split of their money lines, and
annul them completely.

## Problem

The owner reconciled Mercado Pago against Ways and must fix the medio of 21 salary payments (pago ids 234, 235, 238,
239, 241, 246, 250, 251, 266, 268, 273, 275, 277, 282, 283, 291, 295, 298, 300, 361, 363). `ServicioDePagos` rejects
any edit or delete of a pago with `id_movimiento_personal` (409 `gasto_de_pago_de_personal`), and the personal module
has no edit or annul. Known gap: "falta anular un pago".

## Design decisions

- **Edit = dedicated endpoint keyed by pago id**: `PUT /api/pagos/administracion/{id}/lineas-de-personal`, body
  `{ "lineas": [{ "idMedioPago", "importe", "referencia" }] }`, Admin (`GestionDeCatalogo`). Why not the generic PUT:
  its body carries categoría, proveedor, concepto, área, origen and empleado, and every one of those would have to be
  rejected or silently ignored for a staff payment (dto-contract-honesty). A body with only lines makes the invariant
  the contract. Why keyed by pago id and under pagos: the money lines live in the pago, the write reuses the private
  `EjecutarEscrituraAsync` path of `ServicioDePagos` (arqueo recalculation, treasury compensation, cuenta debits,
  `pago.edicion` audit), and the owner's script already has pago ids.
  - The sum of the lines must equal the pago's current importe (400 `importe_distinto_del_pago`), so the staff ledger
    and the CC discount stay untouched. A discount-only payment has no pago, so there is nothing to edit.
- **Annul = endpoint keyed by ledger movement**: `POST /api/personal/{id}/pagos/{idMovimiento}/anular`, Admin only.
  Keyed by movement because a discount-only payment has no pago. Reverses, in one transaction: the pago (soft delete
  through the same baja path as `DELETE /api/pagos/administracion/{id}`), the staff ledger (an `ajuste` movement of
  `+total` that points to the original through the new column `id_movimiento_anulado`), and the CC discount (an
  `ajuste` CC movement of `+descuento` on the same client and punto de venta). Audited as `pagopersonal.anulacion` (the action format forbids underscores).
  - The ledger is append-only, so the annulment is a compensating entry, not a delete. `id_movimiento_anulado` has a
    unique partial index (one annulment per payment), a composite self FK and a CHECK `tipo = 'ajuste'`.
- **Lock order of the annulment**: conciliación (shared) → turno `FOR UPDATE` → personal row → ledger movement →
  cliente row → pago → arqueo → treasury advisory last. Same relative order as the staff payment
  (`ServicioDePagoDePersonal`), the reassignment (personal → movimiento → pago) and the pago edit (turno → pago →
  advisory), so there is no cycle.

## Scope

- Backend: service methods, migration, endpoints, 23505 translation, audit action, contract fields on
  `MovimientoDePersonal` (`IdMovimientoAnulado` on the reversal, `AnuladoPor` on the original).
- Web: "Editar medios" and "Anular" on each payment of the employee account screen.

## Tasks

- [ ] T1 Backend: edit lines + annul + migration + unit and integration tests (delegated, one writer)
- [ ] T2 Web: actions in CuentaDePersonal with Vitest tests (delegated, one writer)
- [ ] T3 Guards (Application.Tests full, SuperficieDeAutorizacion, Web suite), judgment-day, PR

## Route declaration

- T1 delegated: 2+ non-trivial files (service, migration, endpoints, tests).
- T2 delegated: page, modals, API client and tests.

## Checks

- `dotnet test tests/Ways.Application.Tests`
- integration filters for the new endpoints, `SuperficieDeAutorizacion`, `AislamientoDeTenant`
- `npm test` in `src/Ways.Web` (single run)

## Progress

- 2026-11-08: exploration done, design recorded.
