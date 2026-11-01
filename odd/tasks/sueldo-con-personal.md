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
  web picker in the edit modal). Route: delegated writer. Branch `pnader/sueldo-personal-edicion`, stacked on T2 (#606).
  Evidence (writer, uncommitted): no new route and no migration: `idPersonal` is an optional field of the two existing edit
  bodies (`PUT /api/pagos/administracion/{id}` and `PUT /api/pagos/{id}`), so one transaction both edits and converts.
  `ServicioDePagos.ConvertirEnPagoDePersonalAsync` (inside `EjecutarEscrituraAsync`, after the pago lock and its guards)
  debits the employee with the same raw `UPDATE ... RETURNING` + `InsertarMovimientoAsync` pair as `ServicioDePagoDePersonal`,
  links `IdMovimientoPersonal`, sets the concepto `Pago de personal: {nombre}` and the matching treasury row text; the pago,
  items, origin, turno and fecha are untouched, so arqueo and treasury balances do not move. Guards: 404 for pago then employee
  before any 400; `pago_personal_requiere_sueldos` / `pago_personal_sin_proveedor` / `concepto_de_pago_de_personal` (the concepto
  is server generated so a non-blank one is rejected, not dropped); `gasto_de_pago_de_personal` 409 re-checked under the pago
  lock (the conversion is now the one writer of `id_movimiento_personal` after creation). POS keeps its rules (open turno,
  `gasto_turno_cerrado`) and its response (`PagoRegistrado`) has no balance. Tests: `PagoDeSueldoConvertidoEnPersonalTests` (8)
  RED 7 failed -> GREEN; mutation: Sueldos guard disabled -> killed, under-lock re-check removed -> killed by the concurrent
  conversion test. Web: `ModalDeEdicionDePago` shows `Empleado (opcional)` for Sueldos (`personal` prop), POS reads
  `GET /api/pos/personal/opciones`, offline disables the options only. Results: dotnet build ok; Application.Tests 1638, Domain.Tests 2322 passed; integration (new class, PagosEdicion*, PagoDePersonal*, PagosConLineas/ApiConLineas, SuperficieDeAutorizacion, AislamientoDeTenant, OpenApi, ConciliacionDeTesoreria) 173 passed; vitest 308 files / 9428 tests passed; tsc -b ok; lint 0 errors.
  Judgment Day round 1 fixes (G1-G3): G1 the edit modal no longer drops the provider on entering Sueldos (a pago with a
  provider keeps showing the provider select; the employee picker appears once the provider is set to "Sin proveedor", and
  choosing an employee sends `idProveedor: null`); component test in `Pagos.test.tsx` killed by restoring the old clearing.
  G2 five integration tests (fecha in the empresa zone, 2-line total, 400 `pago_personal_sin_proveedor`, `periodo_conciliado`
  409, treasury concepto rewrite), all five killed together by mutating `ServicioDePagos` (UTC date, provider check off, conciliation
  guard off, importe halved, concepto UPDATE neutralized) and guard restored; no real bug found. G3 stale comment in `Pagos.tsx` fixed.
  Results: dotnet build ok; PagoDeSueldoConvertidoEnPersonalTests + PagosEdicionEndpoints 37 passed; vitest Pagos/PagosDelTurno/sueldoConPersonal 172 passed; tsc -b ok; lint 0 errors.

## Checks

- Backend: `dotnet test` unit + the integration tests touched; structural guard tests.
- Web: `npx vitest run`, `npx tsc -b`, `npm run lint`.
- judgment-day APPROVED per PR.
- POS reaches the cajas only with a Ways.Desktop bump + `pos-v*` tag (owner decides).

## Next step

T2.
