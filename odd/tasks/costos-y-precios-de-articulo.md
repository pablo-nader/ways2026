# Costos y precios del artículo

## Objective

Make the article form compute and persist costs the way they are entered, round price
suggestions commercially, and tidy the price-list table and the "acumula" help text.

## Problem / why

- `costo_nominal` is typed by hand and is not derived from `costo_lista` − discount.
- `costo_lista` has no defined IVA basis and cannot be entered per pack or without IVA.
- Price suggestions come out as raw 2-decimal amounts (e.g. 2896) instead of shelf prices.
- The price-list table sorts by name, so the manual "General" list lands between derived
  lists, and "Ver detalle" on derived lists shows nothing useful.
- The help text under "Acumula en una sola línea al vender" takes a full row.

## Scope (authorized by the owner, 2026-10-10)

Approved schema change on `articulos` (tenant scope unchanged):

| Column | Type | Rule |
|---|---|---|
| `costo_lista_por` | text NOT NULL default `'Unidad'` | CHECK in (`'Unidad'`, `'Bulto'`) |
| `costo_lista_incluye_iva` | boolean NOT NULL default `true` | which IVA field the user typed |

- `costo_lista` stores the amount exactly as entered (per unit or per pack, with or without
  IVA according to the flag).
- `costo_nominal` is computed by the server on create/edit: per unit, IVA included, minus
  `descuento_proveedor` (0 when absent). Purchases keep overwriting it with the effective cost.
- `Bulto` requires `unidades_por_bulto` > 0, otherwise 400.
- Both new columns are family-shared fields, like the other costs.
- Existing rows take the defaults; no backfill.
- Rounding of suggestions: per-company parameter `redondeo_sugerencia_precio` (decimal,
  default 100) in the existing `parametros` table; no migration.

Out of scope: redefining purchase cost flow, reinterpreting existing `costo_lista` values.

## Constraints

- TDD mode: off (source: no project/session configuration enables it). Runners:
  `dotnet test` (tests/Ways.Domain.Tests, Ways.Application.Tests, Ways.IntegrationTests),
  `npx vitest run` in `src/Ways.Web`. Never run two suites concurrently against the same
  Docker daemon, nor two vitest runs at once.
- Project skills that apply: dto-contract-honesty, db-error-backstops, web-descriptor-tests,
  react-async-state, claims-match-code, web-test-data-gates.
- Receipt-driven development: off (clone-local). PR gate: judgment-day before any PR.
- Delivery strategy: ask-on-risk; chain strategy stacked-to-main (owner's standing preference).
  Forecast: ~900 authored lines → two PR slices (backend T1+T2, web T3+T4).

## Tasks

- [x] **T1 — Backend: cost entry mode and server-computed nominal cost.** Commit `3c5fd3d6`. Evidence: Domain 1044/1044, Application 754/754, Integration full run 3076/3080 then the 4 stale family expectations fixed and re-run 94/94 (FamiliasPrevisualizacion, ArticulosDeFamilia, ArticulosCostoNominal). Accepted change: nominal is recomputed only when a cost input changes (a purchase cost survives unrelated edits; an alta into a family inherits it). Request `costoNominal` removed. Review: RDD off. Route: delegated
  (writer trigger: entity, EF config, migration, contracts, service, family shared values,
  docs/10, tests). Acceptance: columns + CHECK exist; create/edit compute `costo_nominal`;
  `Bulto` without units per pack → 400; family copy/compare include the new fields; vendor
  still sees no costs. Checks: domain + application + integration tests.
- [x] **T2 — Backend: rounded price suggestions.** Commit `4b77df43`. Evidence: Domain 1057/1057, Application 761/761, Integration 856/856 (Compras, Articulos, Familias, Parametros, Etiquetas, EscriturasSinReintento). Notes: nearest multiple 0 keeps 2-decimal rounding; no range validation on the parameter (≤ 0 = no rounding); purchases resolve the multiple from the point of sale company. Review: RDD off. Route: delegated. `SugeridorDePrecio`
  rounds to the nearest multiple of the company parameter (default 100, half away from
  zero; a multiple ≤ 0 means no rounding); `GET /api/articulos/{id}/sugerencia-precio`
  accepts optional `idEmpresa`; purchases use the purchase's company. Suggestion base cost
  uses the normalized per-unit IVA-included cost. Checks: domain + application + integration.
- [x] **T3 — Web: cost section of the article form.** Commit `a7358b85`. Evidence: tsc clean, oxlint 0 errors, vitest 3925/3925 (160 files); parent spot check 123/123. Accepted change: purchase quick-create stores whether the line cost includes IVA. Review: RDD off. Route: delegated. Two synced fields
  (with / without IVA), Unidad/Bulto selector (default Unidad), read-only computed nominal
  cost preview, family shared fields, API types. Checks: vitest (colocated descriptor tests).
- [x] **T4 — Web: price table, tooltip and parameter.** Commit `bc46c9aa`. Evidence: tsc clean, oxlint 0 errors, vitest 3939/3939 (162 files); parent spot check 39/39. Tooltip not viewed in a browser. Review: RDD off. Route: delegated. Manual lists
  first then derived (name within each group); remove "Ver detalle" and its dead branch;
  info icon with tooltip replacing the help text; `redondeo_sugerencia_precio` in
  `PARAMETROS_CONOCIDOS`; suggestion request sends the session company. Checks: vitest.

## Progress

All four tasks committed (2890 authored insertions / 413 deletions vs main, generated EF files excluded). Pending: full integration suite re-run after T2 (only affected classes ran), judgment-day per PR slice, PRs (stacked-to-main: backend T1+T2, then web T3+T4). Branch: `pnader/article-pricing-logic-0f926d`.

## Next step

T1.
