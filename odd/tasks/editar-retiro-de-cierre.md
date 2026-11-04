# Editar el retiro de cierre de turno

## Objective

Let an Admin correct (and delete) the closing withdrawal of a shift (`MovimientoCaja` with `EsRetiroDeCierre = true`) from
the back office, also on a closed shift, while its period is not conciliated, leaving the recalculation mark.

## Problem / why

Cashiers often type the wrong amount when they close by withdrawal. The owner asked from the start to correct every
movement (withdrawals, payment media of tickets, payments) until conciliation exists. Today
`ReglaDeEdicionDeRetiros.ExigirEditable` rejects the closing withdrawal with 409 `retiro_de_cierre_no_editable`.

## Scope

- `PUT /api/caja/retiros/{id}` and `DELETE /api/caja/retiros/{id}` (Admin) accept the closing withdrawal.
- The amount is editable; the motive of a closing withdrawal stays "Retiro de cierre de turno" (prints map that literal to
  "Cierre"): a request that changes it answers 400 `motivo_de_cierre_no_editable`.
- Web: `/caja/retiros` and the Caja Z page enable the edit/delete buttons for the closing withdrawal; the modal shows the
  motive read-only for it.

## Constraints

- Everything else in `ServicioDeRetiros.EscribirAsync` is unchanged: conciliation guard, lock order, closed-shift arqueo
  recalculation (`RecalculoDeArqueoDeTurnoCerrado`, stamps the mark), treasury `Ajuste`, audit.
- `ImporteDeclarado` is not touched: in close-by-withdrawal it is the fund left in the drawer; correcting the withdrawn
  amount moves `ImporteEsperado`, so the difference becomes the real surplus/shortfall.

## Model summary

No schema change.

## Tasks

- [x] T1 — Backend + web in one slice: drop the closing-withdrawal block, add the motive guard, update domain, integration
  and Vitest tests. Route: delegated (writer trigger: 2+ non-trivial files).
  Evidence: dotnet build 0 errors; Domain.Tests 2597 ok (before the new cases); Application.Tests 1660 ok; IntegrationTests
  `Retiros|SuperficieDeAutorizacion` 56 ok (old 409 test watched failing first); vitest retiros+util+CajaZ ok, full suite
  10246/10247 (the one failure, GrillaDeItemsDeCompra, is unrelated/flaky and passes alone 134/134); tsc -b clean; oxlint warnings only
  pre-existing. Mutation proofs: guard `if (false && ...)` kills the integration 400 test and the domain test; removing
  `readOnly` kills the modal test. Motive guard runs under the lock against the single locked read of the row.
- [x] T2 — judgment-day APPROVED round 1 (0 critical; two inaccurate comments fixed as info). PR, auto-merge + Auto-fix.

## Acceptance criteria

- Admin edits the closing withdrawal of a closed shift: 200, amount saved, anchor `ImporteEsperado` recalculated,
  `fecha_recalculo` stamped, treasury `Ajuste` for the delta, audit row.
- Admin deletes it: same, the delta is the whole amount.
- Changing the motive of a closing withdrawal: 400, nothing written.
- Conciliated period: 409 `periodo_conciliado` as for any withdrawal.
- Web: closing withdrawal buttons enabled unless conciliated; motive read-only in the modal.

## Checks

- `dotnet test tests/Ways.Domain.Tests`
- `dotnet test tests/Ways.IntegrationTests --filter "FullyQualifiedName~Retiros"`
- Structural guard tests (`structural-guard-tests` skill).
- `npm test` in `src/Ways.Web` (retiros files, then full).

## Next step

T2.
