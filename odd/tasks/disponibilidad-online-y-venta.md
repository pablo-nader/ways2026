# Disponibilidad online y a la venta: artículos, ofertas y combos

## Objective

Every article, offer and combo carries a "Disponible online" check (default on) that decides
whether the storefront shows or applies it; every article also carries "Disponible a la venta"
(default on) so an internal-use article can be bought and stocked without ever being sold, in
the POS or online. The storefront applies the available offers to the prices it shows and
lists the available combos as items.

## Problem / why

- `articulos.publicado_web` defaults to off, so a commerce has to publish article by article;
  the owner wants the opposite default: everything is online unless unchecked.
- Nothing marks an article as "not for sale": an internal-use article (cleaning supplies, a
  tool) appears in the POS and in the storefront.
- Offers and combos have no web flag; the public catalog shows list prices "sin ofertas" and
  no combos.

## Scope (authorized by the owner, 2026-10-12, autonomous mode)

Owner's words: "disponible online, por defecto activo (para artículos, ofertas y combos) y
además 'disponible a la venta' para artículos, ambos por defecto activos; podría ser un
artículo de uso interno". Assumption (stated to the owner): existing rows are backfilled to
on for both flags, so only the exceptions get unchecked.

Out of scope: quantity tiers and family offers at cart level, combos with family components
in the storefront (member choice needs its own UI), online checkout (`pedidos-online`).

## Data model (evidence)

- `articulos.publicado_web` renamed to `disponible_online boolean NOT NULL DEFAULT true`, all
  live rows backfilled to true; partial index renamed accordingly.
- `articulos.disponible_venta boolean NOT NULL DEFAULT true` (backfilled true).
- `ofertas.disponible_online boolean NOT NULL DEFAULT true`, `combos.disponible_online boolean
  NOT NULL DEFAULT true` (backfilled true).
- Public policies: `articulos_publico_lectura` becomes `disponible_online AND disponible_venta
  AND activo AND deleted_at IS NULL`; new SELECT-only public policies on `ofertas`
  (`disponible_online AND activo AND deleted_at IS NULL`), `ofertas_listas`, `combos`
  (`disponible_online AND activo AND deleted_at IS NULL`), `combos_componentes` (tenant only).
  The exact-list guard in `VitrinaAislamientoTests` grows from 9 to 13 policies.

## Constraints

- Same runners, skills and delivery rules as the previous features; the structural guard tests
  named in `.claude/skills/structural-guard-tests/SKILL.md` run before any task is called done.
- "Disponible a la venta" off means: excluded from the POS snapshot (web and desktop share it),
  from `/escaneo` and the sale search, from the storefront, and `ServicioDeVentas.EmitirAsync`
  rejects a line with it (409 `articulo_no_vendible`); purchases, stock and reports unchanged.
  Combos whose components include a non-sellable article leave the POS snapshot (the existing
  "all components sellable" rule extends to the new flag).
- Offers on the web: per-article preview at quantity 1 in the vitrina's list, only offers with
  `disponible_online`, reusing the pure domain resolver (`ResolvedorDeOfertas`,
  `EscalonesDeCantidad`) with a dedicated candidate query under Publico mode (the catalog keeps
  its own projection; `ListaDePrecios` must carry the vitrina's original list id so
  `ofertas_listas` targeting matches a derived list).
- Combos on the web: only combos whose components are all articles (no family components),
  all available online and for sale, all with a current price, all `UnidadVenta.Unidad`; price
  is the combo's fixed price. The storefront cart gets a `combo` line type; the checkout
  (later) re-detects combos server-side with `CombosEnVenta`.

- The shared engines (`ServicioDeOfertas.ConstruirContextoAsync`, `ServicioDeCombos.CargarCandidatosParaVentaAsync`) do
  not filter `disponible_online`. The future `pedidos-online` checkout MUST price with online-only offers and combos (a
  parameter or a dedicated context), otherwise the charged price can differ from the storefront.
- A return of an internal-use article (`disponible_venta = false`) is exempt from the sale rule in `EmitirAsync`, so the
  back office can find the article with `incluirNoVendibles=true` on `/escaneo` and on the sale search. The POS snapshot
  stays exclusive: an offline return of an internal-use article is not possible on the device and needs the back office.

## Tasks

- [x] **T1 — Backend: flags, policies, POS and sale enforcement, public offers and combos.**
  Delegated. PR 11. Commits `e0d784bc` (flags, migration, admin, policies), `39d26578` (sale enforcement),
  `cb395b17` (public offers and combos); the doc commit that records this closes the task.
  - Migration `DisponibilidadOnlineYVenta` (rename, two new columns, backfills, policies).
  - Domain/DTOs: `Articulo.DisponibleOnline`/`DisponibleVenta`, `Oferta.DisponibleOnline`,
    `Combo.DisponibleOnline`; listing/grid/detail DTOs rename `publicadoWeb` → `disponibleOnline`
    and add `disponibleVenta`; `OfertaListado`/`AltaOferta`/`EdicionOferta` and the combo
    contracts carry `disponibleOnline` (default true when omitted on create; optional on edit,
    omitted = unchanged, since judgment-day round 1).
  - Admin: `PUT /api/articulos/{id}/disponibilidad { disponibleVenta, disponibleOnline }` replaces
    `PUT .../web` (same discipline: 404-first, no-retry, lock, idempotent); offers and combos
    save the flag through their existing POST/PUT.
  - Sale enforcement: POS snapshot, `/escaneo`, sale search, `EmitirAsync` 409
    `articulo_no_vendible`, presupuestos reuse the same guard if they share the line validation.
  - Public: `GET /api/publico/catalogo` returns `precio` (final) and `precioOriginal` (when an
    available offer applies) plus `oferta` (name) per article; `combos: [{ id, nombre,
    precio, componentes: [{ idArticulo, nombre, cantidad, imagenVersion }] }]` on the first
    unfiltered page (and `GET /api/publico/combos` for the storefront). Public image of a combo:
    none (cards show component images).
  - Tests: unit (defaults, guard), integration (isolation of the four new policies with raw
    `ways_app` connections and mutation evidence; backfill under `ways_app` per
    rls-migration-backfills; offer applied/not applied by flag, list targeting incl. derived
    list; combo listed/excluded by each rule; POS snapshot and `EmitirAsync` with a non-sellable
    article; admin round-trips), structural guards.
- [x] **T2 — Web admin.** Delegated. PR 12. Commits `804e57a2` (articles and selling screens),
  `fd368023` (offers and combos); the doc commit that records this closes the task.
  - Article form: section "Venta y tienda online" (not "Disponibilidad": the form already has an
    empresas section with that title) with two switches (own write), grid badges ("No se
    vende", "Online") and the existing image block; offers modal and grid: checkbox and column
    "Online"; combos modal and grid: the same.
  - Selling pickers (POS search modal, presupuesto and remito line search) send `soloVendibles=true`;
    409 `articulo_no_vendible` has its own copy in the POS charge, presupuesto and remito saves.
- [ ] **T3 — Web storefront and landing.** Delegated. PR 13.
  - Cards show the offer price with the original struck through and an "Oferta" tag; combos
    section in `/online` ("Combos") with cards listing components and "Agregar" creating a
    `combo` cart line (integer quantity, fixed price, components shown in the panel); cart
    totals and `sincronizar` handle both line types; landing "Lo que vas a encontrar" shows the
    offer price too and a "Combos" strip when any.

## Verification evidence

### T1

Route: delegated writer (one), no inline source edits by the parent.

- `dotnet build src/Ways.Api/Ways.Api.csproj -warnaserror`: 0 warnings, 0 errors.
- `dotnet ef migrations has-pending-model-changes`: "No changes have been made to the model since the last migration."
- `dotnet test tests/Ways.Domain.Tests`: 1352 passed. `dotnet test tests/Ways.Application.Tests` (full, holds the structural
  guards `EscriturasSinReintentoEstructuralesTests`, `InventarioDeDependientesTests`, `BajasEstructuralesTests`,
  `EscritoresDeFamiliasEstructuralesTests`): 841 passed.
- Integration, development filter (SuperficieDeAutorizacion, AislamientoDeTenant, Vitrina, Catalogo, Publico, Pos, Ventas,
  Ofertas, Combos): 494 of 495 on the first run; the single failure was an `EdicionOferta` fixture without the now
  required `disponibleOnline` (fixed); re-run of Ofertas|Combos|EscriturasSinReintento: 355/355; new classes below green.
- Full `dotnet test tests/Ways.IntegrationTests`: 3797 passed, 0 failed (see "Full integration run" below).
- `dotnet format --verify-no-changes`: only the baseline noise recorded in the `dotnet-format-no-es-gate` memory
  (ENDOFLINE / WHITESPACE / IDE0008 on every file, changed or not); nothing outside those categories.

New or changed tests: `DisponibilidadAislamientoTests` (10), `DisponibilidadOnlineMigracionTests` (1),
`CatalogoPublicoOfertasTests` (12), `CombosPublicosTests` (9), `ArticulosNoVendiblesTests` (9),
`OfertasYCombosDisponibleOnlineTests` (7), `ReglaDeArticuloVendibleTests` (6, domain), plus the admin, isolation,
catalog and family tests adapted to the rename.

Mutation evidence (mutate, run the named class, revert; every row went red in the named test and green after the revert):

| Clause mutated | Killed by |
|---|---|
| `articulos` policy without `disponible_venta` | `CatalogoOnlineAislamientoTests.LaPolicyDeArticulosDejaVerSoloLosDisponiblesOnlineYParaLaVentaActivosYVivosDelTenantDelGuc` |
| `articulos` policy without `disponible_online` | same test |
| `ofertas` policy without `disponible_online` / `activo` / `deleted_at IS NULL` (three runs) | `DisponibilidadAislamientoTests.LaPolicyDeOfertasDejaVerSoloLasDisponiblesOnlineActivasYVivasDelTenantDelGuc` |
| `combos` policy without `activo` / `disponible_online` / `deleted_at IS NULL` (three runs) | `DisponibilidadAislamientoTests.LaPolicyDeCombosDejaVerSoloLosDisponiblesOnlineActivosYVivosDelTenantDelGuc` |
| migration default of `articulos.disponible_online` or `disponible_venta` = false | `DisponibilidadOnlineMigracionTests.LasFilasExistentes...` |
| catalog: resolver gets the base list instead of the vitrina's original list | `CatalogoPublicoOfertasTests.ConUnaListaDerivadaLasOfertasDirigidasAEllaAplicanSobreElPrecioDerivado` |
| catalog: resolver quantity 3 instead of 1 | `...UnaOfertaConCantidadMinimaMayorQueUnoNoAplicaYUnaDeUnoSi` |
| catalog: no empresa filter on offers | `...UnaOfertaDeOtraEmpresaNoAplica...` |
| catalog: no target lists / no category ancestors | `...UnaOfertaDirigidaAOtraLista...` / `...UnaOfertaDeCategoriaAlcanzaALasSubcategorias...` |
| catalog: offer name taken without checking it discounts | `...UnaOfertaQueNoRebajaElPrecioNoFiguraEnLaRespuesta` |
| catalog: combos on page 2 / with `buscar` / with `idCategoria` (three runs) | `CombosPublicosTests.ElCatalogoLlevaLosCombosSoloEnLaPrimeraPaginaSinFiltros` |
| public combos: no unit rule, no empresa availability of the article, no current price, no combo empresa filter, combos without components, price looked up in the default list, ordering (seven runs) | `CombosPublicosTests.SoloAparecenLosCombos...` / `ElPrecioDeLosComponentesSeBuscaEnLaListaDeLaVitrina` |
| POS snapshot without `disponible_venta` filter | `ArticulosNoVendiblesTests.LaInstantaneaNoTrae...` and `ElFormatoOriginal...` |
| `/escaneo` without the filter, by internal code and by barcode (two runs) | `ArticulosNoVendiblesTests.ElEscaneoNoResuelve...` |
| `GET /api/articulos` ignoring `soloVendibles` | `ArticulosNoVendiblesTests.LaBusquedaDeLaCajaOmite...` |
| combo candidates (`cargable`) without the filter | `ArticulosNoVendiblesTests.ElComboCargableNoOfrece...` |
| `EmitirAsync` without the rule / with the rule also for offline sales | `EmitirUnaVentaConUnArticuloNoVendibleDa409...` / `LaVentaOfflineYaCobradaSeAcepta...` |
| presupuesto / remito without the rule | `ArticulosNoVendiblesTests.UnPresupuestoYUnRemito...` |
| disponibilidad: no row lock / validation before 404 / writes when unchanged / ignores either flag / retrying strategy | `CatalogoOnlineAdminTests` (`...PierdeContraLaBaja...`, `...Da404AntesQueLaValidacion...`, `PedirLaMismaDisponibilidad...`, `CambiarLaDisponibilidad...`, `UnaFallaTransitoria...`) |
| article DTOs with the two flags swapped in the detail, the list and the grid (three runs) | `CatalogoOnlineAdminTests.CambiarLaDisponibilidad...` |
| ofertas / combos: create ignores the flag, edit does not persist it, list does not report it (the round-0 mutants "edit accepts a missing flag" and "400 before 404" no longer exist: see round 1, L1) | `OfertasYCombosDisponibleOnlineTests` |

Survivor, explained: `ServicioDeArticulos.ListarAsync(soloVendibles = true)` as the DEFAULT of the parameter. The only HTTP caller
passes the query value explicitly (`soloVendibles ?? false`), so the mutant is unobservable through the API; the other caller
(`ServicioDeEtiquetas`, named arguments) relies on the default `false` and has no test with an internal-use article.

Dead clauses removed instead of shipped: two guards in the public queries were redundant with a neighbour (the
`PrecioFinal < PrecioOriginal` test next to "first applied offer that discounts" and the `IdFamilia == null` test next to
"the component has an article that is in the dictionary"); the mutation run showed the survivors and they were deleted.

### Judgment-day round 1 fixes (L1-L8)

Route: delegated fix actor (one). Commits: `d5c526f4` (L1 to L3), and the second commit of this round (L4 to L8 plus docs).

- L1: `PUT` of offers and combos with `disponibleOnline` omitted keeps the stored value, `disponible_online_requerido` removed from
  both services. Tests `OfertasYCombosDisponibleOnlineTests` (stored `false` and stored `true` both kept, the rest of the body
  applied, 404 for a missing or soft-deleted id; the raw-JSON combo PUT is 200 and keeps `true`).
- L2: `incluirNoVendibles` on `/escaneo` (both branches, `activo` still required) and on the sale search. Tests in
  `ArticulosNoVendiblesTests`.
- L3: the rule at presupuesto conversion, remito emission and remito invoicing. Tests `ConvertirUnPresupuestoViejo...`,
  `EmitirUnRemitoViejo...`, `FacturarUnRemito...`, each with the control that sells again once the article is sellable.
- L4: `ConsultaDeCombosPublicos` excludes a combo that does not save; the shared price path is
  `ConsultaDeCatalogoPublico.PreciosFinalesAsync`. Tests `UnComboSoloFiguraSiCobraMenosQue...` and
  `ConUnaListaDerivadaElAhorroSeMideContraElPrecioDerivado`; the existing combo fixtures that did not save were repriced.
- L5: `ComboPublico.Codigo` removed; the exact-shape assertion of the endpoint JSON no longer lists `codigo`.
- L6: `CatalogoPublicoOfertasTests` for the `grupo` and `familia` scopes, the hour window and the weekday (the seed helper grew
  `idGrupo`, `idFamilia`, `horaDesde`, `horaHasta`, `diasSemana`). The hour and weekday tests derive their expectation from the
  local clock (the morning/afternoon pair always has exactly one winner), so they are not time dependent.
- L7: `LectorDeCatalogoPublico` decides "no search" with `ConsultaDeCatalogoPublico.Normalizar` (now public); `?buscar=%01` and
  `?buscar=%01%02%20` carry the combos like an unfiltered page.
- L8: the constraint about the shared engines was added to Constraints.

Mutation evidence (mutate, run the named class, revert; every row red in the named test, green after the revert):

| Clause mutated | Killed by |
|---|---|
| offer PUT `?? stored` replaced by `?? true` / `?? false` | `OfertasYCombosDisponibleOnlineTests.EditarUnaOfertaSinDisponibleOnlineConserva...` (both runs) |
| combo PUT `?? stored` replaced by `?? true` / `?? false` | `...EditarUnComboSinDisponibleOnlineConserva...` (and, for `false`, `SinElCampo...ElJsonCrudo...`) |
| `/escaneo` without `incluirNoVendibles` in the internal-code branch / in the barcode branch / ignoring `activo` | `ArticulosNoVendiblesTests.ElEscaneoConIncluirNoVendibles...` (three runs) |
| sale search without the `incluirNoVendibles` override / `/escaneo` endpoint ignoring the parameter | `...LaBusquedaConIncluirNoVendibles...` / `...ElEscaneoConIncluirNoVendiblesResuelve...` |
| conversion exempt again from the rule | `...ConvertirUnPresupuestoViejo...` |
| rule also applied to offline sales (`NumeroPreasignado`) | `...LaVentaOfflineYaCobradaSeAcepta...` (pre-existing test) |
| remito emission without the rule / remito invoicing without the rule | `...EmitirUnRemitoViejo...` / `...FacturarUnRemito...` |
| combo saving test `<=` / no peso rounding / no component quantity / filter removed / offers ignored / derived list ignored (six runs) | `CombosPublicosTests.UnComboSoloFiguraSiCobraMenos...`, `...ConUnaListaDerivadaElAhorro...`, `ElCatalogoYElEndpointDanLosMismosCombos` |
| lector with `IsNullOrWhiteSpace` | `CombosPublicosTests.ElCatalogoLlevaLosCombosSoloEnLaPrimeraPaginaSinFiltros` |
| offers: no `grupo` clause / group not wired / no `familia` clause / family not wired / hour shifted 12 h / weekday shifted one (six runs) | `CatalogoPublicoOfertasTests.UnaOfertaDeGrupo...`, `...UnaOfertaDeFamilia...`, `...LaVentanaHoraria...`, `...LosDiasDeSemana...` |

L5 has no separate mutant: the field is removed from the record, and the exact-shape assertion over the endpoint JSON is the guard.

### Deviations from the brief

1. **No backfill, so no `SET LOCAL` and no `ways_app` backfill test.** The brief asked for a rename plus a platform-GUC
   backfill. The new columns are born `true` through a constant default, which already leaves every existing row (live or
   soft-deleted, published or not) available, so the migration writes no data and `rls-migration-backfills` does not apply.
   `publicado_web` is dropped instead of renamed because its old value (almost always `false`) is exactly what must not survive.
   In its place `DisponibilidadOnlineMigracionTests` migrates an isolated database to the previous migration, seeds four
   articles (one published, two with the old default, one soft-deleted), migrates to HEAD and asserts every row has both flags
   `true`, the old column and index are gone, the four new policies exist, and `ways_app` in public mode now sees the three live
   rows (it saw one before). Mutation-proven above.
2. **`EdicionOferta.DisponibleOnline` and `EdicionCombo.DisponibleOnline` are `bool?` and OPTIONAL on edit** (judgment-day L1).
   The first version made them required (`400 disponible_online_requerido`), which broke every client that PUTs an offer or a
   combo without the field (the deployed web until T2 lands). Now a missing JSON key keeps the stored value (the service reads
   `datos.DisponibleOnline ?? stored`, under the row lock, from the single locked read) and a present one is applied; the
   precedent is `EdicionCombo.Activo`'s nullability, minus the 400. The code `disponible_online_requerido` is gone from offers
   and combos (it remains only on `PUT /api/articulos/{id}/disponibilidad`, a new endpoint where both flags are required).
   T2 may still always send the field.
3. **The sale rule is shared by three documents, not only `EmitirAsync`.** They do not share a line-validation helper, so
   `ReglaDeArticuloVendible.Exigir` (pure, `Ways.Domain.Articulos`) is called from `ServicioDeVentas.EmitirAsync`, from
   `ServicioDePresupuestos.ResolverYMaterializarAsync` and from `ServicioDeRemitos.ResolverYMaterializarAsync` (create/edit of
   the draft). Judgment-day L3 closed the old-draft bypass: the rule is exercised again when the document is concretized,
   in `EmitirAsync` when a presupuesto is converted (`IdPresupuestoOrigen`), in `ServicioDeRemitos.EmitirAsync` and in
   `ServicioDeFacturacionDeRemitos.FacturarAsync` (a remito already emitted whose article became internal-use is not invoiced
   while it stays so; it can still be annulled). In `EmitirAsync` the rule is exempt only for a sale a device already charged
   offline (`NumeroPreasignado`) and for returns (negative sign); the quantity-by-unit rule keeps its three exemptions. The 409
   message names the article (name and internal code).
4. **The sale search is `GET /api/articulos?soloVendibles=true`** (new optional parameter of the generic listing, the one
   `ModalDeBusquedaDeArticulos` uses for the POS). Until T2/T3 send it, the POS search still lists internal-use articles; the sale
   itself is rejected by the 409 either way, the snapshot and `/escaneo` already exclude them.
5. **Combo side kept minimal** (coordination note about the concurrent combos-pesables branch): the snapshot needed no combo
   change at all (the existing rule filters combos against the articles of the snapshot, and the snapshot now excludes
   non-sellable ones); the only combo-engine edits are the `DisponibleOnline` property/column/contracts and one extra predicate
   in `ServicioDeCombos.CargarParaPosAsync` (`a.DisponibleVenta`). The public combos query lives in its own class
   (`CatalogoOnline/ConsultaDeCombosPublicos`). **Weighable components in the storefront depend on the combos-pesables branch
   landing first**: the public rule stays "all components `UnidadVenta.Unidad`" as written for this feature.
6. **`ServicioDeOfertas.DescomponerHoraLocal` became `internal`** so the public query uses the same local date/hour/weekday as the
   POS without instantiating the service.
7. **Commits:** three work-unit commits plus this doc commit. Some files are mixed across units (the `soloVendibles` parameter
   travels in the first commit, with the article service; the `ServicioDeCombos` candidate predicate too), so the first
   commit alone is complete but the rule it describes for the POS arrives in the second.

### Contract changes for the web (T2 / T3)

Renamed or removed:
- `PUT /api/articulos/{id}/web { publicado }` is GONE; use `PUT /api/articulos/{id}/disponibilidad { disponibleVenta, disponibleOnline }`
  (both required, response `{ idArticulo, disponibleVenta, disponibleOnline }`; `400 disponible_venta_requerido` /
  `400 disponible_online_requerido`; this is the only place the latter code survives).
- Article DTOs (`ArticuloListado` in list, detail, create and edit responses; `ArticuloGrillaFila` in the grid): `publicadoWeb` is
  now `disponibleOnline` (default `true`) and there is a new `disponibleVenta` (default `true`). The create (`AltaArticulo`) and
  edit (`EdicionArticulo`) bodies do NOT carry them: they are written only by the `/disponibilidad` endpoint.
- TS mirrors to update: `src/Ways.Web/src/api/tipos.ts` (`publicadoWeb` in the article listing and the grid row), `FormularioArticulo.tsx`,
  `SeccionDeTiendaOnline.tsx`, `GrillaDeArticulos.tsx` and the fixtures that set `publicadoWeb`.

Added:
- `OfertaListado.disponibleOnline`, `AltaOferta.disponibleOnline` (optional, default `true`), `EdicionOferta.disponibleOnline`
  (OPTIONAL: omitted = unchanged; T2 may always send it).
- `ComboListado.disponibleOnline`, `AltaCombo.disponibleOnline` (optional, default `true`), `EdicionCombo.disponibleOnline`
  (OPTIONAL: omitted = unchanged; T2 may always send it).
- `GET /api/articulos?soloVendibles=true` (the POS search should send it; purchases and the back-office must not).
- `incluirNoVendibles=true` (optional) on `GET /api/articulos/escaneo` and on `GET /api/articulos?soloVendibles=true`: returns the
  internal-use article anyway (returns/notas de crédito from the back office; still requires `activo`). The POS snapshot stays
  exclusive: an offline return of an internal-use article is not possible on the device and needs the back office.
- `409 articulo_no_vendible` from `POST /api/ventas` (also when converting a presupuesto), `POST/PUT /api/presupuestos`,
  `POST/PUT /api/remitos`, `POST /api/remitos/{id}/emitir` and `POST /api/remitos/facturacion`.
- `GET /api/publico/catalogo`: articles gain optional `precioOriginal` (decimal) and `oferta` (string), both ABSENT (not `null`)
  when no offer lowers the price; `precio` is the final price. The response gains `combos` (always present, `[]` unless the request is
  `pagina = 1` with no `buscar` and no `idCategoria`).
- `GET /api/publico/combos` (anonymous, `Cache-Control: public, max-age=60`, `404 vitrina_no_publicada`):
  `[{ id, nombre, precio, componentes: [{ idArticulo, nombre, cantidad, imagenVersion }] }]`, sorted by name. There is NO `codigo`
  (it is the POS shortcut: T3 must not show "C<codigo>"). A combo has no image of its own: use
  `GET /api/publico/articulos/{idArticulo}/imagen?v=<imagenVersion>` of its components. Only combos that save are listed: the
  combo's price rounded to the peso must be strictly lower than the sum of its components' storefront prices (list price with the
  available online offer, quantity 1) times their quantity, mirroring `ResolvedorDeCombos` (`cobro >= costo con ofertas` is never
  applied). Small known divergence: the engine prices offers with the real cart quantity, the storefront at quantity 1.
- The storefront never sees articles with `disponible_venta = false`, so a `combo` cart line must reference only listed combos;
  the checkout (later) re-detects combos server-side.

### T2

Route: delegated writer (one), no inline source edits by the parent.

- `npx tsc -b`: red first (renamed DTO fields), then exit 0 on the final tree.
- `npx oxlint`: exit 0, 17 warnings, all the baseline `only-export-components` / `exhaustive-deps` ones; none in a new file.
- `npx vitest run`, whole suite: 210 files, 5930 tests passed on the final tree (runs 1 and 3 green; runs 2 and 4 each had one
  load-induced timeout, 2nd run unidentified, 4th run `NuevaFamilia.test.tsx > con más de 100 artículos…` at its 5 s limit; that
  test takes 2.1 s alone, passes alone, and its only change here is the two renamed fixture fields).

Mutation evidence (mutate, run the named files, revert; every row went red in the named test and green after the revert):

| Clause mutated | Killed by |
|---|---|
| venta switch sends `disponibleOnline: true` instead of the current value | `SeccionDeDisponibilidad.test.tsx` "el interruptor de la venta escribe con su propio PUT…" and "dos escrituras seguidas arrastran lo que dejó la anterior" |
| online switch sends `disponibleVenta: true` instead of the current value | same file, "el interruptor online escribe con su propio PUT…" |
| role gate reduced to `usuario === null` | `SeccionDeDisponibilidad.test.tsx` (vendedor, supervisor, root) and `Articulos.test.tsx` "quien no administra el catálogo no ve la sección" |
| `formularioOfertaVacio` with `disponibleOnline: false` | `ofertas.test.ts` (defaults, `aAltaOferta`) and `Ofertas.test.tsx` (checked on create, POST body) |
| `formularioComboVacio` with `disponibleOnline: false` | `combosAbm.test.ts` "formularioComboVacio" and `Combos.test.tsx` (checked on create, POST body, ambiguous-create reconciliation) |
| POS search modal without `soloVendibles` | `ModalDeBusquedaDeArticulos.test.tsx` "la búsqueda pide soloVendibles=true" |
| presupuesto / remito line search without `soloVendibles` (two runs) | `Presupuesto.test.tsx` / `Remito.test.tsx` "el buscador de la línea pide soloVendibles=true" |
| grid "Online" badge without the `disponibleVenta` condition | `GrillaDeArticulos.test.tsx` "la columna Disponibilidad marca…" |

Decisions and deviations:

1. The section is titled "Venta y tienda online", not "Disponibilidad": `FormularioArticulo.tsx` already renders a "Disponibilidad"
   heading for the empresas the article is available in. The grid column is still "Disponibilidad".
2. `SeccionDeTiendaOnline` was renamed to `SeccionDeDisponibilidad` (git rename, test included); its two switches share one error
   slot, one re-entrancy ref and one inert window with the image writes. The PUT always carries the changed value and the other
   switch as shown on screen.
3. The 409 `articulo_no_vendible` copy is the server message (it names the article by internal code, not by name) plus "Sacalo para
   poder continuar."; wired into the POS charge, and the presupuesto and remito draft saves (`api/articuloNoVendible.ts`). Emitting
   or invoicing a remito does not re-check on the server, so there is nothing to map there.
4. `clienteDeArticulos.listar` gained an optional fourth parameter `{ soloVendibles }`; purchases, stock, transfers, families and the
   offer and family pickers do not send it. The combo component picker first was left alone on purpose, but judgment-day found
   that an internal-use article could then be added as a component; it now sends `soloVendibles=true` (see "Judgment-day
   adjustments").
5. `AltaOferta`/`EdicionOferta`/`AltaCombo`/`EdicionCombo` carry `disponibleOnline: boolean` as required on the web (the form always
   sends it); `comboCoincideConAlta` now also compares it, so the ambiguous-create reconciliation cannot match a combo created with
   the other value.
6. Two commits instead of one: articles plus selling screens (`804e57a2`), offers plus combos (`fd368023`); the shared files
   (`tipos.ts`, `Pos.test.tsx`) were split by hunk. Only the final tree was type-checked and tested; the first commit alone was not
   built in isolation.

#### Judgment-day adjustments (commit on `pnader/disponibilidad-web-admin-ajustes`)

Informational findings of the judgment-day round, applied with no other change:

1. Combo component picker (`SelectorDeArticuloDeCombo.tsx`) sends `soloVendibles=true`; offer and family pickers untouched.
   Test: `Combos.test.tsx` "el buscador de componentes pide soloVendibles=true".
2. `SeccionDeDisponibilidad.tsx`: the toggled switch is disabled during its write and loses focus in real browsers, so
   after the write settles (success or failure) focus returns to that same switch (`react-async-state` rule 12). Tests for both
   switches and both outcomes; jsdom has no focus fix-up, so they move focus away explicitly inside `act`.
3. The "Online" badge (articles grid) and the "Online" column (offers, combos) require the row to be active as well. Tests with an
   inactive row in the three screens.
4. The failure alert of the switches has an id (`art-disponibilidad-error`) referenced by the `aria-describedby` of the switch
   that failed, next to its help text id, like the image error. Tests for both switches and for the association being dropped on
   the next action.
5. Prose: the section is "Venta y tienda online" in `FormularioArticulo.tsx` and `api/catalogoWeb.ts` comments; the comments in
   `api/tipos.ts`, `api/ofertas.ts` and `api/combosAbm.ts` no longer call `disponibleOnline` mandatory on edit (the server
   treats an omitted field as "unchanged"; the web always sends it). The `disponible_online_requerido` copy entries stay,
   with a comment saying they are defensive and currently unreachable.

Mutation evidence (mutate, run the four affected files, revert; each went red in the named test):

| Clause mutated | Killed by |
|---|---|
| combo picker `listar` without `soloVendibles` | `Combos.test.tsx` "el buscador de componentes pide soloVendibles=true" |
| grid "Online" badge without `a.activo` | `GrillaDeArticulos.test.tsx` "la columna Disponibilidad marca…" (inactive row) |
| combos "Online" column without `combo.activo` | `Combos.test.tsx` "la columna "Online" dice Sí solo si el combo está disponible online y activo" |
| offers "Online" column without `o.activo` | `Ofertas.test.tsx` "la grilla muestra la columna "Online" con Sí solo si la oferta está disponible online y activa" |
| switch focus restore removed (`destino?.focus()`) | `SeccionDeDisponibilidad.test.tsx` "el interruptor … recupera el foco…" (the four variants) |

Verification on the adjusted tree: `npx tsc -b` exit 0; `npx oxlint` 0 errors, 17 warnings (baseline); `npx vitest run
src/paginas src/api src/componentes` 153 files, 4562 tests passed; whole suite 210 files, 5939 tests passed.

### Full integration run

`dotnet test tests/Ways.IntegrationTests`, once and alone, on the final tree (before this doc commit): 3797 passed, 0 failed, 39 min 54 s.

## Delivery slices

- PR 11: T1 + T2 (stacked on #403). PR 12: T3.

## Next step

T1 and T2 ship together in one PR (the backend renames contracts the deployed web uses, so
they cannot be split without a broken window); T3 (storefront and landing) is stacked on it.
Both PRs merge after a clean judgment-day round.
