# Tasks: Stage 3 — Artículos y Precios

## Orchestrator Decisions Recorded This Phase

Two design-time open questions had to be resolved before tasks could be ordered.
Recorded here as binding for `sdd-apply`; both are consistent with existing
codebase precedent, not new product decisions.

1. **`codigo_interno` autogeneration format** (design's open question 1):
   plain numeric correlative rendered as a `string`, no prefix, no stored
   left-padding (`AsignadorDeCodigoInternoArticulo.AsignarSiguienteAsync`
   returns `int`; the service converts to `string` unpadded before
   persisting to the `citext` column — UI may zero-pad for display only).
   Same shape as `AsignadorDeNumeroCliente`. **Constraint carried forward for
   stage 5 (documented, not enforced this stage):** the value must stay
   under 7 digits so the future POS scan-resolution logic (stage 5) can
   disambiguate a short internal code from a 13-digit EAN barcode by length
   alone. No realistic tenant reaches 1,000,000 artículos, so no cap is
   coded this stage — a code comment on `AsignadorDeCodigoInternoArticulo`
   documents the assumption for whoever builds stage 5.
2. **Derived-list depth-1 restriction** (design's open question 2, "DB CHECK
   or service-only"): **service-only this stage**, for consistency with
   every other row in design's Protection Rules table (all four are
   service-level, DB-level explicitly deferred as "future hardening, not
   built this stage"). `ServicioDeListasPrecio` rejects a `derivada` lista
   whose `id_lista_base` itself has `Modo == Derivada` before it reaches the
   database.

## Review Workload Forecast

| Field | Value |
|-------|-------|
| Estimated changed lines | ~4,150–6,050 total (incl. EF migration boilerplate) |
| 400-line budget risk | High |
| Chained PRs recommended | Yes |
| Suggested split | Slice 1 → Slice 2 → Slice 3 → Slice 4 → (Slice 5 ∥ Slice 6) |
| Delivery strategy | chained PRs, stacked-to-main (resolved, cached decision) |
| Chain strategy | stacked-to-main |

Decision needed before apply: No — resolved: chained PRs, stacked-to-main.
Six slices are forecast, each individually near or above the 400-line budget
(this is the largest catalog stage to date per proposal.md — 5 new/changed
tables, a transactional history engine, and the heaviest ABM screen so far).
Slice 1 (schema) is the most likely candidate for `size:exception`, same
precedent as stage 1 Slices 3/4 and stage 2 Slice 1. Slices 5 and 6 (the two
web screens) are independent of each other once their respective APIs exist
and can run in parallel.

### Suggested Work Units

| Unit | Goal | Likely PR | Est. lines | Notes |
|------|------|-----------|-----------|-------|
| 1 | Schema (5 tables + 4 alternate keys on existing tables, DB CHANGE GATE), domain entities, `numeraciones_articulos` counter, backstop mapping groundwork | PR 1 | ~1,500–2,000 | Base: `main`. One combined migration, one gate (design: Migration Sequencing). No backfill/seed needed (additive only, unlike stage 2). |
| 2 | Artículos + `codigos_barra` + `articulos_empresas` (service, API, tests) + margin suggestion (`SugeridorDePrecio`) | PR 2 | ~700–950 | Depends on PR 1. Margin suggestion folded in here, not a separate PR — see "Slice Cut Rationale" below. |
| 3 | Precios (history engine, service, API, tests) | PR 3 | ~550–750 | Depends on PR 1 + PR 2. |
| 4 | Listas de Precio ABM (service, API, tests) | PR 4 | ~400–550 | Depends on PR 1 + PR 3. |
| 5 | Web: Artículos screen (heaviest to date) | PR 5 | ~750–1,000 | Depends on PR 2 + PR 3. Independent of PR 6. |
| 6 | Web: Listas de Precio screen | PR 6 | ~250–400 | Depends on PR 4. Independent of PR 5. |

### Slice Cut Rationale (deviation from design.md's prose grouping)

Design's Technical Approach section describes the write-path order as
"articulos → precios → listas_precio ABM → margin suggestion + screens" (one
grouping for the last two). Two adjustments made here, both justified by the
design's own architecture decisions rather than by the prose summary:

- **Margin suggestion moved into Slice 2, not its own PR.** Design decision 8
  states `SugeridorDePrecio` is "called ... by `ServicioDeArticulos`" and
  needs no `precios` data — it is invoked from the artículo create/edit flow
  using `grupos.margen`/`proveedores.margen` (already-existing columns from
  stage 1/2). It has no technical dependency on Slice 3/4 at all; shipping it
  detached from `ServicioDeArticulos` would just add PR-hop overhead with no
  review-focus benefit.
- **Screens split into two independent PRs (5 and 6), not one.** The
  Artículos screen is explicitly "the heaviest screen to date" (identification
  + barcode manager + classification + costs + availability picker +
  per-lista price editor with history drawer); the Listas de Precio screen
  reuses the existing generic catalog descriptor pattern and is light by
  comparison. Bundling them risks exceeding the 400-line budget in a single
  PR for no cohesion benefit — they consume disjoint API surfaces.

Each slice: start = base branch state, finish = its own tests green,
verification = its own unit/integration/race tests, rollback = down-migration
(Slice 1 only) or new-routes-only removal (Slices 2–6), per proposal.md §
Rollback Plan (all-additive).

---

## Slice 1: Schema, Domain Foundation & Counter (PR 1)

**Start**: `main`. **Finish**: migration applied, RLS proven on all 5
tenant-scoped new/changed tables, tests green. **Rollback**: down-migration
(additive only — no destructive change to any existing table).

### 1A. DB CHANGE GATE — BLOCKING

- [x] 1.1 **APPROVED 2026-08-02**, exactly as presented (5 new tables,
  `unidad_venta` enum, standard RLS on all 5, the 4 additive alternate keys,
  the AK naming convention call, `Precio.Monto`, no seed/backfill). **STOP.**
  Present the migration model summary and wait for explicit
  approval before generating anything (CLAUDE.md gate). The summary MUST
  group:
  - **New enum**: `unidad_venta` (`unidad` | `peso`).
  - **New table `articulos`** (tenant-wide, `id_tenant`, **no** `id_empresa`
    — doc 10 §3 availability model, not the ownership pattern): full column
    set per design's Table Shapes (`codigo_interno` citext NOT NULL,
    `nombre`, clasificadores `id_area` NOT NULL/`id_categoria`/`id_marca`/
    `id_grupo` NULL, `id_proveedor_habitual` NULL, `id_alicuota_iva` NOT NULL,
    `unidad_venta`, `unidades_por_bulto` NULL, `es_producto`, `costo_lista`/
    `descuento_proveedor`/`costo_nominal` NULL, `disponible_para_todas`
    BOOLEAN NOT NULL DEFAULT true, `activo`); `ux_articulos_codigo_interno
    (id_tenant, codigo_interno) WHERE deleted_at IS NULL`; composite FKs
    `fk_articulos_{tenant,area,categoria,marca,grupo,proveedor_habitual}`
    (each incl. `id_tenant`) + simple FK `fk_articulos_alicuota_iva` (global
    catalog, no `id_tenant` on the target); `ix_articulos_tenant`.
  - **New table `articulos_empresas`** (junction, tenant-scoped): PK
    `(id_articulo, id_empresa)`; composite FKs to `articulos`/`empresas`
    incl. `id_tenant`; `ix_articulos_empresas_empresa (id_empresa,
    id_tenant)`.
  - **New table `codigos_barra`** (tenant-wide): `id_articulo NOT NULL`,
    `codigo citext NOT NULL`, `activo`; `ux_codigos_barra_codigo_tenant
    (codigo, id_tenant) WHERE deleted_at IS NULL`; composite FK
    `fk_codigos_barra_articulo`.
  - **New table `numeraciones_articulos`** (`id_tenant` PK, `proximo_numero`
    — same shape as `numeraciones_clientes`, no `EntidadBase`).
  - **New table `precios`** (catalog-scoped, `id_tenant`): `id_articulo`,
    `id_lista_precio`, `precio numeric(14,2)`, `vigente_desde timestamptz NOT
    NULL`, `vigente_hasta timestamptz NULL`; `ux_precios_vigente
    (id_articulo, id_lista_precio) WHERE vigente_hasta IS NULL AND
    deleted_at IS NULL`; composite FKs to `articulos`/`listas_precio`.
  - **⚠️ MODIFIED existing tables (highlighted, not additive-only at the
    column level — design decision 7):** `areas`, `marcas`, `grupos`,
    `proveedores` each gain `HasAlternateKey(Id, IdTenant)` (`ak_areas_id_
    id_tenant`, `ak_marcas_id_id_tenant`, `ak_grupos_id_id_tenant`,
    `ak_proveedores_id_id_tenant`, same naming convention as
    `ak_categorias_id_categoria_id_tenant`). Purely additive at the SQL level
    (`ALTER TABLE ... ADD CONSTRAINT UNIQUE`, no column change, no data
    migration) but changes the schema contract of four tables that predate
    this stage — closes the same cross-tenant composite-FK gap ADR-9/ADR-10
    already closed for `empresas`/`categorias`/`listas_precio`, now required
    because `articulos` needs to FK into all four.
  - **RLS**: `HabilitarRlsDeTenant` on all 5 new tenant-scoped tables
    (`articulos`, `articulos_empresas`, `codigos_barra`,
    `numeraciones_articulos`, `precios`) — same precedent as stage 2's 4
    tables.
  - **No backfill/seed**: confirmed additive-only; unlike stage 2's CF
    cliente/General lista seed, no pre-existing tenant needs a default
    artículo, precio, or counter row (the counter is lazily ensured per
    tenant on first artículo create, same pattern as
    `AsignadorDeNumeroCliente.AsegurarContadorAsync`).

### 1B. Domain

- [x] 1.2 [P] Add `Articulo : EntidadTenant` (full field set per the gate
  summary) in `Ways.Domain/Articulos`. *(spec: articulos / Articulo Schema At
  Rest)*
- [x] 1.3 [P] Add `CodigoBarra : EntidadTenant` in `Ways.Domain/Articulos`.
  *(spec: codigos-barra / Codigo De Barra Schema And Cardinality)*
- [x] 1.4 [P] Add `ArticuloEmpresa` junction entity (no soft-delete, PK-only
  row) in `Ways.Domain/Articulos`. *(spec: articulos / articulos_empresas
  Junction Schema)*
- [x] 1.5 [P] Add `Precio : EntidadTenant` (`IdArticulo`, `IdListaPrecio`,
  `Precio`, `VigenteDesde`, `VigenteHasta`) in `Ways.Domain/Precios`. *(spec:
  precios / Price History Never Overwrites)* — the money property is named
  `Monto`, not `Precio` (C# CS0542: a member can't share its containing
  type's name); documented on the property itself.
- [x] 1.6 [P] Add `NumeracionArticulo` entity (`id_tenant` PK,
  `proximo_numero`) + EF config, mirroring `NumeracionCliente`. *(design table
  shapes)*
- [x] 1.7 Add pure `ReglaDeArticulos.ValidarRestriccionDeDisponibilidad`
  (blocks `disponible_para_todas: true → false` without ≥1
  `articulos_empresas` row) + unit tests. *(spec: articulos / Availability
  Model, Scenario "Restricting availability requires at least one subset
  row")*

### 1C. Migration (only after 1.1 approved)

- [x] 1.8 Generate migration `ArticulosYPreciosEtapa3`: `unidad_venta` enum,
  5 new tables, 4 new alternate keys on existing tables, all FKs/indexes
  hand-named in snake_case (EF default naming would produce PascalCase `IX_*`,
  breaking the doc-10 convention — same fix stage 2 applied before
  generating), `HabilitarRlsDeTenant` on all 5 new tables, enum registration
  in `WaysDbContextFactory` + prod DI (per `modo_lista`/`tipo_documento`
  precedent from stage 2). *(design: Migration Sequencing)* — confirm
  `dotnet ef migrations has-pending-model-changes` is clean before committing.

### 1D. codigo_interno counter

- [x] 1.9 Add `AsignadorDeCodigoInternoArticulo.AsignarSiguienteAsync`/
  `AsegurarContadorAsync` (raw ADO.NET on the caller's transaction, same
  shape as `AsignadorDeNumeroCliente` — reuses the same
  `Database.OpenConnectionAsync` discipline so `InterceptorDeContextoDeTenant`
  still fires). Doc-comment records the "orchestrator decision 1" numeric
  format and the stage-5 forward-dependency on staying under 7 digits.
  *(design decision 6; spec: articulos / codigo_interno Mandatory And
  Autogenerated)*

### 1E. db-error-backstops mapping groundwork

- [x] 1.10 Extend `ManejadorDeErrores.ClasificarUnicidad` with **ordering
  care** — `Contains`-based matching means a naive append breaks:
  `ux_articulos_codigo_interno` and `ux_codigos_barra_codigo_tenant` BOTH
  contain the substring `"_codigo"` and would silently fall into the
  existing generic `_codigo` branch (`codigo_duplicado`, a generic message)
  unless a more specific check is inserted **before** it, same way `_cuit`/
  `_numero` are already checked before `_nombre`/`_codigo`. Add, in this
  order, ahead of the existing `_codigo` line:
  - `Contains("_codigo_interno")` → `codigo_interno_duplicado` ("Ya existe un
    artículo con ese código interno en este tenant.")
  - `Contains("codigos_barra")` → `codigo_barra_duplicado` ("Ya existe ese
    código de barras en este tenant.")
  Add a new independent check (no collision risk) for `Contains("_vigente")`
  → `precio_vigente_duplicado` ("Ya existe un precio vigente para este
  artículo en esta lista."). Confirm (no code change) that the existing
  generic `fk_` prefix branch already covers every new composite/simple FK
  (`fk_articulos_*`, `fk_codigos_barra_articulo`, `fk_articulos_empresas_*`,
  `fk_precios_*`) — document this confirmation in a code comment, same
  pattern as the existing family comments. *(design: Backstop Map)*

### 1F. Tests

- [x] 1.11 Integration: RLS proofs for all 5 new tables (EF filter blocks
  cross-tenant read; raw-SQL/`IgnoreQueryFilters` blocked), mirroring
  `AislamientoDeTenantTests`. *(spec: articulos/codigos-barra/precios /
  Tenant Isolation)* — `tests/Ways.IntegrationTests/ArticulosYPreciosRlsTests.cs`
  (SELECT/UPDATE cross-tenant → 0 rows via `USING`; INSERT with foreign
  `id_tenant` → 42501 via `WITH CHECK`; EF/LINQ filter proof for the 4
  ORM-reachable entities) + 2 dedicated `numeraciones_articulos` tests (its
  PK IS `id_tenant`, doesn't fit the parametrized table).
- [x] 1.12 [P] Unit: `ReglaDeArticulos.ValidarRestriccionDeDisponibilidad`
  (blocks toggle-to-false without subset row, allows with one). *(spec:
  articulos / Availability Model)* — `tests/Ways.Domain.Tests/Articulos/ReglaDeArticulosTests.cs`
  (5 cases: block, allow-with-subset, true→true, false→false, false→true).
- [x] 1.13 Integration: `AsignadorDeCodigoInternoArticulo` atomicity under
  concurrency (2 concurrent assigns for the same tenant → distinct values, no
  gap, no duplicate), mirroring `AsignadorDeNumeroClienteConcurrenciaTests`.
  *(design: Backstop Map — "numeraciones_articulos counter race")* —
  `tests/Ways.IntegrationTests/AsignadorDeCodigoInternoArticuloConcurrenciaTests.cs`,
  3 rounds × 2 concurrent assigns, stable across 5 runs (2 full-suite + 3
  isolated).
- [x] 1.14 Regression: existing Domain/Application/IntegrationTests suites
  unedited and green — 74/74 Domain (+5 new), 142/142 Application (+14 new
  migration-independent), 145/145 IntegrationTests (129 baseline + 16 new:
  RLS proofs + counter concurrency), all green twice in a row.

---

## Slice 2: Artículos + codigos_barra + articulos_empresas + Margin Suggestion (PR 2)

**Depends on**: Slice 1. **Start**: PR 1 merged/branch. **Finish**: artículo
CRUD, barcode add/remove, availability toggle, and margin suggestion live
through the API, `codigo_interno`/`codigo_barra` uniqueness races proven,
tests green. **Rollback**: new routes only.

### 2A. Domain

- [x] 2.1 [P] Add `SugeridorDePrecio.Sugerir(costoNominal, costoLista,
  descuentoProveedor, margenGrupo, margenProveedor)` — pure static function:
  base cost = `costoNominal` when present else `costoLista * (1 -
  descuentoProveedor)`; margin = `margenGrupo` when present else
  `margenProveedor`; no DB access. *(design decision 8; spec: precios /
  Margin-Based Price Suggestion — resolved decision 1)*

### 2B. Application

- [x] 2.2 Add `ServicioDeArticulos` (list/create/edit/soft-delete): create
  either accepts a caller-supplied `codigo_interno` (pre-checked for
  uniqueness, unique index as backstop) or calls
  `AsignadorDeCodigoInternoArticulo` when omitted; availability edit calls
  `ReglaDeArticulos.ValidarRestriccionDeDisponibilidad`; exposes a
  `SugerirPrecioAsync` (or equivalent) call wired to `SugeridorDePrecio` using
  the artículo's own cost/grupo/proveedor fields; `GestionDeCatalogo` policy.
  *(spec: articulos / Articulo ABM Lifecycle and Authorization,
  codigo_interno Mandatory And Autogenerated)*
- [x] 2.3 Add `ServicioDeCodigosBarra` (or fold into `ServicioDeArticulos`
  as add/remove methods — implementer's call, document whichever is chosen):
  add/remove barcodes independent of editing other artículo fields.
  *(spec: codigos-barra / Barcode Add/Remove Management)*
- [x] 2.4 Add contracts: `AltaArticulo`/`EdicionArticulo`/`ArticuloListado`,
  `AltaCodigoBarra`, `SugerenciaDePrecio` (response DTO).

### 2C. API

- [x] 2.5 Add `ArticulosEndpoints`: list/create/edit/soft-delete, barcode
  add/remove sub-routes, availability toggle, margin-suggestion read
  endpoint, `GestionDeCatalogo` policy (tenant admin only).

### 2D. Tests

- [x] 2.6 [P] Unit: `SugeridorDePrecio` — grupo wins over proveedor, falls
  back to proveedor when grupo margin is `NULL`/absent, `costo_nominal`
  precedence over `costo_lista * (1 - descuento)`. *(spec: precios /
  Margin-Based Price Suggestion, all 3 scenarios)*
- [x] 2.7 [P] Unit (InMemory, where the write path allows it — same
  transaction-blocked-provider caveat as `ServicioDeClientesTests`): required
  field validation, invalid clasificador/alicuota reference → 400,
  availability guard, cross-tenant 404.
- [x] 2.8 [P] Integration: `codigo_interno` — concurrent omitted-value
  creates yield distinct autogenerated values (no 23505 surfaced); duplicate
  user-supplied value → 409 via `codigo_interno_duplicado`; race test with 2
  concurrent duplicate-user-supplied creates → exactly 1×201 + 1×409, SQLSTATE-
  asserted. *(spec: articulos / codigo_interno Mandatory And Autogenerated,
  all 4 scenarios; db-error-backstops)*
- [x] 2.9 [P] Integration: `codigos_barra` — cross-tenant same-barcode
  allowed; same-tenant duplicate → 409; concurrent duplicate-add race → 1×201
  + 1×409, SQLSTATE-asserted. *(spec: codigos-barra / Barcode Uniqueness Per
  Tenant, all 3 scenarios; db-error-backstops)*
- [x] 2.10 [P] Integration: availability — default-true visible to a later
  empresa; explicit-false subset excludes other empresas; cross-tenant
  empresa reference in `articulos_empresas` → 400 `referencia_invalida` via
  tenant-scoped pre-check (not `IgnoreQueryFilters`). *(spec: articulos /
  Availability Model, articulos_empresas Junction Schema)*
- [x] 2.11 [P] Integration: FK smoke tests for each new `fk_articulos_*` and
  `fk_codigos_barra_articulo` (cross-tenant/nonexistent id → 23503/400).
  *(backstop map)*
- [x] 2.12 Integration: admin create→soft-delete round trip; vendedor 403 on
  create, barcode add/remove, and availability toggle. *(spec: articulos /
  Articulo ABM Lifecycle and Authorization; codigos-barra / Barcode
  Add/Remove Management)*
- [x] 2.13 Regression: Slice 1 suites unedited and green.

---

## Slice 3: Precios (PR 3)

**Depends on**: Slice 1 + Slice 2. **Start**: PR 2 merged/branch. **Finish**:
price history engine live (close-and-open, programmable future, point-in-time
query, derivada resolution), race-proven, tests green. **Rollback**: new
routes only.

### 3A. Domain

- [x] 3.1 [P] Add `ResolverPrecioDerivado(precioBase, porcentaje)` pure
  function in `Ways.Domain.Precios`: `Math.Round(precioBase * (1 +
  porcentaje / 100m), 2, MidpointRounding.AwayFromZero)`. *(design: Price
  Resolution & Rounding; spec: precios / Derived List Price Resolution At
  Read Time)*

### 3B. Application

- [x] 3.2 Add `ServicioDePrecios.AbrirNuevoPrecioAsync(idArticulo,
  idListaPrecio, precio, vigenteDesde, confirmarReemplazo)`: single
  transaction — `SELECT ... FOR UPDATE` the currently open row (if any),
  close it (`vigente_hasta = vigenteDesde` of the new row, or `now()` for an
  immediate change), insert the new open row. Throws `precio_pendiente_existe`
  (409) when the currently open row's `vigente_desde > ahora` (a pending
  future price) and `confirmarReemplazo` is not `true`. *(design decisions 3,
  4; Protection Rules; spec: precios / Price History Never Overwrites,
  Programmable Future Prices At Most One Pending)*
- [x] 3.3 Add `ServicioDePrecios.PrecioVigenteAsync(idArticulo,
  idListaPrecio, fecha)`: `Modo == Fija` → date-filtered `precios` query
  (`vigente_desde <= fecha AND (vigente_hasta IS NULL OR vigente_hasta >
  fecha)`); `Modo == Derivada` → resolve `id_lista_base` (reject if the base
  is itself `Derivada` — orchestrator decision 2, service-only depth-1 guard)
  then call `ResolverPrecioDerivado`. *(design: Price Resolution & Rounding,
  Table Shapes; spec: precios / Current-Price Query Semantics By Date,
  Derived List Price Resolution At Read Time)*
- [x] 3.4 Add contracts: `AltaPrecio`/`ProgramarPrecio`/`PrecioVigente`/
  `HistorialDePrecio`.

### 3C. API

- [x] 3.5 Add precio endpoints nested under `/api/articulos/{id}/precios`
  (folded into `ArticulosEndpoints`, not a standalone top-level resource —
  proposal's Affected Areas note), `GestionDeCatalogo` policy: set/schedule
  price, current-price read, history read.

### 3D. Tests

- [x] 3.6 [P] Unit: `ResolverPrecioDerivado` rounding (AwayFromZero on a tie),
  positive and negative `porcentaje`. *(spec: precios / Derived List Price
  Resolution At Read Time)*
- [x] 3.7 [P] Integration: close-and-open transaction — changing a price
  closes the old row's `vigente_hasta` and opens a new one; historical rows
  remain queryable. *(spec: precios / Price History Never Overwrites, both
  scenarios)*
- [x] 3.8 [P] Integration: pending-future — schedule with none pending
  succeeds; scheduling again without `confirmarReemplazo` → 409
  `precio_pendiente_existe`; with `confirmarReemplazo: true` → replaces.
  *(spec: precios / Programmable Future Prices, At Most One Pending, both
  scenarios)*
- [x] 3.9 [P] Integration: point-in-time query — present date returns active
  row, past date resolves historical row. *(spec: precios / Current-Price
  Query Semantics By Date, both scenarios)*
- [x] 3.10 [P] Integration: derivada resolution — resolved price follows the
  base automatically; base change propagates without a derivada write; no
  `precios` row ever persisted for a derivada lista. *(spec: precios /
  Derived List Price Resolution At Read Time, both scenarios)*
- [x] 3.11 Integration: `ux_precios_vigente` race — 2 concurrent first-price
  creates for the same `(articulo, lista)` (no row to lock yet) → exactly
  1×201 + 1×409 via `precio_vigente_duplicado`, SQLSTATE-asserted.
  *(db-error-backstops; design: Backstop Map)*
- [x] 3.12 [P] Integration: FK smoke tests for `fk_precios_*` (cross-
  tenant/nonexistent id → 23503/400). *(backstop map)*
- [x] 3.13 Integration: history immutability — no code path exposes
  `Precio.Precio` as settable (assert via reflection/public-API surface, not
  a DB trigger — documented exemption, design: Testing Strategy).
- [x] 3.14 Regression: Slice 1 + Slice 2 suites unedited and green.

---

## Slice 4: Listas de Precio ABM (PR 4)

**Depends on**: Slice 1 + Slice 3. **Start**: PR 3 merged/branch. **Finish**:
`listas_precio` create/edit live for both modes, mode-switch and
deactivation guards enforced, tests green. **Rollback**: new routes only —
`listas_precio` table itself is untouched (columns already existed since
stage 2).

### 4A. Application

- [x] 4.1 Add `ServicioDeListasPrecio` (create/edit/soft-delete):
  `derivada` create/edit requires `id_lista_base NOT NULL` +
  `porcentaje NOT NULL` (service-level validation before the DB); rejects a
  `derivada` lista whose `id_lista_base` is itself `Derivada`
  (orchestrator decision 2); blocks `modo` switch once
  `db.Precios.AnyAsync(p => p.IdListaPrecio == id)` is true; blocks
  deactivation while any active `derivada` lista points at it as
  `id_lista_base`, mirroring `ReglaDeClientes.ValidarNoConsumidorFinal`'s
  shape; `GestionDeCatalogo` policy. *(spec: listas-precio-minimal /
  Derivada Mode Resolution And Validation, Blocked Mode Switch Once History
  Exists, Blocked Deactivation While Referenced As Base, Lista ABM Lifecycle
  and Authorization; design: Protection Rules)* — **deviation from the
  task's plan, documented in code** (`Contratos.cs` doc-comment on
  `ListaPrecioAlta`): reused a SINGLE contract for create+edit (ADR-11
  convention, same as `CategoriaAlta`), not the split
  `AltaListaPrecio`/`EdicionListaPrecio` named below, because
  `ServicioDeListasPrecio` extends `ServicioDeCatalogo<T,TListado,TAlta>`
  (design decision 2's "on top" wording, same escape-hatch shape as
  `ServicioDeCategorias`) and that base binds one `TAlta` to both
  `CrearAsync`/`ActualizarAsync`. Also implements, confirmed against
  spec/design/state.yaml before building: `porcentaje` bounds (`> -100` per
  the Slice 3 forward obligation, `< 1000` per the `numeric(5,2)` column);
  `EsDefault` SWAP semantics (assigning `true` atomically unsets whichever
  lista held it in the same `IdEmpresa` scope — two sequential
  `SaveChangesAsync` inside one transaction, old row first, so the unique
  partial index never sees two `true` rows in the same snapshot); a lista
  holding `EsDefault: true` cannot be edited to `false` without a
  replacement in the same request (409 `lista_default_requiere_reemplazo`),
  cannot be saved `EsDefault: true` + `Activo: false` (400
  `lista_default_debe_estar_activa`), and cannot be soft-deleted while
  default (409 `lista_default_no_se_puede_eliminar` — decided because the
  spec is silent on protecting the default row but the unmodified stage-2
  requirement "One Default List Per Tenant" would otherwise break; not
  hardcoded to "General", any lista currently `EsDefault: true` is
  protected, mirroring `ReglaDeClientes.ValidarNoConsumidorFinal`'s shape
  applied to a predicate instead of a fixed id).
- [x] 4.2 Add contracts: `ListaPrecioAlta`/`ListaPrecioListado` (naming
  deviation from the originally planned `AltaListaPrecio`/`EdicionListaPrecio`
  — see 4.1).

### 4B. API

- [x] 4.3 Routes added via the existing generic `MapearCatalogo<T, TListado,
  TAlta, TServicio>` helper (`CatalogosEndpoints.cs`:
  `app.MapearCatalogo<ListaPrecio, ListaPrecioListado, ListaPrecioAlta,
  ServicioDeListasPrecio>("listas-precio")`) instead of a dedicated
  `ListasPrecioEndpoints` file — same precedent as `categorias`, `Politicas.
  GestionDeCatalogo` inherited from the shared mapper. Routes live under
  `/api/catalogos/listas-precio*`. The stage 2 `GET /api/listas-precio`
  read-only reference listing (different path prefix, `ClientesEndpoints`)
  stays as-is, no collision — verified with a regression test (task 4.8).

### 4C. Tests

- [x] 4.4 [P] Unit: `derivada` create without `id_lista_base`/`porcentaje` →
  rejected before DB; depth-1 guard rejects a `derivada`-based-on-`derivada`.
  *(spec: Derivada Mode Resolution And Validation)* —
  `tests/Ways.Application.Tests/Catalogos/ServicioDeListasPrecioTests.cs`
  (18 cases on the InMemory provider, incl. the deviation-driven extras:
  porcentaje bounds, es_default consistency/swap-rejection, mode-switch/
  deactivation guards, protected-default delete guard). The `EsDefault:
  true` swap path (opens a real DB transaction) is NOT covered here — same
  "transaction-blocked-provider caveat" as `ServicioDeArticulosTests`/
  `ServicioDeClientesTests`; covered end-to-end against real Postgres below.
- [x] 4.5 [P] Integration: `id_lista_base` referencing a non-existent lista →
  400 `referencia_invalida` (via the service's tenant-scoped pre-check, same
  observable behavior the spec's 23503 scenario describes — the raw FK
  backstop is the existing generic `fk_` mapping, no dedicated race test
  needed per the Backstop Map); admin creates a `fija` and a `derivada`
  lista; vendedor 403 on create/edit. *(spec: listas-precio-minimal, all
  scenarios under Derivada Mode Resolution And Validation, Lista ABM
  Lifecycle and Authorization)*
- [x] 4.6 [P] Integration: mode-switch blocked once a `precios` row exists,
  allowed before any exists. *(spec: Blocked Mode Switch Once History
  Exists, both scenarios)*
- [x] 4.7 [P] Integration: deactivation blocked while an active `derivada`
  lista depends on it as base, allowed once no dependent remains. *(spec:
  Blocked Deactivation While Referenced As Base, both scenarios)*
- [x] 4.8 Regression: Slice 1 + Slice 3 suites unedited and green — plus the
  `es_default` swap (non-concurrent + genuine two-lista race, db-error-
  backstops: `ux_listas_precio_default_compartido`'s race-test exemption
  closes here, 1×200 + 1×409 `default_duplicado`, stable across 1 full run +
  3 isolated reruns), the protected-default-row guards (edit/deactivate/
  delete), and ADR-8 cross-tenant 404 uniformity (GET/PUT/DELETE) — all in
  `tests/Ways.IntegrationTests/ListasPrecioEndpointsTests.cs` (13 cases).
  Final suite: 86/188/209 (Domain/Application/IntegrationTests), stable
  across 2 full runs.

---

## Slice 5: Web — Artículos Screen (PR 5)

**Depends on**: Slice 2 + Slice 3. **Start**: PR 3 branch per chosen chain
strategy (independent of PR 4). **Finish**: dedicated Artículos ABM
functional against the API, smoke-verified. **Rollback**: new route only.

### 5A. Screen

- [ ] 5.1 Add dedicated `Articulos.tsx` ABM (not the generic catalog
  machine, per design decision 1): identification + inline barcode manager
  (add/remove) + classification (4 selectors) + costs + availability picker
  (toggle + empresa multiselect, shown only when `false`) + per-lista price
  editor (current price, pending-future badge, apply-margin-suggestion
  button, history drawer). *(design: ABM Composition; spec: articulos /
  Articulo ABM Lifecycle, codigos-barra / Barcode Add/Remove Management,
  precios / all requirements)*

### 5B. Wiring + smoke

- [ ] 5.2 Wire `/articulos` route + nav entry; add `articulos.ts`/`precios.ts`
  API clients and `tipos.ts` additions.
- [ ] 5.3 Smoke-verify (`tsc -b`/`oxlint`/`vite build` clean); relies on
  Slice 2/3's integration coverage proving the exact contract shapes the
  screen consumes, same criterion as stage 2's 4.4a/4.4b.

---

## Slice 6: Web — Listas de Precio Screen (PR 6)

**Depends on**: Slice 4. **Start**: PR 4 branch (independent of PR 5).
**Finish**: Listas de Precio ABM functional against the API, smoke-verified.
**Rollback**: new route only.

### 6A. Screen

- [ ] 6.1 Extend `Ways.Web/api/catalogos.ts`'s generic descriptor pattern
  with two extra fields (`modo`, conditionally `id_lista_base`/`porcentaje`)
  — the list shape still fits the generic table/form, unlike `articulos`.
  *(design: ABM Composition)*

### 6B. Wiring + smoke

- [ ] 6.2 Wire `/listas-precio` route + nav entry; add `listasPrecio.ts` API
  client additions.
- [ ] 6.3 Smoke-verify (`tsc -b`/`oxlint`/`vite build` clean); relies on
  Slice 4's integration coverage proving the exact contract shapes the screen
  consumes.

---

## Dependency Summary

```
Slice 1 (schema + domain + counter)
   └─▶ Slice 2 (articulos + codigos_barra + articulos_empresas + margin suggestion)
            └─▶ Slice 3 (precios)
                     └─▶ Slice 4 (listas de precio ABM)
                              Slice 2, Slice 3 ─▶ Slice 5 (web: articulos screen)
                              Slice 4 ─▶ Slice 6 (web: listas de precio screen)
                                       Slice 5, Slice 6 are independent — parallelizable
```

Within each slice, `[P]`-tagged tasks are parallelizable; all others are
sequential (schema → infra → application → tests; the DB CHANGE GATE always
blocks the migration-generation task). Slices 5 and 6 are the only two
slices that can run as parallel PR branches against different parents (PR 3
and PR 4 respectively) — every other slice is a hard sequential dependency
per design's write-path ordering, reaffirmed above with one deviation
(margin suggestion folded into Slice 2, see "Slice Cut Rationale").
