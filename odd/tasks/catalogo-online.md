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

- [ ] **T1 — Backend: publication flag, image, vitrina price list, public catalog.** Delegated.
  PR 7.
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
- [ ] **T3 — Web `/online`: catalog and cart.** Delegated. PR 9.
  - Catalog grid with search box, category chips with counts, product cards (image or placeholder
    mark, name, price formatted `$ 1.234,56`, unit), pagination or "Ver más".
  - Cart: add/remove/change quantity per `unidad_venta` rules, totals, persisted in
    `localStorage` keyed by host, panel with the lines and a disabled "Finalizar pedido" that says
    the next step is coming. No checkout.
  - Tests for helpers (formatting, quantity steps, cart reducer) and page states.

## Verification evidence

(recorded per task as commits land)

## Delivery slices

- PR 7: T1 (stacked on #388). PR 8: T2. PR 9: T3.

## Next step

T1.
