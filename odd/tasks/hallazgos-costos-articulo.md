# Hallazgos del judgment-day de la sección de costos del artículo

## Objective

Close the four informational findings the judgment-day of PR pablo-nader/ways2026#360 left
unfixed in the article form's cost section and its help tooltip.

## Problem / why

- The form shows a nominal cost the server will not store when an article is created inside
  a family (live formula preview vs. the family reference's stored nominal).
- `onSubmit` carries a guard no test can kill (the submit button is already disabled).
- A user without cost visibility gets enabled cost controls for values that are never sent.
- The help tooltip has accessibility gaps (Escape on hover, hover vs. focus, stale position,
  no flip, not hoverable) and a CSS comment that claims more than the code does.

## Scope (authorized by the owner, 2026-10-10)

Web only (`src/Ways.Web`). No backend, no schema, no contract change. Branch
`pnader/costos-articulo-hallazgos`, stacked on `pnader/article-pricing-logic-0f926d` (#360).

## Constraints

- TDD mode: off (source: no project/session configuration enables it). Runner:
  `npx vitest run` in `src/Ways.Web` (never two vitest runs at once), plus `npx tsc -b` and
  `npx oxlint`.
- Project skills: react-async-state (rule 15), web-descriptor-tests, claims-match-code,
  mutation-proof-tests, web-test-data-gates.
- Receipt-driven development: off (clone-local). PR gate: judgment-day before the PR.
- Delivery: single PR onto #360's branch; forecast ~350 authored lines.
- Conventional commits in Spanish, no AI attribution.

## Tasks

- [x] **T1 — Nominal cost of a create locked to a family.** Commit `2ae4d1f6`. Route: delegated
  (writer trigger: costoNominal.ts, FormularioArticulo.tsx, tests). `costoNominalDelFormulario`
  takes `bloqueadoPorFamilia` and returns the form's stored nominal while locked. Evidence: unit
  tests with stored 120 vs formula 142,50; page test `—` → `120,00` → `142,50` after "Sin
  familia". Mutations (clause deleted, `false` and `true` at the call site) each failed.
- [x] **T2 — Unkillable `bultoSinUnidadesPorBulto` guard in `onSubmit`.** Commit `c24a6914`.
  Route: delegated. Unreachable (single submit button, disabled for the condition; no
  `requestSubmit`, no `form=`; quick-create modals stop propagation): removed. New Enter test
  dies when the clause is taken out of the button's `disabled`.
- [x] **T3 — Cost controls with `costosOcultos`.** Commit `8c19fe25`. Route: delegated. The whole
  Costos section is not rendered; no `!costosOcultos` clause added to the Bulto block (false by
  construction: a hidden cost base maps to Unidad). Payload unchanged and asserted (four nulls).
- [x] **T4 — Tooltip accessibility (`IconoDeAyuda`, `ways.css`).** Commit `5fd1b5a2`. Route:
  delegated. All six items shipped; 20 component tests with per-clause mutations. Real browser
  (throwaway harness, light and dark): Escape on hover keeps the modal and the second closes it;
  focus survives mouse leave; follows the modal scroll; flips below near the top edge; pointer
  crosses the gap; hidden tooltip does not intercept the point.

## Progress

- Checks: `npx tsc -b` clean; `npx oxlint` 0 errors (16 pre-existing warnings); `npx vitest run`
  3961/3961 (162 files); parent spot check 67/67.
- Review: RDD off. Judgment-day round 1 over `2fb017e3..5fd1b5a2`: APPROVED, 0 critical,
  0 warning. Informational, not fixed: the Costos fragment children are not re-indented (both
  judges); a visible tooltip re-renders on every scroll event (both; the dedupe was dropped
  because no test could kill it); switching family A → B shows A's nominal until B loads, like
  the other locked fields, and saving is blocked meanwhile (both).
- Open for the owner: a mouse click on the icon focuses it, so the tooltip stays until blur or
  Escape; a tooltip whose icon scrolls out of the modal body keeps floating.
- Next step: PR onto `pnader/article-pricing-logic-0f926d` (#360).
