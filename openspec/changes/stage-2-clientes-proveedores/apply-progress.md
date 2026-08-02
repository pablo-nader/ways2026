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

---

## Batch 4 — Slice 2: Clientes service + API + Web ABM (branch `feat/stage2-slice2-clientes`)

**Trigger:** Slice 1 merged to `main` (PR #10). Orchestrator directed this batch to deliver
clientes as one vertical slice — service + API + web ABM together — instead of the original
layer split (tasks.md Slice 2 = service/API only, Slice 4 = both screens after Slices 2+3).
tasks.md updated in-place to reflect this re-scoping (see its "Re-scoping note"); the
clientes-only portions of section 4A/4B were pulled forward and marked done here, proveedores
portions left pending for Slice 3/4.

No database changes: schema from Slice 1 (`clientes`, `listas_precio`, `numeraciones_clientes`,
`condiciones_fiscales`) already covers everything this batch needed — no migration touched.

### Completed in batch 4

- **2.1–2.2** — `ServicioDeClientes` (`Ways.Application/Clientes/ServicioDeClientes.cs`):
  list (search + pagination + `incluirEliminados`), get, create, edit, soft-delete.
  `CrearAsync` wraps `AsignadorDeNumeroCliente.AsegurarContadorAsync`/`AsignarSiguienteAsync`
  + the `Cliente` insert in one `CreateExecutionStrategy().ExecuteAsync(BeginTransactionAsync)`
  block — same pattern as `ServicioDeAprovisionamiento.CrearTenantAsync` — so a failure between
  taking the number and persisting the row rolls back both (numero gaps stay a rollback-only
  edge case, design decision 2's own stated precedent, not a guaranteed outcome of every failed
  create). `ActualizarAsync`/`EliminarAsync` call `ReglaDeClientes.ValidarNoConsumidorFinal`
  with the row's CURRENT `numero` before touching anything; since `Numero` is never a field of
  `AltaCliente`/`EdicionCliente` (server-assigned only), there is no service-level path to
  renumber a row away from 1 before deleting it — the two-step bypass `ReglaDeClientes`'s doc
  comment flags as this slice's job to close is closed by construction, not by an extra check.
  No in-service role check (mirrors `ServicioDeCatalogo`, not `ServicioDeUsuarios`):
  `GestionDeCatalogo` is a single fixed role (admin), nothing to differentiate internally.
  Contracts (`Ways.Application/Clientes/Contratos.cs`): `AltaCliente`/`EdicionCliente`/
  `ClienteListado` (no `Numero` field on the alta/edicion side; `Saldo` excluded from
  `EdicionCliente` — no CC engine yet, doc 10 §2) + `ListaPrecioAsignable` (id/nombre/esDefault
  reference type, not a `listas_precio` ABM contract).
  **Deviation from tasks.md 2.1's literal wording, corrected (judgment-day round 1 batch)**:
  task 2.1 said "defaults `id_lista_precio` to the tenant's `es_default` list when omitted" —
  that phrase only ever existed in tasks.md, never in design.md. But design.md:29 DID state a
  default for the *other* field: `id_condicion_fiscal` "app defaults to seeded CF row when
  omitted". `specs/clientes/spec.md`'s own scenario ("id_lista_precio and id_condicion_fiscal
  are required... GIVEN a tenant admin submits a cliente missing id_lista_precio... THEN it is
  rejected before reaching the database") contradicts default-on-omit for BOTH fields. The
  override made here is therefore of BOTH tasks.md's wording (`id_lista_precio`) AND
  design.md:29's wording (`id_condicion_fiscal`), resolved in favor of spec.md as the
  higher-authority acceptance contract for both: both fields are REQUIRED, `<= 0`/omitted is
  rejected with 400 `{campo}_requerido` before any DB call. design.md:29 now carries a
  superseded note pointing back here. The web form still only ever shows one selectable list
  this stage (the tenant's `es_default`, pre-selected) since `listas_precio` has no ABM yet —
  functionally equivalent UX, simpler/more deterministic contract.
- **Referencia de solo lectura para `listas_precio`** — `ServicioDeClientes.ListasDePrecioAsignablesAsync`
  + `GET /api/listas-precio` (`ClientesEndpoints`), `GestionDeCatalogo` policy. **Deviation from
  design.md's literal "no service/API this stage" for `listas_precio`** (documented): design
  decision 1 says `listas_precio` gets no dedicated `Servicio`/`Endpoints` this stage (no ABM —
  create/edit/delete stay out of scope, confirmed unchanged). This is NOT that: it's a
  read-only reference listing exposed from `ServicioDeClientes`/`ClientesEndpoints` (not a
  `ServicioDeListasPrecio`/`ListasPrecioEndpoints`), same precedent as `RolListado`/
  `RolesAsignablesAsync`/`GET /api/roles` living inside `ServicioDeUsuarios`/`UsuariosEndpoints`
  to populate a foreign-key selector for a DIFFERENT entity's form. Needed because the web
  cliente form has a lista-precio selector (explicit scope instruction) and there is, as of
  this stage, exactly one selectable list per tenant (the `es_default` General list) — the
  selector still needs a way to fetch it (id + display name), which nothing else in the API
  surface provides.
- **2.3** — `ClientesEndpoints` (`Ways.Api/Endpoints/ClientesEndpoints.cs`): `GET /api/clientes`
  (list), `GET /api/clientes/{id}`, `POST /api/clientes`, `PUT /api/clientes/{id}`,
  `DELETE /api/clientes/{id}`, all under `Politicas.GestionDeCatalogo` (admin-only, mirrors
  `UsuariosEndpoints`/`CatalogosEndpoints` shape) + `GET /api/listas-precio` (see above). Wired
  in `Program.cs` (`app.MapearClientes()`) and `Ways.Application/DependencyInjection.cs`
  (`services.AddScoped<ServicioDeClientes>()`).
- **`ManejadorDeErrores`** — updated the `_numero` suffix exemption comment
  (`ClasificarUnicidad`): no longer claims the race test is deferred to a future slice, since
  `ServicioDeClientes.CrearAsync` and its race test both exist as of this batch. New wording
  explains the 23505 branch stays mapped as a backstop for a direct-bypass scenario, since
  normal operation never reaches it (Postgres's own row lock on `numeraciones_clientes`
  already serializes concurrent creates before either transaction reaches the `clientes`
  insert). The `_cuit` exemption comment is untouched — still accurate, `ServicioDeProveedores`
  doesn't exist yet (Slice 3).
- **2.4** — `ServicioDeClientesTests.cs` (`Ways.Application.Tests/Clientes/`, InMemory
  provider, 10 facts): required-field validation (`id_condicion_fiscal`/`id_lista_precio`/
  `nombre`), invalid-FK-reference 400 (`id_condicion_fiscal`, and `id_lista_precio` of another
  tenant — EF's tenant filter already makes "exists but wrong tenant" indistinguishable from
  "doesn't exist", same 400 either way), CF guard blocks edit AND delete of `numero = 1`,
  non-CF edit/delete succeeds, cross-tenant `ObtenerAsync` → 404 (ADR-8).
  **Deviation from tasks.md 2.4's literal wording** (documented, with technical justification):
  "default credit fields (0/false/0)" and "vendedor blocked, admin allowed" are NOT covered
  here. `CrearAsync` unconditionally calls `AsignadorDeNumeroCliente` (`Database.GetDbConnection()`)
  and `Database.BeginTransactionAsync()` — both throw under the InMemory provider (confirmed:
  same reason `ServicioDeAprovisionamiento`, the only other `AsignadorDeNumeroCliente`
  consumer, has zero `Application.Tests`, only `IntegrationTests`). Every validation check that
  runs BEFORE the transaction opens (all of the above) is InMemory-testable; the actual insert
  is not. "vendedor blocked/admin allowed" was never a `ServicioDeClientes`-level concern in
  this design either way — `GestionDeCatalogo` is enforced entirely at the endpoint
  (`RequireAuthorization`), the same as `ServicioDeCatalogo`'s catalogs (unlike
  `ServicioDeUsuarios`, which has its own `ExigirPermisoDeGestion` because `GestionDeUsuarios`
  covers two roles with different sub-permissions). Both moved to task 2.5's integration
  coverage, where they're testable for real (default credit fields via a real POST + DB
  round-trip; vendedor/admin via real HTTP + the real auth pipeline).
- **2.5** — `ClientesEndpointsTests.cs` (`Ways.IntegrationTests/`, real Postgres via
  `WaysApiFixture`, 6 facts): concurrent create race (2 `Task.WhenAll` POSTs, tenant admin) →
  both `201`, numeros `[2, 3]` (the tenant's Consumidor Final already holds `1` from
  provisioning) — no rendezvous interceptor (see deviation note below); duplicate + `NULL`
  `numero_documento` all accepted; default credit fields (`0`/`false`/`0`) on a create that
  omits them; admin create→soft-delete round trip (soft-deleted row disappears from the default
  list); vendedor → 403; cross-tenant cliente id → 404 (ADR-8). Tenants set up via the REAL
  `/api/plataforma/tenants` provisioning endpoint (not hand-seeded), so each test tenant already
  has its Consumidor Final cliente + General lista_precio + admin user exactly like production —
  matches `AprovisionamientoTests`'s own pattern, reused rather than re-invented.
  **Deviation from tasks.md 2.5's literal "reuse 1.13's hardened rendezvous"** (documented, with
  technical justification): NOT reused. `AsignadorDeNumeroCliente.AsignarSiguienteAsync` is an
  unconditional `UPDATE ... RETURNING` on the counter row — Postgres's own row lock already
  serializes two concurrent transactions without any interceptor forcing it, confirmed without
  a rendezvous in Slice 1 batch 3's `AsignadorDeNumeroClienteConcurrenciaTests`. That's
  qualitatively different from `ParametrosTests`'s upsert (`SELECT` "existing" then
  conditionally `INSERT`), where an unforced race can close before both `SELECT`s run — the
  reason that test needs a forced rendezvous in the first place. Two `Task.WhenAll`-launched
  POSTs against the real API already race for real; no gap in coverage.
- **2.6** — Regression confirmed green: see Verification.
- **Web (re-scoped from Slice 4, clientes-only portion)** — `Ways.Web/src/api/tipos.ts`:
  `TipoDocumento`/`TIPOS_DOCUMENTO`, `ClienteListado`/`AltaCliente`/`EdicionCliente`/
  `ListaPrecioAsignable`, field-for-field mirrors of the C# contracts.
  `Ways.Web/src/api/clientes.ts`: `clienteDeClientes` (listar/crear/actualizar/eliminar/
  listasDePrecioAsignables), same shape as `clienteDeOrganizacion`/`clienteDeCatalogosFiscales`.
  `Ways.Web/src/paginas/Clientes.tsx`: dedicated screen (not `PaginaCatalogo`/the generic
  machine, design decision 1) — search + paginated list, create/edit form (identity, documento,
  condición fiscal selector via the existing `clienteDeCatalogosFiscales.condicionesFiscales()`,
  lista-precio selector via the new `/api/listas-precio`, contact fields, crédito section with
  `limiteCredito`/`creditoIlimitado`), CF row rendered with a "Protegido" badge and its
  Editar/Baja buttons `disabled` (defense in depth on top of the domain guard — the real
  protection is `ReglaDeClientes`/`ck_clientes_cf_protegido`, this is UX only). Route `/clientes`
  wired in `App.tsx` (`RutaProtegida rolesPermitidos={[ROL.Admin]}`, matches
  `Politicas.GestionDeCatalogo`) + nav entry in `Layout.tsx` (gated by the existing
  `puedeGestionarCatalogos`, no new helper needed — same role set as `GestionDeCatalogo`).

### Deviations summary (all documented above, repeated here for visibility)

1. `id_lista_precio`/`id_condicion_fiscal` are REQUIRED, not defaulted-when-omitted
   (spec.md wins over a tasks.md one-liner that design.md never stated).
2. `GET /api/listas-precio` reference listing added (not a `listas_precio` ABM — read-only,
   same precedent as `/api/roles`).
3. Application.Tests (2.4) don't cover "default credit fields"/"vendedor blocked, admin
   allowed" — both untestable/not-applicable at the InMemory service-unit level, covered by
   2.5's integration tests instead.
4. 2.5's race test doesn't use a forced rendezvous interceptor — the counter's own row lock
   already proves the race deterministically, confirmed precedent from Slice 1 batch 3.
5. Slice boundaries re-scoped per orchestrator instruction: clientes web ABM pulled forward
   from Slice 4 into this Slice 2 batch; proveedores remains split across Slices 3/4.

### Blocked

None.

## Verification (batch 4)

- `dotnet build Ways.slnx` — 0 warnings, 0 errors.
- `dotnet test Ways.slnx`, run **twice** for stability, identical results both times:
  - `Ways.Domain.Tests`: **69/69** (unchanged).
  - `Ways.Application.Tests`: **104/104** (94 + 10 new `ServicioDeClientesTests`).
  - `Ways.IntegrationTests`: **109/109** (103 + 6 new `ClientesEndpointsTests`). Docker up
    (Testcontainers-backed real Postgres) both times.
- `Ways.Web`: `npx tsc -b` clean; `npx oxlint` clean (one pre-existing unrelated warning on
  `AuthContext.tsx`, not touched this batch); `npx vite build` succeeds (300 kB JS / 232 kB CSS
  bundle, in line with the existing app).

## TDD Cycle Evidence (batch 4)

Same as prior batches — Standard Mode, no strict-TDD signal from the orchestrator for this run.

## Batch 5 — judgment-day round 1 fixes (Slice 2, branch `feat/stage2-slice2-clientes`)

Surgical fixes to the 9 confirmed findings of judgment-day round 1 on Slice 2 (dual blind
review). NO schema changes this batch (gate: the `ClientesYProveedoresEtapa2` migration is
already merged).

### Completed in batch 5

1. **numero_duplicado backstop proof** — added
   `BackstopClientesYProveedoresTests.UnaFilaConNumeroDuplicadoInsertadaPorFueraDelContadorViolaLaUnicidad`:
   two raw-SQL INSERTs with the same `(id_tenant, numero)`, bypassing
   `AsignadorDeNumeroCliente` entirely; asserts SQLSTATE `23505` +
   `ConstraintName: ux_clientes_numero`. HTTP-level 409 translation documented as unreachable
   through `POST /api/clientes` (the endpoint never accepts a client-supplied `numero`) —
   noted in the test's doc comment instead of a fake HTTP test. Corrected the misleading
   comment in `ManejadorDeErrores.ClasificarUnicidad` (`_numero` branch): the atomicity proof
   (`ClientesEndpointsTests.LaCreacionConcurrenteAsignaNumerosSecuencialesSinExponerElBackstop`)
   and the backstop proof (this new raw-SQL test) are two different assertions, not one. Fixed
   the stale `ClientesRaceTests` pointer in `ServicioDeClientesTests`'s class doc comment — the
   actual end-to-end create test lives in `ClientesEndpointsTests`.
2. **limite_credito server validation** — `ServicioDeClientes.CrearAsync`/`ActualizarAsync`
   now call `ExigirLimiteCreditoValido`, rejecting `LimiteCredito < 0` with 400
   `limite_credito_invalido`. Service-level only, per the NO-schema-changes gate — a DB
   `CHECK (limite_credito >= 0)` would need a new migration; documented as a future option in
   the method's doc comment, same precedent as `ck_clientes_cf_protegido`. Unit test
   (`CrearConLimiteCreditoNegativoEsRechazado`) + HTTP test
   (`CrearConLimiteCreditoNegativoDevuelve400`).
3. **Cross-tenant write 404 parity (ADR-8)** — added
   `UnPutSobreUnClienteDeOtroTenantDevuelve404`/`UnDeleteSobreUnClienteDeOtroTenantDevuelve404`
   to `ClientesEndpointsTests`: PUT/DELETE against another tenant's cliente id now has explicit
   coverage (previously only GET was covered).
4. **CF guard end-to-end HTTP tests** — added
   `UnPutSobreElConsumidorFinalDevuelve409`/`UnDeleteSobreElConsumidorFinalDevuelve409` to
   `ClientesEndpointsTests`: provisions a tenant, locates its CF row (`numero == 1`) via the
   platform-keyed context, asserts 409 `consumidor_final_protegido` through the live HTTP
   pipeline (not just the InMemory-backed `ServicioDeClientesTests`).
5. **listas-precio endpoint coverage** — added
   `UnAdminSoloVeLasListasDePrecioDeSuPropioTenant` (asserts the admin's list is the only one
   returned, cross-tenant isolation) and `UnVendedorNoPuedeListarListasDePrecio` (403) to
   `ClientesEndpointsTests`.
6. **Field-specific length codes** — `NormalizarOpcional` now takes a `campo` parameter (same
   shape as `NormalizarRequerido`) and throws `{campo}_muy_largo` instead of a generic
   `campo_muy_largo` shared across the eight optional fields. Added
   `CrearConEmailDemasiadoLargoEsRechazadoConElCodigoDelCampo` asserting `email_muy_largo`.
7. **Deviation note correction** — `tasks.md` 2.1's note and `apply-progress.md` batch 4's
   deviation note both claimed the omitted-default wording "only ever existed in tasks.md,
   never in design.md" — true for `id_lista_precio`, but **false** for `id_condicion_fiscal`:
   `design.md:29` did state "(app defaults to seeded `CF` row when omitted)" for that field.
   Corrected both notes: the override made by task 2.1 is of BOTH tasks.md's wording
   (`id_lista_precio`) AND design.md:29's wording (`id_condicion_fiscal`), resolved in favor of
   `spec.md`'s "id_lista_precio and id_condicion_fiscal are required" scenario as the
   higher-authority acceptance contract for both fields. Added a superseded-note directly on
   `design.md:29` pointing back to this resolution.
8. **IdEmpresa pre-check** — added `ExigirEmpresaValidaAsync` (tenant-scoped, same shape as
   `ExigirCondicionFiscalValidaAsync`/`ExigirListaPrecioValidaAsync`) before insert/update, for
   consistency with the other two reference checks. The `fk_clientes_empresa` 23503 backstop is
   unchanged. Unit test `CrearConIdEmpresaInexistenteEsRechazado` asserts the friendly 400
   `referencia_invalida`.
9. **Typo** — `src/Ways.Web/src/api/tipos.ts`: fixed `idLista Precio` → `idListaPrecio` in the
   `AltaCliente` doc comment.

### Files changed

- `src/Ways.Application/Clientes/ServicioDeClientes.cs` — items 2, 6, 8.
- `src/Ways.Api/Seguridad/ManejadorDeErrores.cs` — item 1 (comment only).
- `tests/Ways.IntegrationTests/BackstopClientesYProveedoresTests.cs` — item 1 (new test).
- `tests/Ways.IntegrationTests/ClientesEndpointsTests.cs` — items 2, 3, 4, 5 (new tests).
- `tests/Ways.Application.Tests/Clientes/ServicioDeClientesTests.cs` — items 1 (stale pointer
  fix), 2, 6, 8 (new tests).
- `src/Ways.Web/src/api/tipos.ts` — item 9 (typo).
- `openspec/changes/stage-2-clientes-proveedores/tasks.md` — item 7 (note correction).
- `openspec/changes/stage-2-clientes-proveedores/design.md` — item 7 (superseded note).
- `openspec/changes/stage-2-clientes-proveedores/apply-progress.md` — item 7 (this entry +
  batch 4's deviation note corrected in place).

### Blocked

None.

## Verification (batch 5)

- `dotnet build Ways.slnx` — 0 warnings, 0 errors.
- `dotnet test Ways.slnx`, run **twice** for stability, identical results both times:
  - `Ways.Domain.Tests`: **69/69** (unchanged).
  - `Ways.Application.Tests`: **107/107** (104 + 3 new: `CrearConLimiteCreditoNegativoEsRechazado`,
    `CrearConEmailDemasiadoLargoEsRechazadoConElCodigoDelCampo`,
    `CrearConIdEmpresaInexistenteEsRechazado`).
  - `Ways.IntegrationTests`: **117/117** (109 + 8 new: 1 backstop raw-SQL test, 2 cross-tenant
    write 404 tests, 2 CF-guard HTTP tests, 2 listas-precio tests, 1 limite_credito HTTP test).
    Docker up (Testcontainers-backed real Postgres) both times.
- `Ways.Web`: `npx tsc -b` clean; `npx oxlint` clean (same pre-existing unrelated warning on
  `AuthContext.tsx` as batch 4, not touched this batch).

## TDD Cycle Evidence (batch 5)

Same as prior batches — Standard Mode, no strict-TDD signal from the orchestrator for this run;
surgical fix batch driven by judgment-day findings rather than a task list.
