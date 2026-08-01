# Apply progress: Stage 1 — Organization and Catalogs

## Batch 1 (this apply)

**Scope:** Slice 1 only (`tasks.md` § Slice 1: Tenancy plumbing + org tables + RLS), stopped
at DB CHANGE GATE #1 (task 1.6) as instructed. No EF Core migration was generated or applied.

**Branch:** `feat/stage1-slice1-tenancy` (off `main`). Not pushed, no PR opened — judgment-day
review happens in a separate follow-up round per `CLAUDE.md`.

**Status:** `done` for everything up to and including the gate; `blocked` on 1.8 (migration
generation) pending explicit user approval of the model summary below.

### Completed (see tasks.md for the authoritative checklist)

- 1.1, 1.2 — apply-time verification of ADR-6 / ADR-9. Both confirmed on the pinned
  EF Core 10.0.10 / Npgsql 10.0.3; no fallback needed. Verified two ways: (a) reflection over
  the shipped `Microsoft.EntityFrameworkCore.dll` confirming the keyed `SetQueryFilter`/
  `HasQueryFilter(string, LambdaExpression)` overloads exist; (b) a runnable probe building
  the actual composite-FK/query-filter shapes this design needs and asserting behavior
  (see full detail in the apply's final chat report).
- 1.3–1.5 — Domain: `EntidadTenant`, `Tenant`/`Empresa`/`PuntoVenta`/`EstadoTenant`,
  `PoliticaDeRoles` additions (`ActorDeGestion`, `ValidarAlcanceDeTenant`,
  `RolesAsignablesPor(actor, esDePlataforma)`) + unit tests.
- 1.7 — `RlsMigrationBuilderExtensions` (`CrearFuncionesDeContextoDeTenant`,
  `HabilitarRlsDeTenant`).
- 1.9–1.14 — Infrastructure: `ITenantActual`/`ModoDeAcceso`, `TenantActualDeSesion`,
  `TenantActualFijo`, `InterceptorDeContextoDeTenant`, EF configurations for the 3 org
  entities, named query filters (`"BajaLogica"`, `"Tenant"`) on `WaysDbContext`,
  `SaveChangesAsync` tenant stamping/tamper rejection, `OnValidatePrincipal` tenant
  resolution wiring in `Ways.Api/Program.cs`, startup `rolsuper`/`rolbypassrls` guard in
  `InicializadorDeBaseDeDatos`.
- 1.15 — Org seed method added to `InicializadorDeBaseDeDatos` (code only; cannot execute
  until 1.8's migration exists).
- 1.16 — `tests/Ways.IntegrationTests` scaffolded (`WebApplicationFactory` + `Testcontainers.PostgreSql`
  fixture). `ways_app` role provisioning left as a `TODO` (nothing to grant on yet).
- 1.17 — Isolation test *names* stubbed with `[Fact(Skip = ...)]` in
  `AislamientoDeTenantTests.cs`, one per design.md's Test Strategy assertion. Bodies blocked
  on 1.8. The EF-filter-layer half of this guarantee has fast, currently-green coverage via
  `Ways.Application.Tests/Persistencia/FiltroDeTenantTests.cs` (InMemory provider).
- 1.18 — Regression confirmed: `Ways.Domain.Tests` and `Ways.Application.Tests` unedited
  (only a package reference added to the latter's `.csproj`), full suite green.

### Blocked / not started

- 1.6 — Model summary presented in the apply's final report; **awaiting explicit user
  approval**.
- 1.8 — Generate migration 1 (`Organizacion`). **Blocked on 1.6.**

### Deferred within already-completed tasks (reported, not silent)

- `TenantActualDeSesion.Suplantar` (tenant impersonation, ADR-16) not implemented: not
  exercised by any Slice 1 test surface; belongs with `ServicioDeAprovisionamiento`
  (not tasked in Slice 1).
- `InterceptorDeContextoDeTenant`'s `is_local: true` variant (mid-transaction re-apply,
  ADR-3/ADR-16) deferred with the above.
- The hand-written `"Tenant"` query filter for `Usuario` (ADR-6) deferred to Slice 2:
  `usuarios.id_tenant` does not exist yet.
- `OnValidatePrincipal`'s tenant resolution reads `ways:id_tenant` defensively (the claim
  isn't emitted by any login path yet — that's Slice 2's retrofit). Until then every
  non-root session resolves to `ModoDeAcceso.Ninguno` (fail-closed), which has no
  functional effect in this slice since no tenant-scoped endpoint exists yet.
- Seed literal values (`tenant.Nombre = "Ways"`, `empresa.RazonSocial = "Ways"`,
  puntos de venta `"Local 1"` / `"Local 2"`) are placeholders — doc 09/10 don't specify the
  real names. Flagged for user confirmation before 1.8.

### Next batch

Resume with 1.8 once the user approves the model summary, then continue with the rest of
Slice 1's now-unblocked verification (1.15's seed actually running, 1.17's real bodies,
`ways_app` role provisioning in the fixture).
