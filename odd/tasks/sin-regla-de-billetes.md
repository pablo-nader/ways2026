# Sin regla de billetes en el vuelto

## Objective
Allow cash payments above the total when the customer adds money to get round change
(total 7500, pays 10500, change 3000).

## Problem
`BilletesArgentinos.EsVueltoJustificado` (owner decision 2026-09-16) requires every bill to be
strictly greater than the change. 10500 = 10000 + 500 and 500 < 3000, so `vuelto_no_justificado`
rejects the sale. Same rule in pago a cuenta corriente and in the web/offline mirrors.

## Decision (owner, 2026-11-02)
Remove the bill rule entirely. Remaining guards: negative amounts/change, coverage with tolerance,
`medio_no_admite_vuelto`, and rule 8 (change <= Σ importe − total).

## Scope
- Domain: `ValidadorDePagos`, `ValidadorDePagoACuenta`, delete `BilletesArgentinos` if unused.
- Web: `api/pagos.ts`, `api/cuentaCorriente.ts` mirrors and their tests; error-message mapping.
- Docs: docs/01 §B6 parity note.

## Tasks
- [x] T1 — Remove the rule in backend + web, with tests and docs (route: delegated writer, 2+ non-trivial files).
- [ ] T2 — POS release (bump Ways.Desktop + tag pos-v*) — owner decision.

## Checks
- `dotnet test` unit project(s) for Domain/Application.
- `npx vitest run` in src/Ways.Web.

## Progress
- Worktree `.claude/worktrees/sin-regla-billetes`, branch `pnader/sin-regla-billetes`.
- Writer pass: rule 3 (ValidadorDePagos) and rule 5 (ValidadorDePagoACuenta) removed, numbering of the rest kept; `BilletesArgentinos` and its tests deleted; web mirrors (`esVueltoJustificado`, `efectivoEntregado`, `BILLETES_ARGENTINOS`, `EFECTIVO_MAXIMO`) removed from pagos.ts/cuentaCorriente.ts; stale comments and docs (01 §B6/§I.12, 05, 10/09) updated; integration tests adjusted (not run, no Docker): CambioDeMedios now expects `medio_no_admite_vuelto`, PagosACuenta tests for the old rejection became acceptance tests.
- Verified: Domain tests 2583 pass, Application tests 1660 pass, solution and integration project build, Vitest (pagos, cuentaCorriente, ModalDeCambioDeMedios) 204 pass, tsc clean.
