# Apply progress: Stage 2 — Clientes y Proveedores

## Batch 1 (this apply)

**Scope:** Slice 1 only (`tasks.md` § Slice 1: Schema, Domain Foundation & Provisioning),
stopped at the DB CHANGE GATE (task 1.1) as instructed. No EF Core migration was generated
or applied. Branch `feat/stage2-slice1-fundacion` off `main`, work-unit commits, no push/PR.

### Completed

- **1.2–1.5** — Domain: `Cliente : EntidadTenant` (`Ways.Domain/Clientes`), `Proveedor :
  EntidadTenant` (`Ways.Domain/Proveedores`), `ListaPrecio : CatalogoSimple`
  (`Ways.Domain/Catalogos`, reuses the generic EF base per design decision 1),
  `NumeracionCliente` (standalone, `id_tenant` PK — no `EntidadBase`), `TipoDocumento`/
  `ModoLista` native-enum-shaped C# enums.
- **1.6** — `ReglaDeClientes.ValidarNoConsumidorFinal`/`EsConsumidorFinal` (pure, no DB) +
  6 unit tests (`tests/Ways.Domain.Tests/Clientes/ReglaDeClientesTests.cs`).
- **1.8** — `AsignadorDeNumeroCliente` (`Ways.Application/Clientes`, static — no DI
  lifecycle, `IWaysDbContext` passed per call): `AsegurarContadorAsync` (idempotent
  `INSERT ... ON CONFLICT DO NOTHING`) + `AsignarSiguienteAsync` (`UPDATE ... RETURNING`),
  raw ADO.NET on `Database.GetDbConnection()`/`CurrentTransaction`, never
  `SqlQuery<T>()`/`FromSqlRaw<T>()` (design decisions 2/3, stage-1 slice-2 precedent). Opens
  via `Database.OpenConnectionAsync()` when the connection isn't already open, so
  `InterceptorDeContextoDeTenant` fires and sets the RLS GUCs — never a raw
  `conexion.OpenAsync()` on the ADO.NET connection directly.
- **1.9** — `PlantillaDeAprovisionamiento.V1` extended in place (design decision 5): added
  `ListaPrecioGeneral`/`ClienteConsumidorFinal` records, removed the now-closed
  `ItemsDiferidos` gap declaration. 2 unit tests replace the old `ItemsDiferidos` test.
- **1.12** — `ManejadorDeErrores`: new `23514` case for `ck_clientes_cf_protegido` → 409
  `consumidor_final_protegido`; `ClasificarUnicidad` extended with `_cuit` →
  `cuit_duplicado`, `_numero` → `numero_duplicado`, `_default` → `default_duplicado` (the
  existing `_nombre`/`fk_` prefix mappings already cover `listas_precio`'s nombre pair and
  all 4 tables' new FKs — confirmed, no change needed there).
- **1.13** — `ParametrosTests.InterceptorDeRendezVous.EsperarSiCorresponde`: `gate.Wait()` →
  `gate.Wait(TimeSpan.FromSeconds(10))` + `Assert.True(...)` with a message, so a broken
  rendezvous fails fast instead of hanging the test session.
- **EF configurations** (not a separate numbered task, but required for 1.2–1.5 to be
  usable): `ClienteConfiguration`, `ProveedorConfiguration`, `ListaPrecioConfiguration`
  (extends `ConfiguracionDeCatalogo<ListaPrecio>`), `NumeracionClienteConfiguration` — all 4
  tables' full column/index/FK/check-constraint shape from `design.md` § Table Shapes,
  wired into `WaysDbContext` (new `DbSet`s + a hand-written `NumeracionCliente` tenant query
  filter, same pattern as `Tenant`'s) and `IWaysDbContext`. `TipoDocumento`/`ModoLista`
  registered as native Postgres enums in `WaysDbContextFactory` and
  `DependencyInjection.ConfigurarNpgsql` (prod), plus `WaysApiFixture`'s two test-only
  Npgsql configuration points — same "model ahead of migration" precedent as stage-1's
  `CatalogosDeTenant` (`bc59d37`), confirmed safe: `db.Model.FindEntityType(...)` builds and
  validates the model without a live connection, and `MapEnum` registration ahead of the
  actual Postgres type didn't break anything on its own (see Deviations below for what DID
  break and how it was resolved).
- **1.14–1.17 (integration, GATED)** — `ClientesYProveedoresRlsTests.cs` (RLS SELECT/UPDATE
  proofs for `clientes`/`proveedores`/`listas_precio` + a `numeraciones_clientes` visibility
  test), `BackstopClientesYProveedoresTests.cs` (`ck_clientes_cf_protegido` 23514 backstop +
  4 FK smoke tests, one theory covering all 4 tables' `fk_*_tenant`),
  `ClientesProvisioningYBackfillTests.cs` (provisioning creates CF+General list; a
  pre-existing tenant gains both via backfill and a second host start doesn't duplicate
  them). All marked `[Fact(Skip = ...)]`/`[Theory(Skip = ...)]` — real assertions written
  against the real API/EF surface, not stubs, but they cannot run until the migration exists
  (10 skipped entries in the full run, see Verification below).
- **Model-shape unit tests (no DB)** — `ModeloDeClientesYProveedoresTests.cs` (Application.Tests):
  builds `WaysDbContext`'s Npgsql model without connecting, asserts `ux_clientes_numero`,
  the 4 `clientes` FKs, `numero_documento` has no unique index anywhere, `ux_proveedores_cuit`
  shape, the `listas_precio` default pair alongside the reused nombre pair, and
  `numeraciones_clientes`'s `id_tenant`-as-PK/no-identity shape. Mirrors
  `ModeloDeCatalogosTests`/`ModeloDeOrganizacionTests`.
- **1.18** — Regression confirmed green: see Verification.

### Deferred (deviation from the literal task list — see below)

- **1.10** — Wiring `ServicioDeAprovisionamiento.CrearTenantAsync` to create the General
  `listas_precio` row + the Consumidor Final `Cliente` inside the provisioning transaction
  was written, then **reverted**, in this same batch.
- **1.11** — `InicializadorDeBaseDeDatos.BackfillDeClientesYListasPrecioAsync` was written,
  then **not wired** into `EjecutarAsync` (call site never added) in this same batch.

Both pieces of code touch `clientes`/`listas_precio`, which don't exist until the migration
lands. `InicializadorDeBaseDeDatos.EjecutarAsync` runs on **every** integration test's first
`CreateClient()` (`WaysApiFixture`) — wiring the backfill call broke the ENTIRE integration
suite (63/74 failing, `42P01: relation "clientes" does not exist`), not just the new
clientes-specific tests. Wiring `ServicioDeAprovisionamiento` alone (backfill left unwired)
narrowed the blast radius to exactly `AprovisionamientoTests`' 2 tests that call
`CrearTenantAsync` for real — still an existing-suite regression (task 1.18 requires the
existing suites to stay green), and re-commenting the code out to "defer" it would violate
this repo's own `S125`/`S1144` analyzer severities (`error`, `.editorconfig` lines 303-304 /
354-355: no commented-out code, no unused private members). The only option that satisfies
both "implement it" and "don't break the existing suite" was: write it once to confirm it
compiles and reads correctly against the real entities (confirmed, then reverted via `git
checkout --`), and defer the actual wiring to the batch that generates the migration — this
also matches `tasks.md`'s own ordering, which places 1C (Migration, task 1.7) BEFORE 1D
(counter/provisioning/backfill, tasks 1.8–1.11): 1.10/1.11's *wiring* genuinely depends on
1.7 already having landed, even though 1.8/1.9 (the counter helper and the template data)
don't. `AsignadorDeNumeroCliente` and the `Cliente`/`ListaPrecio` entities/configs are fully
ready for both call sites to wire in with a two-line diff once the migration exists.

### Blocked

- **1.1** — DB CHANGE GATE: model summary presented to the user below (see the executor's
  final response for this batch); awaiting explicit approval.
- **1.7** — Generate migration `ClientesYProveedoresEtapa2`. Blocked on 1.1.
- **1.10, 1.11** — See Deferred above. Unblocked by 1.7 landing (same batch).

## Verification

- `dotnet build Ways.slnx` — 0 warnings, 0 errors.
- `dotnet test Ways.slnx`:
  - `Ways.Domain.Tests`: 69/69 (baseline 61 + 8: 6 `ReglaDeClientesTests` + net +1 from
    replacing `LosItemsDiferidosEstanDeclaradosNoDescartadosEnSilencio` with 2 new
    `PlantillaDeAprovisionamientoTests`).
  - `Ways.Application.Tests`: 91/91 (baseline 85 + 6 `ModeloDeClientesYProveedoresTests`).
  - `Ways.IntegrationTests`: 74 passed + 10 skipped = 84 total (baseline 74 passed, 0
    regressions, +10 new GATED tests skipped pending the migration). Docker was up
    (Testcontainers-backed real Postgres) for this run.

## TDD Cycle Evidence (batch 1)

Strict TDD mode was not signaled by the orchestrator for this run (no
`sdd-init/{project}` testing-capabilities injection was provided in the launch prompt); this
batch followed Standard Mode — implementation followed by test coverage per task, not a
RED-GREEN-REFACTOR cycle log.

---

## Batch 2 (this apply)

**Trigger:** DB CHANGE GATE (task 1.1) **APPROVED by the user, 2026-08-02**, exactly as
presented in batch 1 — all 4 tables, enums, RLS, partial unique indexes (incl. both
single-default guarantees on `listas_precio` and the tenant-wide `cuit` index),
`ck_clientes_cf_protegido`, and the full backfill plan (CF + General list per pre-existing
tenant, transactional per tenant, idempotent).

**Status:** Slice 1 **complete and runtime-verified**. Migration generated and applied
(via `WaysApiFixture`'s owner-role `MigrateAsync` in every integration test run — no
standalone local Postgres was migrated by hand, same as stage 1's pattern), tasks 1.10/1.11
wired, all 10 previously-gated integration tests un-skipped and green, full suite run twice
for stability with identical results both times.

### Completed in batch 2

- **1.7** — Generated `ClientesYProveedoresEtapa2` via `dotnet ef migrations add`. Before
  generating, added 4 explicit snake_case index names to the EF configurations
  (`ix_clientes_condicion_fiscal`, `ix_clientes_lista_precio`,
  `ix_proveedores_condicion_fiscal`, `ix_listas_precio_lista_base`) — without them, EF's
  scaffolder names FK-support indexes with its own convention (`IX_clientes_id_condicion_fiscal`
  etc.), which would have been the first `IX_`-prefixed index in the entire migration history
  (grepped: zero prior occurrences), breaking the doc-10 snake_case convention every other
  migration keeps without exception. Regenerated after the fix — the migration diff matches
  the approved model exactly, with clean names throughout. Hand-added
  `migrationBuilder.HabilitarRlsDeTenant(...)` for all 4 tables at the end of `Up()` (EF's
  scaffolder has no way to know about this raw-SQL convention), same pattern as
  `CatalogosDeTenant`. `dotnet ef migrations has-pending-model-changes` confirms clean both
  right after generation and again after wiring 1.10/1.11 (no model drift from the wiring).
  File: `src/Ways.Infrastructure/Persistencia/Migraciones/20260802172552_ClientesYProveedoresEtapa2.cs`.
- **1.10** — `ServicioDeAprovisionamiento.CrearTenantAsync` re-wired exactly as written (and
  reverted) in batch 1: after seeding área/medios de pago, creates the General `listas_precio`
  row, looks up the seeded `CF` `CondicionFiscal`, calls
  `AsignadorDeNumeroCliente.AsegurarContadorAsync`/`AsignarSiguienteAsync` for the new tenant
  (always returns `1` on a fresh counter), then inserts the Consumidor Final `Cliente` — all
  inside the existing provisioning transaction (spec: Tenant Provisioning With Template Seed).
- **1.11** — `InicializadorDeBaseDeDatos.BackfillDeClientesYListasPrecioAsync` re-wired into
  `EjecutarAsync`, running after `SembrarCatalogosFiscalesAsync` (needs `CF` to already exist).
  Finds tenants missing *both* a `numero = 1` cliente and an `es_default` lista_precio,
  processes each in its own transaction (execution-strategy wrapped, same pattern as
  `ServicioDeAprovisionamiento`), explicit `IdTenant` per entity (platform mode, no
  impersonation needed or supported by `TenantActualFijo`).
- **1.14–1.17** — `[Skip]` removed from all 10 gated `[Fact]`/`[Theory]` methods across
  `ClientesYProveedoresRlsTests.cs`, `BackstopClientesYProveedoresTests.cs`,
  `ClientesProvisioningYBackfillTests.cs`. All green against Postgres real.
- **Incidental fix, found while running the full suite** — `CatalogosGlobalesRlsTests.cs` had
  3 methods (`LaPlataformaPuedeEscribirEnUnCatalogoGlobal`,
  `UnaSesionDeTenantNoPuedeInsertarEnUnCatalogoGlobal`,
  `SinContextoResueltoNoSePuedeEscribirEnUnCatalogoGlobal`) that only ever opened raw
  connections (`AbrirConexionCrudaAsync`), never `fixture.CreateClient()` — a pre-existing
  latent test-ordering fragility that this slice was the first to expose. When one of those 3
  ran before any method that *does* call `CreateClient()` (order is not guaranteed within a
  class), it seeded a raw `condiciones_fiscales` row directly, which made
  `SembrarCatalogosFiscalesAsync`'s idempotency guard (`AnyAsync()` over the *whole* table,
  not the specific base codes it's about to insert) see a non-empty table and skip seeding the
  base 5 rows — including `CF`. Harmless before this slice (nothing depended on `CF`
  specifically existing); now fatal, since `BackfillDeClientesYListasPrecioAsync` does. Fixed
  by making all 3 methods call `fixture.CreateClient()` first, matching the convention every
  other method in that class (and virtually every other integration test in the suite)
  already follows. Test-only change — no production behavior touched, and the class's own RLS
  assertions are unaffected (still checking the same policies against the same real Postgres).
- **1.18** — Regression re-confirmed, twice, after all wiring: see Verification.

### Blocked

None. Slice 1 is fully done.

## Verification (batch 2, final)

- `dotnet build Ways.slnx` — 0 warnings, 0 errors.
- `dotnet ef migrations has-pending-model-changes` — clean, both after generating the
  migration and after wiring 1.10/1.11.
- `dotnet test Ways.slnx`, run **twice** for stability, identical results both times:
  - `Ways.Domain.Tests`: **69/69**.
  - `Ways.Application.Tests`: **91/91**.
  - `Ways.IntegrationTests`: **91/91**, 0 skipped (baseline 74 + 17 newly-active test cases:
    the 10 un-skipped `[Fact]`/`[Theory]` methods include 2 theories with `MemberData`
    (3 tables × 2 = 6 cases) and 1 theory with `InlineData` (4 tables) — Skip collapses a
    theory to 1 reported entry regardless of row count, active theories expand to one result
    per data row, which is why batch 1's "10 skipped" becomes "17 additional passing" here;
    the math reconciles exactly: 7 (RLS theories + 1 fact) + 8 (backstop theory + 4 facts) +
    2 (provisioning/backfill facts) = 17). Docker up (Testcontainers-backed real Postgres).

## TDD Cycle Evidence (batch 2)

Same as batch 1 — Standard Mode, no strict-TDD signal from the orchestrator for this run.
