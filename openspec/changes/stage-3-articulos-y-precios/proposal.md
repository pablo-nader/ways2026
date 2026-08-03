# Proposal: Stage 3 — Artículos y Precios

## Intent

Implement Etapa 3 of `docs/10-modelo-de-datos.md`: `articulos`, `codigos_barra`,
`precios` (with history), and the completion of `listas_precio` (derived lists +
ABM). Doc 10 marks stage 3 as the unlock for the POS — no sale can be modeled
until an article with a resolvable price exists. This is the largest catalog
slice so far: schema (4 write paths: articulos, codigos_barra, articulos_empresas,
precios) + API + a field-heavy React ABM, plus a margin-based price-suggestion
helper. Expect several chained PRs (flagged explicitly for `sdd-tasks`).

## Scope

### In Scope
- `articulos`: tenant-wide (doc 10 §3 availability model — NOT the `id_empresa
  NULL` catalog pattern). `disponible_para_todas = true` (default) covers all
  empresas including future ones automatically; `articulos_empresas` junction
  holds explicit subsets only when `false`. Full column set: clasificadores
  (`id_area`, `id_categoria`, `id_marca`, `id_grupo`), `id_alicuota_iva`,
  `unidad_venta` (enum `unidad`|`peso`), costs (`costo_lista`,
  `descuento_proveedor`, `costo_nominal`), `es_producto`.
- `codigos_barra`: N per artículo, `UNIQUE (codigo, id_tenant)` — one barcode
  belongs to exactly one artículo, no overrides. `codigo_interno` `UNIQUE
  (id_tenant, codigo_interno)`.
- `precios` with history: per `(articulo, lista_precio)`, close-and-open on
  change, `vigente_desde`/`vigente_hasta`, **programmable** (future
  `vigente_desde` allowed), never overwritten.
- `listas_precio` completed: `modo = derivada` becomes functional (resolved at
  read/sale time from the base list + `porcentaje`, no stored `precios` rows),
  full ABM (create/edit, both `fija` and `derivada`). General stays the only
  seeded default — no Empleados default (user declined; tenants create their
  own).
- Margin-based price suggestion in the artículos ABM: `costo` + `margen`
  (grupo or proveedor) → proposed price, one click to apply — **never
  automatic**.
- ABMs: dedicated `articulos` screen (identification incl. barcode manager,
  classification, costs, availability picker, per-lista price editor with
  history view) and `listas_precio` ABM.

### Out of Scope
- `ofertas` (stage 4)
- `stock`/`movimientos_stock` (stage 5 per doc 10's sequence, referenced here as stage 5)
- Legacy data migration
- POS / selling flow itself

## Capabilities

### New Capabilities
- `articulos`: artículo CRUD/ABM, availability model (`disponible_para_todas` +
  `articulos_empresas`), `codigos_barra` management (N per artículo, tenant-unique).
- `precios`: price-with-history engine (close-and-open, programmable future
  `vigente_desde`, current-price-at-date resolution), margin-based price
  suggestion helper (manual apply only).

### Modified Capabilities
- `listas-precio-minimal`: `modo = derivada` becomes functional (resolved at
  read time, no stored rows); full tenant-facing ABM replaces the
  platform-seed-only behavior. `sdd-spec` may rename this capability (drop
  "-minimal") since it is no longer minimal — decided at spec time, not here.

## Approach

1. **Reuse, not redesign.** `EntidadTenant`, RLS helper, keyed platform
   context, `ManejadorDeErrores` mapping contract, and the stage-1 generic
   catalog machine (only where the shape genuinely fits — `listas_precio`'s
   ABM likely does, `articulos` almost certainly does not, given its field
   count and the availability-junction relationship; design decides).
2. **DB CHANGE GATE** before every migration: full model summary (tables,
   columns, indexes, constraints, RLS, tenancy scoping) presented for explicit
   approval — this stage has 4 new/changed write paths, so the gate summary
   groups them clearly.
3. `articulos` ships tenant-wide (no `id_empresa` column) — availability is
   modeled as a separate junction, not ownership, per doc 10's 2026-08-02
   decision.
4. `precios` never UPDATEs a price value: a change closes the vigente row
   (`vigente_hasta = now()` or the new row's `vigente_desde`) and inserts a new
   one. Current-price queries filter `vigente_hasta IS NULL` (or `<= :fecha` /
   `> :fecha` for historical/point-in-time lookups).
5. Derived lists compute price on read (`base_precio * (1 ± porcentaje)`); no
   `precios` rows are created for them.
6. Every new unique/FK constraint gets its `db-error-backstops` mapping and
   race test in the same work unit that introduces it.

## Affected Areas

| Area | Impact | Description |
|---|---|---|
| `src/Ways.Domain/Articulos` (new) | New | `Articulo`, `CodigoBarra`, availability rule |
| `src/Ways.Domain/Precios` (new) | New | `Precio` (history), derived-price resolution, margin suggestion |
| `src/Ways.Domain/Catalogos` | Modified | `ListaPrecio` gains derivada resolution logic |
| `src/Ways.Infrastructure` | Modified | EF configs, migrations, RLS, new backstop mappings |
| `src/Ways.Application` | Modified | `ServicioDeArticulos`, `ServicioDePrecios`, `ServicioDeListasPrecio` (new ABM) |
| `src/Ways.Api` | New | `ArticulosEndpoints`, `PreciosEndpoints`(-ish, folded into articulos), `ListasPrecioEndpoints` |
| `src/Ways.Web` | New | Artículos ABM screen (heaviest screen to date), Listas de Precio ABM |

## Risks

| Risk | Likelihood | Mitigation |
|---|---|---|
| Reviewer overload (largest catalog slice: 4 write paths + heavy ABM) | High | Chained PRs, stacked-to-main; `sdd-tasks` slices by write path (articulos → codigos_barra → precios → listas ABM → margin suggestion → screens) |
| Programmable future prices create ambiguous ordering if unconstrained | Med | Default: at most one *pending* (not-yet-vigente) row per `(articulo, lista)` — see question round |
| Derived-list read-time resolution adds a join/compute step to every price lookup | Med | Keep resolution in one Domain service, unit-testable without DB |
| Margin suggestion precedence (grupo vs. proveedor) ambiguous when both exist | Med | Explicit rule needed before design — see question round |
| Availability toggle (`disponible_para_todas` true→false) UX/validation | Low-Med | Service-level guard: `false` requires ≥1 `articulos_empresas` row |

## Rollback Plan

All additive: new tables (`articulos`, `codigos_barra`, `articulos_empresas`,
`precios`); `listas_precio` gets new *behavior* (service/API), no destructive
column change (the `modo`/`id_lista_base`/`porcentaje` columns already exist,
unused, since stage 2). New endpoints and screens are new routes, removable
without touching stage 1/2 flows.

## Dependencies

- Stage 1 & 2 (merged): `EntidadTenant`, RLS helpers, catalog machine, keyed
  platform context, `ManejadorDeErrores` contract, `listas_precio` table.
- DB Change Gate approval (blocking, before each migration).

## Success Criteria

- [ ] `articulos` tenant-wide with working availability model (default-true
      auto-covers future empresas; explicit subset via `articulos_empresas`)
- [ ] `codigos_barra`: N per artículo, tenant-unique, no cross-artículo overrides
- [ ] `precios`: price changes never overwrite; history queryable at any date;
      future `vigente_desde` schedulable
- [ ] `listas_precio` derivada mode resolves correctly at read time with no
      stored rows; ABM (create/edit) works for both modes
- [ ] Margin-based price suggestion proposes but never auto-applies a price
- [ ] Artículos + Listas de Precio ABMs working end-to-end in `Ways.Web`
- [ ] Every new constraint has its `db-error-backstops` mapping + race test

## Resolved product decisions (question round)

Resolved with the user (2026-08-02). Binding for spec/design/tasks:

1. **Margin suggestion: grupo wins, over `costo_nominal`.** `grupos.margen` takes
   precedence (the purpose-built margin grouper); `proveedores.margen` is the
   fallback when the artículo has no grupo (or its grupo has no margen). Base is
   `costo_nominal` when present, else `costo_lista * (1 - descuento_proveedor)`.
   The suggestion is always click-to-apply, never automatic.
2. **At most ONE pending future price per (articulo, lista).** Scheduling a new
   future price replaces the still-pending one (UI confirms the replacement).
3. **Lista `modo` switch is BLOCKED once the lista has any `precios` history.**
   Create a new lista instead — explicit and auditable.
4. **Deactivating a lista referenced as `id_lista_base` is BLOCKED** while active
   derived lists point at it (same protection spirit as Consumidor Final).
5. **`codigo_interno` is MANDATORY (user overrode the optional default).**
   Every artículo has one, unique per tenant. When omitted at creation, the
   system autogenerates it from a per-tenant atomic counter (same pattern family
   as `numeraciones_clientes`); user-supplied values are allowed when unique.
   Doc 10's `NULL`-able marking is superseded by this decision.
