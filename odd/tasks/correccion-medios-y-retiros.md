# Corrección de medios de pago y retiros

## Objective

Let cashiers fix the medio de pago of a ticket from the POS while the turno is open, and let
the back office fix a venta's medios and the retiros a cashier recorded wrong, as long as the
conciliación de tesorería has not frozen that date.

## Problem / why

Cashiers pick the wrong medio (efectivo vs. tarjeta, one card vs. another) and type retiros
wrong. Today nothing edits a venta's `cobros` and `movimientos_caja` is append-only, so the
arqueo and the libro de tesorería carry the mistake forever.

## Owner decisions (2026-11-01)

- A venta with an approved CAE cannot change medios (anular and redo). Without CAE the change
  is allowed and the venta's aporte moves from the old medio's `acumulados_de_facturacion` to
  the new one; no factura is emitted or anulled by the change.
- Cuenta corriente is excluded: neither the old nor the new medio may be `CuentaCorriente`.
- Admin may edit importe and motivo of a retiro and delete it. The automatic
  "Retiro de cierre de turno" row is not editable.
- Every new grid (POS tickets del turno, back-office ventas, back-office retiros) has a filter
  on every column and click-to-sort on every column.

## Scope

- POS: change medios of a ticket of the open turno (`VentasDelTurno`).
- Back office (Admin): change medios of any venta; edit/delete retiros; open or closed turno.
- Guards: CAE, cuenta corriente, conciliación (`GuardaDeConciliacion` shared lock first),
  turno abierto for the POS, medios validation (`ValidadorDePagos`), same total.
- Closed turno: recalculate the arqueo with the #322 marca (`fecha_recalculo`,
  `importe_esperado_original`), append `movimientos_tesoreria` Ajuste for retiros, and
  compensate `movimientos_cuenta` for electronic medios that already credited a cuenta.
- Audit every edit (`AccionAuditada`).

Out of scope: editing cuenta corriente cobros, editing the cierre retiro, Mercado Pago.

## Constraints

- Offline queue: the replay dedup compares pagos (`ExigirMismoContenido`). Editing a venta the
  device has not acknowledged would make its retry answer 409; the POS only edits ventas it
  lists from the server, and the dedup must accept a replay of an already-edited venta.
- Lock order follows `ServicioDePagos.EjecutarEscrituraAsync`: conciliación shared → turno →
  rows; single read under lock.
- POS web changes reach the cajas only with a Ways.Desktop bump + `pos-v*` tag.

## Delivery strategy

Forecast well above 400 authored lines → chained PRs, `stacked-to-main` (project protocol).
Each slice passes judgment-day before its PR.

## Tasks

- [x] T1 — Backend: change a venta's medios (service, POS + admin endpoints, guards, facturación
  acumulados, closed-turno arqueo + cuenta compensation, audit, offline dedup), with tests.
- [ ] T2 — Backend: retiros list (filters + sort) and admin edit/delete (soft delete, arqueo
  recalc, tesorería Ajuste, audit), with tests and migration.
- [ ] T3 — Backend: back-office ventas list with per-column filters and sort, with tests.
- [ ] T4 — Web: reusable sortable header + POS `VentasDelTurno` sort and "cambiar medio" modal.
- [ ] T5 — Web: back-office ventas grid (filters + sort) with medio edit.
- [ ] T6 — Web: back-office retiros grid (filters + sort) with edit/delete.

## Acceptance criteria

- POS changes medios only with the turno open; admin with any turno; both refused with 409
  when the date is conciliada, the venta has CAE, or a cuenta corriente medio is involved.
- Total of cobros unchanged; arqueo and libro reflect the change; audit row written.
- Grids filter on every column and sort on every column.

## Progress

| Task | Route | Commit | Review | Checks |
|------|-------|--------|--------|--------|
| T1 | delegated (writer: 2+ non-trivial files) | a672d216 (refactor), f5e2fc1d (feat) | pending (judgment-day at slice) | build 0 errors; Domain.Tests 2258/2258; Application.Tests 1639/1639; integration CambioDeMedios + SuperficieDeAutorizacion + regression (pagos edición, checkout, anulación, offline/reservas, facturación, cuentas, conciliación, sin reintento) 430/430; 5 mutants killed |

## T1 notes

- Endpoints: `PUT /api/ventas/{id}/medios` (OperacionDePos, open turno) and `PUT /api/ventas/administracion/{id}/medios`
  (GestionDeCatalogo). Codes: `venta_turno_cerrado`, `venta_con_cae`, `venta_con_factura_en_tramite`, `venta_anulada`,
  `comprobante_sin_cambio_de_medios`, `cambio_de_medios_con_cuenta_corriente`, `total_de_cobros_distinto` (400),
  `cambio_de_medios_sin_diferencia`, `periodo_conciliado`, plus `ValidadorDePagos` codes.
- No schema change: old cobros soft-deleted, new inserted; offline replay matches the deleted cobros born with the
  comprobante (`created_at`). Facturación: stored decision aportes rewritten; ventas without a decision row recompute the old
  devengo with current percentages (documented limitation). Closed turno: shared arqueo recalculation + one `ajuste` per
  credited medio, or cierre rows for a medio the cierre never credited.
- Open: behavioral transient-retry test for the new non-retrying site (structural guard only).

## Next step

T2.
