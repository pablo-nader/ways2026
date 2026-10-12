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

## Tasks

- [ ] **T1 — Backend: flags, policies, POS and sale enforcement, public offers and combos.**
  Delegated. PR 11.
  - Migration `DisponibilidadOnlineYVenta` (rename, two new columns, backfills, policies).
  - Domain/DTOs: `Articulo.DisponibleOnline`/`DisponibleVenta`, `Oferta.DisponibleOnline`,
    `Combo.DisponibleOnline`; listing/grid/detail DTOs rename `publicadoWeb` → `disponibleOnline`
    and add `disponibleVenta`; `OfertaListado`/`AltaOferta`/`EdicionOferta` and the combo
    contracts carry `disponibleOnline` (default true when omitted on create; required on edit).
  - Admin: `PUT /api/articulos/{id}/disponibilidad { disponibleVenta, disponibleOnline }` replaces
    `PUT .../web` (same discipline: 404-first, no-retry, lock, idempotent); offers and combos
    save the flag through their existing POST/PUT.
  - Sale enforcement: POS snapshot, `/escaneo`, sale search, `EmitirAsync` 409
    `articulo_no_vendible`, presupuestos reuse the same guard if they share the line validation.
  - Public: `GET /api/publico/catalogo` returns `precio` (final) and `precioOriginal` (when an
    available offer applies) plus `oferta` (name) per article; `combos: [{ id, codigo, nombre,
    precio, componentes: [{ idArticulo, nombre, cantidad, imagenVersion }] }]` on the first
    unfiltered page (and `GET /api/publico/combos` for the storefront). Public image of a combo:
    none (cards show component images).
  - Tests: unit (defaults, guard), integration (isolation of the four new policies with raw
    `ways_app` connections and mutation evidence; backfill under `ways_app` per
    rls-migration-backfills; offer applied/not applied by flag, list targeting incl. derived
    list; combo listed/excluded by each rule; POS snapshot and `EmitirAsync` with a non-sellable
    article; admin round-trips), structural guards.
- [ ] **T2 — Web admin.** Delegated. PR 12.
  - Article form: section "Disponibilidad" with two switches (own write), grid badges ("No se
    vende", "Online") and the existing image block; offers modal and grid: checkbox and column
    "Online"; combos modal and grid: the same.
- [ ] **T3 — Web storefront and landing.** Delegated. PR 13.
  - Cards show the offer price with the original struck through and an "Oferta" tag; combos
    section in `/online` ("Combos") with cards listing components and "Agregar" creating a
    `combo` cart line (integer quantity, fixed price, components shown in the panel); cart
    totals and `sincronizar` handle both line types; landing "Lo que vas a encontrar" shows the
    offer price too and a "Combos" strip when any.

## Verification evidence

(recorded per task as commits land)

## Delivery slices

- PR 11: T1 (stacked on #403). PR 12: T2. PR 13: T3.

## Next step

T1.
