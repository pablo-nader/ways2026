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
