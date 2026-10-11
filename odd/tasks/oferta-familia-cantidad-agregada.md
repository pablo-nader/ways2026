# oferta-familia-cantidad-agregada

## Objective

A family-scoped offer with `cantidad_minima` must count the units of every article of that family
in the sale, not the units of a single line.

## Problem

`ResolvedorDeOfertas.Coincide` (`src/Ways.Domain/Ofertas/ResolvedorDeOfertas.cs:116`) compares
`linea.Cantidad >= cantidadMinima` with the quantity of ONE line. Family "alfajores" with an offer
"2 x 3500" (`CantidadMinima = 2`, `PrecioUnitario = 1750`) and a sale of 1 alfajor A + 1 alfajor B
resolves as two lines of quantity 1: no discount on either.

## Why

Owner request (2026-10-11): mixed articles of one family must reach the threshold together.

## Scope

- Family scope only. Article, group and category offers keep the per-line quantity.
- Threshold semantics are unchanged: once the family reaches the threshold, EVERY unit of the
  family gets the benefit (3 alfajores with "2 x 3500" = 3 at 1750), same as a single article today.
- Out of scope: bundle/remainder arithmetic, aggregation for group/category/article scopes,
  reliquidación (it does not use the offers engine).

## Constraints

- The offline device never re-evaluates offers (owner decision): the server ships resolved curves.
- POS bundles already installed must keep working: existing snapshot fields keep their meaning; new
  data travels in new optional fields.
- No schema change.

## Delivery

Strategy: chained PRs, stacked-to-main (cached owner preference). Forecast ~700 authored lines.
- PR 1 = T1. PR 2 = T2.

## Tasks

- [x] **T1 — Engine and server batch aggregate by family** (route: delegated direct; trigger: 2+
  non-trivial files).
  - `LineaAResolver` gains the family aggregate quantity; `Coincide` uses it when the offer scope is
    a family; `ServicioDeOfertas` sums quantities per `IdFamilia` across the batch.
  - The legacy per-article snapshot curve keeps its current values (a family threshold reached by
    the article's own quantity).
  - Acceptance: A×1 + B×1 of one family with min 2 → both discounted; A×1 alone → not; an
    article-scoped offer with min 2 and two lines of quantity 1 → not; a line without family is
    unaffected. Covered at domain, application and sale-creation level.
  - Checks: `dotnet test tests/Ways.Domain.Tests --filter "FullyQualifiedName~Ofertas"`,
    `dotnet test tests/Ways.Application.Tests --filter "FullyQualifiedName~Ofertas|FullyQualifiedName~Pos"`,
    focused integration tests for offers/checkout.
- [x] **T2 — Offline snapshot and POS cart aggregate by family** (route: delegated direct).
  - Snapshot carries `idFamilia` per article and, per list price, the resolved results by family
    quantity in a new optional field; the web client sums cart quantities per family and picks the
    tier with both quantities. Old bundles ignore the new fields.
  - Acceptance: offline cart A×1 + B×1 shows and charges 1750 each; server discrepancy check does
    not flag that sale.
  - Checks: `npx vitest run src/pos`, `dotnet test tests/Ways.Application.Tests --filter "FullyQualifiedName~Pos"`.

## Progress

- 2026-10-11: explored; current per-line behavior confirmed from code.
- 2026-10-11: T1 done (commit `ed19a3be`). `LineaAResolver.CantidadDeFamilia` is required; `ServicioDeOfertas.ResolverAsync` sums per family over the batch; `ResolverConEscalonesAsync` and the legacy curve keep the line's own quantity (delta-shifted aggregate). Checks green: build, Domain Ofertas (114), Application Ofertas|Pos (136), integration VentasCheckout|OfertasResolucion|InstantaneaDePos (67).
- 2026-10-11: fix(etiquetas): `ServicioDeOfertas.ResolverAsync` is now private; callers choose `ResolverDocumentoAsync` (sums by family: ventas, remitos, presupuestos, `POST /api/ofertas/resolver`) or `ResolverPorLineaAsync` (own quantity: labels). Offline discrepancy recomputation reads the same batch call as the sale (`ServicioDeVentas.cs:169`), so it is already family-aggregated.
- 2026-10-11: T2 done (commits `f23f3054` server contract + grid, `6e0d4a64` web client). Contract: `ArticuloDeInstantanea.idFamilia` (omitted when null) and `PrecioDeListaDeInstantanea.escalonesDeFamilia` (omitted when empty), cells `{cantidadDeFamiliaDesde, cantidadDesde, precioFinal, descuentoUnitario, aplicadas}`; grid = family thresholds x own thresholds, resolved by the real engine (`EscalonesDeCantidad.ConstruirGrillaDeFamilia`), no collapsing. Client: `preciosVigentesDeLasLineas` is the single rule used by preview, enqueued payload and synthetic ticket. No snapshot version bump needed: fields are optional, the content ETag changes once and devices re-download. Checks green: build, Domain Ofertas (120), Application Ofertas|Pos|Etiquetas (145), integration InstantaneaDePos|VentasCheckout (51), tsc, vitest src/pos (449). Mutation evidence recorded for the per-family sum and the `cantidadDeFamiliaDesde <= cantidadDeFamilia` clause.
- 2026-10-11: review fixes (commits `41fe9075`, `81b4efe7`). (1) Per-family sum rounded to 3 decimals with `redondearImporte` (0.1 + 0.7 reaches 0.8). (2) `POST /api/ofertas/resolver` takes `agregacion`: `"Documento"` (default when omitted, installed POS bundles) or `"PorLinea"`; invalid name -> 400 `cuerpo_invalido`, out-of-range integer -> 400 `agregacion_invalida`; search modal and ConsultaPrecios send `PorLinea`, the cart (Pos.tsx) sends `Documento` via the client default. (3) The test for a line missing from the snapshot was reworded to what it proves. Checks green: build, Application (145), integration Ofertas|VentasCheckout (113), tsc, vitest (1530 + Pos.test 256).

## Next step

Judgment-day on the T2 slice, then PR 2.
