# Retiro obligatorio y efectivo en gastos

## Objective

Tighten the cash-handling flows of the POS: a withdrawal that opened the drawer must end in
a registered withdrawal, a cash expense must show whether the till can cover it and open the
drawer, and a new expense must carry an area by default.

## Problem / why

- The "Monto retirado" modal can be cancelled after the drawer is already open, leaving an
  audited drawer opening with no withdrawal.
- A cash expense paid from the till gives no hint about available cash and does not open the
  drawer, so the cashier cannot physically pay it.
- The expense creation form has no area field (only the edit modal does), so every expense is
  stored without area.

## Scope

Web only (`src/Ways.Web`). No backend, schema or desktop changes.

Out of scope: the "Cerrar caja" flow, the `/caja` inline movement form, server-side rejection
of amounts above available cash, cash checks when editing an expense.

## Decisions

- D1: The first retiro modal ("¿Querés retirar efectivo?") stays dismissable: the drawer is
  still closed there. Only the amount modal (drawer already open) is locked.
- D2: A failed `Retiro` POST keeps the locked modal open with its error so the cashier retries.
- D3: "Cash from the till" means medio `comportamiento === 'Efectivo'` AND
  `origenFondos === 'CajaTurno'`. Efectivo paid from Tesorería neither warns nor opens the drawer.
- D4: Available cash is the ancla line of `detalle.resumen` (`importeEsperado`, 0 when absent):
  sales in cash + fondo + refuerzos − retiros − cash expenses − vueltos.
- D5: The shortfall is a non-blocking inline warning shown before submitting.
- D6: The drawer pulse is queued after the expense POST succeeds, only when the desktop seam
  is present. The expense itself is the audit trail; no extra `AperturaCajon` movement.
- D7: Default area follows the rest of the system (`useCatalogosDeArticulo`): first active area.
  Creation forms have no empty "Seleccione" option when at least one area exists.

## Tasks

- [x] T1 — Non-dismissable amount modal in Retirar. Route: delegated (Modal + Pos + tests).
  - `Modal` gains a non-dismissable mode: no X, Escape and backdrop ignored (Escape still
    swallowed only by the top modal of the stack).
  - The "Monto retirado" modal uses it and loses its "Cancelar" button.
  - Acceptance: with the amount modal open, X is absent, Escape/backdrop do nothing, and the
    modal closes only after a successful withdrawal with amount > 0.
- [x] T2 — Cash expense: availability, warning and drawer. Route: delegated.
  - Pure helper for available cash and "is cash from the till" with unit tests.
  - `GastosDelTurno` shows available cash when D3 holds, warns when importe exceeds it, and
    queues `pulsoDeCajon()` after a successful registration.
  - `ShellPos` passes the desktop seam into `GastosDelTurno`.
- [x] T3 — Area on expense creation. Route: delegated.
  - `GastosDelTurno` creation form gets the area select, defaulted per D7, sent in the request.
  - `Gastos.tsx` (admin) creation form drops the empty option and defaults per D7.

## Checks

- Vitest for the touched files (one vitest process at a time), `tsc` and lint of `Ways.Web`.

## Progress

- T1 7670cf63 feat(pos): el modal de monto de Retirar no se puede descartar. RED observado (Modal 4 tests, Pos 2 tests), GREEN; mutaciones: sin condicion de la X, sin preventDefault de Escape, sin chequeo de tope de pila, noDescartable reemplazado por onCerrar en Pos: cada una mata sus tests.
- T2 16627f94 feat(gastos): efectivo disponible, faltante y apertura de cajón. RED observado (10 tests de helpers, 5 de componente), GREEN; mutaciones D3 (sin origen, sin comportamiento), ancla, faltante sin Math.max, seam sin pasar en ShellPos: todas muertas. La captura previa al await no es matable: registrarGasto cierra sobre los valores del render.
- T3 8171f7fa feat(gastos): área al crear un gasto. RED observado, GREEN; mutaciones: default sin filtro de activas, sin reset, idArea null, sin default tardío, sin reset en admin, opción vacía habilitada: todas muertas.
- Checks: vitest completo 181 archivos / 4677 tests verdes; tsc -b exit 0; oxlint exit 0 (solo warnings preexistentes).
- Seguimiento de revisión (fix(gastos)): pulso del cajón aislado del catch del alta; "Registrar" deshabilitado mientras `areas === null` (Gastos admin no tiene la ventana: sin medios ni empresa cargados el formulario no se completa); aviso si fallan las áreas; sin faltante con importe no positivo; fixtures de áreas inactivas fuera de los tests de componente; fila de creación rebalanceada (6 campos col-md-2, observaciones col-md-10 + Registrar col-md-2). Vitest completo 181 archivos / 4682 tests, tsc -b 0, oxlint 0.
