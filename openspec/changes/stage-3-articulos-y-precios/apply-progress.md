# Apply Progress: Stage 3 — Artículos y Precios

## Slice 1: Schema, Domain Foundation & Counter (PR 1) — IN PROGRESS, gate pending

**Branch**: `feat/stage3-slice1-schema` (off `main`, no push/PR yet).

### Status summary

| Bucket | Status |
|---|---|
| 1A — DB CHANGE GATE (1.1) | Presented, **awaiting explicit approval** |
| 1B — Domain (1.2–1.7) | Done |
| 1C — Migration (1.8) | Blocked on gate approval |
| 1D — codigo_interno counter (1.9) | Done |
| 1E — db-error-backstops mapping (1.10) | Done |
| 1F — Tests (1.11–1.14) | 1.12 done, 1.14 done (regression green), 1.11/1.13 blocked on 1.8 |

### What shipped this batch

**Domain** (`Ways.Domain`):
- `Articulos/Articulo.cs` — `Articulo : EntidadTenant`, full field set (tenant-wide, no
  `id_empresa`).
- `Articulos/CodigoBarra.cs` — `CodigoBarra : EntidadTenant`.
- `Articulos/ArticuloEmpresa.cs` — junction, PK-only, no `EntidadBase`/`EntidadTenant` (no
  soft-delete).
- `Articulos/NumeracionArticulo.cs` — counter entity, mirrors `NumeracionCliente`.
- `Articulos/UnidadVenta.cs` — enum (`Unidad`, `Peso`).
- `Articulos/ReglaDeArticulos.cs` — pure rule `ValidarRestriccionDeDisponibilidad`.
- `Precios/Precio.cs` — `Precio : EntidadTenant`. **Deviation**: the money column is exposed
  as `Monto`, not `Precio` — C# forbids a member sharing its containing type's name (CS0542).
  Documented on the property; every other identifier matches the design/spec vocabulary.

**Infrastructure** (`Ways.Infrastructure.Persistencia.Configuraciones`):
- `ArticuloConfiguration`, `CodigoBarraConfiguration`, `ArticuloEmpresaConfiguration`,
  `NumeracionArticuloConfiguration`, `PrecioConfiguration` — new.
- `AreaConfiguration`, `MarcaConfiguration`, `GrupoConfiguration`, `ProveedorConfiguration` —
  each gained `HasAlternateKey(Id, IdTenant)` (design decision 7). Names follow the
  established full-column convention (`ak_{tabla}_{columna_id}_id_tenant`, same as
  `ak_categorias_id_categoria_id_tenant`/`ak_listas_precio_id_lista_precio_id_tenant`/
  `ak_empresas_id_empresa_id_tenant`) — **not** the literal `ak_areas_id_id_tenant` string in
  tasks.md, which reads as a markdown line-wrap artifact inconsistent with the convention it
  cites as authoritative. Used: `ak_areas_id_area_id_tenant`, `ak_marcas_id_marca_id_tenant`,
  `ak_grupos_id_grupo_id_tenant`, `ak_proveedores_id_proveedor_id_tenant`.
- `Articulo` itself also gained its own alternate key, `ak_articulos_id_articulo_id_tenant`
  (needed for `codigos_barra`/`articulos_empresas`/`precios`'s composite FKs into it) — not
  called out explicitly in design.md/tasks.md (which only calls out the 4 *existing*-table
  additions) but structurally required, same silent precedent as `empresas`/`categorias`/
  `listas_precio`'s own alternate keys.
- `WaysDbContext`: 5 new `DbSet<T>` (concrete class only, not `IWaysDbContext` yet — no
  Application consumer in this batch, same precedent as stage-1's tenant catalogs); manual
  tenant query filters for `NumeracionArticulo` and `ArticuloEmpresa` (neither inherits
  `EntidadTenant`, so the generic loop doesn't reach them); write-guard
  `RechazarEscriturasDeNumeracionArticulo` (mirrors the cliente one).

**Application** (`Ways.Application.Articulos`):
- `AsignadorDeCodigoInternoArticulo.AsegurarContadorAsync`/`AsignarSiguienteAsync` — raw
  ADO.NET on the caller's transaction, mirrors `AsignadorDeNumeroCliente` exactly. Doc comment
  records orchestrator decision 1 (plain numeric, unpadded, `int`→`string` at the service
  layer) and the stage-5 forward dependency (<7 digits, documented not enforced).

**API** (`Ways.Api.Seguridad.ManejadorDeErrores`):
- `ClasificarUnicidad` ordering fix: `_codigo_interno` and `codigos_barra` branches inserted
  **before** the generic `_codigo` branch (both would otherwise silently fall into
  `codigo_duplicado`). New independent `_vigente` → `precio_vigente_duplicado` branch (no
  collision risk). Comment added at the `fk_` prefix branch confirming (no code change) it
  already covers all 8 new FK names this slice introduces.

**Tests** (migration-independent only, per the gate):
- `tests/Ways.Domain.Tests/Articulos/ReglaDeArticulosTests.cs` — 5 cases (task 1.12).
- `tests/Ways.Application.Tests/Persistencia/ModeloDeArticulosYPreciosTests.cs` — schema-shape
  assertions against the Npgsql-configured (but unconnected) model: unique indexes + filters,
  FK sets, the new alternate keys — mirrors `ModeloDeClientesYProveedoresTests`.
- `tests/Ways.Application.Tests/Persistencia/GuardDeNumeracionArticuloTests.cs` — mirrors
  `GuardDeNumeracionClienteTests` (InMemory, no DB).
- `tests/Ways.Application.Tests/Persistencia/FiltroDeTenantEnArticuloEmpresaTests.cs` — proves
  the manual tenant filter on `ArticuloEmpresa` (InMemory, no DB).
- 1.11 (RLS integration proofs) and 1.13 (counter concurrency integration) are **not**
  implemented yet — both require the physical tables from the still-pending migration (1.8),
  which is blocked on gate approval. They're the very next batch after approval + migration.

### Regression-hunting note (real bug found and fixed, not scope creep)

Adding `HasAlternateKey(Id, IdTenant)` to `Proveedor` (and, transitively, wiring
`Articulo`'s composite FK to it) reproducibly broke 9 existing `ServicioDeProveedoresTests`
against the EF Core **InMemory** provider only (never reproduces against Npgsql/Postgres):
`System.InvalidOperationException: The value of 'Proveedor.IdTenant' is unknown... property is
also part of a foreign key for which the principal entity in the relationship is not known.`

Root cause: `WaysDbContext.EstamparTenant()` stamped `IdTenant` on a newly `Added` entity via
a direct CLR property assignment (`entrada.Entity.IdTenant = ...`) *after* the entity was
already tracked with `IdTenant == 0`. For an entity whose `IdTenant` participates in **both**
a store-generated-adjacent alternate key (`Id` is identity, `(Id, IdTenant)` is the alternate
key) **and** a same-column composite FK to another table (`fk_proveedores_empresa`, sharing
`IdTenant`), the InMemory provider's change-tracker doesn't reliably pick up that later raw
mutation — `ListaPrecio` (which has the exact same key/FK shape) never hit this because its
only InMemory-tested seed path sets `IdTenant` in the object initializer under **Plataforma**
mode, never through the tenant-mode post-hoc stamping path.

**Fix**: `EstamparTenant()` now sets the value through the tracked-property API
(`entrada.Property(e => e.IdTenant).CurrentValue = ...`) instead of the raw CLR setter — same
end value, but it properly clears EF's internal "temporary key" state. This is a
provider-compatibility fix with no behavior change against Npgsql; verified via the full
Domain/Application/IntegrationTests suites (all green, see below). Flagged here because it's
exactly the kind of latent trap that `Articulo` (Slice 2, same alternate-key + tenant-mode-
created shape) would have hit again if left unfixed.

### Build/test results (this batch)

- `dotnet build Ways.slnx` — clean, 0 warnings, 0 errors.
- `dotnet test Ways.Domain.Tests` — **74/74** (baseline 69 + 5 new).
- `dotnet test Ways.Application.Tests` — **142/142** (baseline 128 + 14 new).
- `dotnet test Ways.IntegrationTests` (real Postgres via Testcontainers, Docker up) —
  **129/129**, unchanged from baseline (no new integration tests yet — correctly gated on
  1.8/migration).

### Commits (work-unit style, on `feat/stage3-slice1-schema`)

1. `feat(articulos): agregar las entidades de dominio y las claves alternas del gate` —
   domain entities, EF configurations, 4 existing-table alternate keys, `WaysDbContext`
   wiring (DbSets, manual tenant filters, write guard, and the `EstamparTenant`
   provider-compatibility fix found while validating this batch — same file, bundled in).
2. `feat(articulos): agregar el contador atomico de codigo_interno` —
   `AsignadorDeCodigoInternoArticulo`.
3. `fix(errores): ordenar los backstops de unicidad nuevos antes del generico _codigo` —
   `ManejadorDeErrores` ordering fix + `_vigente` branch.
4. `test(articulos): agregar las pruebas independientes de la migracion` — domain unit tests,
   model-shape tests, guard test, manual-filter test.

### Next batch (after gate approval)

1. `dotnet ef migrations add ArticulosYPreciosEtapa3` (task 1.8) — hand-name every FK/index in
   snake_case, `HabilitarRlsDeTenant` on the 5 new tables, register `UnidadVenta` in
   `WaysDbContextFactory` + `DependencyInjection.cs` + `WaysApiFixture.cs`. Confirm
   `dotnet ef migrations has-pending-model-changes` is clean.
2. Apply the migration, then implement 1.11 (RLS proofs, 5 tables) and 1.13 (counter
   concurrency race) — both currently blocked on the physical schema.
3. Proceed to Slice 2 (`articulos` + `codigos_barra` + `articulos_empresas` + margin
   suggestion) per the chained-PR plan.
