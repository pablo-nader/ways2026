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
- [x] **T2 — Web admin: publish toggle and image on the article, price list on the vitrina.**
  Delegated. PR 8. Done in `575aa5ba` (article, grid) and `eb507303` (vitrina); route: delegated
  direct (one writer; trigger: 2+ non-trivial files and broad reading).
  - Article form: "Publicado en la tienda" switch (own write, not the big article PUT) and image
    block (upload-on-select, preview, quitar) like the vitrina logo; grid column "Tienda".
    The grid filter "Solo publicados" is NOT done: `GET /api/articulos/grilla` has no such
    parameter and filtering the loaded page on the client would lie under pagination.
  - Vitrina page: "Lista de precios de la tienda" select (default list when empty).
- [ ] **T3 — Web `/online`: catalog and cart.** Delegated. PR 9.
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

T2 (commits `575aa5ba`, `eb507303`), all observed in `src/Ways.Web`:

- `npx tsc -b`: exit 0. `npx oxlint`: exit 0 (16 pre-existing `only-export-components` warnings, none in new files).
- `npx vitest run`: 188 files, 4997 tests passed.
- New tests: `SeccionDeTiendaOnline.test.tsx` (43 at the T2 commits, 46 after the judgment-day fix round below),
  `catalogoWeb.test.ts`, 6 page-level cases in `Articulos.test.tsx`, 2 grid cases, 9 vitrina page cases, helper cases
  in `vitrinas.test.ts`.

Judgment-day fix round (branch `pnader/catalogo-web-admin-ajustes`, commits `765553a9`, `a2f58776`), observed in
`src/Ways.Web`: `npx tsc -b` exit 0; `npx oxlint` exit 0 (same pre-existing warnings); `npx vitest run src/paginas
src/componentes` 110 files, 2927 tests passed; `npx vitest run` 188 files, 5009 tests passed.

- L1 Escape: `ConfirmacionDeBaja` listens in the capture phase and stops propagation; `Modal` ignores an Escape that is
  already `defaultPrevented`. Test inside a real `Modal` (gate closes, editor stays, no `confirm`, next Escape closes the editor).
- L2: `alCambiar()` runs after the re-read of an uncertain failure or 404, success or failure of the re-read.
- L3: `ConfirmacionDeBaja` takes an optional `regreso` ref; the section passes its heading (`tabIndex=-1`). Focus asserted after confirm and cancel.
- L4: Root neither requests the lists nor renders the selector; the selector is no longer blocked when the lists fail
  to load; `lista_invalida` copy changed. The server validates the list on every save, so a stale saved list is
  rejected until the user picks another or the default.
- L5: the re-read sets `version` to `Date.now()` when the article has an image. Clearing `urlRota` was dropped: it is
  keyed by URL, so the new URL already revives the preview (the test passes without it).
- L6: the grid's "Tienda" column shows a "Con imagen" badge from `tieneImagen`.

| Clause | Killed by |
|---|---|
| `Modal` `evento.defaultPrevented` removed | Modal "Escape con defaultPrevented" case |
| `ConfirmacionDeBaja` `stopImmediatePropagation` removed | its "no llega a los listeners de burbujeo" case |
| both of the above removed (the original bug) | section-in-Modal Escape case |
| `regreso={tituloRef}` removed | "al confirmar ... el foco va al título" case |
| `alCambiar()` after re-read removed | the three uncertain-failure cases |
| `setVersion` in the re-read removed | "vista previa se vuelve a pedir con una versión nueva" |
| `\|\| errorDeListas !== ''` restored on the selector | the two failed-load cases |
| `if (esRoot) return` removed from the lists effect | Root "no pide las listas" |
| Root `lista_invalida` text mapping removed | Root `lista_invalida` case |
| grid badge reads `publicadoWeb` instead of `tieneImagen` | grid "Con imagen" case |

Mutation evidence (clause changed, tests run, reverted; each went red):

| Clause | Killed by |
|---|---|
| section role gate (`puedeGestionarCatalogos` removed) | vendedor / supervisor / root cases + page-level "quien no administra" |
| re-entrancy ref in `escribir` removed | double click on the switch, double file change, double confirm |
| client size check `>` → `> x1000` and `>` → `>=` | unit boundary cases + "más de 1 MB" and "exactamente 1 MB" component cases |
| `lista_invalida` field placement (code compared against another string) | "se explica junto al campo" (vitrina page) |
| `lista_invalida` copy entry removed | copy unit case + the same page case |

Deviations and decisions (to confirm):

- The section is shown only to Admin (`puedeGestionarCatalogos`), not Root: the three writes need
  `GestionDeCatalogo`, which excludes Root. The gate reads `AuthContext` directly and hides the section
  without a provider.
- `Formulario` gained read-only `publicadoWeb`/`tieneImagen` (initial state of the section); they are not in
  `aAlta`/`aEdicion` and do not count as unsaved changes. After a write the grid is refreshed through the
  existing `alGuardar` request (`alCambiarTienda`).
- The section's write responses are not token-gated: the modal stays inert during the write (`alDeEscribir`),
  the state belongs to the instance and nothing navigates (stated in the component doc).
- Vitrina price lists come from `GET /api/catalogos/listas-precio` (`OperacionDePos`, so Root gets a 403 there):
  Root does not request the lists nor see the selector (it reads "La lista de precios de la tienda la elige el
  administrador del comercio."); an Admin whose lists fail to load keeps a usable selector with the saved list and
  "Lista por defecto". Root cannot choose a list until the backend offers a readable endpoint for it.
- `ArticuloListado`/`FilaDeGrillaDeArticulos` require `publicadoWeb`/`tieneImagen` (the server always sends
  them), so about 15 test fixtures got the two defaults.

## Delivery slices

- PR 7: T1 (stacked on #388). PR 8: T2. PR 9: T3.

## Next step

T2 is committed on `pnader/catalogo-web-admin`; it still needs its judgment-day round before PR 8. Open
follow-up: a `soloPublicados` parameter on `GET /api/articulos/grilla` (and its filter in the grid). Then T3
(web `/online`: catalog and cart).
