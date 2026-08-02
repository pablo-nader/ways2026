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

---

## Batch 3 — judgment-day round 1 fixes (branch `feat/stage2-slice1-fundacion`)

**Trigger:** first judgment-day round (dual blind review) over the batch 1–2 diff surfaced 1
CRITICAL, 1 GATE-APPROVED schema hardening item (approved by the user 2026-08-02, same
session as the original DB CHANGE GATE), 2 confirmed items, and 3 hardening items, plus 3
comment-only items. All applied this batch.

### Completed in batch 3

1. **Per-artifact backfill idempotency (CRITICAL)** — `BackfillDeClientesYListasPrecioAsync`'s
   coverage check was a UNION of tenants-with-CF and tenants-with-default-lista: a tenant with
   only ONE of the two rows (partial backfill from an earlier failed run, or hand-migrated
   data) was permanently skipped. Rewritten to evaluate each artifact independently — the
   default lista is created if the tenant has none, the CF cliente is created if the tenant
   has none — still one transaction per tenant. New test
   `BackfillPorArtefactoTests.UnTenantConSoloListaYOtroConSoloClienteCfGananLaMitadFaltantePorBackfill`
   (own `WaysApiFixture`, to avoid the "seed before first `CreateClient()`" trick depending on
   test-method execution order within a shared fixture) seeds one tenant with only the lista
   and another with only the CF cliente, and asserts each gains exactly its missing half
   without duplicating the half it already had.
2. **Composite `fk_clientes_lista_precio` (GATE-APPROVED schema hardening)** — the FK was a
   single column against `listas_precio.id_lista_precio` (PK, globally unique across tenants):
   an `id_lista_precio` belonging to ANOTHER tenant was a row that genuinely exists, so the FK
   never rejected it — only RLS did, at runtime. Added alternate key `(Id, IdTenant)` on
   `ListaPrecio` (`ak_listas_precio_id_tenant`) and changed the FK to composite
   `(IdListaPrecio, IdTenant) → listas_precio(Id, IdTenant)`. The unmerged migration
   `20260802172552_ClientesYProveedoresEtapa2` was edited in place (Up/Down, `.Designer.cs`,
   `WaysDbContextModelSnapshot.cs`) rather than regenerated wholesale — a `migrations remove` +
   `migrations add` cycle was tried first and discarded: it picked up an unrelated spurious
   `AddCheckConstraint`/`DropCheckConstraint` pair for `ck_categorias_padre_no_self` (already
   present since the `CatalogosDeTenant` migration) as an EF diffing artifact of reverting and
   rescaffolding, and it also dropped the hand-written `HabilitarRlsDeTenant(...)` calls that
   aren't part of the model and only exist because a human added them to the original
   migration. Hand-editing avoided both regressions. `dotnet ef migrations
   has-pending-model-changes` confirmed clean after the edit. New smoke test
   `BackstopClientesYProveedoresTests.UnClienteConIdListaPrecioDeOtroTenantViolaLaFkCompuesta`
   inserts a cliente whose `id_lista_precio` is a real row of a DIFFERENT tenant → 23503.
3. **42501 INSERT proofs (confirmed)** — `ClientesYProveedoresRlsTests` only had SELECT/UPDATE
   cross-tenant proofs; added `UnInsertConIdTenantAjenoSeRechaza` (mirrors
   `CatalogosDeTenantRlsTests`'s method of the same name) as a `[Theory]` over
   clientes/proveedores/listas_precio/numeraciones_clientes → 42501, plus one EF-level (LINQ)
   cross-tenant-read proof,
   `ElFiltroDeEfNuncaDevuelveFilasDeOtroTenantParaLasEntidadesNuevas`, mirroring
   `AislamientoDeTenantTests.ElFiltroDeEfNuncaDevuelveFilasDeOtroTenant` for the 3 new
   EF-tracked entities (`NumeracionCliente` is SQL-crudo-only by design, out of this proof).
   Class docstring updated to describe what the class now actually covers.
4. **Composite-FK smoke tests (triaged real)** — added the 4 missing cases to
   `BackstopClientesYProveedoresTests`: `fk_clientes_empresa`, `fk_proveedores_empresa`,
   `fk_listas_precio_empresa` (all composite, nullable `id_empresa` with a non-null-but-bogus
   value to force MATCH SIMPLE to evaluate) and `fk_listas_precio_lista_base` (simple,
   self-referencing) — all 23503, `ConstraintName` asserted on each.
5. **Counter concurrency test (hardening)** — new
   `AsignadorDeNumeroClienteConcurrenciaTests.DosAsignacionesConcurrentesDelMismoTenantDanNumerosDistintosYConsecutivos`:
   3 rounds of 2 concurrent `AsignadorDeNumeroCliente.AsignarSiguienteAsync` calls against the
   same tenant's counter, against real Postgres. No rendezvous interceptor needed (unlike
   `ParametrosTests`'s race test) — the `UPDATE ... RETURNING` on the counter row is
   unconditional, so Postgres's own row lock serializes the two transactions. All 6 assigned
   numbers collected across the 3 rounds and asserted to be exactly consecutive, no gaps or
   duplicates.
6. **`NumeracionCliente` ChangeTracker guard (hardening)** — `NumeracionCliente`'s doc comment
   already claimed `AsignadorDeNumeroCliente` (raw SQL) is its only legitimate writer, but
   nothing enforced it: an `Added`/`Modified` entry reaching `SaveChanges` via the
   `ChangeTracker` went through silently. New `WaysDbContext.RechazarEscriturasDeNumeracionCliente`
   (called first inside `EstamparTenant`) throws `InvalidOperationException` for any such
   entry — same defense-in-depth pattern as the `IdTenant` tamper guard. 3 new unit tests
   (`GuardDeNumeracionClienteTests`, InMemory provider, no Postgres needed since the guard
   throws before `base.SaveChanges()` ever runs) cover Added-rejected, Modified-rejected, and
   Unchanged-passes-through. `ClientesYProveedoresRlsTests.NumeracionesClientesEsInvisibleParaOtroTenant`
   (the one existing test that wrote a `NumeracionCliente` via `db.NumeracionesClientes.Add`)
   was migrated to `AsignadorDeNumeroCliente.AsegurarContadorAsync`, the now-only-legal path.
7. **Spanish comments (3 items)** — exemption notes on the `_cuit`/`_numero` branches of
   `ManejadorDeErrores.ClasificarUnicidad` explaining why they're exempt from the
   `db-error-backstops` race-test requirement until Slice 2/3's create endpoints exist (tasks
   2.5/3.5 will add the race tests then); a note on `ReglaDeClientes` documenting the known
   renumber-then-delete two-step bypass of `ck_clientes_cf_protegido` and why closing it is
   explicitly out of scope for this slice (closed by `ServicioDeClientes`'s service-level guard
   in Slice 2); a note on `BackfillDeClientesYListasPrecioAsync`'s doc comment listing the two
   invariants its per-artifact coverage check depends on.

### Verification performed this batch

- `dotnet build Ways.slnx` → 0 errors, 0 warnings.
- `dotnet ef migrations has-pending-model-changes` → clean, after the in-place composite-FK
  edit.
- `dotnet test Ways.slnx`, run **twice** for stability, identical results both times:
  - `Ways.Domain.Tests`: **69/69** (unchanged — no Domain-layer tests touched this batch).
  - `Ways.Application.Tests`: **94/94** (91 + 3 new `GuardDeNumeracionClienteTests`).
  - `Ways.IntegrationTests`: **103/103** (91 + 12 new: 5 `BackstopClientesYProveedoresTests` +
    5 `ClientesYProveedoresRlsTests` (4-case `UnInsertConIdTenantAjenoSeRechaza` theory + 1
    EF-level fact) + 1 `BackfillPorArtefactoTests` + 1
    `AsignadorDeNumeroClienteConcurrenciaTests`). The pre-existing `ParametrosTests`
    rendezvous-flake risk documented in batch 1F/1.13 did not surface in either run. Docker up
    (Testcontainers-backed real Postgres) both times.

### Blocked

None.

## Post-verdict INFO fixes (judgment-day, feat/stage2-slice1-fundacion)

Two INFO-level (non-blocking) items applied after the APPROVED judgment-day verdict on the
`feat/stage2-slice1-fundacion` slice:

1. **Tolerant default-list lookup** —
   `InicializadorDeBaseDeDatos.BackfillDeClientesYListasPrecioAsync`'s
   `idListaDefaultPorTenant` lookup now filters `l.EsDefault && l.IdEmpresa == null` instead of
   just `l.EsDefault`. The unfiltered `ToDictionaryAsync(l => l.IdTenant, l => l.Id)` would throw
   on a duplicate key if a tenant ever ended up with both a shared default and a per-empresa
   default lista at once; scoping to the documented "compartida" invariant makes that future
   state degrade gracefully instead of crashing startup. Comment above the query updated to
   explain the filter and the degrade-not-crash rationale.
2. **AK naming convention** — renamed `ak_listas_precio_id_tenant` to
   `ak_listas_precio_id_lista_precio_id_tenant` (matching `ak_empresas_id_empresa_id_tenant` and
   siblings) in all four locations: `ListaPrecioConfiguration.cs`,
   `20260802172552_ClientesYProveedoresEtapa2.cs`, its `.Designer.cs`, and
   `WaysDbContextModelSnapshot.cs`.

### Verification performed this batch

- `dotnet build Ways.slnx` → 0 errors, 0 warnings.
- `dotnet ef migrations has-pending-model-changes` → clean.
- `dotnet test Ways.slnx`:
  - `Ways.Domain.Tests`: **69/69**.
  - `Ways.Application.Tests`: **94/94**.
  - `Ways.IntegrationTests`: **103/103**.
  - Baseline unchanged (69/94/103), all green.
