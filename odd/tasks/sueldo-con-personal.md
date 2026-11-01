# Sueldo con personal

## Objective

A Sueldos payment can name the employee, and naming one records it as a pago de personal
(the employee's libro-de-horas balance drops), on every screen that loads or edits a payment.

## Problem / why

The payment forms always offered the provider picker; a Sueldos payment carried no employee,
so a salary paid from Pagos or from the POS cash drawer never reached the employee's account.

## Decisions (owner, 2026-11-01)

- Sueldos with an employee = **pago de personal** (existing `POST /personal/{id}/pagos`), not
  a label on the gasto. "Sin empleado" keeps the plain Sueldos payment.
- POS: least privilege. The cashier sees only id + nombre of the employees (no saldo, no
  valor hora), pays only from the turno's cash (`CajaTurno`) and cannot discount the cuenta
  corriente. Online only.
- Edit: assigning an employee to an existing plain Sueldos payment converts it into a pago de
  personal in one transaction (the plain payment is removed/annulled and the pago de personal
  is created), honoring the turno/arqueo rules the payment already lives under.

## Scope

- Back office Pagos create form (done, PR #600).
- POS `GastosDelTurno`: backend options endpoint + POS-allowed pago de personal; web picker.
- Edit modal: backend conversion endpoint; web picker in `ModalDeEdicionDeGasto`/its renamed
  equivalent.

## Out of scope

- Editing an existing pago de personal (still blocked, unchanged).
- Offline POS queue for pagos de personal.

## Tasks

- [x] T1 — Back office create form: Sueldos shows the employee picker and saves as pago de
  personal. Route: delegated writer. Evidence: PR #600 (merged 2026-11-01), commit 7de0bb53,
  judgment-day APPROVED.
- [x] T2 — POS: personal options for the cashier + pago de personal from `CajaTurno` allowed to
  the POS role (no CC discount); web picker in the POS payment screen. Route: delegated writer
  (backend + web, 2+ non-trivial files). Branch `pnader/sueldo-personal-pos`.
  Evidence (writer, uncommitted): separate POS routes `GET /api/pos/personal/opciones` (id + nombre, vivos) and
  `POST /api/pos/personal/{id}/pagos`, both `OperacionDePos`; `ServicioDePagoDePersonal.PagarDesdeElPosAsync` reuses the
  back-office write path and rejects (403 `prohibido`, after the 404) any origin other than `CajaTurno` and any
  `descuentoDeCuentaCorriente`; back-office route unchanged. Tests: `PagoDePersonalDelPosTests` (5) RED 4 failed (404) ->
  GREEN; guard mutation (call removed) killed by the reject test. Web: `PagosDelTurno` (the file is not named GastosDelTurno)
  shows `Empleado (opcional)` on Sueldos, saves via `/pos/personal/{id}/pagos`, offline disables it. Results: dotnet build ok;
  Application.Tests 1638, Domain.Tests 2322 passed; integration (PagoDePersonal*, CuentaDePersonal*, AislamientoDeTenant,
  SuperficieDeAutorizacion, RutasDeLaSpa, PersonalEndpoints, OpenApi) 194 passed; vitest 308 files / 9415 tests passed;
  tsc -b ok; lint 0 errors. Commits 7f8a2d75 + 883729c3 (fixes F1 saldo, F2 retrofecha, F3 offline) + doc; judgment-day APPROVED (round 1 scoped re-judgment clean).
  Judgment-day fixes (status fixed): F1 the POS route answers `ReciboDePagoDePersonalDelPos` (idMovimiento, fecha, importe),
  never the employee balance; back office unchanged. F2 the POS date is always today in the PV zone: `fecha` may be omitted
  (the web no longer sends it) and any other date answers 400 `fecha_no_es_hoy`; the test rejects past and future dates and
  fails with the guard disabled (mutation killed, guard restored). F3 offline the employee select stays enabled, only the
  employee options are disabled, so "Sin empleado" remains selectable for a plain Sueldos pago; no auto-reset.
- [ ] T3 — Edit: convert a plain Sueldos payment into a pago de personal (backend transaction +
  web picker in the edit modal). Route: delegated writer. Branch stacked on T2 or main.

## Checks

- Backend: `dotnet test` unit + the integration tests touched; structural guard tests.
- Web: `npx vitest run`, `npx tsc -b`, `npm run lint`.
- judgment-day APPROVED per PR.
- POS reaches the cajas only with a Ways.Desktop bump + `pos-v*` tag (owner decides).

## Next step

T2.
