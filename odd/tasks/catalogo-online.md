# Catálogo online: artículos publicados, imágenes y carrito

## Objective

A commerce publishes articles one by one (flag per article, the owner's choice on 2026-10-11)
and `/online` shows them with price and image, searchable and grouped by category, with a real
cart (quantities, totals, persisted in the browser). Checkout, orders, stock reservation and
payment are the next feature (`pedidos-online`).

## Problem / why

- `/online` is a shell: no public read model for articles, prices or images.
- Articles have no "published on the web" flag and no image.
- Prices live in `listas_precio`/`precios`; the vitrina must say which list the web uses.

## Scope (authorized by the owner, 2026-10-11, autonomous mode; "flag por artículo, dale")

Stacked on `landing-del-comercio` (branch `pnader/landing-web-pr`, PR #388). Assumptions made
by the orchestrator, to be confirmed by use: the catalog ignores stock in this feature (stock
gating belongs to orders); offers (`ofertas`) are not applied on the web yet, only the list
price; one image per article, stored like the logo (bytea ≤ 1 MB, png/jpeg/webp).

## Data model (evidence)

- `articulos.publicado_web boolean NOT NULL DEFAULT false` (tenant-wide: the published vitrina
  belongs to one empresa and availability per empresa already comes from
  `disponible_para_todas` / `articulos_empresas`).
- `vitrinas.id_lista_precio integer NULL` FK → `listas_precio` (NULL = the `es_default` list);
  the list must be active and of the same tenant (checked in the service, FK with id_tenant).
- Table `imagenes_articulo` (tenant scope): `id_imagen_articulo` PK, `id_tenant`, `id_articulo`
  FK (UNIQUE among live rows: one image per article for now), `contenido bytea ≤ 1 MB`,
  `content_type` (png/jpeg/webp by magic bytes), `actualizado_en timestamptz` (ETag), audit.
- `Publico` mode SELECT-only policies added on: `articulos` (`publicado_web AND activo`),
  `articulos_empresas`, `listas_precio`, `precios`, `categorias`, `imagenes_articulo` (all with
  `id_tenant = app_tenant_publico()`). No other table.

## Constraints

- Same runners, skills and delivery rules as the previous features (judgment-day before each PR;
  auto-merge + Auto-fix; stacked PRs; no AI attribution).
- Quantity rules per `unidad_venta` (memory): `Unidad` = integers, `Peso` or absent = up to 3
  decimals.
- Price resolution: the current price of the article in the vitrina's list (fixed: the row with
  `vigente_hasta IS NULL`; derived: base price ± `porcentaje`, rounded like the POS does). Reuse
  the existing price logic instead of re-implementing it; if the POS resolver cannot run under
  Publico mode, write a dedicated projection and say so.
- Visual language: same as the landing (`src/publico/*`, Bricolage headings, accent, cards).

## Tasks

- [x] **T1 — Backend: publication flag, image, vitrina price list, public catalog.** Delegated.
  PR 7. Done in `b97dc4ee` (schema, domain, admin) and `e02f9dac` (public catalog); route: delegated
  direct (one writer; trigger: 2+ non-trivial files and broad reading).
  - Migration `CatalogoOnline`: the column, the FK on vitrinas, the table, the policies, the
    inventories (EtiquetasDeTablas, dependents golden, non-retry list, authorization allowlist).
  - Admin: `PUT /api/articulos/{id}/web` body `{ publicado }` (404-first, Admin/Supervisor roles
    that can edit articles), `GET/PUT/DELETE /api/articulos/{id}/imagen` (raw image body like the
    logo; 413/415), `idListaPrecio` added to `GET/PUT /api/empresas/{id}/vitrina` (400
    `lista_invalida` when inactive or missing), `publicadoWeb` and `tieneImagen` exposed in the
    article listing/grid DTO.
  - Public (Publico mode, tenant from host, 404 `vitrina_no_publicada` when none):
    `GET /api/publico/catalogo?buscar=&idCategoria=&pagina=&tamano=` → `{ categorias: [{id,
    nombre, cantidad}], articulos: [{ id, nombre, descripcion, idCategoria, precio, unidadVenta,
    tieneImagen, imagenVersion }], total, pagina, tamano }`, only articles published, active,
    available for the vitrina's empresa and with a current price in the list; sorted by name;
    `Cache-Control: public, max-age=60`. `GET /api/publico/articulos/{id}/imagen` with ETag/304
    without loading the bytes, 404 when the article is not published.
  - Tests: unit (flag, image sniffing reuse), integration (isolation: an unpublished or other
    tenant's article never appears; the derived-list price; the empresa availability; 404s;
    admin round-trips; mutation evidence on the policy predicates and the availability clause).
- [ ] **T2 — Web admin: publish toggle and image on the article, price list on the vitrina.**
  Delegated. PR 8.
  - Article form: "Publicado en la tienda" switch (own write, not the big article PUT) and image
    block (upload-on-select, preview, quitar) like the vitrina logo; grid column/filter
    "Tienda".
  - Vitrina page: "Lista de precios de la tienda" select (default list when empty).
- [x] **T3 — Web `/online`: catalog and cart.** Delegated. PR 9. Done in `df705a12` and `b46a0f00`;
  route: delegated direct (one writer; trigger: 2+ non-trivial files and broad reading).
  - Catalog grid with search box, category chips with counts, product cards (image or placeholder
    mark, name, price formatted `$ 1.234,56`, unit), pagination or "Ver más".
  - Cart: add/remove/change quantity per `unidad_venta` rules, totals, persisted in
    `localStorage` keyed by host, panel with the lines and a disabled "Finalizar pedido" that says
    the next step is coming. No checkout.
  - Tests for helpers (formatting, quantity steps, cart reducer) and page states.

## Verification evidence

T1 (commits `b97dc4ee`, `e02f9dac`), all observed:

- `dotnet build src/Ways.Api/Ways.Api.csproj -warnaserror`: 0 warnings, 0 errors.
- `dotnet ef migrations has-pending-model-changes`: no pending changes.
- `dotnet test tests/Ways.Domain.Tests`: 1262 passed. `dotnet test tests/Ways.Application.Tests`: 819 passed.
- `dotnet test tests/Ways.IntegrationTests` (alone, at the end): 3530 passed, 0 failed (31 m 52 s).
- `dotnet format --verify-no-changes` over the changed `.cs`: only baseline noise (CRLF/`System`-last
  imports/`var` rules the repo does not follow); nothing else.
- New tests: 23 Domain (image validator, `ImagenDeArticulo`, family classification), 3 integration
  classes: `CatalogoOnlineAislamientoTests` (13, raw `ways_app` connection, no EF filter),
  `CatalogoOnlineAdminTests` (29), `CatalogoPublicoTests` (21).

Mutation evidence (clause removed or inverted, test run, reverted; every row went red):

| Clause | Killed by |
|---|---|
| policy `articulos`: `publicado_web` / `activo` / `deleted_at IS NULL` (each removed) | `LaPolicyDeArticulos…`, `ElContextoPublicoDeEf…` |
| policy `listas_precio` / `precios` / `categorias` / `imagenes_articulo`: `deleted_at IS NULL` (each) | the matching `LaPolicyDe…` test + the EF one |
| policy helper: `id_tenant = app_tenant_publico()` removed | 8 policy tests |
| policy helper: `app_modo() = 'publico'` removed | the 3 `ElGucPublicoSinElModoPublico…` cases |
| availability: whole clause, each half, and the `id_empresa` conjunct | `SoloAparecenLosPublicados…` (+10 others when only restricted ones show) |
| price existence: removed, other list, future (`vigente_desde`), expired and open-ended (`vigente_hasta`) | `SoloAparecenLosPublicados…`, price tests |
| amount: other list mixed in; oldest overlapping window wins | `ElPrecioSaleDeLaLista…`, `SoloAparecen…` |
| list resolution: inactive, other empresa, empresa default, derived-of-derived, derived treated as fixed | `UnaListaDeLaTiendaQueNoSePuedeUsar…`, `LaDefaultEsLaCompartida…`, price test |
| search: name half, code half, `%` escape, trim, 100-char cap | `LaBusquedaEncuentraPorNombreOCodigo…` |
| categories: inactive, other empresa, filter on invisible, chips honour the filter, invisible id reported, chip order | `LasCategoriasCuentan…`, contract test |
| paging: size cap, size floor, page floor, order by id | `LaPaginaYElTamano…`, contract test |
| image (public): availability, published-article join, article id | `LaImagenDeUnArticuloQueElCatalogoNoMuestra…` |
| 304 branch removed | admin, public and logo ETag tests |
| public: cache header, suspended tenant serves | `ElCatalogoDevuelve…`, `SinVitrinaPublicada…` |
| admin: `FOR UPDATE` on publish / on image, post-lock liveness check, 404 after body, writes always, always insert, no soft delete, no `actualizado_en` bump | the lock-holder race tests, 404 matrix, round trip |
| admin: non-retrying strategy → retrying (publish, upload, delete) | `UnaFallaTransitoria…` x3 + `EscriturasSinReintentoEstructurales` |
| admin: `GestionDeCatalogo` removed from each of the 3 writes | role test + `SuperficieDeAutorizacionTests` |
| 1 MB limit `>` → `>=` | domain + admin 1 MB test |
| DTO: `tieneImagen` / `publicadoWeb` hard-coded in grid, list, detail, projection, edit response | admin round-trip |
| vitrina list: inactive, other tenant (platform actor), other empresa, not persisted, not validated, `FOR KEY SHARE` removed, read-back | `UnaListaInvalida…`, `…Plataforma…`, `UnaListaQueSeDaDeBaja…` |

Not observable, so not tested: the `idsArticulo.Contains(...)` conjunct of the page price query (it only
narrows rows already keyed by article id).

Deviations and decisions (to confirm):

- The default list is the shared one (`id_empresa IS NULL AND es_default`), as the back-office grid
  does; an empresa-own default is ignored. A vitrina list that is unusable (inactive, other empresa,
  derived without a fixed base, no default) gives an empty catalog, not an error.
- The public policies do not decide price validity, list activity or whether an image's article is
  published (only `articulos` filters publication); `ConsultaDeCatalogoPublico` does. Documented in doc 09.
- Added `ix_articulos_publicado_web` (partial index) beyond the approved model summary.
- Admin `GET /api/articulos/{id}/imagen` follows the article group (`OperacionDePos`: a Vendedor can
  read); the three writes need `GestionDeCatalogo` (Admin only), the same as editing an article.
- No audit rows: the article ABM does not audit.
- Removing an image is a soft delete that keeps its bytes; a later upload creates a new row.
- `ArticuloListado` and `ArticuloGrillaFila` gained `publicadoWeb`/`tieneImagen` (additive, defaulted);
  `ServicioDeVitrinas` now takes `GuardaDeReferencias`.
- Search covers name and internal code, not barcodes; categories are a flat list, no hierarchy roll-up.
- `ValidadorDeImagen` extracted from `Vitrina`; its public API (`ValidarLogo`, `DetectarTipoDeLogo`, …) is
  unchanged.

T3 (commits `df705a12`, `b46a0f00`), all observed in `src/Ways.Web`:

- `npx tsc -b`: clean. `npx oxlint`: no finding in `src/online`, `src/publico` or `src/api/publico.ts` (the
  existing `only-export-components` warnings elsewhere are the baseline). `npx vite build`: built, `online` entry 15.9 kB.
- `npx vitest run src/publico src/online src/landing`: 10 files, 208 passed. `npx vitest run` (whole suite, alone):
  191 files, 4975 passed, 0 failed.
- New tests (73; `src/online` totals 87 with the 14 of `Tienda.test.tsx`): `carrito.test.ts` (26: quantity rules, formatting, reducer, persistence),
  `useCatalogo.test.tsx` (9: generation gates, append, errors), `CatalogoDeLaTienda.test.tsx` (22: render, search
  debounce, chips, Ver más, states, add/steppers), `Carrito.test.tsx` (12: panel, vaciar, persistence, provider),
  `clienteDelCatalogo.test.ts` (4), plus the existing `Tienda.test.tsx` adapted to the catalog mock.

Mutation evidence (clause changed, `vitest run src/online`, reverted; every row went red):

| Clause | Killed by |
|---|---|
| `Unidad` rule: cart validates every line as `Peso` | 6 tests (cart rules, typed text, persistence filter, reducer add/change, typed-quantity UI) |
| storage key without the host | 4 (key test, round trip per host, provider with two hosts, other host's cart not visible) |
| first-read response applied without the generation gate | stale-response test (hook) + stale search test (page) |
| first-read failure applied without the gate | late-failure test (hook) |
| Ver más response applied without the gate | late-page-after-filter-change test (hook) |
| merge on add: replaces instead of summing / always appends a new line | 4 each (reducer merge, decimals and 999 cap, last price wins, same-tick double add) |
| search without pause (immediate request per key) / pause shortened to 50 ms | debounce test (299 ms no request, 300 ms one request with the last term) |
| Enter does not search | Enter test |
| Ver más appends without dedupe by id / `hayMas` always true | append test / 4 tests |
| persistence: `imagenVersion` not validated | corrupt-lines test |

Design decisions (to confirm):

- The cart line is a snapshot `{ idArticulo, nombre, precio, unidadVenta, cantidad, imagenVersion }`. Every list the
  catalog receives (first read, another filter, "Ver más") syncs the lines whose article is in it (`sincronizar`:
  name, price, unit and image version; a quantity that is no longer valid for a changed unit is rounded, min 1 for
  `Unidad`). Lines whose article is not in any list read stay as they were: repricing is the job of `pedidos-online`.
  Adding the same article again (only reachable by two "Agregar" in the same instant) sums quantities and takes the
  newer read.
- Quantity rule reuses `api/cantidadPorUnidad.ts` (`Unidad` integers, `Peso` up to 3 decimals) plus a 999 cap per
  line; the stepper buttons add one (`Unidad`) or half a kilo (`Peso`), the field accepts comma or point; going
  below the minimum with "−" removes the line; a typed invalid value shows a hint and the cart keeps the last good one.
- Cart `cantidad` (header badge) counts lines, not units. Persistence key `ways.tienda.carrito:<host>`; invalid or
  repeated lines are discarded one by one, a corrupt blob or blocked storage gives an empty cart.
- The "Todos" chip shows no count: the server's chip counts exclude uncategorised articles, so a total there would
  not match the sum. A selected category that disappears from the chips after a search stays selected and "Todos"
  stays visible (not pressed) so the user can leave that filter; the server answers with an empty page.
- The catalog empty state ("El catálogo se está preparando.") only shows when the FIRST good read, asked without
  filters, returned no articles (`vacioAlEntrar`, decided from the response, never from the current filter). From then
  on the search bar is never replaced; an unfiltered empty answer after that shows "Por ahora no hay artículos
  publicados.", and with any filter "No encontramos artículos para esa búsqueda.".
- The cart provider owns the `aria-live` region ("X agregado al carrito", "X quitado del carrito", "Carrito vaciado").
- Two open tabs of the same shop: a `storage` event for the cart key replaces the in-memory cart with the stored one
  (validated line by line) and nothing is written back when the stored value is already equal. There is no merge: the
  whole cart is saved, so if two tabs change something at once the last writer wins and what the other had unsaved is
  lost. The change from the other tab is not announced.
- Typed quantities apply on blur or Enter, not keystroke by keystroke; a rejected value reverts to the last good one
  and a polite (`aria-live="polite"`) message says the quantity did not change. No `role="alert"` while typing.
- Focus targets when a control disappears (jsdom does not implement the browser's focus fix-up, so the tests assert the
  chosen target): "Agregar" → the stepper's "+"; "−" removing a line on a card → that card's "Agregar"; in the panel
  "Quitar" or "−" removing a line → the next line's "Quitar", else the previous, else "Cerrar".

T3 judgment-day round 1 fixes (branch `pnader/catalogo-web-online-ajustes`, on top of `763463ac`), all observed in `src/Ways.Web`:

| Id | Commit | What | Killed by (mutation applied, test red, reverted) |
|---|---|---|---|
| L11 | `76a41bd4` | `TarjetaDeArticulo` in `memo`, quantity by prop from a `cantidades` map, actions in their own stable context | `memo` removed / actions object not memoized → "agregar un artículo no vuelve a dibujar la tarjeta de otro" |
| L2 | `483ec60e` | `sincronizar` reducer action dispatched by the catalog on every list it receives | effect removed → 4 UI tests; `Math.max(1,…)` dropped, "same array" return dropped, `precio` comparison dropped → reducer tests |
| L5 | `5a84da47` | `storage` listener + `reemplazar`; `guardarCarrito` skips an equal value | `storageArea` check / key check / `null` key / cleanup / equality guard each removed → the matching test |
| L1, L8, L10, L12 | `c95adf40` | `vacioAlEntrar`/`respuestaSinFiltros` from the response; `<main>` without `role`; "Todos" with an active category; bar schedules nothing on mount | old predicate → 3 page tests (bar remounted); `vacioAlEntrar` without `prev` → 2 tests; `role="status"` on `<main>` → skeleton test; chip condition → 2 tests; mount guard / `buscar` dedupe → their tests; pause 300 → 50 ms → 2 debounce tests |
| L7 | `07337c08` | typed quantity applied on blur/Enter, polite message | live commit, blur commit, Enter handler, and each of the three `setRechazada(false)` removed → the matching test |
| L3 | `e4c58a5f` | explicit focus targets | focus call, next-only neighbour, `Cerrar` fallback, stepper `alQuitar`, card `mas`/`agregar` targets, ungated effect → the matching test |
| L4, L6, L9 | `e9f4c5f9` | 6 px padding / −6 px margin on the scrollers, `--pub-borde-campo` token, `.publico-boton-chico` 44 px | each value reverted → computed-style or contrast test |

- `npx tsc -b`: clean. `npx oxlint`: no finding in `src/online` or `src/publico` (only the baseline `only-export-components` warnings elsewhere).
  `npx vite build`: built, `online` entry 18.2 kB.
- `npx vitest run src/online src/publico src/landing`: 13 files, 272 passed. `npx vitest run` (whole suite): 194 files, 5039 passed. A
  first whole-suite run had two timeouts in `Familia.test.tsx` and `NuevaFamilia.test.tsx` (unrelated, load-induced); both pass alone
  (127 tests) and the second whole-suite run was green.
- Found while testing: `CarritoProvider` saves in an effect, so a test that cleared `localStorage` before unmounting let the unmount flush
  write the cart into the next test. `afterEach` in `CatalogoDeLaTienda.test.tsx` and `Carrito.test.tsx` now unmounts first.
- Limits of the evidence: the focus tests and the "stays mounted" test assert targets and element identity in jsdom, not the browser's
  focus fix-up; the contrast test reads the stylesheet text, it does not render; the render-count test measures a card's render through
  the image URL call, not render cost.

## Delivery slices

- PR 7: T1 (stacked on #388). PR 8: T2. PR 9: T3.

## Next step

Feature complete; next: pedidos-online.
