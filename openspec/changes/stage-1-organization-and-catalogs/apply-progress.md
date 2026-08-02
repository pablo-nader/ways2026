# Apply progress: Stage 1 — Organization and Catalogs

## Batch 1

**Scope:** Slice 1 only (`tasks.md` § Slice 1: Tenancy plumbing + org tables + RLS), stopped
at DB CHANGE GATE #1 (task 1.6) as instructed. No EF Core migration was generated or applied.

### Completed

- 1.1, 1.2 — apply-time verification of ADR-6 / ADR-9. No fallback needed.
- 1.3–1.5 — Domain: `EntidadTenant`, `Tenant`/`Empresa`/`PuntoVenta`/`EstadoTenant`,
  `PoliticaDeRoles` additions + unit tests.
- 1.7 — `RlsMigrationBuilderExtensions`.
- 1.9–1.14 — Infrastructure: `ITenantActual`/`ModoDeAcceso`, `TenantActualDeSesion`,
  `TenantActualFijo`, `InterceptorDeContextoDeTenant`, EF configurations, named query
  filters, `SaveChangesAsync` stamping/tamper rejection, `OnValidatePrincipal` wiring,
  startup role guard.
- 1.15 — Org seed method added (code only, not yet exercised — no migration existed yet).
- 1.16 — `tests/Ways.IntegrationTests` scaffolded, `ways_app` role creation left `TODO`.
- 1.17 — Isolation test *names* stubbed with `[Fact(Skip = ...)]`, bodies not written.
- 1.18 — Regression confirmed green (30 + 14 passed, 7 skipped, 0 failed).

### Blocked

- 1.6 — Model summary presented to the user; awaiting approval.
- 1.8 — Generate migration 1. Blocked on 1.6.

Full detail of this batch's deviations and open questions is preserved below under
"Batch 1 — deferred items", since some of them are still relevant after batch 2.

---

## Batch 2 (this apply)

**Trigger:** DB CHANGE GATE #1 **approved by the user, 2026-07-31**, with three explicit
decisions recorded in `state.yaml`:

1. Migration 1 (`Organizacion`) approved exactly as presented.
2. `tenants` **also** gets RLS with the analogous policy (`ENABLE` + `FORCE` +
   `USING/WITH CHECK (app_es_plataforma() OR id_tenant = app_tenant_actual())`) — its PK
   column is literally named `id_tenant`, so the same pattern applies verbatim.
3. Seed placeholder names stay as-is; the `RolesAsignablesPor(actor, esDePlataforma)`
   interpretation from batch 1 is accepted as documented.

**Status:** `done` for 1.8 (migration generated, builds clean) and for implementing 1.17 in
full; **not runtime-verified** for the parts that need a live Postgres (1.15's seed running
for real, 1.17's assertions actually passing) because the Docker daemon is unreachable in
this environment — confirmed with `docker version` both before batch 1 and again in batch 2.

### Completed in batch 2

- **1.8** — Ran `dotnet ef migrations add Organizacion --project src/Ways.Infrastructure
  --startup-project src/Ways.Infrastructure` (the design-time factory made a real startup
  project reference unnecessary — using `Ways.Api` as `--startup-project` failed because it
  doesn't reference `Microsoft.EntityFrameworkCore.Design`, which is intentional: only
  `Ways.Infrastructure` needs it). Then hand-added, in the generated migration's `Up()`:
  `migrationBuilder.CrearFuncionesDeContextoDeTenant()` once, then
  `HabilitarRlsDeTenant("tenants")`, `HabilitarRlsDeTenant("empresas")`,
  `HabilitarRlsDeTenant("puntos_venta")` — the EF scaffolder has no way to know about this
  raw-SQL convention, so it never appears in the auto-generated body. Mirrored in `Down()`:
  the three tables drop first (which drops their policies with them), then the three
  `app_*` functions are dropped explicitly with `DROP FUNCTION IF EXISTS`.
  File: `src/Ways.Infrastructure/Persistencia/Migraciones/20260801011312_Organizacion.cs`.
- **1.16** — `WaysApiFixture` completed: `InitializeAsync` now (a) starts the Postgres
  container, (b) runs the migration directly against it using a throwaway `WaysDbContext`
  constructed with the **owner** connection string (mirrors `WaysDbContextFactory`'s
  `MapEnum` setup), (c) creates `ways_app` (`LOGIN ... NOSUPERUSER NOBYPASSRLS`) with
  data-only `GRANT`s (`SELECT, INSERT, UPDATE, DELETE` on tables, `USAGE, SELECT` on
  sequences) via a raw `NpgsqlConnection` as the owner. `ConfigureWebHost` points the API
  host's `ConnectionStrings:Ways` at `ways_app`, never at the owner — so the API under test
  runs exactly as production would: connected as a role that cannot bypass RLS.
- **1.17** — All 7 stubs became real `[Fact]` bodies in `AislamientoDeTenantTests.cs`,
  plus the tenants-RLS coverage the gate approval asked for (folded into
  `RlsBloqueaUnaLecturaQueSalteaElFiltroDeEf`, since it's the same "raw SQL / two tenants"
  setup): EF filter isolation, RLS-without-EF on both `empresas` and `tenants`,
  `WITH CHECK` rejecting a cross-tenant insert, fail-closed on an explicitly-empty GUC,
  no GUC leakage across a reused pooled connection, the ADR-15 policy-coverage query, and
  the `ways_app` role-guard query. Each test creates its own tenants (via a
  platform-mode `WaysDbContext` against the real container) instead of relying on the
  app's own seed, so tests don't collide with each other or depend on host bootstrap order.

### Verification performed this batch

- `dotnet build Ways.slnx` → **0 errors, 0 warnings** (includes the new migration file and
  the completed integration test project).
- `docker version` → client reachable, **daemon unreachable** (`failed to connect to the
  docker API at npipe:////./pipe/dockerDesktopLinuxEngine`), same result before and after
  this batch's changes — not something this batch's code caused or can fix.
- `dotnet test tests/Ways.IntegrationTests/Ways.IntegrationTests.csproj` → **0 passed, 7
  failed**, all 7 failing identically at `WaysApiFixture`'s constructor with
  Testcontainers' own `ArgumentException` ("Docker is either not running or misconfigured"),
  **not** inside any test body/assertion. This is the honest, expected state: implemented
  and gated only by the daemon, not faked green, not silently skipped.
- `dotnet test Ways.slnx` (full suite) → `Ways.Domain.Tests` 30/30, `Ways.Application.Tests`
  14/14, `Ways.IntegrationTests` 0/7 (daemon-gated, see above). **0 unexpected failures.**

### Deferred items (reported, not silent) — still open after batch 2

- `TenantActualDeSesion.Suplantar` / the `is_local: true` interceptor variant (ADR-16):
  still not implemented, still not exercised by anything in Slice 1's test surface.
  Belongs with `ServicioDeAprovisionamiento`.
- The hand-written `"Tenant"` query filter for `Usuario` (ADR-6): still deferred to Slice 2
  (`usuarios.id_tenant` doesn't exist yet).
- `OnValidatePrincipal`'s `ways:id_tenant` claim reading: still defensive/unused — no login
  path emits it yet (Slice 2).
- Seed literal values (`"Ways"` / `"Ways"` / `"Local 1"` / `"Local 2"`): confirmed to stay
  as-is by the user (gate decision 3) — no longer open, recorded here for traceability.
- The RLS policy shape for `tenants` was an open question in batch 1's report; **resolved**
  by gate decision 2 and implemented in 1.8. No longer open.

---

## Batch 3 (this apply)

**Trigger:** coordinator reported the Docker daemon came up and ran
`dotnet test tests/Ways.IntegrationTests` directly: **5 failed / 2 passed**, all real
runtime failures (not the environment failure of batch 2). Asked to reproduce, diagnose
root cause (test harness vs. production bug), fix, and get to a stable 7/7.

**Status:** `done`. Root cause confirmed as a **test-harness bug**, fixed, suite green and
stable across two consecutive runs.

### Reproduction

`docker version` now shows a reachable server (Docker Desktop 4.77.0). Ran
`dotnet test tests/Ways.IntegrationTests/Ways.IntegrationTests.csproj
--logger "console;verbosity=detailed"` myself and captured the full output (the
coordinator's earlier run had truncated the exception message). All 5 failures shared the
identical exception, thrown from `SaveChangesAsync` inside
`AislamientoDeTenantTests.CrearTenantConEmpresaAsync`:

```
Microsoft.EntityFrameworkCore.DbUpdateException : An error occurred while saving the
entity changes. See the inner exception for details.
---- Npgsql.PostgresException : 42501: new row violates row-level security policy for
table "tenants"
```

The 2 tests that passed (`WaysAppNoTieneRolsuperNiRolbypassrls`,
`LaCoberturaDePoliciesEsCompleta`) are exactly the two that never call
`CrearTenantConEmpresaAsync` — they only use `AbrirConexionCrudaAsync`, which sets the
GUC by hand via raw SQL rather than through a `WaysDbContext`.

### Root cause — confirmed test-harness bug, NOT a production bug

`WaysApiFixture.CrearContextoDeAplicacion` built a `WaysDbContext` against
`AppConnectionString` with `DbContextOptionsBuilder<WaysDbContext>().UseNpgsql(...)` but
**never called `.AddInterceptors(new InterceptorDeContextoDeTenant(tenantActual))`**.
Without the interceptor, `ConnectionOpened(Async)` never fires the `set_config` calls, so
`app.acceso` / `app.tenant_id` stay unset on every connection this helper opens — including
the ones used by the *platform-mode* seeding helper. `app_es_plataforma()` then evaluates
to `false` (GUC unset ⇒ `app_modo()` falls back to `'ninguno'`), so
`WITH CHECK (app_es_plataforma() OR id_tenant = app_tenant_actual())` rejects even the
"platform seeds its own fixture data" inserts — exactly the `42501` observed.

Checked `Ways.Infrastructure/DependencyInjection.cs` (production wiring) side by side:
line 39 does `.AddInterceptors(sp.GetRequiredService<InterceptorDeContextoDeTenant>())`
correctly, resolved from DI. **Production is not broken** — the coordinator's stated
hypothesis ("platform mode broken in production too") does not hold; the gap was isolated
to the one place in the test project that builds a `WaysDbContext` by hand instead of
through DI, and forgot to replicate that one line of wiring.

### Fix

`WaysApiFixture.CrearContextoDeAplicacion` now adds
`.AddInterceptors(new InterceptorDeContextoDeTenant(tenantActual))` to the options builder,
mirroring `DependencyInjection.AgregarInfrastructure` exactly. One-line functional change
plus a comment explaining why this specific helper needed it explicitly.

### Verification

- `dotnet build Ways.slnx` → 0 errors, 0 warnings.
- `dotnet test tests/Ways.IntegrationTests` → **7/7 green**, run twice in a row (not
  flaky): `WaysAppNoTieneRolsuperNiRolbypassrls`, `ElFiltroDeEfNuncaDevuelveFilasDeOtroTenant`,
  `NoHayFugaDeGucEntreConexionesDelPool`, `SinGucElResultadoEsCeroFilasNoUnError`,
  `RlsBloqueaUnaLecturaQueSalteaElFiltroDeEf`, `WithCheckRechazaUnInsertConIdTenantAjeno`,
  `LaCoberturaDePoliciesEsCompleta`.
- `dotnet test Ways.slnx` (full suite) → `Ways.Domain.Tests` 30/30,
  `Ways.Application.Tests` 14/14, `Ways.IntegrationTests` 7/7. **No regression, 0 failures.**

### Next batch

Slice 1 is functionally complete and verified end-to-end against real Postgres, including
RLS on all three tables. Nothing left gated on user decisions or on the environment. Ready
for judgment-day review before PR (per `CLAUDE.md`'s PR validation gate), then Slice 2.

---

## Batch 4 — judgment-day round 1 fixes

**Trigger:** first judgment-day round (dual blind review) over the batch 1–3 diff surfaced
2 confirmed WARNINGs and 7 approved hardening items. All 9 approved by the user
2026-07-31; this batch applies them.

### Completed in batch 4

1. **`SaveChanges` coverage** — `WaysDbContext` now overrides all four public save entry
   points (`SaveChanges()`, `SaveChanges(bool)`, `SaveChangesAsync(CancellationToken)`,
   `SaveChangesAsync(bool, CancellationToken)`); all four call `EstamparTenant()` before
   delegating to `base`. New sync test
   `FiltroDeTenantTests.SaveChangesSyncEstampaElIdTenantYRechazaElTamper` proves the sync
   path stamps and rejects tamper the same as the async one.
2. **Connection invariants guard (ADR-3)** — new `InvariantesDeConexion` static helper
   (`Ways.Infrastructure/Persistencia/InvariantesDeConexion.cs`) inspects a connection
   string via `NpgsqlConnectionStringBuilder` for `Multiplexing`/`NoResetOnClose`.
   `InicializadorDeBaseDeDatos.VerificarInvariantesDeConexion` calls it alongside the
   existing role guard: throws in Production, warns otherwise. 3 new unit tests
   (`InvariantesDeConexionTests`) cover the pure function directly.
3. **Seeder keyed filter** — the three bare `IgnoreQueryFilters()` calls in
   `InicializadorDeBaseDeDatos` (roles, root user, tenants) now use
   `IgnoreQueryFilters(["BajaLogica"])` explicitly, so a future named filter can't be
   silently skipped.
4. **Platform-mode stamping validation** — `WaysDbContext.EstamparTenant()` now throws if
   an `Added` `EntidadTenant` row in platform mode still carries `IdTenant == 0`
   (unset), instead of relying on the FK shape to catch it downstream.
5. **`TenantActualFijo` guard** — its constructor now validates `Tenant` mode requires a
   non-null id, mirroring `TenantActualDeSesion.Establecer`.
6. **UPDATE reassignment RLS test** — new
   `AislamientoDeTenantTests.WithCheckRechazaUnUpdateQueReasignaIdTenant` targets a
   raw-SQL `UPDATE ... SET id_tenant = <otro tenant>` and asserts a `PostgresException`.
   (Correction in batch 5: this initial version used the wrong PK column, so it passed
   vacuously on a 42703 error before RLS ever evaluated — see batch 5.)
7. **Seeder uses `TenantActualFijo`** — `InicializadorDeBaseDeDatos` no longer depends on
   the mutable `TenantActualDeSesion`/`.Establecer(...)`. `DependencyInjection` now
   registers a keyed (`ClaveContextoPlataforma = "plataforma"`) scoped `WaysDbContext`
   bound to the immutable `TenantActualFijo.Plataforma`, resolved via
   `[FromKeyedServices(...)]` in the initializer's constructor — the DI wiring change
   ADR-2 asked for, without touching the request-scoped registration used everywhere
   else.
8. **Sync interceptor path** — `InterceptorDeContextoDeTenant.ConnectionOpened` now runs
   Npgsql's synchronous `ExecuteNonQuery()` directly instead of
   `.GetAwaiter().GetResult()` over the async path.
9. **403 → 404 for platform targets** — `PoliticaDeRoles.ValidarAlcanceDeTenant` now
   throws `NoEncontrado` (404), not `Prohibido` (403), when a tenant actor targets a
   platform-scoped account, unifying with the cross-tenant rule (ADR-8).
   `PoliticaDeRolesTenantTests.UnAdminNoPuedeGestionarUnaCuentaDePlataforma` updated to
   assert 404. `design.md`'s ADR-8 got a one-line note extending the rule to
   platform-scoped targets.

### Verification performed this batch

- `dotnet build Ways.slnx` → 0 errors, 0 warnings.
- `dotnet test Ways.slnx` → `Ways.Domain.Tests` 30/30, `Ways.Application.Tests` 18/18
  (14 + 4 new: 1 sync `SaveChanges` test + 3 `InvariantesDeConexion` tests),
  `Ways.IntegrationTests` 8/8 (7 + 1 new UPDATE-reassignment test). 0 failures, Docker
  daemon reachable throughout.

### Completed in batch 5 (judgment-day round 2 fix)

1. **Vacuously-green RLS UPDATE test, fixed** — round 2 of judgment-day found that
   `WithCheckRechazaUnUpdateQueReasignaIdTenant`'s SQL targeted `WHERE id = $2`, but the
   `empresas` PK column is `id_empresa`. The statement died with `42703` (undefined
   column) before RLS ever evaluated, so the test asserted only `PostgresException` and
   passed for the wrong reason — it did not actually prove `WITH CHECK` rejects the
   `UPDATE`. Fixed to `WHERE id_empresa = $2`, and both this test and
   `WithCheckRechazaUnInsertConIdTenantAjeno` now assert `PostgresException.SqlState ==
   "42501"` (insufficient_privilege), so no future schema typo can make either test pass
   vacuously again. Re-run against real Postgres confirms both now fail with `42501` —
   `WITH CHECK` genuinely rejects the raw-SQL `UPDATE`.

### Next batch

Ready for a re-judged clean round, then PR per `CLAUDE.md`'s PR validation gate.

---

## Slice 2: usuarios retrofit + suspension + mail login (PR 2)

## Batch 6

**Scope:** Slice 2 only (`tasks.md` § Slice 2), branch `feat/stage1-slice2-usuarios` off
`main` (slice 1 / PR 1 already merged). Stopped at **DB CHANGE GATE #2** (task 2.1) as
instructed. No EF Core migration was generated or applied — `usuarios.id_tenant` exists in
the C# model (`Usuario`, `UsuarioConfiguration`) but not yet in any migration file.

### Completed in batch 6

- **2.4** — `ServicioDeAutenticacion.IniciarSesionAsync` now resolves by `Mail` instead of
  `Usuario` (`SolicitudDeLogin(Mail, Password)`). Anti-enumeration behavior preserved
  byte-for-byte: same error message/code for unknown mail and wrong password, dummy-hash
  verification runs either way, account-state checks stay strictly after password
  verification.
- **2.5** — Suspended/baja-tenant check added right after the `DeletedAt` check, before
  `Bloqueado`/`Inactivo`. Resolved via a **second, platform-mode `IWaysDbContext`**
  (`[FromKeyedServices(ClavesDeContexto.Plataforma)]`, new keyed registration in
  `Ways.Infrastructure.DependencyInjection`) instead of the request's own login-mode
  context — this was a genuine design gap found at apply time: `ModoDeAcceso.Login`'s RLS
  policies (design.md) only grant `usuarios` SELECT/UPDATE, nothing on `tenants`, so reading
  the tenant's `Estado` during login needs a session that's actually allowed to see it.
  Chose the existing platform-mode keyed context (already used by
  `InicializadorDeBaseDeDatos`) over adding a new `tenants_login_lectura` RLS policy — no
  new anonymous-reachable read surface, reuses an already-trusted internal path. New shared
  constant `Ways.Application.Abstracciones.ClavesDeContexto.Plataforma` (Application can't
  reference `Infrastructure.DependencyInjection`'s existing constant, so both sides declare
  the same literal `"plataforma"` on purpose — documented in both places). Error code
  `tenant_suspendido`, 403, covers both `Suspendido` and `Baja` (via `Tenant.PuedeOperar`,
  reused from slice 1).
- **2.6** — `PoliticaDeRoles.ValidarConsistenciaDeRolYAlcance(rolDestino, idTenantDestino)`
  added (pure, domain-only): root must have `idTenant == null`, every other role must have
  it non-null. Wired into `ServicioDeUsuarios.CrearAsync` (computes the target tenant: the
  actor's own tenant if tenant-scoped, an explicit `CrearUsuario.IdTenant` if the actor is
  platform — client-supplied tenant is only ever trusted from a platform actor, never from a
  tenant one) and `ActualizarAsync` (when the role changes). Closed the judgment-day
  carried-forward INFO from slice 1: `PoliticaDeRoles.ValidarAlcanceDeTenant`/
  `ActorDeGestion` had no production call site — now wired into
  `ServicioDeUsuarios.BuscarAsync`, the single choke point behind
  `ObtenerAsync`/`ActualizarAsync`/`CambiarPasswordAsync`/`DesbloquearAsync`/`EliminarAsync`.
  In practice this call is defense-in-depth (the EF "Tenant" filter + RLS already make a
  cross-tenant row invisible before the domain check even runs), which is the point: three
  independent layers, not one.
  **Bug caught while wiring this**: `ListarAsync`'s `incluirEliminados` branch called bare
  `IgnoreQueryFilters()` (no filter keys) — harmless before this slice (`Usuario` had no
  tenant filter), but a real cross-tenant leak the moment `Usuario` gets one. Fixed to
  `IgnoreQueryFilters(["BajaLogica"])`, same pattern already used elsewhere in the codebase
  (`InicializadorDeBaseDeDatos`, judgment-day batch 4).
  `ExigirDisponibilidadAsync`'s `usuario`-uniqueness pre-check was also re-scoped to
  `IdTenant` (was a global check, which would have wrongly rejected the "two tenants both
  have an `admin`" case the spec requires to work).
- **2.7** — `Ways.Web`: `Login.tsx`'s field is now `type="email"`, `name="mail"`, labeled
  "Correo electrónico"; `AuthContext.iniciarSesion(mail, password)` posts `{ mail, password
  }`; `tipos.ts`'s `UsuarioAutenticado` gained `idTenant: number | null`. `npx tsc -b` and
  `npx oxlint` both clean (the one oxlint warning on `AuthContext.tsx`,
  `only-export-components`, pre-dates this change — same file shape as before, not
  introduced here).
- **Domain** — `Usuario.IdTenant` (`int?`, `NULL` = plataforma, doc 09/ADR-1). Explicitly
  does **not** inherit `EntidadTenant` (unchanged design decision from design.md).
- **Infrastructure (EF model, no migration yet)** —
  `UsuarioConfiguration`: `id_tenant` column mapping, FK to `tenants` (`Restrict`),
  `ux_usuarios_usuario` rebuilt as `(id_tenant, usuario)` with `.AreNullsDistinct(false)`
  (confirmed this Npgsql EF Core provider fluent method exists and compiles — Postgres 15+
  `NULLS NOT DISTINCT`, pinned Postgres 17), new `ix_usuarios_tenant` index.
  `WaysDbContext`: new hand-written `"Tenant"` named query filter for `Usuario`
  (`AplicarFiltroDeTenantEnUsuario`) — three-way OR: platform sees everything, **login
  mode sees everything** (the whole reason mail-based login can resolve *any* tenant's
  account without a tenant in context yet), a tenant session sees only same-`IdTenant` rows.
  New keyed `IWaysDbContext` registration (`ClavesDeContexto.Plataforma`) alongside the
  existing keyed `WaysDbContext` one.

### A real bug the new tests caught (not a production bug — a filter bug, fixed before merge)

While writing `FiltroDeUsuarioTests`/`ServicioDeAutenticacionTests` (InMemory), a genuine
issue turned up in `AplicarFiltroDeTenantEnUsuario`: under `ModoDeAcceso.Ninguno` (no
context resolved — the fail-closed state), `TenantActual.Id` is `null`. A platform user's
`Usuario.IdTenant` is also `null`. The raw comparison `IdTenant == TenantActual.Id` is then
`null == null` → **true** in C#'s lifted equality — so an unresolved session would have seen
every platform-staff account instead of nothing, breaking the documented fail-closed
guarantee ("unset GUC ⇒ zero rows, not everything"). Fixed by gating the comparison branch
on `TenantActual.Modo == ModoDeAcceso.Tenant` explicitly:
`esTenant && (IdTenant == TenantActual.Id)`. `Tenant`/`Empresa`/`PuntoVenta`'s existing
filters were never at risk of this — their compared column is never itself `NULL` (a real
tenant always has an id), so this collision is specific to `Usuario`'s nullable `IdTenant`.
New regression test `FiltroDeUsuarioTests.SinContextoResueltoNoVeNingunaCuenta` pins it down.

Also chased down (and ruled out as a real bug) an EF Core InMemory-provider red herring
while debugging the above: `.Include(u => u.Rol)` on a `Usuario` whose `RolId` doesn't
match any seeded `Rol` row silently drops the whole result row under InMemory (non-nullable
FK treated as effectively required for `Include` purposes, unlike a real LEFT JOIN in
Postgres). Cost real time to isolate; not a change to production code — the fix was seeding
a `Rol` row in the test setup, same as production always has via
`InicializadorDeBaseDeDatos.SembrarRolesAsync` before any `Usuario` exists.

### A real EF Core 8+ gate interaction, handled explicitly (not silently)

Running the *existing* (slice 1) `Ways.IntegrationTests` suite against real Postgres with
this batch's Infrastructure changes applied — but no migration — surfaced
`PendingModelChangesWarning`: EF Core 8+'s `Database.MigrateAsync()` compares the live model
against the last migration's snapshot and **throws by default** if they differ, which they
now genuinely do (`usuarios.id_tenant` is in the model, not in any migration). This is the
documented, expected interaction for "model ahead of migrations mid-development," and
exactly the state the DB CHANGE GATE puts a slice in on purpose. Fixed by suppressing that
specific warning **only** in `WaysApiFixture.MigrarComoOwnerAsync` (test fixture, not
production) via `ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))`,
with a comment explaining why and that it must come out once migration 2 lands (at that
point model and snapshot agree again and the warning stops firing on its own — nothing to
remember to revert by hand). Confirmed safe: none of the 8 existing tests touch `usuarios`
against real Postgres (they only exercise `Tenants`/`Empresas`), so applying migrations 1–3
with the pre-slice-2 `usuarios` shape and running those 8 tests unmodified is exactly as
safe as it was before this batch.

### Verification performed this batch

- `dotnet build Ways.slnx` → 0 errors, 0 warnings.
- `dotnet test Ways.slnx` → `Ways.Domain.Tests` **38/38** (30 unedited + 8 new:
  `ValidarConsistenciaDeRolYAlcance` scenarios), `Ways.Application.Tests` **28/28** (18
  unedited + 10 new: 5 `FiltroDeUsuarioTests` + 5 `ServicioDeAutenticacionTests`, both
  InMemory, exercise the mail-login/suspension/tenant-filter logic without needing the
  pending migration), `Ways.IntegrationTests` **8/8 unedited, green** + **7 new tests,
  `Skip`**, all pointing at gate #2 (`UsuariosYLoginTests.cs` — mail login for tenant/root,
  anti-enumeration, suspension blocks login, suspension cuts an active session,
  cross-tenant `usuario = "admin"` collision-free proof, platform `NULLS NOT DISTINCT`
  duplicate-rejection proof). Docker daemon reachable throughout
  (`docker version` confirmed, Testcontainers ran against it). 0 unexpected failures.
- `npx tsc -b` (Ways.Web) → clean. `npx oxlint` → 1 pre-existing warning, unrelated to this
  batch's changes (file export shape, not touched here).

### Deferred items (reported, not silent)

- **2.1–2.3** — blocked on DB CHANGE GATE #2 approval, per instructions. The gate summary is
  the centerpiece of this batch's return to the user.
- Backfill of *existing* pre-migration `usuarios` rows (task 2.3) is not yet written — only
  possible to design concretely once the migration exists (needs the real column to write
  into). `SembrarRootAsync` already leaves new roots at `IdTenant = null` by construction, so
  no change was needed there.
- `CrearUsuario.IdTenant` (root selecting a target tenant when creating a non-root user) has
  no `Ways.Web` UI yet — by design: tenant provisioning (`ServicioDeAprovisionamiento`, ADR-16)
  is still deferred to a later slice, and building a tenant-picker for an ABM whose
  provisioning flow doesn't exist yet would be dead UI. The API contract is ready for when
  it lands.

### Next batch

Slice 2 is code-complete up to the gate. Once DB CHANGE GATE #2 is approved: generate
migration 2, backfill existing `usuarios` rows (task 2.3), un-skip the 7 gated integration
tests in `UsuariosYLoginTests.cs` and confirm they pass for real against Postgres, then
judgment-day review before PR 2.

---

## Batch 7 — DB CHANGE GATE #2 approved, slice 2 runtime-verified

**Trigger:** the user approved gate #2 on 2026-08-01, exactly as presented (see the gate
summary in batch 6): `id_tenant` column + FK, per-tenant unique index with
`NULLS NOT DISTINCT`, RLS standard policy plus the two login-mode policies on `usuarios`,
and the backfill policy. Asked to: generate migration 2 including the backfill, un-skip and
green the 7 gated integration tests (target: 38 domain / 28 application / 15/15 integration
incl. slice 1's 8), remove the `PendingModelChangesWarning` suppression if no longer needed,
commit as work units, update the SDD artifacts.

**Status:** `done`. Full suite green — **38 domain / 28 application / 15/15 integration**
(stable across 3 consecutive runs) — with three genuine, previously-latent bugs found and
fixed along the way (none hidden, all reported below).

### Completed in batch 7

- **2.2** — Migration `UsuariosMultiTenant` generated
  (`dotnet ef migrations add UsuariosMultiTenant`), then hand-added the RLS calls the
  scaffolder can't produce (same technique as migration 1): `HabilitarRlsDeTenant("usuarios")`
  plus `CREATE POLICY usuarios_login_lectura`/`usuarios_login_actualiza` in `Up()`; matching
  `DROP POLICY` × 3 + `NO FORCE`/`DISABLE ROW LEVEL SECURITY` in `Down()` (the table existed
  without RLS before this migration, so `Down()` restores exactly that). File:
  `src/Ways.Infrastructure/Persistencia/Migraciones/20260801154718_UsuariosMultiTenant.cs`.
- **2.3** — `InicializadorDeBaseDeDatos.BackfillDeUsuariosAsync` added: assigns the lowest-id
  tenant to every `Usuario` row with `IdTenant == null && RolId != Root`, after
  `SembrarOrganizacionAsync`. Idempotent by construction (a fresh install only has `root` at
  that point, filtered out by the role check; a redeploy finds nothing left to backfill).
- The `PendingModelChangesWarning` suppression added in batch 6 (`WaysApiFixture`, test-only)
  is **removed** — model and migration snapshot agree again now that migration 2 exists, so
  the warning no longer fires. Confirmed by running the full integration suite clean without it.
- The 7 tests in `UsuariosYLoginTests.cs` are un-skipped and green against real Postgres.

### Three real bugs found while getting from "compiles" to "actually green against Postgres"

None of these were visible before this batch because **no test in this project had ever
booted the real API host** (`WebApplicationFactory.CreateClient()`) against a live database —
confirmed as a known gap in `state.yaml`'s carried-forward notes, and now closed. All three
are genuine, all three are fixed, none were worked around silently:

1. **`Database.SqlQuery<T>()` crashes with `IndexOutOfRangeException`** inside EF Core's
   `NavigationExpandingExpressionVisitor`, for *any* raw-SQL query against this project's
   model (proven by reproducing it identically on `main`, via a temporary `git worktree`,
   before touching slice 2's own filter — this is a pre-existing bug, not something slice 2
   introduced). It broke `InicializadorDeBaseDeDatos.VerificarRolSinBypassAsync` (the
   `rolsuper`/`rolbypassrls` startup guard, ADR-5) the moment any test finally exercised real
   app startup. Root-caused to `SqlQuery<T>()` specifically (plain LINQ queries against the
   same model work fine) via a battery of isolated repro tests. Fixed by rewriting that one
   method with plain ADO.NET (`db.Database.GetDbConnection()` + a raw command), which never
   enters that LINQ pipeline at all.
2. **`OnValidatePrincipal` checked account validity before resolving the session's tenant.**
   Once `Usuario` got its own tenant filter (batch 6) — which fails closed under
   `ModoDeAcceso.Ninguno`, the default before the tenant is resolved — the very first check in
   `OnValidatePrincipal` (`db.Usuarios.AnyAsync(u => u.Id == usuarioId && ...)`) always saw
   zero rows for *any* tenant-scoped account, silently rejecting every tenant session on the
   request right after login. Fixed by reordering: resolve the mode/tenant from the
   already-decrypted cookie claims **first** (`ResolverModoDeLaSesionAsync`, renamed from
   `ResolverTenantDeLaSesionAsync`), then check account validity under the now-correct
   context — which also makes that check tenant-scoped as a bonus defense layer (ADR-8).
3. **The integration test fixture's connection-string override never reached `Program.cs`.**
   `Program.cs` is minimal hosting (`WebApplication.CreateBuilder`) and reads
   `builder.Configuration` synchronously inside its own top-level statements
   (`AgregarInfrastructure`), before `WebApplicationFactory` gets a chance to apply
   `ConfigureWebHost`'s `ConfigureAppConfiguration` override — confirmed with a temporary log
   line showing the stale `appsettings.json` value (`localhost:5432`) instead of the
   container's. Fixed by setting `ConnectionStrings__Ways` as a **process environment
   variable** in `WaysApiFixture.InitializeAsync`, which `WebApplication.CreateBuilder` reads
   fresh when `Program.Main` actually runs (deferred until the first `CreateClient()`). That
   surfaced a second issue — the env var is process-global, and xUnit runs different test
   classes' fixtures in parallel by default, so two `WaysApiFixture` instances (one per test
   class) raced to overwrite it, and whichever class booted its host second sometimes
   connected to the *other* class's already-destroyed container. Fixed with a new
   `[CollectionDefinition("Ways.IntegrationTests secuencial", DisableParallelization = true)]`
   applied to both `AislamientoDeTenantTests` and `UsuariosYLoginTests`, forcing them to run
   sequentially. A third, smaller issue in the same area: `ways_app` lacked `CREATE` on schema
   `public`, so `InicializadorDeBaseDeDatos`'s always-runs-on-boot `Database.MigrateAsync()`
   failed with `42501` even though nothing was actually pending — Postgres requires `CREATE`
   to even *attempt* `CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory"`, existing or not.
   Granted `CREATE` alongside the existing data grants (documented as not weakening the RLS
   proof, which depends on `NOSUPERUSER`/`NOBYPASSRLS`, and as arguably more representative of
   production, where ADR-5 already documents the app role as the table owner).

Two test-only bugs (not production) were also found and fixed while un-skipping
`UsuariosYLoginTests.cs`: seed helpers that inserted a `Usuario` before any `Rol` row existed
(`fk_usuarios_rol` violation — fixed by booting the host first, which seeds roles), a global
`usuario = "admin"` count assertion made fragile by other tests in the same class sharing one
Postgres instance (fixed by scoping the count to the test's own two tenants), and a raw-string
`ProblemDetails` body comparison that could never match because of the per-request `traceId`
(fixed by comparing `title`/`codigo` instead of the whole JSON body).

### Verification performed this batch

- `dotnet build Ways.slnx` → 0 errors, 0 warnings.
- `dotnet test Ways.slnx` → **`Ways.Domain.Tests` 38/38, `Ways.Application.Tests` 28/28,
  `Ways.IntegrationTests` 15/15** (8 unedited slice-1 tests + 7 slice-2 tests, all real
  Postgres). 0 failures, 0 skipped. Re-ran `Ways.IntegrationTests` two additional times in a
  row to confirm stability (not flaky): 15/15 both times.
- Confirmed bug #1 (`SqlQuery<T>` crash) pre-dates this slice: reproduced identically against
  `main` via a temporary `git worktree`, before attributing it to slice 2's own changes.

### Commits (work units, branch `feat/stage1-slice2-usuarios`, no push)

1. `feat(usuarios): generar la migracion UsuariosMultiTenant (gate #2 aprobada)` — migration
   in its own commit, as requested.
2. `feat(usuarios): backfill de id_tenant y arreglo de un bug real en el guard de rol`
3. `fix(auth): resolver el tenant de la sesion antes de revisar la cuenta`
4. `fix(integration-tests): arrancar el host real de la API contra el contenedor`
5. `test(integration): habilitar las 7 pruebas de login/suspension (gate #2)`
6. `docs(sdd): registrar el batch 7 (gate #2 aprobada, slice 2 verificado en runtime)`

### Batch 8 — judgment-day round 1 on Slice 2 (branch `feat/stage1-slice2-usuarios`)

All 7 items approved by the user on 2026-08-01 after the blind dual-review round were fixed:

1. **[CRITICAL] Mail-uniqueness check broken by tenant filter.** `ExigirDisponibilidadAsync`
   ran the `tomadoMail` query on tenant-filtered `db.Usuarios`: a tenant admin creating/editing
   a user could never see a mail collision belonging to another tenant, so the check silently
   passed and the real conflict only surfaced at `SaveChangesAsync` as an untranslated `23505`
   — a generic 500, and a cross-tenant enumeration oracle (409 same-tenant vs 500 other-tenant
   distinguished where the mail lived). Fixed at both layers: (a) the mail check now runs with
   `IgnoreQueryFilters(["Tenant"])` — `usuario` stays per-tenant, only `mail` (globally unique
   per `ux_usuarios_mail`) goes global; (b) `ManejadorDeErrores` now has a backstop case that
   translates `DbUpdateException { InnerException: PostgresException { SqlState: "23505",
   ConstraintName: "ux_usuarios_mail" } }` into the same domain 409 (`mail_duplicado`), so a
   genuine race between two concurrent creates can never surface a 500/oracle either.
2. **`ServicioDeUsuariosTests` added** (`Ways.Application.Tests`, InMemory, mirrors
   `ServicioDeAutenticacionTests`): own-tenant management OK, cross-tenant target → 404,
   platform-account target → 404, `ValidarConsistenciaDeRolYAlcance` rejection on create,
   per-tenant `usuario` duplicate rejected, same `usuario` across two tenants OK, and the
   CRITICAL regression case — cross-tenant mail duplicate → 409. Plus a real-Postgres
   integration test (`UsuariosYLoginTests.CrearUnUsuarioConElMailDeOtroTenantDevuelve409NoUnError500`)
   that creates a user in tenant A, then tries the same mail from an admin logged into tenant B
   through the full HTTP API, asserting 409/`mail_duplicado`.
3. **Login timing symmetry.** `ServicioDeAutenticacion` no longer calls
   `hasheador.Hashear("usuario-inexistente")` on every unknown-mail attempt (a second,
   expensive `Hashear` on top of the `Verificar`, breaking the timing symmetry with the
   known-mail path). It now lazily computes that discardable hash once (thread-safe,
   `static` field — `IHasheadorDeContrasenas` is a singleton) and reuses it, so the
   unknown-mail path costs exactly one `Verificar` after warm-up, same as the known-mail path.
4. **Login-with-existing-cookie integration test added**
   (`UsuariosYLoginTests.LoguearseConUnaCookieDeOtroTenantYaActivaReemplazaLaSesionPorCompleto`):
   logs in as tenant A, then — same `HttpClient`/cookie, no logout — logs in as tenant B;
   asserts the second login succeeds and `/api/auth/me` reflects the new session, not the old
   one. Locks in the ADR-3 GUC-reapplication assumption at runtime.
5. **`Usuario.IdTenant` tamper guard.** `Usuario` doesn't derive from `EntidadTenant` (its
   `IdTenant` is nullable = platform), so it fell outside `WaysDbContext.EstamparTenant`'s
   `ChangeTracker.Entries<EntidadTenant>()` loop. Added an explicit second loop over
   `ChangeTracker.Entries<Usuario>()` that rejects a `Modified` entry whose `IdTenant` changed,
   mirroring the `EntidadTenant` check exactly (same exception, same message). Unit-tested in
   `FiltroDeUsuarioTests.SaveChangesRechazaModificarElIdTenantDeUnUsuarioExistente`.
6. **Keyed-DI constant unification.** `DependencyInjection.ClaveContextoPlataforma` now reads
   `= ClavesDeContexto.Plataforma` instead of repeating the `"plataforma"` literal
   independently (Infrastructure can reference Application, not the other way around) — a typo
   in either constant is now a compile error, not a silent keyed-DI resolution mismatch.
7. **Stale comments fixed.** `PoliticaDeRoles`'s class-level "Reglas vigentes" doc now lists
   the tenant-scoping rule and the 404-not-403 rule (ADR-8), previously undocumented there.
   `TenantActualDeSesion`'s doc comment now names both mutators (`OnValidatePrincipal` *and*
   `AuthEndpoints`, the login endpoint) and explains why re-applying tenant impersonation on an
   already-open connection isn't needed yet: both mutators run before any connection opens,
   and each EF query opens its own (no ambient transaction crossing them).

#### Verification performed this batch

- `dotnet build Ways.slnx` → 0 errors, 0 warnings.
- `dotnet test Ways.slnx` → **`Ways.Domain.Tests` 38/38, `Ways.Application.Tests` 36/36**
  (28 previous + 8 new: 7 in `ServicioDeUsuariosTests` + 1 tamper-guard test in
  `FiltroDeUsuarioTests`), **`Ways.IntegrationTests` 17/17** (15 previous + 2 new: the
  existing-cookie login test and the cross-tenant mail 409 test), all against a real Postgres
  container (Docker daemon confirmed up before running). 0 failures, 0 skipped.

#### Commits (work units, branch `feat/stage1-slice2-usuarios`, no push)

1. `fix(usuarios): chequear la unicidad del mail sin el filtro de tenant`
2. `fix(api): traducir el 23505 de ux_usuarios_mail al 409 de negocio`
3. `test(usuarios): cubrir ServicioDeUsuarios con InMemory y el 409 cross-tenant` — bundles
   `ServicioDeUsuariosTests.cs` (item 2) with both new cases added to `UsuariosYLoginTests.cs`
   in the same file edit: the cross-tenant-mail 409 integration test (item 2's Postgres case)
   and the existing-cookie re-login test (item 4).
4. `fix(auth): precalcular el hash descartable del login para simetria de tiempos`
5. `fix(tenancy): rechazar el tamper de id_tenant en Usuario`
6. `fix(di): unificar la clave de contexto de plataforma en una sola constante`
7. `docs: actualizar comentarios desactualizados de PoliticaDeRoles y TenantActualDeSesion`
8. `docs(sdd): registrar el batch 8 de judgment-day (ronda 1) en el slice 2`

### Batch 9 — judgment-day round 2 on Slice 2 (branch `feat/stage1-slice2-usuarios`)

Two independent blind review agents re-judged the round-1 diff; the orchestrator triaged
contradictions with direct code verification. 9 items confirmed/approved:

1. **[CRITICAL] Tamper guard broke the backfill.** `WaysDbContext.EstamparTenant` rejected
   ANY `Modified` `Usuario` whose `IdTenant` changed, no matter the reason — but
   `InicializadorDeBaseDeDatos.BackfillDeUsuariosAsync` is the one legitimate NULL→value
   mutation of that column in the whole system, and it made that mutation by loading the rows
   and assigning the property through the `ChangeTracker`. Every real upgrade with a
   pre-existing non-root account crashed the host on boot; a fresh install escaped the bug
   because `huerfanos.Count == 0`, which is why nothing had caught it. Fixed by rewriting the
   backfill as a set-based `ExecuteUpdateAsync` — chosen over narrowing the guard to
   `OriginalValue != null` because it never touches the `ChangeTracker` at all, so it can't
   trip the guard without opening a loophole in it; the guard stays maximally strict for every
   other `Modified` path, consistent with `EstamparTenant`'s defense-in-depth intent. Still
   passes RLS: it runs in platform mode, and `usuarios_tenant`'s `WITH CHECK (app_es_plataforma()
   OR ...)` lets any `id_tenant` value through under that mode. Regression test added:
   `InicializadorDeBaseDeDatosTests.ElBackfillNoRompeElArranqueYAsignaElTenant1AUnaCuentaHuerfana`
   (real Postgres) — seeds a non-root `Usuario` with `IdTenant == null` via EF *before* the
   host's first boot (the only moment `EjecutarAsync`, and so the backfill, runs), asserts the
   host boots without throwing and the account ends up in tenant 1.
2. **DB-write timing asymmetry in login.** `ServicioDeAutenticacion.IniciarSesionAsync`:
   known-mail-wrong-password persists `RegistrarIntentoFallido` (an extra UPDATE round trip);
   unknown-mail did no DB write at all, on top of already sharing the same hashing cost since
   batch 8. Fixed by adding an equivalent no-op round trip on the unknown-mail path —
   `db.Usuarios.AsNoTracking().AnyAsync(u => u.Id == -1, ct)`, a single cheap SELECT that pays
   the same network round-trip cost without writing or filtering anything real. Preserves
   lockout semantics exactly (no change to `RegistrarIntentoFallido`/threshold logic).
3. **Missing 23505 backstop for the `usuario` index.** `ManejadorDeErrores` only had the
   backstop case for `ux_usuarios_mail`. Added the symmetric case for
   `ConstraintName == "ux_usuarios_usuario"` → 409 `usuario_duplicado`, same shape as the
   existing mail case.
4. **[Triaged real] Mail pre-check was a no-op under RLS for tenant actors.**
   `ServicioDeUsuarios.ExigirDisponibilidadAsync` used `IgnoreQueryFilters(["Tenant"])` for the
   mail-availability check, but that only disables the EF query filter — the `usuarios_tenant`
   RLS policy still hides other tenants' rows under `app.acceso='tenant'`, so a tenant actor's
   cross-tenant mail collision was ALWAYS caught by the `ManejadorDeErrores` exception backstop,
   never by the pre-check, contradicting the inline comment (and the batch-8 fix, which
   addressed the symptom — cross-tenant collisions returning 409 instead of 500 — without
   actually making the pre-check see the row). Fixed by running the mail check through a new
   platform-keyed `IWaysDbContext dbPlataforma` constructor parameter on `ServicioDeUsuarios`
   (same pattern as the tenant-suspension check in `ServicioDeAutenticacion`), which RLS lets
   see any tenant. Comment corrected to explain why `IgnoreQueryFilters` alone doesn't cut it.
   The per-tenant `usuario` uniqueness check is untouched (still scoped to the request's own
   tenant context, correctly).
5. **[Approved hardening] Backstop race coverage.** The existing cross-tenant-mail 409 test
   seeds the colliding account *before* creating the second one, so after item 4's fix it
   always goes through the pre-check, never the exception backstop. Added
   `UsuariosYLoginTests.DosAltasConcurrentesConElMismoMailDisparanElBackstopDelSaveChanges`:
   two `POST /api/usuarios` with the same mail fired concurrently (`Task.WhenAll`) from two
   different tenant admins — both pre-checks race before either commits, so both pass, and the
   real `ux_usuarios_mail` 23505 surfaces on whichever `SaveChangesAsync` loses, which
   `ManejadorDeErrores` translates. Asserted the DB-level invariant (exactly one 201, one 409
   `mail_duplicado`) rather than which code path caught it, since that invariant holds
   regardless of the exact interleaving — confirmed stable across two full suite re-runs, and
   the EF `DbUpdateException`/`PostgresException 23505` trace is visible in the test log,
   confirming the backstop path does fire in practice.
6. **[Approved hardening] `usuarios` RLS raw-SQL isolation tests.** New
   `UsuariosRlsTests.cs`, mirroring `AislamientoDeTenantTests.RlsBloqueaUnaLecturaQueSalteaElFiltroDeEf`
   for the one table with the extra login-mode policies: tenant-mode raw connection can't
   read or update another tenant's `usuarios` row (`RlsBloqueaLeerYActualizarUnUsuarioDeOtroTenant`,
   update case asserts 0 rows affected, not an exception — RLS filters the row out of the
   UPDATE's visible set, it doesn't reject a `WITH CHECK` on a row it could see); can't read a
   platform account (`id_tenant IS NULL`) either (`RlsBloqueaLeerUnaCuentaDePlataformaDesdeUnaSesionDeTenant`);
   and the two login-mode policies (`app_modo() = 'login'`) don't leak visibility outside
   login mode — with no GUC set at all, `usuarios_tenant` fails closed and neither login policy
   applies, so the count is exactly 0 (`LasPoliciesDeLoginNoAplicanFueraDeModoLogin`). Plus one
   HTTP-level test in `UsuariosYLoginTests.cs`:
   `UnAdminDeUnTenantRecibe404AlConsultarUnUsuarioDeOtroTenant` — tenant-A admin gets 404 on
   `GET /api/usuarios/{tenant-B-user-id}` through the full stack (EF filter + RLS +
   `PoliticaDeRoles.ValidarAlcanceDeTenant` all engaged at once).
7. **[Approved hardening] Doc 08 alignment.** `docs/08-usuarios-y-login.md` still documented
   `{ usuario, password }` login and the pre-tenant schema (no `id_tenant`, global
   `ux_usuarios_usuario`). Updated: login section now describes `{ mail, password }` and links
   to the `usuarios-y-login` openspec spec for the full contract; schema snippet now has
   `id_tenant integer NULL REFERENCES tenants(id_tenant)`, the composite
   `(id_tenant, usuario) NULLS NOT DISTINCT` unique index (with a short why-comment), and
   `ix_usuarios_tenant`, plus a pointer to the `usuarios-tenant-scoping` spec for the full
   rationale and a short paragraph on the RLS/login-mode policies.
8. **[Approved hardening] Lazy hash.** Replaced the hand-rolled double-checked locking
   (`_hashDescartable` + `lock`) with `LazyInitializer.EnsureInitialized` over a
   `Lazy<string>?` field, `Lazy<T>`'s default `ExecutionAndPublication` mode giving the same
   thread-safety guarantee without hand-writing it. The `Lazy<string>` itself can't be a plain
   static field constructed inline because its factory needs the instance-scoped `hasheador`
   dependency (not static) — so it's published once, cold, via `LazyInitializer` on a static
   field; `hasheador` always resolves to the same DI singleton no matter which
   `ServicioDeAutenticacion` instance wins the race to initialize it (unchanged assumption from
   batch 8, now just documented explicitly in the new doc comment).
9. **[Approved hardening] Added-state `Usuario` clarity.** Between extending the
   `EstamparTenant` `Usuario` loop to validate `Added` entries or documenting why it's exempt,
   chose the comment: unlike `EntidadTenant`, `Usuario`'s `Added` `IdTenant` is derived from
   `ActorDeGestion.IdTenant` (`ServicioDeUsuarios.CrearAsync`, doc 09 ADR-8) — a trusted
   identity value deliberately kept separate from the connection's `TenantActual` — so there's
   no single connection-scoped value to stamp or validate against without duplicating that
   business rule in the persistence layer. Validating would also need to special-case `NULL`
   as always-legal (root/platform accounts), unlike `EntidadTenant`'s `IdTenant == 0` sentinel,
   which isn't reusable here. RLS's `WITH CHECK` on `usuarios_tenant` remains the real backstop
   for an `Added` row with a mismatched `id_tenant`.

#### Verification performed this batch

- `dotnet build Ways.slnx` → 0 errors, 0 warnings.
- `dotnet test Ways.slnx` → **`Ways.Domain.Tests` 38/38, `Ways.Application.Tests` 36/36**
  (unchanged from batch 8 — the `ServicioDeUsuarios` fix (item 4) only changes which context
  the InMemory tests already exercised), **`Ways.IntegrationTests` 23/23** (17 previous + 6
  new: the backfill boot regression, 3 `usuarios` RLS isolation tests, the concurrent-creates
  backstop test, and the cross-tenant 404 HTTP test), all against a real Postgres container
  (Docker daemon confirmed up before running). 0 failures, 0 skipped. Re-ran the full suite a
  second time to confirm stability (not flaky, in particular the concurrent-creates race
  test): 38/36/23 both times.

#### Commits (work units, branch `feat/stage1-slice2-usuarios`, no push)

1. `fix(tenancy): reescribir el backfill de usuarios como ExecuteUpdateAsync`
2. `docs(tenancy): documentar por que Added de Usuario no valida id_tenant en EstamparTenant`
3. `fix(auth): nivelar el timing del login por mail desconocido y usar Lazy para el hash descartable`
4. `fix(api): traducir el 23505 de ux_usuarios_usuario al 409 de negocio`
5. `fix(usuarios): chequear la disponibilidad del mail contra un contexto de plataforma`
6. `test(integration): cubrir el backfill, el RLS de usuarios, la carrera del backstop y el 404 cross-tenant`
7. `docs(usuarios): actualizar el login por mail y el esquema en el doc 08`
8. `docs(sdd): registrar el batch 9 de judgment-day (ronda 2) en el slice 2`

### Batch 10 — judgment-day round 3 (final iteration) on Slice 2 (branch `feat/stage1-slice2-usuarios`)

3 items approved by the user, all surgical fixes (no refactors):

1. **Mirrored race test for the `usuario` backstop.** The round-2 hardening
   (`DosAltasConcurrentesConElMismoMailDisparanElBackstopDelSaveChanges`) only exercised the
   `ux_usuarios_mail` 23505→409 branch of `ManejadorDeErrores`; the symmetric
   `ux_usuarios_usuario` branch (added in batch 9, item 3) had no equivalent race test. Added
   `DosAltasConcurrentesConElMismoUsuarioEnElMismoTenantDisparanElBackstopDelSaveChanges`
   (`UsuariosYLoginTests.cs`): same tenant, same `NombreUsuario`, distinct mails, two
   `Task.WhenAll` concurrent `POST /api/usuarios` from the same logged-in admin — asserts
   exactly one 201 and one 409 `usuario_duplicado`. Used a short literal
   (`"vendedor-concurrente-usuario"`) instead of `nameof(...)` for the shared `NombreUsuario`:
   the test method's own name is 86 characters, well past `ServicioDeUsuarios.Normalizar`'s
   40-char cap on `usuario`, which the first run caught as two `BadRequest`s instead of the
   expected 201/409 split.
2. **Eager warm-up of the discardable hash.** `ServicioDeAutenticacion`'s lazy
   `_hashDescartable` (batch 8/9) meant the FIRST unknown-mail login after process start still
   paid Hashear+Verificar (2 derivations) vs. 1 for every later request — the timing symmetry
   only held after that first request warmed the cache. Added
   `ServicioDeAutenticacion.PrecalentarHashDescartable(IHasheadorDeContrasenas)`, a public
   static method that runs the same `LazyInitializer.EnsureInitialized` as the existing lazy
   path; `ObtenerHashDescartable` now calls it and reads the field, so there's one code path,
   not two. Wired into `InicializadorDeBaseDeDatos.EjecutarAsync` (Infrastructure already
   references `Ways.Application.Abstracciones`, so referencing `Ways.Application.Usuarios` too
   is consistent) — called first, before migrations, since it doesn't depend on the database.
   Runs exactly once per process, on the same request-independent path that already seeds
   roles/root/org.
3. **Comment precision.** The `_hashDescartable` doc comment claimed "Hashear es mucho más
   caro que Verificar" — false, both cost exactly one PBKDF2 derivation. Reworded to state the
   real asymmetry precisely: without the cache, the unknown-mail path pays TWO derivations
   (Hashear + Verificar) against the ONE the known-mail path pays (Verificar only).

#### Verification performed this batch

- `dotnet build Ways.slnx` → 0 errors, 0 warnings.
- `dotnet test Ways.slnx` → **`Ways.Domain.Tests` 38/38, `Ways.Application.Tests` 36/36**
  (unchanged — this batch only touched Infrastructure startup wiring and one integration
  test), **`Ways.IntegrationTests` 24/24** (23 previous + 1 new race test). 0 failures, 0
  skipped. Docker daemon confirmed up throughout. Ran the new race test in isolation 4 times
  in a row to confirm stability (not flaky) before the full-suite run.

#### Commits (work units, branch `feat/stage1-slice2-usuarios`, no push)

1. `fix(auth): precalentar el hash descartable del login al arrancar el proceso`
2. `test(integration): agregar la carrera concurrente del backstop de ux_usuarios_usuario`
3. `docs(sdd): registrar el batch 10 de judgment-day (ronda 3, iteracion final) en el slice 2`

### Next batch

Slice 2's judgment-day round 3 findings are all fixed and verified; round 3 was the final
iteration approved by the user. Next: open PR 2 (per `CLAUDE.md`'s PR validation gate),
stacked on PR 1 per the chosen `stacked-to-main` chain strategy. No open items block Slice 3
or Slice 4 from starting in parallel.

---

## Slice 3: Catalogs + parametros (PR 3)

## Batch 1

**Scope:** Slice 3 § 3A (`tasks.md` 3.1–3.7: domain + persistence machine) and 3.8's summary,
branch `feat/stage1-slice3-catalogos` off `main` (slice 1 / PR 1 merged; slice 2 / PR 2 not
yet merged at branch-cut time, but this slice only depends on slice 1's tenant plumbing per
`tasks.md`'s dependency graph, so branching off `main` is correct). Stopped at **DB CHANGE
GATE #3** (task 3.8) as instructed. No EF Core migration was generated or applied — the gate
summary is in the coordinator's return report, not duplicated here.

### Completed in batch 1

- **3.1** — `CatalogoSimple : EntidadTenant { Id, Nombre, Activo, IdEmpresa? }` in
  `Ways.Domain/Catalogos`.
- **3.2** — `ConfiguracionDeCatalogo<T>` (`Ways.Infrastructure/Persistencia/Configuraciones`):
  maps table/PK/audit/tenant FK/optional composite empresa FK (ADR-9)/the catalog index pair
  (ADR-11's `ux_*_nombre_compartido` / `ux_*_nombre_empresa`), abstract `Tabla`/`ColumnaId`/
  `ConfigurarPropio`. Also added `ix_{tabla}_empresa` (explicit FK-order index) to match the
  existing `PuntoVentaConfiguration` convention — not in the design pseudocode, needed so EF
  doesn't fall back to an auto-named index for the FK.
- **3.3** — `Area` (+`Orden`), `Marca` (no extra columns), `Grupo` (+`Margen`), `MedioPago`
  (+`Orden`/`Comportamiento`/`AdmiteVuelto`/`RequiereReferencia`/`RecargoPorcentaje`) +
  `ComportamientoMedioPago` enum (`efectivo`/`electronico`/`cuenta_corriente`) + their 4 thin
  `ConfiguracionDeCatalogo<T>` subclasses.
- **3.4** — `Categoria` (+`Orden`/`IdCategoriaPadre`) + `CategoriaConfiguration` (adds the
  `(Id, IdTenant)` alternate key and the self composite FK, ADR-9) + `ReglaDeCategorias`
  (`ValidarProfundidad`, `ValidarSinCiclo`, pure, ADR-12) + 7 unit tests in
  `ReglaDeCategoriasTests.cs` (depth 1/2/3 accepted, depth-4 rejected, re-parent-overflow
  rejected, cycle rejected via `ValidarSinCiclo`, no-cycle accepted).
- **3.5** — `CondicionFiscal`, `AlicuotaIva`, `TipoComprobante` (+`ClaseComprobante` enum:
  `venta`/`compra`) — all extend `EntidadBase`, **not** `EntidadTenant`: no `id_tenant`
  column, so `WaysDbContext`'s generic tenant-filter loop never touches them (only the
  `BajaLogica` soft-delete filter applies, same as every other `EntidadBase`). Verified by
  `ModeloDeCatalogosTests.LosCatalogosGlobalesNoTienenColumnaIdTenantNiFiltroDeTenant`.
- **3.6** — `Parametro : EntidadTenant` (+`IdEmpresa` required, `IdPuntoVenta?`, `Clave`,
  `Valor` jsonb-as-string) + `ResolucionDeParametros.Resolver` (pure: punto_venta ?? empresa
  ?? default declarado) + 5 unit tests in `ResolucionDeParametrosTests.cs`.
- **3.7** — `ParametroConocido` (record: `Clave`, `TipoClr`, `ValorPorDefecto`) with the 4
  keys doc 10 §9 names (`tolerancia_pago`, `vuelto_maximo`, `importe_adicional_recarga`,
  `slots_tickets_espera`); `Buscar(clave)` throws `parametro_desconocido` (400) on an unknown
  key, case-insensitive lookup; 6 unit tests in `ParametroConocidoTests.cs`.
- **3.8 (summary only)** — gate summary prepared and returned to the coordinator/user; **no
  migration generated**, per instructions. Also included, for context, a preview of what
  gates #4 (`CatalogosGlobales`) and #5 (`Parametros`) will look like — those will each get
  their own gate request in a later continuation, per `tasks.md`'s own sequencing (3.10,
  3.12).
- **Infrastructure plumbing needed to make the above compile/test cleanly** (not separate
  `tasks.md` items, same precedent as slice 1/2 folding "EF configurations" into adjacent
  domain/infra tasks):
  - `WaysDbContext`: 9 new `DbSet<T>` properties (5 tenant catalogs + 3 global + `Parametro`).
    **Deliberately not added to `IWaysDbContext`** (`Ways.Application.Abstracciones`) in this
    batch — that interface is Application-layer surface, consumed by `ServicioDeCatalogo<T>`/
    `ServicioDeParametros` (tasks 3.15–3.18, section 3D, which `tasks.md` sequences *after*
    all three remaining gates). Extending it now with no consumer would be scope creep past
    "domain + persistence machine, up to gate #3."
  - `DependencyInjection.ConfigurarNpgsql` and `WaysDbContextFactory`: added
    `MapEnum<ComportamientoMedioPago>("comportamiento_medio_pago")` and
    `MapEnum<ClaseComprobante>("clase_comprobante")`, mirroring the existing
    `EstadoUsuario`/`EstadoTenant` pattern — needed for the EF model to build at all once
    `MedioPago`/`TipoComprobante` reference those column types.

### RLS identifier guard (carried judgment-day INFO, closed this batch)

`state.yaml` carried forward: *"`RlsMigrationBuilderExtensions.HabilitarRlsDeTenant` interpolates
the table name; add an identifier guard when it starts being called for the catalog tables."*
Added proactively in this batch, ahead of the helper's first new call site (migration 3 isn't
generated yet, but the guard needs to exist before it is): `HabilitarRlsDeTenant` now validates
`tabla` against `^[a-z_][a-z0-9_]*$` before interpolating it into any raw SQL, throwing
`ArgumentException` on anything else (empty, uppercase, leading digit, or an actual injection
attempt). 12 new tests in `RlsMigrationBuilderExtensionsTests.cs` (4 valid identifiers accepted,
7 invalid rejected, `null` rejected). Existing callers (`tenants`, `empresas`, `puntos_venta`,
`usuarios` — all migrations 1–2) are unaffected: all four are literal lowercase snake_case
strings already, so the guard is a no-op for them and only starts mattering once the catalog
tables (migration 3) call it in the same fashion.

### Tests added this batch

- `Ways.Domain.Tests/Catalogos/`: `ReglaDeCategoriasTests.cs` (8), `ResolucionDeParametrosTests.cs`
  (5), `ParametroConocidoTests.cs` (6) — 19 new domain tests, no database.
- `Ways.Application.Tests/Persistencia/ModeloDeCatalogosTests.cs` (15 tests) — the
  "generic-service" proof for the persistence machine (ADR-11), same technique as slice 1's
  `ModeloDeOrganizacionTests.cs`: builds the real `WaysDbContext` model against the Npgsql
  provider without connecting to a database, and asserts, once per catalog via `[Theory]`
  instead of by hand five times: the shared index pair exists with the right partial
  filters, the optional composite empresa FK doesn't force `IdTenant` nullable (ADR-9,
  re-confirmed for the catalog machine specifically), `Categoria`'s alternate key + self FK,
  the three global catalogs have no `id_tenant`/no `Tenant` filter, and `parametros`' two
  ADR-13 partial unique indexes.
- `Ways.Application.Tests/Persistencia/RlsMigrationBuilderExtensionsTests.cs` (12 tests) — the
  RLS identifier guard, see below.

### A real EF Core 9+ DI trap found while getting the integration suite to run with the new
### model (not a production bug — a test-fixture bug, fixed before this batch closes)

Adding the two new `MapEnum` calls made every existing host-booting integration test
(`UsuariosYLoginTests`, `UsuariosRlsTests`, `InicializadorDeBaseDeDatosTests`, etc. — added in
slice 2 batches 7–10) fail two different ways in sequence:

1. **`PendingModelChangesWarning`** (expected, documented): the C# model now knows about the
   catalog entities, migration 3 doesn't exist yet — exactly the mid-gate state the DB CHANGE
   GATE protocol produces on purpose (same trap as slice 2 batch 6, `usuarios.id_tenant`).
   Fixed the same way, but this time in **two** places instead of one: `WaysApiFixture.
   MigrarComoOwnerAsync` (as before) **and**, newly, the actual API host's own
   `InicializadorDeBaseDeDatos.EjecutarAsync` → `Database.MigrateAsync()`, because — unlike
   slice 2 batch 6 — the integration suite by this point already has tests that boot the real
   host via `CreateClient()` (closed in slice 2 batch 7). Rather than suppress the warning in
   *production* `DependencyInjection.ConfigurarNpgsql` (which would leave it disabled in real
   deploys, not just this dev-time window), `WaysApiFixture.ConfigureWebHost` now replaces the
   test host's `WaysDbContext`/keyed-platform-`WaysDbContext` DI registrations with copies
   that add `ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))` —
   production code untouched.
2. **[CRITICAL, test-fixture-only] Replacing `AddDbContext<WaysDbContext>` a second time
   doesn't replace the first registration — it *adds* to it.** Since EF Core 9, `AddDbContext`
   records its options delegate as an `IDbContextOptionsConfiguration<TContext>` service,
   which is designed to be **cumulative** (multiple `AddDbContext`/`ConfigureDbContext` calls
   compose). `RemoveAll<DbContextOptions<WaysDbContext>>()` (which looked sufficient by
   analogy with the "replace the DbContext for testing" pattern) does **not** remove this
   service — it's a different type. The result: production's `AgregarInfrastructure`
   registration (already applied before `ConfigureWebHost` runs) and the fixture's new one
   both got applied to the same final options, so `MapEnum<ComportamientoMedioPago>(...)` (and
   the other three) ran **twice** — Npgsql's `NpgsqlTypeMappingSource.FindEnumMapping` then
   throws `InvalidOperationException: Sequence contains more than one matching element` the
   first time any query touches a property of that enum type, surfacing as a bare 500 on
   `POST /api/auth/login` and everywhere else. Root-caused by bisecting: the exact same
   duplicate-mapping pattern for `EstadoUsuario`/`EstadoTenant` across 3+ call sites (owner
   migration context, per-request context, keyed-platform context, `CrearContextoDeAplicacion`)
   had existed since slice 1/2 without ever tripping this — the difference here was the
   **second, competing `AddDbContext<WaysDbContext>` registration** added in this batch's
   first fix attempt, not the enum count itself. Fixed by also removing
   `IDbContextOptionsConfiguration<WaysDbContext>` before re-adding. Confirmed: 24/24 green,
   twice in a row (not flaky).

### Verification performed this batch

- `dotnet build Ways.slnx` → 0 errors, 0 warnings.
- `dotnet test Ways.slnx` → **`Ways.Domain.Tests` 57/57** (38 unedited + 19 new, the domain
  tests above), **`Ways.Application.Tests` 63/63** (36 unedited + 15 `ModeloDeCatalogosTests`
  + 12 `RlsMigrationBuilderExtensionsTests`), **`Ways.IntegrationTests` 24/24** (all 24
  pre-existing slice 1/2 tests, unedited except for the fixture fix above), stable across two
  consecutive full runs. 0 failures, 0 skipped, Docker daemon reachable throughout.
- Slices 1–2 regression: unedited, still green (confirms the fixture fix didn't touch any
  test *behavior*, only DI plumbing needed for the new model to coexist pre-migration).

### Deferred items (reported, not silent)

- **3.9–3.14** — blocked on DB CHANGE GATE #3/#4/#5 approvals, per instructions. Gate #3's
  summary is the centerpiece of this batch's return to the user; gates #4/#5 get their own
  requests in later continuations.
- **3.15–3.18 (Application + API)** and **3.19–3.20 (integration tests)** — not started this
  batch. `tasks.md` sequences section 3D *after* 3C (all three migrations), and this batch's
  instructed scope was "domain + persistence machine, stop at the first DB CHANGE GATE."
  `IWaysDbContext` is untouched for the same reason (see above).
- Seed data (task 3.14: three fiscal catalogs + tenant provisioning template) — described in
  the gate summary as a preview (seed *shape*, not seed *code*); `InicializadorDeBaseDeDatos`
  wiring is deferred to when the migrations that back it exist.

### Next batch

Once gate #3 (`CatalogosDeTenant`) is approved: generate migration 3, present gate #4
(`CatalogosGlobales`), then gate #5 (`Parametros`), generating each migration only after its
own approval — `tasks.md` 3.9–3.13. Then section 3D (Application + API) and 3E (tests),
followed by judgment-day review before PR 3.

---

## Batch 2 — gate #3 approved, migration 3 generated, scope addition (3F provisioning)

**Trigger:** the coordinator relayed two user decisions: (1) DB CHANGE GATE #3 approved
exactly as presented — generate migration 3 and continue per `tasks.md`, gates #4/#5 remain
hard stops each with their own summary; (2) `ServicioDeAprovisionamiento` (ADR-16) added to
Slice 3 as new section 3F (tenant provisioning), landing at the end of the slice, with the
visual ABM staying in slice 4.

### Completed in batch 2

- **`tasks.md` § 3F added** — 7 new tasks (3.21–3.27): `TenantActualDeSesion.Suplantar`
  (ADR-2/ADR-3 deferred impersonation scope), the `is_local: true` `set_config` variant
  (ADR-3), `PlantillaDeAprovisionamiento.V1` (ADR-16, área General + Efectivo + Transferencia),
  `ServicioDeAprovisionamiento.CrearTenantAsync` (execution-strategy-wrapped transaction,
  ADR-16's documented `BeginTransaction`/`EnableRetryOnFailure` trap), the platform-only
  `POST /api/plataforma/tenants` endpoint, unit tests, and integration tests (end-to-end
  provisioning + rollback-atomicity proof + 403 for a tenant actor). **Not implemented yet**
  — this batch only added the task breakdown, per the user's own sequencing ("lands at the
  end of Slice 3"); 3.9 (migration 3) was the only implementation task between this approval
  and gate #4.
- **Review Workload Forecast revised** — flagged the ~350–500 line addition from 3F, revised
  Unit 3 estimate to ~1,250–1,800 lines, and recommended splitting Unit 3 into **3a (catalogs
  + parametros, 3A–3E)** and **3b (tenant provisioning, 3F)** as two independently mergeable
  work units under the existing `stacked-to-main` chain strategy, or recording
  `size:exception` if the user prefers a single PR 3.
- **3.9 — migration 3 (`CatalogosDeTenant`) generated and hand-finished.** `dotnet ef
  migrations add CatalogosDeTenant` scaffolds by diffing the **entire** pending model against
  the last migration snapshot — it doesn't know about gate boundaries. The first attempt swept
  in gate #4's three global fiscal tables (`condiciones_fiscales`/`alicuotas_iva`/
  `tipos_comprobante` + the `clase_comprobante` enum) **and** gate #5's `parametros` table,
  none of which gate #3 approved. Fixed by temporarily excluding those 4 entities via
  `modelBuilder.Ignore<T>()` in `WaysDbContext.OnModelCreating` **only during scaffold
  generation** (removed immediately after, confirmed by rebuilding — the model is "ahead of
  migrations" again for gates #4/#5, same documented mid-gate state as everywhere else in this
  project). Regenerated cleanly to exactly 5 tables. One residual leak survived even with the
  `Ignore<T>()`s: the scaffolder still emitted a `Npgsql:Enum:clase_comprobante` annotation in
  `Up()`/`Down()` **and** both snapshot files (`*.Designer.cs` and
  `WaysDbContextModelSnapshot.cs`), because `WaysDbContextFactory`'s design-time `MapEnum<T>()`
  calls register enum types at the Npgsql-options level regardless of whether any entity in
  the current diff actually uses them. Hand-stripped all four occurrences — leaving a stray
  `CREATE TYPE clase_comprobante` in migration 3's snapshot would have made EF think that enum
  already existed when gate #4's migration is generated later, silently skipping the real
  `CREATE TYPE` there and breaking at runtime the first time `tipos_comprobante.clase` is
  touched against a real database. Then hand-added the RLS calls (same technique as migrations
  1–2): `HabilitarRlsDeTenant("areas"/"categorias"/"marcas"/"grupos"/"medios_pago")` in `Up()`;
  `Down()` needs no explicit `DROP POLICY` since `DropTable` cascades the policies with it
  (same as migration 1, unlike migration 2 which added RLS to a pre-existing table). File:
  `src/Ways.Infrastructure/Persistencia/Migraciones/20260801231600_CatalogosDeTenant.cs`.

### Verification performed this batch

- `dotnet build Ways.slnx` → 0 errors, 0 warnings.
- `dotnet test Ways.slnx` → **`Ways.Domain.Tests` 57/57, `Ways.Application.Tests` 63/63,
  `Ways.IntegrationTests` 24/24** — all pre-existing slice 1/2 tests, unedited, all against
  real Postgres with migration 3 now part of the applied chain (`Database.MigrateAsync()` ran
  it during `WaysApiFixture`/`InicializadorDeBaseDeDatos` boot for every test in the suite).
  Stable across 2 consecutive integration-suite runs.
- **Real proof RLS landed correctly on the 5 new tables**, without writing new integration
  tests yet (3.19 is still pending, deferred with the rest of 3D/3E): the existing, fully
  generic ADR-15 policy-coverage test
  (`AislamientoDeTenantTests.LaCoberturaDePoliciesEsCompleta`) queries `pg_class`/
  `pg_attribute`/`pg_policies` for **any** table with an `id_tenant` column — not hardcoded to
  specific table names — and asserts zero rows lack `ENABLE`+`FORCE`+a policy. It passed with
  `areas`/`categorias`/`marcas`/`grupos`/`medios_pago` now included in that query's scope,
  confirming the hand-added `HabilitarRlsDeTenant` calls actually took effect against a real
  database, not just that the migration ran without throwing.

### Deferred items (reported, not silent)

- **3.10–3.13** — gate #4 (`CatalogosGlobales`) is next; its summary is the centerpiece of
  this batch's return to the coordinator/user. Gate #5 (`Parametros`) follows in a later
  continuation, per instructions — not batched together.
- **3.14–3.27** (seed, Application/API, tests, 3F provisioning) — not started.

### Next batch

Present gate #4 (`CatalogosGlobales`) summary and STOP — no migration generated until
approved. Then, once approved: generate migration 4, present gate #5 (`Parametros`), generate
migration 5 once approved, seed data (3.14), Application/API (3D), tests (3E), then 3F
(tenant provisioning) at the end of the slice, followed by judgment-day review before PR
3(a)/3(b).

---

## Batch 3 — gate #4 approved with modifications, migration 4 generated, delivery decision

**Trigger:** the coordinator relayed the user's gate #4 approval **with two modifications**
(apply before generating): (1) restore write-protection RLS on the 3 global tables — override
of ADR-11's original "API-surface-only, no RLS" design — read-all/write-plataforma-only; (2)
`alicuotas_iva.nombre` gets a unique index. Plus a delivery decision: Slice 3 ships as **one
PR with `size:exception`**, not the 3a/3b split offered in batch 2.

### Completed in batch 3

- **`design.md` ADR-11 updated** with an explicit override note (user decision, 2026-08-01):
  the three fiscal catalogs now get `ENABLE`/`FORCE ROW LEVEL SECURITY` too — permissive
  `FOR SELECT USING (true)` (readable in every access mode) plus `FOR ALL USING/WITH CHECK
  (app_es_plataforma())` (every write command restricted to platform mode). The API surface
  is unchanged (still read-only `GET` for tenants) — RLS is a second, independent layer behind
  it, same two-layer pattern as every scoped table in this document. Also updated the *Data
  model shape* table and the migration-sequencing table (gate #4 row) to stop saying "no RLS."
- **`RlsMigrationBuilderExtensions.HabilitarRlsDeCatalogoGlobal(tabla)` added** — reuses the
  existing identifier guard (`ValidarIdentificadorDeTabla`, closed in batch 1). 6 new unit
  tests in `RlsMigrationBuilderExtensionsTests.cs` (3 valid identifiers, 3 invalid).
- **`AlicuotaIvaConfiguration`**: added `ux_alicuotas_iva_nombre UNIQUE(nombre) WHERE
  deleted_at IS NULL` (user decision — doc 10 didn't ask for it, but two alícuotas both named
  "21%" has no business meaning and would break any selector).
- **Migration 4 (`CatalogosGlobales`) generated and hand-finished.** Same `Ignore<T>()`
  scaffold-isolation technique as migration 3 (documented in batch 2), this time excluding
  only `Ways.Domain.Catalogos.Parametro` (gate #5, still unapproved) — confirmed clean: exactly
  3 `CreateTable` calls (`alicuotas_iva`, `condiciones_fiscales`, `tipos_comprobante`), no
  `parametros` leakage, and this time `clase_comprobante`'s enum annotation is **correctly**
  present (it's genuinely created here, unlike migration 3's stray registration). Hand-added
  `HabilitarRlsDeCatalogoGlobal` on the 3 tables at the end of `Up()`. File:
  `20260801233937_CatalogosGlobales.cs`. Removed the temporary `Ignore<T>()` immediately after
  generation — confirmed zero net diff on `WaysDbContext.cs` (same verification as batch 2).
- **Integration coverage added**: `CatalogosGlobalesRlsTests.cs`, 6 tests, real Postgres, raw
  SQL (no EF) as `ways_app`:
  - `UnaSesionDeTenantPuedeLeerUnCatalogoGlobal` — SELECT succeeds in tenant mode.
  - `UnaSesionDeTenantNoPuedeInsertarEnUnCatalogoGlobal` — INSERT throws `PostgresException`,
    `SqlState == "42501"`.
  - `LaPlataformaPuedeEscribirEnUnCatalogoGlobal` — INSERT succeeds in platform mode.
  - `SinContextoResueltoNoSePuedeEscribirEnUnCatalogoGlobal` — no GUC set ⇒ INSERT also
    rejected with 42501 (fails closed, ADR-4).
  - `UnaSesionDeTenantNoPuedeActualizarUnCatalogoGlobal` / `...NoPuedeBorrarDeUnCatalogoGlobal`
    — **see the correction below**, these assert `0` rows affected, not a thrown exception.

### A precise correction to the literal request (reported, not silently "fixed")

The coordinator's instruction said to expect `42501` for tenant-mode INSERT **and**
UPDATE/DELETE. The first attempt at the UPDATE/DELETE tests wrote exactly that and both
failed — `ExecuteNonQueryAsync()` returned `0` with no exception thrown. This is correct
Postgres RLS behavior, not a bug in the policy: `FOR ALL` policies (like
`{tabla}_escritura_plataforma`) supply **both** the `USING` clause (governs which existing
rows a command can even see/target) and the `WITH CHECK` clause (governs the resulting row).
For `UPDATE`/`DELETE`, only `USING` matters for row selection — `WITH CHECK` only fires for
rows that already passed `USING` (and only for `INSERT`/`UPDATE`, `DELETE` has no `WITH
CHECK` in Postgres at all). Since `condiciones_fiscales_lectura` is `FOR SELECT` only, it does
**not** extend row-visibility to `UPDATE`/`DELETE` targeting — only the write policy's
`USING (app_es_plataforma())` does, and that's false in tenant mode, so the row is invisible
to the `UPDATE`/`DELETE` before `WITH CHECK` is ever reached. Result: `0` rows affected, same
mechanism and same security guarantee as the pre-existing cross-tenant `UPDATE` case in
`AislamientoDeTenantTests` (already documented there as "0 filas, no una excepción"). The only
way to make `UPDATE` genuinely throw `42501` would be to give the write policy a permissive
`USING (true)` — but that would ALSO make `DELETE` succeed for a tenant (Postgres `DELETE`
has no `WITH CHECK` gate at all), which is a real hole, not a cosmetic one. Kept the policy as
designed (secure) and fixed the two test assertions to match reality — added a verification
read after the UPDATE case confirming the row's `nombre` is genuinely untouched, for extra
confidence beyond the `0`-rows return value.

### Verification performed this batch

- `dotnet build Ways.slnx` → 0 errors, 0 warnings.
- `dotnet test Ways.slnx` → **`Ways.Domain.Tests` 57/57, `Ways.Application.Tests` 69/69** (63
  previous + 6 `HabilitarRlsDeCatalogoGlobal` tests), **`Ways.IntegrationTests` 30/30** (24
  previous, unedited + 6 new `CatalogosGlobalesRlsTests`). Stable across 2 consecutive
  integration-suite runs. Docker daemon reachable throughout.

### Delivery decision recorded (user, 2026-08-01)

Slice 3 ships as **one PR (PR 3)**, not the 3a/3b split offered in batch 2 —
**`size:exception`** recorded against the 400-line reviewable budget. `tasks.md`'s Review
Workload Forecast updated accordingly. Work-unit commits inside the branch stay granular
regardless (per `work-unit-commits` skill), so the diff is still reviewable commit-by-commit
even though it isn't split across separate PRs.

### Deferred items (reported, not silent)

- **3.12–3.13** — gate #5 (`Parametros`) is next; its summary is the centerpiece of this
  batch's return to the coordinator/user, per instructions (hard stop, not batched with this
  approval).
- **3.14–3.27** (seed, Application/API, tests, 3F provisioning) — not started.

### Next batch

Present gate #5 (`Parametros`) summary and STOP — no migration generated until approved. Then,
once approved: generate migration 5 (same `Ignore<T>()` pattern won't be needed — it's the
last gated table), seed data (3.14), Application/API (3D), tests (3E), then 3F (tenant
provisioning) at the end of the slice, followed by judgment-day review before the single PR 3.

---

## Batch 4 — gate #5 approved (last DB gate), Slice 3 finished end to end (3.13–3.27)

**Trigger:** the coordinator relayed the user's approval of gate #5 exactly as presented —
the last DB gate of the stage, no more approval stops — and asked to finish the entire rest
of the slice in one batch: migration 5, seed, the full catalog/parametros Application+API
layer, section 3F (tenant provisioning), and integration tests for everything gated until
now, with the full suite green (exact counts, run twice).

**Status:** done. Full suite: **`Ways.Domain.Tests` 60/60, `Ways.Application.Tests` 69/69,
`Ways.IntegrationTests` 39/39**, stable across 2 consecutive integration-suite runs. Slice 3
is code-complete.

### Completed in batch 4

**3.13 — migration 5 (`Parametros`).** Generated with `dotnet ef migrations add Parametros`;
no `Ignore<T>()` isolation needed this time — it's the last gated table, nothing left to leak
into it. Hand-added `HabilitarRlsDeTenant("parametros")` (standard tenant RLS — `parametros`
is a tenant table, not a gate #4 global one). File: `20260801235736_Parametros.cs`.

**3.14 — seed.** `InicializadorDeBaseDeDatos.SembrarCatalogosFiscalesAsync`: 5 condiciones
fiscales, 6 alícuotas IVA, 10 tipos de comprobante (venta-side only — doc 10's own "Etapas
sugeridas" puts comprobantes de compra behind clientes/proveedores, a later stage).
`CodigoAfip` left `NULL` throughout (real AFIP codes are an electronic-invoicing concern, out
of scope; a made-up code would be worse than an honest gap). Idempotent per table, same
pattern as `SembrarRolesAsync` — never overwrites an existing row.

**3.15–3.18 — Application + API for the 5 tenant catalogs + 3 fiscal catalogs.**
`ServicioDeCatalogo<T,TListado,TAlta>` (list/get/create/edit/soft-delete + `Instanciar`/
`AplicarPropios`/`Proyectar` hooks), 4 thin subclasses (`ServicioDeAreas`/`Marcas`/`Grupos`/
`MediosPago`), the `ServicioDeCategorias` escape hatch (depth/cycle validation via raw-SQL
recursive CTEs, see the real bug below), `ServicioDeCatalogosFiscales` (read-only projection
for the 3 global tables), `CatalogosEndpoints.MapearCatalogo<T,TListado,TAlta,TServicio>` (1
line per catalog) wired for all 5 tenant catalogs + the 3 fiscal `GET`s. `IWaysDbContext`
extended with the 9 new `DbSet<T>` properties (deferred since batch 1) plus a `DatabaseFacade
Database { get; }` — needed for ADR-16's `CreateExecutionStrategy`/`BeginTransactionAsync`/
`GetDbConnection` from the Application layer, which required adding a
`Microsoft.EntityFrameworkCore.Relational` package reference to `Ways.Application.csproj`
(still provider-agnostic — "relational" is the DB category, not Npgsql specifically).

**3.17 — `ServicioDeParametros`.** Resolution (`ResolucionDeParametros`, pure, from batch 1)
+ typed-key validation via `JsonSerializer.Deserialize(valor, conocido.TipoClr)`, rejecting a
type mismatch with `parametro_tipo_invalido` (400). `idEmpresa`/`idPuntoVenta` travel
explicit in every call (ADR-10 — no "current empresa" in session yet).

**3.21–3.22 — `Suplantar`/`ReaplicarSobreConexionAsync` (ADR-16 deferred pieces).** Added to
`ITenantActual` (Application abstraction — `ServicioDeAprovisionamiento` can't reference
Infrastructure's concrete `TenantActualDeSesion` directly), implemented by
`TenantActualDeSesion` (real impersonation scope + `is_local: true` GUC reapply on the
transaction's already-open connection) and `TenantActualFijo` (`NotSupportedException` — no
immutable context ever provisions a tenant). Shared modo→GUC string mapping factored out of
`InterceptorDeContextoDeTenant` into `ModoDeAccesoExtensions` to avoid duplicating it.

**3.23 — `PlantillaDeAprovisionamiento.V1`.** `Ways.Domain.Organizacion` (Domain — design's
own Test Strategy table lists it under `Ways.Domain.Tests`, no database): área "General",
medios "Efectivo"/"Transferencia" exactly as approved, plus `ItemsDiferidos` declaring the
stage-2/3 gaps (lista de precios, Consumidor Final) instead of silently omitting them.

**3.24–3.25 — `ServicioDeAprovisionamiento.CrearTenantAsync` + `POST /api/plataforma/tenants`.**
`CreateExecutionStrategy().ExecuteAsync(...)` wrapping `BeginTransactionAsync` (the documented
trap: `EnableRetryOnFailure` makes EF throw on a bare `BeginTransaction` outside this
wrapper) → `tenants` row (platform mode) → `Suplantar(tenant.Id)` + `ReaplicarSobreConexionAsync`
→ `empresas`/`puntos_venta`/plantilla (auto-stamped `IdTenant` via the now-impersonated
`TenantActual`, proving `Suplantar` actually works end to end) → tenant admin `Usuario`
(`IdTenant` set explicitly — `Usuario` doesn't inherit `EntidadTenant`, no auto-stamp) →
commit. New `Politicas.SoloPlataforma` (root-only) guards the endpoint.

### Two real bugs found while wiring `ServicioDeCategorias` against real Postgres (fixed,
### not worked around)

1. **[CRITICAL] Opening the raw ADO.NET connection directly bypasses EF's interceptor
   pipeline entirely — RLS then blocks the query from seeing anything, silently.** The
   depth/cycle CTEs run over raw ADO.NET (same reason as `InicializadorDeBaseDeDatos.
   VerificarRolSinBypassAsync`: `Database.SqlQuery<T>()` crashes against this project's
   model). The first version opened the connection with `Db.Database.GetDbConnection().OpenAsync()`
   — mirroring `VerificarRolSinBypassAsync`'s pattern exactly, which is safe *there* because
   that query (`pg_roles`) isn't RLS-scoped. `categorias` **is** RLS-scoped, and
   `GetDbConnection().OpenAsync()` opens the connection **without ever going through EF's
   `RelationalConnection` wrapper**, so `InterceptorDeContextoDeTenant` — an EF
   `DbConnectionInterceptor` — never fires, the tenant GUC never gets set, and RLS fails
   closed (ADR-4): the CTE always saw zero rows, not an error, which is what made this hard
   to notice at first (an integration test failed with a wrong *value*, not an exception).
   Root-caused with a temporary debug log showing `nivelDelPadre=0` for every parent
   regardless of its real depth. Fixed by using `Database.OpenConnectionAsync()`/
   `CloseConnectionAsync()` (EF's own connection-lifecycle methods, which DO invoke
   interceptors) instead of the raw connection's own `OpenAsync()`/`CloseAsync()`.
2. **A pre-existing documentation bug in `ReglaDeCategorias.ValidarProfundidad`'s XML doc
   (batch 1), only surfaced once real SQL fed it real numbers.** The doc comment said
   `nivelDelPadre` was "la cantidad de ancestros del padre elegido" (0-indexed ancestor
   count). The batch-1 unit tests (still correct, still passing, never touched) actually
   exercise a *different* convention: `nivelDelPadre` = the padre's own 1-indexed depth (1
   for a root, 2 for a root's child, etc.) — the two conventions agree only in the "no padre"
   case (both 0), which is exactly why 19 passing pure-function tests never caught the
   mismatch. Writing `NivelDeAsync`'s CTE against the *documented* convention
   (`count(*) - 1`, ancestor count) made a real 4-level category tree get accepted instead of
   rejected. Fixed the CTE to `count(*)` (matching the tests' actual, correct convention) and
   corrected the misleading XML doc on `ReglaDeCategorias.ValidarProfundidad` itself so the
   next reader doesn't get misled the same way.

### A precise correction to the literal task wording (reported, not silently changed)

3.19 asks for "fiscal catalogs GET 200 / write 403." ADR-11's own design has **no write route
mapped at all** for the 3 fiscal catalogs — the API surface itself is read-only, RLS
(`HabilitarRlsDeCatalogoGlobal`, gate #4) is the real backstop behind it. A write attempt
therefore gets `404` (route doesn't exist), not `403` (route exists, a policy denies it).
Tested and documented as `404`, matching the actual, approved design instead of forcing a
route to exist just to produce a specific status code.

### Verification performed this batch

- `dotnet build Ways.slnx` → 0 errors, 0 warnings (including one legitimate suppression:
  `AD0001` in `Ways.Api.csproj`, a confirmed ASP.NET Core analyzer crash on the generic
  `MapearCatalogo<T,TListado,TAlta,TServicio>` helper — no diagnostic is ever produced, the
  build succeeds identically without the generic helper present; documented inline).
- `dotnet test Ways.slnx` → **`Ways.Domain.Tests` 60/60** (57 previous + 3
  `PlantillaDeAprovisionamientoTests`), **`Ways.Application.Tests` 69/69** (unchanged from
  batch 3 — no new Application-layer unit tests this batch; `ServicioDeCategorias`'s CTE
  logic can only be proven against real Postgres, InMemory doesn't support raw ADO commands),
  **`Ways.IntegrationTests` 39/39** (30 previous, unedited + 9 new: `CatalogosTests.cs` (4),
  `ParametrosTests.cs` (2), `AprovisionamientoTests.cs` (3)). Stable across 2 consecutive
  full runs of the integration suite. Docker daemon reachable throughout.

### Deferred items (reported, not silent)

- None outstanding for Slice 3's own scope. Slice 4 (Web ABMs) remains a separate slice, per
  `tasks.md`.

### Next batch

Slice 3 is code-complete. Ready for judgment-day review (dual blind review, per `CLAUDE.md`'s
PR validation gate) before opening the single PR 3 (`size:exception`, per the batch 3 delivery
decision).

## Judgment-day slice 3, round 1 — 8 confirmed issues fixed (2026-08-02)

Dual blind review found 8 confirmed issues (1 CRITICAL, 6 confirmed/triaged, 1 record-only user
decision). All fixed in the same branch (`feat/stage1-slice3-catalogos`, unpushed).

1. **CRITICAL — `categorias` self-parent → unbounded recursive CTE.** A PUT with
   `IdCategoriaPadre == id` passed `ReglaDeCategorias.ValidarSinCiclo` because that check only
   ever looked at `descendientes` (a list that, by construction, never contains the node
   itself) — a cycle of length 1 persisted silently, and the next `WITH RECURSIVE` walk over
   that row looped forever. Fixed in two layers: (a) domain — `ValidarSinCiclo` now takes
   `idPropio` and rejects `idDestino == idPropio` explicitly, before ever consulting
   `descendientes`; (b) schema — `ck_categorias_padre_no_self` CHECK constraint
   (`id_categoria_padre IS DISTINCT FROM id_categoria`) added to `CategoriaConfiguration` and
   merged **in place** into migration `CatalogosDeTenant` (unpushed branch, model already
   gate-approved — no new migration file; `table.CheckConstraint(...)` added inside the
   existing `CreateTable` call, Designer.cs and `WaysDbContextModelSnapshot.cs` hand-updated to
   match, confirmed via a throwaway `dotnet ef migrations add` used only to generate the exact
   diff and then discarded). `dotnet ef migrations has-pending-model-changes` → clean.
2. **`ManejadorDeErrores` 23505/23503 backstops generalized.** Replaced two more one-off
   `ConstraintName` cases with `ClasificarUnicidad(string)`, a suffix/prefix classifier over
   the ~10 new unique indexes (catalogs' `ux_*_nombre_*`, fiscal catalogs' `ux_*_nombre`/`ux_*_codigo`,
   `parametros`' two `ux_parametros_*`) grouped into 3 codes (`nombre_duplicado` /
   `codigo_duplicado` / `parametro_duplicado`) — same 409 code the app-level pre-check already
   throws, so the client sees the same response whether the race resolved before or after
   `SaveChangesAsync`. Added a generic 23503 → 400 `referencia_invalida` mapping for any
   `fk_*` constraint. The two pre-existing `usuarios` cases are untouched (already specific,
   matched first in the switch).
3. **Root removed from `GestionDeCatalogo`.** "Root administra tenants, no opera ninguno" (doc
   09/design.md) — the same criterion `SoloPlataforma` already encodes. `Politicas.cs` updated;
   new integration test asserts a Root session gets 403 on a tenant catalog endpoint. No
   existing test assumed Root access to catalogs (they all log in as `Admin`), so nothing else
   changed.
4. **Soft-deleted rows visible to the `categorias` CTEs.** Added `deleted_at IS NULL` to the
   anchor and recursive branches of all three CTEs (`SqlNivel`/`SqlAltura`/`SqlDescendientes`),
   and a new explicit existence check (`ExistePadreAsync`, a 4th tiny CTE-free query) that
   rejects an `IdCategoriaPadre` resolving to nothing visible for this tenant (nonexistent,
   another tenant's row, or soft-deleted) with a domain 400 (`categoria_padre_invalido`)
   **before** the depth/cycle validation runs — without it, a missing padre silently resolved
   `nivelDelPadre = 0`, indistinguishable from "no padre".
5. **`parametros` cross-validation (user decision, no schema change).** `ServicioDeParametros`
   now validates that a given `idPuntoVenta` belongs to the declared `idEmpresa` (tenant-scoped
   `db.PuntosVenta.AnyAsync`) before both `ResolverAsync` and `EstablecerAsync`, domain 400
   (`punto_venta_no_pertenece_a_la_empresa`) otherwise — `parametros.id_punto_venta` has no FK
   to `empresas` in the schema (only to `puntos_venta`), so nothing in the DB previously caught
   a real-but-wrong-empresa point of venta. This also fixed a latent gap in the existing
   `ParametrosTests` scenario ("otro punto de venta sin fila propia cae al de empresa"), which
   used a made-up id (`999999`) that never corresponded to a real row — replaced with a second,
   genuinely-seeded `PuntoVenta` of the same empresa.
6. **18 new raw-SQL RLS tests for the 6 tenant tables.** New
   `CatalogosDeTenantRlsTests.cs`, `[Theory]`-parametrized over `areas`/`categorias`/`marcas`/
   `grupos`/`medios_pago`/`parametros` (3 theories × 6 tables): cross-tenant SELECT → 0 rows,
   cross-tenant UPDATE → 0 rows affected, INSERT with a foreign `id_tenant` → 42501 — same
   conventions as `CatalogosGlobalesRlsTests` and `AislamientoDeTenantTests`, generalized
   instead of repeated by hand once per table. `AislamientoDeTenantTests.LaCoberturaDePoliciesEsCompleta`
   already proved every `id_tenant` table *has* a policy; this proves the policy actually
   isolates rows.
7. **`ix_parametros_punto_venta` named explicitly.** The FK-backing index on
   `(id_punto_venta, id_tenant)` had no explicit `HasDatabaseName`, so EF fell back to its
   convention name (`IX_parametros_id_punto_venta_id_tenant`) — inconsistent with every other
   `ix_<tabla>_<columna>` index in the project. Named in `ParametroConfiguration`; the unmerged
   `Parametros` migration (and its Designer.cs / the model snapshot) edited in place to match —
   no new migration file, same reasoning as item 1.
8. **ADR-10 `DeLaEmpresa` deferral recorded (user decision, no implementation).** The
   `query.DeLaEmpresa(idEmpresa)` extension design.md already describes stays unimplemented —
   deferred to slice 4's ABM or the first multi-empresa tenant, whichever comes first. One-line
   note added to design.md's ADR-10 and to this change's `state.yaml`.

### Verification performed this round

- `dotnet build Ways.slnx` → 0 errors, 0 warnings.
- `dotnet ef migrations has-pending-model-changes` → clean (both in-place migration edits —
  `CatalogosDeTenant`'s CHECK constraint and `Parametros`' renamed index — reconciled against
  the model).
- `dotnet test Ways.slnx` → **`Ways.Domain.Tests` 61/61** (+1: self-parent rejected),
  **`Ways.Application.Tests` 72/72** (+3: `ServicioDeParametrosTests`, InMemory provider),
  **`Ways.IntegrationTests` 64/64** (+25: 7 new `CatalogosTests`/`ParametrosTests` cases + 18
  `CatalogosDeTenantRlsTests` theory cases). Stable across 2 consecutive full-solution
  `dotnet test Ways.slnx` runs, 0 skipped, 0 failures. Docker daemon reachable throughout.

### Next batch

Ready for judgment-day round 2 (re-judge the fixed diff) before opening the single PR 3.

## Judgment-day slice 3 round 2 — confirmed-item fixes

One confirmed issue (flagged by both blind judges): the `db-error-backstops` skill's Hard
Rule #5 requires a race test per new backstop family, and `parametros` (`parametro_duplicado`)
had a 23505 mapping (round 1) but no race test.

1. **Race test for `parametro_duplicado`.** New `ParametrosTests.
   DosEstablecimientosConcurrentesConLaMismaClaveYElMismoAlcanceDisparanElBackstopDelSaveChanges`.
   `EstablecerAsync` is an upsert (`existente is null` check, then insert-or-update), unlike
   `ServicioDeCatalogo.CrearAsync` (unconditional insert + a pre-check that already throws a
   409 domain error on its own, so `CatalogosTests`' analogous race test passes even without
   genuine concurrency). Two plain `Task.WhenAll`-fired `PUT`s on a fresh key were empirically
   flaky: with a warm connection pool (e.g. inside the full suite) the winner's SELECT+INSERT+
   commit can finish before the loser's own SELECT runs, so the loser sees the row already
   committed and does a legitimate UPDATE (200) instead of racing the INSERT — reproduced
   reliably (3/3 full-suite runs failed with `[OK, OK]`, no 409) before the fix. Fixed with a
   deterministic rendezvous: a `WithWebHostBuilder`-derived factory (isolated to this test only,
   doesn't touch the shared fixture) registers a test-only `DbCommandInterceptor`
   (`InterceptorDeRendezVous`) that retains the first two `parametros`-reading `SELECT`s behind
   a `CountdownEvent(2)` until both arrive, guaranteeing both requests see "no existe" and both
   attempt the INSERT — the 23505 backstop then fires deterministically for the loser,
   regardless of pool warmth. Stable across 6+ consecutive full-solution `dotnet test Ways.slnx`
   runs after the fix (0 failures).
2. **`ClasificarUnicidad` scope documented.** XML doc on `ManejadorDeErrores.cs` now states the
   suffix match is deliberately schema-wide (any `_nombre`/`_codigo` unique index), that it
   harmlessly also covers seed-only indexes like `ux_roles_nombre` (never hit by a client write
   path), and that the fiscal `codigo_duplicado` family has no client write path today — exempt
   from race-testing per the skill's decision gates until an alta/edición endpoint exists.
3. **`fk_*` → 400 tradeoff documented + observability preserved.** Comment on the generic
   `fk_*` branch acknowledges it also catches system-managed FKs (turning would-be logged 500s
   into unlogged 400s) as a deliberate tradeoff (a closed FK list would need manual upkeep per
   migration). Added `log.LogWarning` with the constraint name (new private
   `LogYClasificarReferenciaInvalida` helper) so that tradeoff doesn't cost observability.
4. **TOCTOU residual risk recorded.** `state.yaml` now documents the accepted residual risk: a
   narrow race between `ExistePadreAsync`'s pre-check and the `categoria` write if the parent is
   concurrently soft-deleted — the row would reference a soft-deleted parent, but the
   `deleted_at`-filtered CTEs still prevent the infinite-recursion class. Revisit if cascading
   soft-delete lands.

### Verification performed this round

- `dotnet build Ways.slnx` → 0 errors, 0 warnings.
- `dotnet test Ways.slnx` → **`Ways.Domain.Tests` 61/61**, **`Ways.Application.Tests` 72/72**,
  **`Ways.IntegrationTests` 65/65** (+1: the new race test). Stable across 6+ consecutive
  full-solution runs, 0 skipped, 0 failures. Docker daemon reachable throughout.

### Next batch

Ready for judgment-day round 3 (re-judge the fixed diff) before opening the single PR 3.

---

## Slice 4: Web ABMs (PR 4)

## Batch 10

**Scope:** Slice 4 only (`tasks.md` § Slice 4), branch `feat/stage1-slice4-abm-web` off
up-to-date `main` (PRs #2/#4/#6 — slices 1-3 — already merged). No database changes in this
slice; no backend files touched at all (verified with `git status --short` against
`src/Ways.Api`/`Ways.Application`/`Ways.Domain`/`Ways.Infrastructure`/`tests` before
committing anything).

### A real scope gap found before writing any UI code

Before building the "organización" screens (tasks 4.5-4.7), the actual mapped API surface was
audited end to end (`grep` over `src/Ways.Api/Endpoints`, `Program.cs`, and
`Ways.Application/Organizacion`). Result: **there is no `GET`/`PUT` endpoint anywhere for
`tenants`, `empresas`, or `puntos_venta`** — only `POST /api/plataforma/tenants`
(`AprovisionamientoEndpoints.cs`, the provisioning transaction from Slice 3 §3F) exists.
`design.md`'s component map names a `ServicioDeOrganizacion` (`Ways.Application/Organizacion/`)
that was never actually built by any task in `tasks.md` Slices 1-3 — a real gap between the
design's component map and the task breakdown that planned the work, not something this batch
could have silently worked around. Per the apply's hard execution rule #2 ("no backend changes
expected — if you need a backend change, STOP and report instead of making it"), no endpoint
was added.

**Consequence:** tasks 4.5 (Tenants: list/create/suspend), 4.6 (Empresas: list/edit) and 4.7
(PuntosVenta: list/edit) are only partially buildable:
- 4.5's "create" is fully covered by 4.8's provisioning screen (`POST /api/plataforma/tenants`
  does exist and works end to end — see the smoke test below).
- 4.5's "list"/"suspend" and all of 4.6/4.7 have **zero API surface** to build against. Marked
  `[ ]` **BLOCKED** in `tasks.md` with the specific missing endpoints named, not silently
  skipped or faked with dead UI calling routes that don't exist.

This also surfaced a second-order consequence for the parámetros editor (task not numbered in
`tasks.md` but required by the apply's "Scope" section): `ServicioDeParametros.ResolverAsync`/
`ListarAsync`/`EstablecerAsync` all take `idEmpresa` as an explicit argument (ADR-10: no
"empresa actual" in the session yet), and with no endpoint to list a tenant's empresas, the
`Ways.Web` editor has no way to offer a dropdown — it takes the empresa id as a plain number
input, documented inline in the component and in `docs/10-modelo-de-datos.md` §9, not hidden.

### Completed in batch 10

- **4.1** — `src/Ways.Web/src/api/tipos.ts`: types for the 5 tenant catalogs (`AreaListado`/
  `Alta`, `MarcaListado`/`Alta`, `GrupoListado`/`Alta`, `MedioPagoListado`/`Alta`,
  `CategoriaListado`/`Alta`), the 3 fiscal catalogs (read-only), `ParametroListado`/`Alta`/
  `Resuelto` + the `PARAMETROS_CONOCIDOS` registry (mirrors `ParametroConocido`), and
  `SolicitudDeAprovisionamiento`/`ResultadoAprovisionamiento`. Plus `puedeGestionarCatalogos`
  (admin-only, mirrors `Politicas.GestionDeCatalogo`) and `puedeAprovisionarTenants`
  (root-only, mirrors `Politicas.SoloPlataforma`). `src/Ways.Web/src/api/catalogos.ts`: the
  field-descriptor type (`DescriptorDeCatalogo<TListado, TAlta>`, `CampoDescriptor`) and one
  concrete descriptor per catalog (`descriptorAreas`/`Marcas`/`Grupos`/`MediosPago`/
  `Categorias`), each declaring only its own columns beyond `nombre`/`activo` — `idEmpresa`
  never appears in a descriptor (ADR-10 deferred, single-empresa UX is fine per the apply's
  scope).
- **4.2** — `PaginaCatalogo.tsx`: one generic component (list + incluir-inactivos toggle +
  create/edit form + baja lógica), same shape as the existing `Usuarios.tsx` (Box/Cargando/
  ErrorApi conventions, `rounded-0` Bootstrap classes). Renders extra columns/fields purely
  from the descriptor (`campos`), including a `select` type for `MedioPago.Comportamiento`.
- **4.3** — `RutaCatalogo.tsx` resolves `/catalogos/:recurso` to a concrete descriptor via a
  `switch`, not a runtime `Record<string, Descriptor>` lookup: a heterogeneous registry
  indexed by string loses the `TListado`/`TAlta` pairing per catalog under TS's contravariant
  function-parameter checking (tried first, reverted — documented in the file's own comment).
  `CatalogosFiscales.tsx`: read-only lists for the 3 global catalogs (no form, matches the API
  — no POST/PUT/DELETE mapped for them at all, ADR-11 gate #4).
- **4.4** — `Categorias.tsx`: builds the tree client-side from the flat listing
  (`idCategoriaPadre`), shows the computed level per node (1-3), "+ Subcategoría" only offered
  below level 3 (soft UX guardrail — the server's `categoria_profundidad_excedida`/
  `categoria_ciclo`/`categoria_padre_invalido` remain the real backstop, surfaced via
  `ErrorApi.message`).
- **4.8** — `NuevoTenant.tsx`: root-only form for `POST /api/plataforma/tenants`; shows
  `passwordTemporal` once with a "Copiar" button (`navigator.clipboard`, falls back silently
  to on-screen text if permission is denied) and an explicit "no se vuelve a mostrar" warning.
- **Parámetros editor** (scope-required, no `tasks.md` number): `Parametros.tsx` — manual
  `idEmpresa` entry (documented gap above), upsert form restricted to
  `PARAMETROS_CONOCIDOS` (matches `ParametroConocido.Buscar`'s closed-list rejection), a
  "Probar" button hitting `GET /api/parametros/{clave}` to show the resolved value live, and
  the configured-parameters table. No delete: `ParametrosEndpoints.cs` never mapped a
  `DELETE`, so the editor doesn't offer one either.
- **Routing/nav** — `App.tsx`: 5 new routes, each gated by the exact role the API's own policy
  requires (`GestionDeCatalogo` = admin-only ⇒ `rolesPermitidos={[ROL.Admin]}` for the 4
  catalog routes + categorías + parámetros; `SoloPlataforma` = root-only ⇒
  `rolesPermitidos={[ROL.Root]}` for `/organizacion/nuevo-tenant`). One product-level
  narrowing, not a security change: `/catalogos-fiscales`'s real API policy is "any
  authenticated session" (no `RequireAuthorization` policy attached, just the global
  fallback), but the nav/route both restrict it to `[ROL.Root, ROL.Admin]` — POS staff
  (vendedor/supervisor) have no current use for browsing fiscal reference tables, and
  narrowing the *menu* doesn't weaken the API's own authorization if that assumption changes
  later. `Layout.tsx`: catalog nav links generated from `DESCRIPTORES_DE_CATALOGO` (the same
  registry `RutaCatalogo` would have used) instead of hardcoded, so adding a 5th catalog to
  the machine only touches `catalogos.ts`.
- **4.10** — `docs/10-modelo-de-datos.md`: status callouts in §1 (padrones — which catalogs
  have full ABM vs. read-only) and §9 (parámetros — why the empresa id is manual), plus a new
  row in the "Etapas sugeridas" table recording flow-A (subdomain login, ADR-7) as still
  deferred with the reason (needs pre-session tenant resolution + wildcard DNS/TLS at the
  hosting layer).

### 4.9 — smoke verification (honest account)

No browser/e2e automation tool is available in this environment (confirms `design.md`
ADR-17's own gap — `Ways.Web` has no Playwright harness, flagged there as a follow-up, not
something this batch could add without inflating an already-large slice). What **was** run,
real:

1. `npx tsc -b` (Ways.Web) → clean, 0 errors.
2. `npx oxlint` → 1 warning, pre-existing (`AuthContext.tsx`, `only-export-components`,
   unrelated to this batch — same file, not touched here). 0 new warnings.
3. `npx vite build` → succeeds, 273 KB JS / 232 KB CSS bundle, no errors.
4. **Real end-to-end contract smoke test** (not a browser click-through, but a genuine
   integration proof, since a live host + Postgres were available): `docker compose -f
   compose.dev.yml up -d`, `dotnet run --project src/Ways.Api`, then `curl` against the real
   running API with cookie-jar sessions:
   - Logged in as the seeded root (`test@test.com`) → `POST /api/plataforma/tenants` → 201,
     exact `ResultadoAprovisionamiento` shape (`idTenant`/`idEmpresa`/`idPuntoVenta`/
     `idUsuarioAdmin`/`passwordTemporal`) matching `NuevoTenant.tsx`'s type.
   - Root → `GET /api/catalogos/areas` → 403, confirming the nav's admin-only gating for
     catalog routes is correct (root really is locked out at the API, not just hidden in the
     menu).
   - `GET /api/catalogos-fiscales/condiciones-fiscales`/`alicuotas-iva` → exact field-for-field
     match against `CondicionFiscalListado`/`AlicuotaIvaListado` (camelCase confirmed:
     `codigoAfip`, not `codigo_afip`).
   - Logged in as the freshly-provisioned tenant admin → `GET /api/catalogos/medios-pago` →
     the 2 template rows (`Efectivo`/`Transferencia`) with the exact shape
     `MedioPagoListado` expects.
   - `POST /api/catalogos/areas` → 201 with the exact `AreaListado` shape; a duplicate → 409
     `nombre_duplicado` with the same friendly `title` text `PaginaCatalogo.tsx` displays via
     `ErrorApi.message` (no code-to-message mapping needed client-side — the server's
     `ProblemDetails.title` is already the user-facing text, same convention `Usuarios.tsx`
     already uses).
   - `POST /api/catalogos/medios-pago` with an accented name (`"Crédito"`, sent as a real
     UTF-8 file payload) → 201, confirming the JSON round-trips non-ASCII correctly (a first
     attempt sending accents as a raw shell argument hit a `500`/`error_interno` — root-caused
     to Git Bash on Windows mis-encoding the argument as CP-1252 before curl ever sent it, an
     artifact of the terminal, not the API; a real browser's `JSON.stringify` always emits
     valid UTF-8, confirmed by retrying with a proper UTF-8 file and getting 201).
   - `POST /api/catalogos/categorias` (root category) → 201, exact `CategoriaListado` shape.
   - `PUT /api/parametros?idEmpresa=2` (upsert `tolerancia_pago`) → 200, then
     `GET /api/parametros/tolerancia_pago?idEmpresa=2` → resolved value matches, exact
     `ParametroResuelto` shape.
   - Torn down after: `kill` on the API process, `docker compose -f compose.dev.yml down -v`
     (removes the throwaway dev Postgres volume — no smoke-test data left behind).

### Verification performed this batch

- `dotnet build Ways.slnx` → 0 errors, 0 warnings (no backend files changed; run to confirm
  the "no backend changes expected" constraint held).
- `dotnet test Ways.slnx` → **`Ways.Domain.Tests` 61/61**, **`Ways.Application.Tests` 72/72**,
  **`Ways.IntegrationTests` 65/65** — identical to the state at the end of Slice 3's judgment-day
  round 2, confirming zero regression from a slice that touched no backend code. Docker
  daemon reachable throughout.
- Frontend: see "4.9 — smoke verification" above.

### Deferred items (reported, not silent)

- Tasks 4.5 (list/suspend part)/4.6/4.7 — blocked on missing backend endpoints, see above and
  `tasks.md`. Needs a follow-up slice/change to add `ServicioDeOrganizacion` +
  `OrganizacionEndpoints` (list/edit empresas and puntos de venta, list/suspend tenants) before
  those screens can be built.
- The empresa-scoped catalog filter (ADR-10 `DeLaEmpresa`) stays unimplemented per the apply's
  explicit instruction — single-empresa UX is fine for this stage.
- Flow-A subdomain login (ADR-7) stays unimplemented — recorded in `docs/10-modelo-de-datos.md`
  this batch, no functional change.
- No Playwright/e2e harness (ADR-17) — the smoke test above is the closest available proxy in
  this environment.

### Commits (work units, branch `feat/stage1-slice4-abm-web`, no push, no PR)

1. `feat(web): agregar tipos y cliente de catalogos/parametros/aprovisionamiento`
2. `feat(web): agregar el componente generico PaginaCatalogo (ADR-11)`
3. `feat(web): agregar la pantalla de arbol de categorias (escape hatch)`
4. `feat(web): agregar la vista de catalogos fiscales de solo lectura`
5. `feat(web): agregar el editor de parametros operativos (ADR-13)`
6. `feat(web): agregar el alta de tenants (aprovisionamiento, ADR-16)`
7. `feat(web): conectar las rutas y el menu de la etapa 4`
8. `docs: actualizar doc 10 con el estado de la etapa 4 y el gap de organizacion`
9. `docs(sdd): registrar el batch 10 (etapa 4) — tasks.md + apply-progress.md` (this commit)

### Next batch

Slice 4 is functionally complete for everything the merged API surface supports, verified
build-clean and contract-clean end to end. Ready for judgment-day review before PR 4 — but PR 4
as originally scoped (`tasks.md`'s "Suggested Work Units" table) cannot close 4.5/4.6/4.7
without a prior backend change; recommend the coordinator decide whether to (a) open PR 4 now
covering only the buildable ABM surface and file a follow-up change for organization
list/edit/suspend, or (b) hold PR 4 and add the missing endpoints first. Not decided by this
batch — reported for the coordinator per the apply's hard execution rule.

---

## Batch 11 — close the organization gap inside Slice 4 (user decision, 2026-08-02)

**Trigger:** the coordinator authorized closing batch 10's organization gap NOW, inside this
same slice, as an explicit scope extension — "one judgment-day + one PR closes the whole
stage." Explicitly gated on no schema change being needed; none was (`tenants`/`empresas`/
`puntos_venta` and their `DbSet`s already existed from Slice 1 / Slice 3F). No DB CHANGE GATE
applies to this batch.

**Status:** `done`. Backend (`ServicioDeOrganizacion` + `OrganizacionEndpoints`, new tasks.md
section 4B) implemented with unit + integration tests, then 4.5-4.7 unblocked and the
`Parametros.tsx` manual-`idEmpresa` limitation fixed. Full suite green (61/84/73, +12
application / +8 integration vs. batch 10), stable across 2 consecutive runs. Extended smoke
test proved every new endpoint end to end against a live Postgres + the real API host.

### Backend: `ServicioDeOrganizacion` + `OrganizacionEndpoints`

- **Contracts** (`Ways.Application/Organizacion/Contratos.cs`): `TenantListado`/`TenantEdicion`
  (only `Nombre` is editable here — `Estado` changes through the dedicated
  suspend/reactivate actions, not this general edit), `EmpresaListado`/`EmpresaEdicion`,
  `PuntoVentaListado`/`PuntoVentaEdicion` (`IdEmpresa` deliberately not editable — structural,
  not descriptive).
- **`ServicioDeOrganizacion`**: list/detail/edit for the three entities, plus
  suspend/reactivate for tenants. Empresas/puntos de venta reuse the exact
  `ServicioDeUsuarios.BuscarAsync` pattern: the EF tenant filter (+ RLS underneath) already
  makes a cross-tenant row invisible, and `PoliticaDeRoles.ValidarAlcanceDeTenant` is the
  explicit domain-layer defense-in-depth on top (ADR-8, same 404-not-403 rule). Listing needed
  **zero** extra scoping code: the existing `WaysDbContext` tenant filter already resolves
  "platform sees everything, a tenant session sees only its own" via the same `esPlataforma ||
  id_tenant == tenantActual.Id` predicate every other `EntidadTenant`/`Tenant` query already
  uses — `db.Empresas.OrderBy(...).ToListAsync()` is correct for both a root session and a
  tenant admin session, no `IgnoreQueryFilters` anywhere in this service.
  `CambiarEstadoTenantAsync` (backing both `SuspenderTenantAsync`/`ReactivarTenantAsync`) is
  idempotent (repeating the same action is a no-op, not an error) and rejects any transition
  once a tenant is `EstadoTenant.Baja` (409 `tenant_dado_de_baja`) — alternating
  Activo/Suspendido must never accidentally reactivate a tenant that was properly decommissioned.
  The three tenant-scoped methods (`ActualizarTenantAsync`/`Suspender`/`Reactivar`) trust the
  API's own `Politicas.SoloPlataforma` for authorization, with no duplicate in-service role
  check — same style choice as the existing `ServicioDeAprovisionamiento` (the closest analog:
  another platform-only service), not the `ServicioDeUsuarios` style (which layers an internal
  `ExigirPermisoDeGestion()` on top of its own API policy). Documented explicitly in the
  class's XML doc so the asymmetry with `ServicioDeUsuarios` reads as a deliberate choice, not
  an oversight.
- **`OrganizacionEndpoints`**: `MapearOrganizacion()` maps a *second* `MapGroup` onto
  `/api/plataforma/tenants` (`AprovisionamientoEndpoints` already owns `POST /` there) —
  confirmed this is a supported ASP.NET Core pattern (different verb+template combinations
  don't conflict) via the passing test suite, not just by inspection. New
  `Politicas.GestionDeOrganizacion` (root or admin — identical claim shape to
  `GestionDeUsuarios`, kept as its own named policy rather than reused, matching the existing
  one-named-policy-per-concern convention even where claim sets overlap).
- **No new `db-error-backstops` work needed.** No new unique index or FK was introduced (the
  schema is unchanged) — the existing generic backstops (`fk_*` → 400 `referencia_invalida`,
  the `_nombre`/`_codigo` family classifier) already cover anything this surface could trigger,
  and neither `TenantEdicion`/`EmpresaEdicion`/`PuntoVentaEdicion` write a column that
  participates in any unique constraint.
- **DI**: `ServicioDeOrganizacion` registered scoped in `Ways.Application.DependencyInjection`,
  same lifetime as every other application service.

### Tests

- **Unit** (`ServicioDeOrganizacionTests.cs`, InMemory, 12 cases): own-empresa edit OK,
  cross-tenant empresa/punto-de-venta → 404 (both `Obtener`/`Actualizar`), platform full
  access (list + edit any empresa), tenant-scoped listing only returns the caller's own rows,
  tenant name edit, suspend→reactivate alternation, idempotent re-suspend, `Baja` rejects both
  actions (409), and a validation case (empty `RazonSocial` → 400). Reused the exact
  `ContextoFijo`/`RelojFijo`/`CrearContexto` helper shapes from `ServicioDeUsuariosTests.cs`
  for consistency.
- **Integration** (`OrganizacionTests.cs`, real Postgres, 8 cases): admin edits own empresa OK
  via real HTTP; admin gets 404 editing another tenant's empresa; admin gets 403 both
  suspending a tenant and listing tenants (platform-only routes); platform lists **and**
  edits any empresa/punto de venta (proves the cross-tenant read AND write both work for a
  root session, not just read); the tenant-name edit; and the big one —
  `PlataformaSuspendeUnTenantYSuUsuarioPierdeLaSesionEnLaProximaRequest` extends Slice 2's
  `SuspenderElTenantCortaLaSesionActivaEnLaProximaRequest` (which suspended by writing
  `tenant.Estado` directly via a throwaway `WaysDbContext`) to suspend through the **real**
  `POST .../suspender` endpoint instead, then proves the same three things end to end: the
  admin's already-open session gets cut on its next request (`OnValidatePrincipal`
  revalidation, ADR-2), a fresh login attempt is blocked (403 `tenant_suspendido`), and — new
  compared to Slice 2's version — `ReactivarUnTenantSuspendidoPermiteVolverAIniciarSesion`
  proves the reverse: reactivating through the endpoint lets a previously-blocked login
  succeed again.
- **A real, if previously invisible, test-only bug found and fixed while writing the
  integration tests**: `TenantListado` carries an `EstadoTenant` enum, and reading it back
  with a fresh `new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } }`
  (the same pattern `CatalogosTests.OpcionesJson` already used for
  `TipoComprobanteListado.Clase`) silently deserialized every property to its C# default
  (`Nombre = null`, `Estado = Activo` — the enum's first member) instead of throwing, because
  a freshly-constructed `JsonSerializerOptions` does **not** default
  `PropertyNameCaseInsensitive` to `true` — unlike the zero-argument
  `HttpContent.ReadFromJsonAsync<T>()` overload this file's *other* DTOs (without an enum
  field) already relied on, which does. Confirmed empirically with a small throwaway console
  probe before touching any test code, to avoid guessing. Two tests failed with exactly this
  signature (`Nombre` read back as `null`, `Estado` read back as `Activo` regardless of the
  real value) until `PropertyNameCaseInsensitive = true` was added explicitly. **The same bug
  already existed, latent, in `CatalogosTests.OpcionesJson`** — invisible there because
  `LosCatalogosFiscalesSonDeSoloLecturaParaUnTenant` only asserts `Assert.NotEmpty(tipos!)`
  (list count, not field content) on the one DTO that uses those options. Fixed in both files
  in the same commit, with a comment on each explaining why the bare `ReadFromJsonAsync<T>()`
  calls elsewhere in the same files never needed this.

### Frontend: unblocking 4.5-4.7 and fixing the parametros gap

- **`api/organizacion.ts`** + `tipos.ts` additions: `TenantListado`/`Edicion`,
  `EmpresaListado`/`Edicion`, `PuntoVentaListado`/`Edicion`, `clienteDeOrganizacion`.
- **`Tenants.tsx`** (root-only): list + inline nombre edit + suspender/reactivar buttons
  (only the applicable one shown per row, based on `estado`), a "Nuevo tenant" link to the
  existing provisioning screen. No baja action — `EstadoTenant.Baja` has no dedicated action
  in this batch's scope (matches the backend, which also doesn't expose one).
- **`Empresas.tsx`**/**`PuntosVenta.tsx`** (root or admin): list + edit descriptive fields,
  no alta/baja (both stay platform-only via provisioning). Show a "Tenant" column only for a
  root session (`usuario.rolId === ROL.Root`) — a tenant admin's own list is implicitly
  single-tenant, showing the column would be noise.
- **`Parametros.tsx`**: replaced the manual "Id de empresa" number input (documented
  limitation from batch 10) with a `<select>` populated by `GET /api/empresas` — auto-selects
  the first (only, in this stage) empresa on load. Also upgraded the "punto de venta" field
  from a manual id input to a `<select>` populated by `GET /api/puntos-venta`, filtered
  client-side to the selected empresa's own points — a small UX bonus beyond what was strictly
  asked, made possible by the same new endpoint.
- **Routing/nav**: `/organizacion/tenants` (root), `/organizacion/empresas` and
  `/organizacion/puntos-venta` (root or admin) — each gated by the exact role its backend
  policy requires, same convention as every other Slice 4 route.

### Verification performed this batch

- `npx tsc -b` (Ways.Web) → clean, 0 errors.
- `npx oxlint` → 1 warning, pre-existing (`AuthContext.tsx`), 0 new.
- `npx vite build` → succeeds, 287 KB JS / 232 KB CSS.
- `dotnet build Ways.slnx` → 0 errors, 0 warnings.
- `dotnet test Ways.slnx` → **`Ways.Domain.Tests` 61/61**, **`Ways.Application.Tests` 84/84**
  (72 + 12 new), **`Ways.IntegrationTests` 73/73** (65 + 8 new). Stable across 2 consecutive
  full-solution runs, 0 skipped, 0 failures. Docker daemon reachable throughout.
- **Extended real end-to-end smoke test** against a live Postgres + the real API host (a
  throwaway container on a free port, `-p 5555:5432` — the repo's default dev port 5432 was
  occupied by an unrelated project's container on this machine; not a code change, an
  environment accommodation, torn down after): logged in as the seeded root, listed/edited a
  tenant/empresa/punto de venta, provisioned a second tenant, then as that tenant's admin
  confirmed `GET /api/empresas` scopes to the caller's own tenant, a cross-tenant `PUT` on
  another tenant's empresa returns 404, `GET /api/plataforma/tenants` returns 403 for a
  non-root actor, and the full `suspender` (root) → login blocked (403) →
  `reactivar` (root) → login succeeds cycle, all through real HTTP against a real database.
  Every JSON shape matched the TS contracts exactly (camelCase field names confirmed:
  `nombreFantasia`, `idPuntoVenta`, etc.).

### Deferred items (reported, not silent)

- None outstanding for the organization surface this batch added. `Baja` (hard decommission,
  as opposed to `Suspendido`) has no dedicated UI action — not asked for, and the backend
  doesn't expose one either; a tenant that's `Baja` simply can't be suspended/reactivated
  through the existing actions (409), by design.
- The empresa-scoped catalog filter (ADR-10 `DeLaEmpresa`) remains deferred, unchanged from
  batch 10 — still out of this stage's scope.
- Flow-A subdomain login (ADR-7) remains deferred, unchanged from batch 10.
- No Playwright/e2e harness (ADR-17) — unchanged; the extended smoke test above is still the
  closest available proxy in this environment.

### Commits (work units, branch `feat/stage1-slice4-abm-web`, no push, no PR)

10. `feat(organizacion): agregar ServicioDeOrganizacion + endpoints de tenants/empresas/puntos_venta`
11. `test(integration): cubrir organizacion punta a punta y corregir un bug latente de deserializacion`
12. `feat(web): agregar tipos y cliente de organizacion (tenants/empresas/puntos_venta)`
13. `feat(web): agregar la pantalla de tenants (listar/editar/suspender/reactivar)`
14. `feat(web): agregar las pantallas de empresas y puntos de venta`
15. `fix(web): resolver la empresa del editor de parametros con un selector real`
16. `feat(web): conectar las rutas y el menu de organizacion`
17. `docs(sdd): registrar el batch 11 — cierre del gap de organizacion` (this commit, next)

### Next batch

Slice 4 is now code-complete for the **entire** stage, including the organization surface that
batch 10 found missing. Nothing left gated on user decisions, backend gaps, or the environment.
Ready for judgment-day review (dual blind review, per `CLAUDE.md`'s PR validation gate) before
opening the single PR 4 (`size:exception`, per the batch 11 delivery decision, tasks.md) —
closing the entire `stage-1-organization-and-catalogs` change.

---

## Batch 12 — judgment-day round 1 polish on Slice 4 (branch `feat/stage1-slice4-abm-web`)

**Trigger:** first judgment-day round (dual blind review) over the batch 10–11 diff. One
triaged-real item, three confirmed-minor items, one suggestion accepted, one symmetric-test
suggestion accepted. All approved by the user 2026-08-02; this batch applies them.

### Completed in batch 12

1. **[Triaged real] Snake_case error-code keys.** `ServicioDeOrganizacion.Normalizar`/
   `NormalizarOpcional` and `ServicioDeAprovisionamiento.Normalizar` both interpolated the
   human-readable field label directly into the machine `codigo` (e.g. `"razón social"` →
   `"razón social_requerido"`), the same pattern already fixed once before in
   `ServicioDeUsuarios.Normalizar` at the time it was written — this one just never got the
   memo. Both private helpers now take a separate stable snake_case `codigo` parameter
   (`razon_social`, `cuit`, `nombre_punto_venta`, `nombre_tenant`, `nombre_fantasia`,
   `domicilio`, `horario`, `whatsapp`, `instagram`, `facebook`, `sitio_web`, `punto_venta`,
   `mail_admin`) independent from the human `campo` label used only in the message text.
   `ServicioDeUsuarios.Normalizar` was checked and confirmed already safe (its `campo`
   arguments — `"usuario"`, `"mail"` — are already snake_case-safe single words), so it needed
   no change; documented here so the scope-rule check is traceable. Test:
   `ServicioDeOrganizacionTests.ActualizarUnaEmpresaConRazonSocialVaciaEsRechazada` now also
   asserts `error.Codigo == "razon_social_requerido"` (previously only asserted the 400
   status).
2. **[Confirmed minor] Categoria parent dropdown filter.** `Categorias.tsx`'s
   `FormularioCategoria` only excluded the node itself from the "categoría padre" `<select>`,
   letting a user pick one of the node's own descendants (a cycle the backend rejects, ADR-12)
   or a level-3 node (which can never have children under `PROFUNDIDAD_MAXIMA`). Now takes the
   already-computed `arbol: Nodo[]` (was the flat `items` list) and filters through two new
   helpers, `idsDeSubarbol` (walks the tree to collect a node's descendant ids) and `aplanar`
   (flattens the tree depth-first, preserving hierarchy order) — the select now excludes self,
   the full descendant subtree, and any `nivel === PROFUNDIDAD_MAXIMA` node, with a light
   indent (`—` repeated by depth) in the option label as a readability bonus.
3. **[Confirmed minor] Stale comment in `NuevoTenant.tsx`.** The doc comment still said "no
   endpoint exists yet to list/suspend tenants" — false since batch 11 shipped `Tenants.tsx`
   in this same branch. Updated to point at `Tenants.tsx` instead.
4. **[Suggestion] Dead-branch comment in `Parametros.tsx`.** The comment described fetching
   "the caller's own empresa if tenant admin, all of them if platform," but the route is
   Admin-only (`App.tsx`, `rolesPermitidos={[ROL.Admin]}`) — the platform branch is
   unreachable from this screen. Corrected to describe only the reachable admin-only path.
5. **[Suggestion] Symmetric PV test.** Added
   `ServicioDeOrganizacionTests.UnAdminNoPuedeEditarUnPuntoDeVentaDeOtroTenant` (unit,
   mirrors the existing `UnAdminNoPuedeEditarUnaEmpresaDeOtroTenant`) and
   `OrganizacionTests.UnAdminRecibe404AlEditarElPuntoDeVentaDeOtroTenant` (HTTP-level, mirrors
   `UnAdminRecibe404AlEditarLaEmpresaDeOtroTenant`) — both assert 404 per ADR-8, closing the
   one asymmetry left between the empresa and punto-de-venta cross-tenant-edit coverage.

### Verification performed this batch

- `npx tsc -b` (Ways.Web) → clean, 0 errors.
- `npx oxlint` → 1 warning, pre-existing (`AuthContext.tsx`, unrelated to this batch), 0 new,
  exit code 0.
- `dotnet build Ways.slnx` → 0 errors, 0 warnings.
- `dotnet test Ways.slnx` → **`Ways.Domain.Tests` 61/61**, **`Ways.Application.Tests` 85/85**
  (84 + 1 new: the PV cross-tenant-edit unit test), **`Ways.IntegrationTests` 74/74** (73 + 1
  new: the PV cross-tenant-edit HTTP test). 0 failures, 0 skipped.

### Commits (work units, branch `feat/stage1-slice4-abm-web`, no push, no PR)

18. `fix(organizacion): separar el codigo snake_case del label humano en los errores de validacion`
19. `fix(web): filtrar el subarbol y el nivel 3 del selector de categoria padre`
20. `docs(web): corregir comentarios desactualizados en NuevoTenant y Parametros`
21. `test(organizacion): agregar la cobertura simetrica de punto de venta cross-tenant`
22. `docs(sdd): registrar el batch 12 — polish de judgment-day ronda 1 en el slice 4` (this
    commit, next)

### Next batch

Ready for a re-judged clean round over the batch 10–12 diff, then the single PR 4 per the
batch 11 delivery decision.
