# Apply Progress: Stage 3 — Artículos y Precios

## Slice 1: Schema, Domain Foundation & Counter (PR 1) — DONE, ready for judgment-day

**Branch**: `feat/stage3-slice1-schema` (off `main`, no push/PR yet).

## Batch 2 — Post-gate: migration, RLS proofs, counter concurrency

DB CHANGE GATE (task 1.1) was approved 2026-08-02 exactly as presented in batch 1: 5 new
tables, `unidad_venta` enum, standard RLS on all 5, the 4 additive alternate keys on existing
tables, the AK naming convention call, `Precio.Monto`, no seed/backfill. The deferred
price-resolution functions (`SugeridorDePrecio`/`ResolverPrecioDerivado`) staying in Slices
2/3 per tasks.md was also confirmed correct — no change to that call.

### What shipped this batch

**Migration** (task 1.8):
- `UnidadVenta` registered in the three enum-mapping surfaces the gate summary called for:
  `WaysDbContextFactory` (design-time), `DependencyInjection.ConfigurarNpgsql` (production),
  and all three enum-list spots in `WaysApiFixture` (owner/app/`CrearContextoDeAplicacion`).
- First scaffold caught a real convention violation before it shipped: `dotnet ef migrations
  add` produced an auto-named PascalCase support index,
  `IX_articulos_empresas_id_articulo_id_tenant`, because `ArticuloEmpresaConfiguration` was
  missing an explicit `HasIndex` for `fk_articulos_empresas_articulo`'s `(IdArticulo,
  IdTenant)` columns — every *other* composite FK in this batch already had one. Fixed the
  config (added `ix_articulos_empresas_articulo`), deleted the bad scaffold by hand (no local
  Postgres for `ef migrations remove` — its own connection-string fallback isn't a live
  server), and regenerated cleanly. No `Ignore<T>()` isolation was needed: nothing beyond
  this gate's approved model exists in code yet (unlike stage 1, which had 3 gates' worth of
  entities modeled simultaneously), so the scaffold diff was exactly the 5 tables + 4 alternate
  keys + enum, nothing more.
- `HabilitarRlsDeTenant` hand-added at the end of `Up()` for the 5 new tables (`articulos`,
  `articulos_empresas`, `codigos_barra`, `numeraciones_articulos`, `precios`), same placement
  precedent as stage 2's migration (ADR-15 — same migration that creates the table enables its
  policy).
- `dotnet ef migrations has-pending-model-changes` — clean after the manual RLS addition.
- Migration file: `src/Ways.Infrastructure/Persistencia/Migraciones/20260803001552_ArticulosYPreciosEtapa3.cs`.

**Tests** (tasks 1.11, 1.13 — unblocked by the migration):
- `tests/Ways.IntegrationTests/ArticulosYPreciosRlsTests.cs` — parametrized theory over
  `articulos`/`articulos_empresas`/`codigos_barra`/`precios` (SELECT/UPDATE cross-tenant → 0
  rows via `USING`; INSERT with a foreign `id_tenant` → 42501 via `WITH CHECK`), plus an
  EF/LINQ filter proof for the 4 ORM-reachable entities (including `ArticuloEmpresa`'s manual
  filter) and 2 dedicated `numeraciones_articulos` tests (PK IS `id_tenant`, doesn't fit the
  parametrized shape — mirrors `ClientesYProveedoresRlsTests.NumeracionesClientesEsInvisibleParaOtroTenant`).
- `tests/Ways.IntegrationTests/AsignadorDeCodigoInternoArticuloConcurrenciaTests.cs` — mirrors
  `AsignadorDeNumeroClienteConcurrenciaTests`: 3 rounds × 2 concurrent
  `AsignarSiguienteAsync` calls, asserts exactly-consecutive-no-duplicates. Verified stable
  across 5 runs total (2 full-suite runs + 3 additional isolated runs) — the row lock on
  `numeraciones_articulos` serializes the race by construction, same mechanism already proven
  for `numeraciones_clientes`.

### Build/test results (batch 2, run twice)

| Suite | Run 1 | Run 2 |
|---|---|---|
| `Ways.Domain.Tests` | 74/74 | 74/74 |
| `Ways.Application.Tests` | 142/142 | 142/142 |
| `Ways.IntegrationTests` (real Postgres, Testcontainers) | 145/145 | 145/145 |

145 = 129 baseline + 16 new (12 parametrized RLS cases + 2 `numeraciones_articulos` proofs + 1
EF/LINQ filter proof + 1 concurrency test). Identical counts both runs, no flakes.

### Commits (batch 2, work-unit style, migration in its own commit)

5. `feat(persistencia): registrar el enum unidad_venta en las fabricas de contexto` —
   `WaysDbContextFactory`, `DependencyInjection.cs`, `WaysApiFixture.cs`.
6. `fix(persistencia): nombrar en snake_case el indice de soporte de fk_articulos_empresas_articulo` —
   the missed `HasIndex` on `ArticuloEmpresaConfiguration`, found via the first scaffold.
7. `feat(persistencia): generar la migracion ArticulosYPreciosEtapa3 con RLS` — the migration
   itself + designer + model snapshot.
8. `test(articulos): agregar las pruebas de integracion de RLS y concurrencia del contador` —
   tasks 1.11/1.13.

### Next

Slice 1 is complete and runtime-verified. Judgment-day (dual blind review) runs next, per the
solo-dev PR protocol, before this PR is created. Slice 2 (`articulos` + `codigos_barra` +
`articulos_empresas` + margin suggestion, per the chained-PR plan) starts only after Slice 1's
PR merges.

### Status summary

| Bucket | Status |
|---|---|
| 1A — DB CHANGE GATE (1.1) | **Approved 2026-08-02**, exactly as presented |
| 1B — Domain (1.2–1.7) | Done |
| 1C — Migration (1.8) | Done |
| 1D — codigo_interno counter (1.9) | Done |
| 1E — db-error-backstops mapping (1.10) | Done |
| 1F — Tests (1.11–1.14) | Done — all 4 |

All 14 Slice 1 tasks complete. Slice 1 is runtime-verified end to end and ready for the
judgment-day review round.

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
- 1.11 (RLS integration proofs) and 1.13 (counter concurrency integration) were deferred to
  batch 2 (see below) — both require the physical tables from the migration, blocked on gate
  approval at the time of this batch.

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

### Batch 1 next-steps — superseded, see "Batch 2" above

Everything listed here (migration generation, RLS proofs, counter concurrency) was completed
in batch 2 after gate approval. Left in place for the historical record of what batch 1 hadn't
done yet; see the "Batch 2" section above for what actually happened and the final "Next"
section for what comes after Slice 1.
