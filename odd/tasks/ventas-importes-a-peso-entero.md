# Ventas: importes a peso entero

## Objective

Every sale amount that is computed (not typed) is a whole number of pesos.

## Problem

An offer stores a per-unit price (`ofertas.precio_unitario`). A "x3 for 3500" offer has to be
loaded as 1166.66, and the line is computed as `cantidad × unitario` rounded to 2 decimals, so
the POS charges 3499.98. Argentina no longer uses cents in retail; the smallest note is $10.

## Scope (authorized by the owner, 2026-10-11)

- Sale line totals (offers, discounts, manual adjustment %), document totals and whatever derives
  from them: payments, change, cuenta corriente consumption repricing.
- Derived price lists (base × %).
- Both the server authority and the web/POS mirror.

Out of scope, explicitly: purchases, costs, supplier invoices, percepciones, and the fiscal
breakdown (neto/IVA per rate keeps cents; only the total is whole). No schema change:
columns stay `numeric(14,2)`.

## Constraints

- Rounding rule: nearest whole peso, half away from zero (same midpoint rule used today).
- `CalculadorDeTotales` invariant `total == sum(items.Total)` must keep holding.
- The line net is the rounded true net: `neto = RoundPeso(cantidad × precioFinalUnitario)`; the
  displayed discount is `bruto − neto`, never an independently rounded figure.
- Server and client must produce identical line totals (offline discrepancy detector compares nets).
- Stored unit prices are not rewritten.

## Tasks

- [x] T1 — Server: central money helper in Domain + whole-peso line/document totals in
  `CalculadorDeTotales`; align the duplicated formulas (`ServicioDeVentas.NetoDeLinea`,
  `ServicioDeFacturacionFiscal` subtotal, `ReliquidadorDeConsumos`, `ResolvedorDeOfertas`,
  `EscalonesDeCantidad`) and derived price lists (`ResolvedorDePrecios`). Tests first.
  Route: delegated writer (2+ non-trivial files).
- [x] T2 — Web/POS: `calcularTotalesDeLinea` mirror and its consumers at whole pesos; replace the
  private rounding copies in `api/pagos.ts` and `api/cuentaCorriente.ts` with the central helper.
  `api/compras.ts` and `sugerenciasDePercepcion.ts` are untouched. Tests first.
  Route: delegated writer.
- [x] T3 — Judgment-day on the slice, then PR.

## Acceptance criteria

- Offer unit price 1166.66 × 3 → line total 3500.00, on server and on the web preview.
- A sale with any mix of offers, manual % and weighed quantities has whole-peso line totals and total.
- Fiscal composition still satisfies `neto + IVA == total` per rate.
- Purchases code and tests are byte-identical.

## Checks

- `dotnet test` for Domain.Tests and Application.Tests; integration tests touching ventas.
- `npm test` (vitest) in `src/Ways.Web`, plus `tsc`/lint.

## Delivery

Strategy: `ask-on-risk`. Forecast: ~300–450 authored lines, mostly test expectations.

## Progress

- T1 done — commit `279befb2` (delegated writer; trigger: 2+ non-trivial files). `RedondeoDePeso` in
  `Ways.Domain/Dinero`; `CalculadorDeTotales.BrutoDeLinea/NetoDeLinea` are the single formula, reused by
  the offline discrepancy check, fiscal subtotal and `ReliquidadorDeConsumos`. Domain 1096 and
  Application 780 tests passed. Mutation evidence recorded (ToEven, 2 decimals, independent discount).
- T2 done — commit `c4e206b1` (delegated writer). `redondearAPeso`/`productoAPeso` in `importes.ts`
  with exact decimal subtraction; `pagos.ts` and `cuentaCorriente.ts` delegate to the central helper
  and stay at 2 decimals (typed amounts). vitest 4468 passed, tsc clean. One expectation changed in
  `paginas/Pos.test.tsx` (outside the planned surface, accepted: 87,50 -> 87,00).
- Integration expectations — commit `c36cadba`. 4 of 3149 failed, all legitimate; final run 3149 passed.
- Review: RDD off (clone-local); assessment `high` (unassessable: untracked file). Independent check is
  the judgment-day round of T3.
- Authored lines: ~800 against a 300-450 forecast (rewritten test expectations).

Accepted behavior changes: a manual % on a small line rounds to the peso (12.5% of 100 = 13); an offline
price difference below one peso of line net is no longer flagged as a discrepancy.

- T3 judgment-day — round 1: both judges confirmed one CRITICAL (presupuesto conversion and remito
  invoicing recomputed stored items through the rounding formula: stored cents and half-peso ties
  failed with 409/500). Owner decision: respect what is stored. Fix commit `74513cf1`
  (`TotalesGuardados.Sumar`; header must equal the sum of stored items; amounts carried unchanged).
  Integration 3161 passed, Domain 1100, Application 780, vitest 4467. Scoped re-judgment: both judges
  F1 RESOLVED, clean round. JUDGMENT: APPROVED.
- Delivery: single PR. Server and web mirror must land together (parity constraint); most of the
  size is rewritten test expectations.

Accepted tradeoff from the fix: a raw UPDATE of an item's cantidad or precio_unitario that leaves the
stored amounts untouched is no longer caught by the conversion/invoicing integrity check.

Next step: merge the PR, then the POS desktop release.

## Follow-ups

- POS desktop bundle needs a `Ways.Desktop` version bump + `pos-v*` tag for the cashiers to get T2.
- Sale amounts still print ",00" on screen; dropping the decimals from display is an open product decision.
