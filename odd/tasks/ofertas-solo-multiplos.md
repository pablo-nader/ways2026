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

- [x] T1 — Backend: column + migration, domain rule, engine (line-level strict discount), sale totals,
  API DTOs (alta/edición/listado), docs/10. Unit + integration tests.
  Implemented in `162b767b`; unit suites green; integration 795/795 (offers, sales, combos, remitos,
  presupuestos, catalog, labels, snapshot, `SuperficieDeAutorizacionTests`, `AislamientoDeTenantTests`), 22 m.
- [x] T2 — Offline: snapshot representation of strict components (escalones + family grid), TS engine
  (`instantaneaOffline.ts`, ticket synthesis, combos motor), server discrepancy check. Tests + parity.
  Implemented in `56d9c749` on `pnader/ofertas-solo-multiplos-t2` (stacked on T1). Evidence below.
- [ ] T3 — Web admin: checkbox in the offer modal, mapping helpers, summary text. Vitest. Also the online
  POS cart preview (`api/ventas.ts` `previaDeLinea`) must use `descuentoDeLinea` when present (judge A: 3 units
  preview 11250 while the server charges 11500).
- [ ] T4 — Engine follow-ups from judgment-day T1 (WARNING, both judges): base selection among non-acumulable
  offers of equal priority ranks by per-unit discount and ignores strict coverage; combo detection compares
  against the full per-unit discount (conservative, judge A). Rank by the line discount at the actual quantity.

## Judgment-day T1 (2026-10-30)

`JUDGMENT: APPROVED` — no CRITICAL from either judge. Warnings recorded as T3/T4 above; family offer reported as
applied with 0 covered units when another base wins on some lines (judge B, SUGGESTION) stays informational.

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

## T2 evidence and decisions (2026-10-30)

- Route: delegated direct (writer trigger: 2+ non-trivial files, C# and TS).
- Commit: `56d9c749` feat(pos).
- Snapshot shape: every flat price, escalón and family cell where a strict offer applies carries `aplicadasCompletas`
  (the full server resolution, server order, strict entries with `multiploDe`/`porFamilia`); the legacy
  `precioFinal`/`descuentoUnitario`/`aplicadas` keep only the common offers (`PrecioParaInstantanea.Separar`, same clamp
  as the engine). Key absent when no strict offer applies → byte-identical JSON and ETag for existing data. No version
  bump (additive optional key; v1 legacy format uses the same common-only fields). T1's exclusion in
  `ResolverConEscalonesAsync` removed: strict `cantidad_minima` is a threshold like any other. Deviation from the
  suggested `Multiplos: [...]` list: the full ordered list keeps the server's `aplicadas` order (ticket `idOferta`, badge)
  and lets the device feed the exact server list to the coverage port.
- Old-device behaviour (explicit): a POS without `aplicadasCompletas` charges list price on the strict offer's units —
  never less than the server (customer overpays on complete groups until the POS updates) — and the discrepancy audit
  flags it. Folding the benefit into the legacy fields was rejected: old POS would apply it to the remainder too.
- Device: `pos/coberturaDeOfertas.ts` ports `CoberturaDeOfertas` with exact bigint arithmetic; `resolverDocumentoOffline`
  rebuilds the full resolution, runs combos with the full per-unit discount (as the server), then adds `descuentoDeLinea`
  over units outside combos with family pooling in cart order. Ticket (`calcularTotalesDeLinea` gained `descuentoDeLinea`)
  and payload (`descuentoDeOfertaDeLinea` instead of `descuentoUnitario`) use it.
- Server: `LineaDeVenta.DescuentoDeOfertaDeLinea` (offline only, instead of `DescuentoUnitario`; 400
  `precio_offline_incompleto` without price, `precio_offline_invalido` with both / on an importe line / negative / above
  the bruto of units outside combo). Charged via `LineaParaCalcular.DescuentoDeOfertaDeLinea`; the discrepancy check
  compares against `DescuentosSoloMultiplosDeLaVenta`, which now also counts offline lines (cantidad − server-detected
  combo units).
- Files outside the listed surfaces, required: `src/Ways.Application/Ventas/Contratos.cs` (new DTO field),
  `src/Ways.Web/src/api/ajusteManual.ts` and `src/Ways.Web/src/api/combos.ts` (the single shared line formula gained
  `descuentoDeLinea`, used by the ticket; T3 passes it from `previaDeLinea`).
- RED/GREEN: application tests `LaCurvaDeLaInstantaneaLlevaLaOfertaSoloMultiplosAparteDeLosCamposDeSiempre` and
  `ElPrecioPlanoSeparaLaOfertaSoloMultiplosYSinEllaNoLlevaLaClave` red before the service/armador change, green after
  (the `Separar` unit test was written after its helper). Server-side offline sale and TS tests were written after the
  code; their RED evidence is the mutations below.
- Mutation evidence: TS floor removed in `enGruposCompletos` → 14 tests red (parity + acceptance), reverted;
  `conDescuentoDeLinea` bypassed → 4 offline tests red (3 → 11500, resolution shape, family, combo), reverted; combo units
  not subtracted → combo test red, reverted; payload branch forced to `descuentoUnitario` → payload test red, reverted;
  discrepancy expectation passed as `null` → `UnaVentaOfflineSoloMultiplosBienCobradaNoSeAuditaYUnaMalCobradaSi` and
  `LaVentaOfflineDeFamiliaYConComboCobraComoElServidorYNoSeAudita` red, reverted; offline `DescuentoDeOfertaDeLinea` not
  charged → same two red, reverted; `Separar` keeping strict offers in the legacy fields → 3 application tests red,
  reverted.
- Checks: `dotnet build` 0 errors (34 pre-existing warnings, none in touched files); Domain 2059/2059; Application
  1377/1377 (structural guards included); integration 274/274 over OfertasSoloMultiplos, InstantaneaDePosEndpoints,
  CombosInstantanea, CombosVenta, CombosVentaOffline, VentaOfflinePrecio, VentaOfflineLimiteDeCredito,
  VentaAjusteManual, VentaPorImporte, OfertasResolucion, ArticulosNoVendibles, SuperficieDeAutorizacion,
  AislamientoDeTenant; web `tsc -b` clean, `npm run lint` exit 0 (pre-existing warnings), `vitest run src/pos` 954/954,
  full `vitest run` 8732/8732.
- Pending / for later tasks: the cart preview (`previaDeLinea`, online and offline) still multiplies the per-unit
  discount (T3: pass `resultado.descuentoDeLinea` to `calcularTotalesDeLineaConCombo`, the offline previa already carries
  it). T4: if base selection starts ranking by the line discount at the actual quantity, the winner can change inside a
  curve tramo (e.g. 3 vs 4 units), which the piecewise-constant escalones cannot express — T4 must add thresholds or keep
  the snapshot consistent. Reaching the cash registers needs a POS release (owner's call).

## Next step

Judgment-day for the T2 slice, then T3 (web admin + cart preview).
