# Horarios en 24 h y retiros en una línea

Locator: `odd/tasks/horarios-24h-y-retiros-en-una-linea.md` — Engram mirror: `odd/horarios-24h-y-retiros-en-una-linea/tasks`

## Objective

Every time shown by Ways.Web (screens and printed tickets) uses the 24-hour clock, and the
closing ticket lists each withdrawal on a single line.

## Problem

- `new Date(iso).toLocaleString('es-AR')` renders 16:05:09 as `04:05:09` with no AM/PM marker
  (observed on Node ICU 78.2: `12/10/2026, 04:05:09`). An afternoon opening or closing time is
  indistinguishable from a morning one. About 24 call sites share the pattern; three more in
  `EditorDePrecios.tsx` use `toLocaleString()` with no locale at all.
- The closing ticket prints each withdrawal on two lines: `HH:mm  $importe` and then
  `  <motivo> (<empleado>)`. The motive text ("Retiro de efectivo", "Retiro de cierre de turno")
  repeats the section title "Retiros:".

## Scope

- One shared date-time formatter in `src/Ways.Web/src/formato/fechas.ts` forcing `hourCycle: 'h23'`;
  every date-time call site in `src/Ways.Web/src` uses it.
- `armarCierreDeTurno` in `impresion/plantillas.ts`: one line per withdrawal — time, short label,
  amount. "Retiro de efectivo" → `Efectivo`, "Retiro de cierre de turno" → `Cierre`; any other
  motive (manual back-office movement) keeps its own text. The employee name is dropped from the line.

Out of scope: backend, the recalculation mark on the reprinted closing ticket (pending the
owner's answer on the reported cash discrepancy), number formatting (`toLocaleString` with
fraction-digit options).

## Constraints

- Web-only change; the POS desktop bundle needs a Ways.Desktop release to reach the tills.
- Date-only formatting and numeric formatting are untouched.

## Tasks

- [x] T1 — 24-hour date-time formatter and call-site migration. Route: delegated (writer; ~25 files).
      Checks: `formato/fechas.test.ts` proves 16:05 renders as `16:05` (RED first), full web suite, tsc, lint.
- [x] T2 — Withdrawals on one line in the closing ticket. Route: delegated (same writer).
      Checks: `impresion/plantillas.test.ts` RED first, full web suite.

## Acceptance criteria

- No date-time in the web renders an afternoon hour as 01–11.
- No `toLocaleString('es-AR')` without options and no bare `toLocaleString()` on a date remains in
  non-test code under `src/Ways.Web/src`.
- Closing ticket: `16:05 Efectivo .... $ 1.000,00` and `18:00 Cierre .... $ 5.000,00`, one line each.

## Progress / evidence

- T1 done. RED: fechas.test.ts 2 failed (fechaHoraATexto not a function); GREEN: fechas + plantillas 72 passed. tsc -b --noEmit: clean. oxlint: 0 errors (pre-existing warnings only). Full vitest: 206 files / 5781 tests passed. Residual rg for toLocaleString('es-AR')/toLocaleString() in non-test code: empty. Shared helper: fechaHoraATexto (hourCycle h23); 24 call sites + 7 tests migrated. formatearHora (hour12:false) renders 00:30 on this ICU, left unchanged. T1 commit 64acac5b.
- T2 done. RED: plantillas.test.ts 2 failed (one-line withdrawals, long free-text motive); GREEN: fechas + plantillas 73 passed. tsc clean, oxlint 0 errors, full vitest 206 files / 5782 tests passed. Long motives are truncated by the existing dosColumnas (amount stays on the line). T2 commit cd72b92d.
- Judgment day (two blind judges over main..cd72b92d): no severe findings. Both raised one WARNING — `formatearHora` used `hour12: false`, which could print midnight as 24:xx on some engines; on Node ICU 78.2 it prints 00:30 either way. Aligned to `hourCycle: 'h23'` anyway in 7c32496f (plantillas tests 53 passed, tsc clean). JUDGMENT: APPROVED.
- Review tier: RDD off (clone-local); `gentle-ai review assess` unassessable on committed work, treated as high: writer self-verification + two independent judges + parent spot check (fechas + plantillas 73 passed).

## Next step

Open the PR. Pending outside this feature: release of Ways.Desktop so the tills get the new bundle; the recalculation mark on the reprinted closing ticket waits for the owner's answer.
