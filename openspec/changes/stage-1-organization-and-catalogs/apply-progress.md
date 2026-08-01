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

### Next batch

Nothing left gated on user decisions for Slice 1 itself. What remains is purely
environmental: get a reachable Docker daemon and run
`dotnet test tests/Ways.IntegrationTests` to get real pass/fail signal on the 7 isolation
cases (including the new `tenants`-table coverage) before this slice's PR goes through
judgment-day review. If they pass, Slice 1 is functionally complete; if any fail, fix and
re-run before proceeding to Slice 2.
