# Cambio de origen de fondos de un pago

## Objective

Let an Admin, from the back office (`/pagos`), change where a payment's money came from — the shift's cash drawer
(`CajaTurno`) or the company's general cash (`Tesoreria`) — after the fact, including on a closed shift, leaving a trace.

## Problem / why

Cashiers mark supplier payments with the wrong origin (paid from the drawer but marked general cash, and vice versa).
Until bank/treasury reconciliation exists the owner must fix these from the back office. Every other correction
(withdrawals, sale payment methods, payment media) already works on closed shifts; the origin is the only gap.

## Scope

- Only payments that already carry a shift (`id_turno_caja IS NOT NULL`, i.e. registered from the POS). Owner decision
  2026-11-03: back-office payments without a shift stay `Tesoreria`; choosing a target shift is out of scope.
- Only through `PUT /api/pagos/administracion/{id}` (policy `GestionDeCatalogo`). The POS `PUT /api/pagos/{id}` rejects
  the new field.
- Both directions, combinable with the existing edits (media, amount, category...) in the same request.

## Constraints

- The shift does not change, so the first lock key (shift) is unchanged; origin must be re-read under the payment lock
  (single read under lock), never trusted from the pre-lock read.
- Closed shift: recalculate the arqueo when the transition affects it (`RecalculoDeArqueoDeTurnoCerrado`, stamps
  `fecha_recalculo` / `id_empleado_recalculo`).
- Treasury cash chain (`movimientos_tesoreria`): net effect of old vs new origin and amount, as an `Ajuste` row.
- Conciliation guard unchanged (same shift and payment date).
- Audit payload records `origen_fondos` before/after.
- No schema change expected.

## Model summary

No new tables or columns. `pagos.origen_fondos` becomes editable for rows with `id_turno_caja` set;
`ck_pagos_caja_turno_requiere_turno` already holds for both values in that case.

## Tasks

- [x] T1 — Backend: optional `origenFondos` in `SolicitudDeEdicionDePagoConLineas`, admin-only; transition-aware
  `AfectaElArqueo` / `AjusteDeTesoreriaPara`; guards (POS rejects, no shift → 409); audit; unit + integration tests.
  Route: delegated (writer trigger: 2+ non-trivial files).
  Evidence: `dotnet build` 0 errors; `dotnet test tests/Ways.Domain.Tests` 2596 passed; `dotnet test tests/Ways.Application.Tests`
  1660 passed (structural guards); integration `Pagos*` + `SuperficieDeAutorizacion` 315 passed; filtered PagosEdicion/
  PagosOrigenFondos/PagosDeAdministracion 58 passed. Mutations killed: domain origen anterior (2 fail), tesorería origen
  anterior (2 fail), guarda `pago_sin_turno` (1 fail), rechazo POS (1 fail), origen anterior capturado tarde (2 fail).
  Codes: `origen_solo_administracion` (400, POS), `pago_sin_turno` (409, admin). Commit a36d1caa. judgment-day: APPROVED round 1 (0 critical; suggestions: baja test name tightened, OpenAPI summaries and Ajuste detail text left as info).
- [x] T2 — Web: origin selector in `ModalDeEdicionDePago` only for admin and payments with a shift; request builder;
  Vitest tests. Route: delegated.
  Evidence: `npx tsc -b` clean; `npm run lint` 0 errors; vitest Pagos/PagosDelTurno/edicionDePago 196 passed; full
  `vitest run` 330 files / 10222 tests passed. POS never sends the field (builder omits it without `origen`); 409
  `pago_sin_turno` mapped in `copiaDeFalloDeEdicion`.
- [ ] T3 — judgment-day, PR(s) stacked-to-main, auto-merge + Auto-fix.

## Acceptance criteria

- Admin changes CajaTurno → Tesoreria on a closed shift: the shift's expected cash rises by the payment's cash lines, the
  shift shows "Arqueo recalculado", the treasury chain gets the matching egress adjustment, audit shows both origins.
- Tesoreria → CajaTurno: symmetric.
- Admin request on a payment without a shift asking for CajaTurno → 409, nothing written.
- POS request carrying `origenFondos` → 400.
- Unchanged origin behaves exactly as today.

## Checks

- `dotnet test tests/Ways.Domain.Tests`
- `dotnet test tests/Ways.IntegrationTests --filter "FullyQualifiedName~PagosEdicion|FullyQualifiedName~PagosOrigenFondos"`
- Structural guard tests (see `structural-guard-tests` skill).
- `npm test` in `src/Ways.Web` (filtered to pagos files, then full).

## Progress

- 2026-11-03: exploration done; scope decided by owner (POS payments only).

## Next step

T3.
