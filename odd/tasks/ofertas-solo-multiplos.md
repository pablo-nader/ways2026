# Ofertas por cantidad: solo múltiplos

## Objective

Let a quantity offer be marked "solo múltiplos": the offer benefit applies only to complete groups of
`cantidad_minima` units; the remainder pays the regular price.

## Problem / why

Today `ResolvedorDeOfertas.Coincide` only checks `cantidad >= CantidadMinima` and every unit gets the
offer price. Example: article at 4000 with "2x7500" (precio_unitario 3750, cantidad_minima 2):
3 units cost 11250. The owner needs 1 = 4000, 2 = 7500, 3 = 11500, 4 = 15000.
The legacy system already worked this way (`docs/01-features-existentes.md` B2:
`(precio - precioCant) * N * floor(cantidad / N)`).

## Scope

- New boolean `solo_multiplos` on `ofertas` (default false: existing offers unchanged).
- Applies to every benefit type (precio_unitario, porcentaje, importe_fijo) and every scope.
- Strict offer: discounted units = `N * floor(q / N)`, where q is the line quantity, or the aggregated
  family quantity for family offers. For family offers the remainder falls on the last lines of the
  document (deterministic; the owner confirmed family members share the same price).
- Server engine, sale totals, offline snapshot (escalones/family grid) and POS offline engine,
  offline discrepancy check, combos interplay, admin web form, docs/10 part `03-articulos--ofertas.md`
  and the offline snapshot part.

## Constraints

- One sale line per article; the strict discount is a line-level amount (precedent: combos'
  `UnidadesConsumidasPorCombo` / `AhorroDeCombo`). Whole-peso rounding of the line net stays.
- The device never re-evaluates offers: the server precomputes; the device only applies the
  precomputed structure.
- Reaching the cash registers requires a POS release (Ways.Desktop bump + `pos-v*` tag) — owner's call.

## Data model summary (evidence)

`ofertas.solo_multiplos boolean NOT NULL DEFAULT false`. Only meaningful with `cantidad_minima`
present; with `cantidad_minima` null the flag is rejected (400) — see T1.

## Tasks

- [ ] T1 — Backend: column + migration, domain rule, engine (line-level strict discount), sale totals,
  API DTOs (alta/edición/listado), docs/10. Unit + integration tests.
  Implemented in `162b767b`; unit suites green; integration class written but NOT run (Docker daemon was
  down in the writer session) — tick only after `OfertasSoloMultiplosTests` + guard tests pass.
- [ ] T2 — Offline: snapshot representation of strict components (escalones + family grid), TS engine
  (`instantaneaOffline.ts`, ticket synthesis, combos motor), server discrepancy check. Tests + parity.
- [ ] T3 — Web admin: checkbox in the offer modal, mapping helpers, summary text. Vitest.

## Route per task

- T1: delegated direct (writer trigger: 2+ non-trivial files).
- T2: delegated direct (writer trigger).
- T3: delegated direct (writer trigger).

## Acceptance

- 4000 article, 2x7500 strict: 1 → 4000, 2 → 7500, 3 → 11500, 4 → 15000 online and offline.
- Same offer non-strict: unchanged (3 → 11250).

## Progress

- Exploration done (2026-10-30). Branch `pnader/ofertas-solo-multiplos`.

## T1 evidence and decisions (2026-10-30)

- Route: delegated direct (writer).
- Commit: `162b767b` feat(ofertas).
- Checks: `dotnet build` 0 errors (only pre-existing warnings); `dotnet test tests/Ways.Domain.Tests` 2059/2059;
  `dotnet test tests/Ways.Application.Tests` 1375/1375 (includes the structural guards that live there:
  `InventarioDeDependientesTests`, `BajasEstructuralesTests`, `EscriturasSinReintento*`).
  Integration: `OfertasSoloMultiplosTests` (10 tests) compiles; not run — Docker unavailable. Pending.
- RED/GREEN: `CoberturaDeOfertasTests` 9/20 red against a stub (unit-times-per-unit discount), green after.
- Mutation evidence: `Math.Floor` removed from `CoberturaDeOfertas.EnGruposCompletos` → 9 tests red, reverted;
  snapshot filter `Where(c => !c.SoloMultiplos)` removed → `LaCurvaDeLaInstantaneaDejaAfueraALaOfertaSoloMultiplos`
  red; `agregarPorFamilia` branch forced to document → `SinDocumentoCadaLineaCuentaSusPropiosGruposDeFamilia` red.
- Engine: `PrecioConOfertas` keeps the per-unit discount; each `OfertaAplicada` carries `MultiploDe` (N when strict)
  and `PorFamilia`. `CoberturaDeOfertas.DescuentosDeLinea` turns a document into line amounts: non-strict covers
  all units, strict article/group/category covers `N*floor(u/N)`, strict family pools the units of the lines that
  carry it and fills them in document order (remainder on the last lines). Per unit, the summed discount is
  clamped to the original price (coverages are nested prefixes). Winner selection unchanged.
- Totals: `LineaParaCalcular.DescuentoDeOfertaDeLinea` (signed like `Cantidad`) replaces
  `(cant − combo) × descuentoUnitario`; one whole-peso rounding as before. Only set when a strict offer applies.
- Combos: strict groups count over the units NOT consumed by combos (venta and `resolver-con-combos`). Combo
  detection still compares against the full per-unit offer discount (conservative). `id_oferta` snapshot rule
  unchanged (a family remainder line keeps `id_oferta` with 0 offer discount).
- Edit semantics: `soloMultiplos` null keeps the stored value; an edit that drops `cantidadMinima` makes it common;
  explicit `true` without minimum is 400 `solo_multiplos_sin_cantidad_minima`. No DB CHECK (single writer).
- Offline (for T2): `ResolverConEscalonesAsync` excludes strict candidates from the curve and the family grid, so
  the device charges list price; the discrepancy check (`ServicioDeVentas` `NetoDeLinea` with
  `resultado.DescuentoUnitario`) still uses the per-unit expectation and is T2's to fix.
- Also touched (in scope): `Etiquetas/ServicioDeEtiquetas.cs` and `CatalogoOnline/ConsultaDeCatalogoPublico.cs`
  price one unit with the line discount.

## Next step

Run `OfertasSoloMultiplosTests` and the integration guards with Docker up; then tick T1 and start T2.
