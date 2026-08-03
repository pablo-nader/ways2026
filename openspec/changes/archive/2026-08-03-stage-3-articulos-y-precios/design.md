# Design: Stage 3 — Artículos y Precios

## Technical Approach

Reuse, not redesign (proposal §Approach). `articulos` and `precios` get dedicated
entities/services (like `Cliente`/`Proveedor`, not `ServicioDeCatalogo<T>` — too many
fields, availability junction, history semantics don't fit the generic shape).
`listas_precio` keeps `CatalogoSimple` (already fits) and gains a `ServicioDeListasPrecio`
+ endpoints + ABM. Four write paths ship as four chained PRs, in dependency order:
`articulos` (+ `codigos_barra`, `articulos_empresas`) → `precios` → `listas_precio` ABM →
margin suggestion + screens.

**Key insight closing the open design question (proposal risk #2):** the "at most ONE
pending future price" rule needs **no extra constraint**. The existing doc 10 index
`UNIQUE (id_articulo, id_lista_precio) WHERE vigente_hasta IS NULL AND deleted_at IS NULL`
already caps open rows at one — because every price write (immediate or scheduled) is the
**same** close-and-open transaction: close whatever row is currently open (`vigente_hasta =
nuevo_vigente_desde`), insert one new open row. There is never a second open row to collide
with, so `now()` never appears in an index predicate. "Current price" is a **read-time**
filter, not a write-time state: `WHERE vigente_desde <= :fecha AND (vigente_hasta IS NULL OR
vigente_hasta > :fecha)`. A future-dated row becomes current by date arriving, not by a job
running — confirms proposal's "programmable, no cron" claim.

## Architecture Decisions

| # | Decision | Alternatives considered | Rationale |
|---|---|---|---|
| 1 | `articulos`/`codigos_barra`/`articulos_empresas` are dedicated entities + `ServicioDeArticulos`, not `ServicioDeCatalogo<T>` | Extend the generic catalog machine | 14+ fields, availability junction, barcode collection — none of `ConfiguracionDeCatalogo<T>`'s shape (name-scoped dedupe, `id_empresa` ownership) applies; forcing it would need more overrides than the base saves (same call `Cliente`/`Proveedor` already made) |
| 2 | `listas_precio` keeps `CatalogoSimple`; adds `ServicioDeListasPrecio` on top | New dedicated entity | Shape (name + flags, name-scoped dedupe) still fits — only *behavior* is new (derivada resolution, ABM), not a structural mismatch |
| 3 | `precios` never has an entity-level `Update`; only `AbrirNuevoPrecioAsync(articulo, lista, precio, vigenteDesde, confirmarReemplazo)` closes-and-opens in one transaction | Mutable `Precio.Precio` setter | History-is-append-only is a hard product rule (doc 10 principle 6 analog); a settable property invites an accidental UPDATE that erases audit trail |
| 4 | One-pending-future enforced by the existing single-open-row unique index + `SELECT ... FOR UPDATE` row lock on that open row before closing it | A `WHERE vigente_desde > now()` partial index | `now()` is not `IMMUTABLE` — Postgres rejects it in an index predicate; the transactional discipline achieves the same guarantee without one |
| 5 | Derived-list price resolution is a pure function in `Ways.Domain.Precios` (`ResolverPrecioDerivado(precioBase, porcentaje)`), called from the same service that resolves fija prices | Materialize derived rows nightly | Proposal already rejects stored rows; keeping resolution pure keeps it unit-testable without DB, same testing bar as `PoliticaDeRoles` |
| 6 | `codigo_interno` autogeneration reuses the `numeraciones_clientes` pattern: new `numeraciones_articulos(id_tenant PK, proximo_numero)` + static `AsignadorDeCodigoInternoArticulo`, not a shared multi-`tipo` counter table | One `numeraciones` table with a `tipo` discriminator | `numeraciones_clientes` already set the one-table-per-counter-family precedent (doc 09's `numeraciones_comprobante` is genuinely different — keyed by `punto_venta`+`tipo`, not by tenant alone); a `tipo` column would be the first mixed-purpose counter table in the codebase, against principle 4 ("los padrones son datos, no enums" — same smell for counters) |
| 7 | Composite FK targets missing an `(Id, IdTenant)` alternate key — `areas`, `marcas`, `grupos`, `proveedores` — get one added in this migration | Simple (non-composite) FK from `articulos` to those tables | `articulos` is tenant-wide and cross-tenant FK bypass (RLS-only protection) is exactly the gap ADR-9/ADR-10 closed for `empresas`/`categorias`/`listas_precio`; leaving these four as simple FKs would reopen it for the biggest write surface yet |
| 8 | Margin suggestion (`SugeridorDePrecio.Sugerir(costoNominal, costoLista, descuentoProveedor, margenGrupo, margenProveedor)`) is a pure static function in `Ways.Domain.Precios`, called but never auto-applied by `ServicioDeArticulos` | Compute in the API/Web layer | Same testability bar as decision 5; grupo-over-proveedor precedence (resolved decision 1) is business logic, belongs in Domain |

## Table Shapes (DB CHANGE GATE — grouped for one combined migration)

| Table | Scope | Key columns | Constraints |
|---|---|---|---|
| `articulos` | tenant-wide (`id_tenant`, **no** `id_empresa`) | `codigo_interno` citext NOT NULL, `nombre`, clasificadores, `id_alicuota_iva` NOT NULL, `unidad_venta` (new enum), costs, `disponible_para_todas` bool default true | `ux_articulos_codigo_interno (id_tenant, codigo_interno) WHERE deleted_at IS NULL`; `fk_articulos_{tenant,area,categoria,marca,grupo,proveedor_habitual,alicuota_iva}`; `ix_articulos_tenant` |
| `articulos_empresas` | junction, tenant-scoped | `id_articulo, id_empresa, id_tenant` | PK `(id_articulo, id_empresa)`; composite FKs to `articulos`/`empresas` incl. `id_tenant`; rows exist only when `disponible_para_todas = false` (service-enforced, see Protection Rules) |
| `codigos_barra` | tenant-wide | `id_articulo, codigo citext, activo` | `ux_codigos_barra_codigo_tenant (codigo, id_tenant) WHERE deleted_at IS NULL`; `fk_codigos_barra_articulo` composite |
| `numeraciones_articulos` | tenant | `id_tenant` PK, `proximo_numero` | same shape as `numeraciones_clientes` — no `EntidadBase`, `AsignadorDeCodigoInternoArticulo` is the only writer (raw SQL, decision 6) |
| `precios` | catalog (`id_tenant`) | `id_articulo, id_lista_precio, precio numeric(14,2), vigente_desde timestamptz NOT NULL, vigente_hasta timestamptz NULL` | `ux_precios_vigente (id_articulo, id_lista_precio) WHERE vigente_hasta IS NULL AND deleted_at IS NULL`; composite FKs to `articulos`/`listas_precio` |

**Availability resolution** (query extension, `Ways.Application.Articulos` or an
`IQueryable<Articulo>` extension method `DisponibleEnEmpresa(idEmpresa)`):
`WHERE a.DisponibleParaTodas || EF.Functions... EXISTS(articulos_empresas WHERE
id_articulo = a.Id AND id_empresa = idEmpresa)`. Index: `ix_articulos_empresas_empresa
(id_empresa, id_tenant)` supports the `EXISTS` side; the `disponible_para_todas` side is a
plain boolean column scan already covered by `ix_articulos_tenant`. One reusable method,
called from both the ABM listing and (future stage) the POS catalog query.

## Price Resolution & Rounding

`ServicioDePrecios.PrecioVigenteAsync(idArticulo, idListaPrecio, fecha)`:
1. If `listas_precio.Modo == Fija`: query `precios` with the date filter above → `decimal`.
2. If `Modo == Derivada`: resolve `id_lista_base` recursively (one level only — service
   rejects a derived list based on another derived list, protection rule below), then
   `ResolverPrecioDerivado(precioBase, porcentaje) = Math.Round(precioBase * (1 +
   porcentaje / 100m), 2, MidpointRounding.AwayFromZero)`. AwayFromZero (not banker's
   rounding): matches point-of-sale cash-rounding expectations, avoids the "ties always
   round to the even cent" surprise for a decimal a cashier reads off a screen.

## Protection Rules (service-level; DB-level noted as future hardening)

| Rule | Enforcement today | DB-level option (not built this stage) |
|---|---|---|
| `modo` switch blocked once `precios` history exists | `ServicioDeListasPrecio` checks `db.Precios.AnyAsync(p => p.IdListaPrecio == id)` before allowing `Modo` edit | Trigger, or a generated `tiene_historial` flag |
| Deactivating a lista referenced as `id_lista_base` while active derived lists point at it | Same service, mirrors `ReglaDeClientes.ValidarNoConsumidorFinal` shape | none — always transactional, low volume |
| `disponible_para_todas: true → false` requires ≥1 `articulos_empresas` row first | `ServicioDeArticulos` validates before flip | none |
| Pending-future replacement needs confirmation | `ServicioDePrecios` throws `precio_pendiente_existe` (409) unless `confirmarReemplazo: true`, only when the current open row's `vigente_desde > ahora` | n/a — UX gate, not data integrity |

## ABM Composition

`articulos` screen (dedicated, heaviest to date): identification + barcode manager
(`codigos_barra` CRUD inline) + classification (4 selectors) + costs + availability picker
(toggle + empresa multiselect, shown only when `false`) + per-lista price editor (current
price, pending-future badge, history drawer). `listas_precio` ABM: extends the existing
generic catalog descriptor pattern (`Ways.Web/api/catalogos.ts`) with two extra fields
(`modo`, conditionally `id_lista_base`/`porcentaje`) — the *list* shape still fits the
generic table/form; only `articulos` doesn't.

## Migration Sequencing

One combined migration (same precedent as stage 1/2): `ArticulosYPrecios` — adds
`unidad_venta` enum, `articulos`, `articulos_empresas`, `codigos_barra`,
`numeraciones_articulos`, `precios`, the four missing alternate keys (decision 7), and
calls `HabilitarRlsDeTenant` for every new tenant-scoped table in the same migration
(ADR-15 precedent). Single DB CHANGE GATE, presented as one grouped summary per proposal
§Approach point 2 — all five tables are interdependent (FKs), splitting the gate would not
reduce review risk.

## Backstop Map (db-error-backstops)

| Constraint | 23505/23503 mapping | Race test |
|---|---|---|
| `ux_articulos_codigo_interno` | extend `ClasificarUnicidad` (`_codigo` family reused, or new `codigo_interno_duplicado`) | two concurrent creates, one client-supplied duplicate `codigo_interno` |
| `ux_codigos_barra_codigo_tenant` | new `codigo_barra_duplicado` | two concurrent barcode adds, same code |
| `ux_precios_vigente` | new `precio_vigente_duplicado` | two concurrent first-price creates (no row to lock yet) |
| `fk_articulos_*`, `fk_codigos_barra_articulo`, `fk_articulos_empresas_*`, `fk_precios_*` | generic `fk_` prefix mapping (already in place) | none new — existing generic backstop covers it |
| `numeraciones_articulos` counter race | n/a (no unique to violate — row lock serializes) | atomicity test, same shape as `AsignadorDeNumeroClienteConcurrenciaTests` |

## Testing Strategy

| Layer | What | Approach |
|---|---|---|
| Unit (Domain) | Price resolution (fija + derivada + rounding), margin suggestion precedence, availability predicate | Pure, no DB — mirrors `PoliticaDeRoles`/`ReglaDeClientes` |
| Integration | RLS raw-SQL proofs per new table, availability `EXISTS` query, close-and-open transaction, pending-future confirm gate | Real Postgres, `Ways.IntegrationTests`, SQLSTATE-asserted per skill |
| Race | Concurrent `codigo_interno` autogen, concurrent duplicate `codigo_barra`, concurrent first-`precio` insert | `Task.WhenAll`, assert exactly one 201 + one 409 |
| History immutability | No code path exposes `Precio.Precio` as settable; a raw UPDATE attempt is not blocked by a CHECK (documented exemption — no trigger this stage) | Assert via reflection/API surface, not DB trigger |

## Open Questions

- [ ] Barcode-only articles without a manual `codigo_interno`: autogen format (padding,
  prefix) not yet fixed by product — `sdd-tasks` needs a concrete pattern before task 1.
- [ ] Whether `ServicioDeListasPrecio`'s derived-list depth-1 restriction needs a DB CHECK
  or stays service-only, same tradeoff as the other protection rules above.
