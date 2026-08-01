# Tasks: Stage 1 — Organization and Catalogs

## Review Workload Forecast

| Field | Value |
|-------|-------|
| Estimated changed lines | ~2,900–4,200 total (incl. EF migration boilerplate) |
| 400-line budget risk | High |
| Chained PRs recommended | Yes |
| Suggested split | PR 1 → PR 2 → PR 3 → PR 4 (design's 4 cut points) |
| Delivery strategy | chained PRs (user decision, 2026-07-31) |
| Chain strategy | stacked-to-main |

Decision needed before apply: No — resolved: chained PRs, stacked-to-main.
Chained PRs recommended: Yes (accepted)
Chain strategy: stacked-to-main — each slice PR merges to main in order; split further within a slice if reviewable lines exceed ~400.
PR validation gate: every PR must pass a clean judgment-day round (dual blind review, fix, re-judge) before merge — see CLAUDE.md.
400-line budget risk: High (mitigated by the split above)

### Suggested Work Units

| Unit | Goal | Likely PR | Notes |
|------|------|-----------|-------|
| 1 | Tenancy plumbing + org tables + RLS | PR 1 | ~900–1,300 lines. Base: `main`. Gate #1+#2 of migration sequencing land here. Independently mergeable: adds new tables/infra, touches nothing existing except `PoliticaDeRoles` additively. |
| 2 | usuarios retrofit + suspension + mail login | PR 2 | ~500–700 lines. Depends on PR 1 (`usuarios.id_tenant` FK → `tenants`). Base per chosen chain strategy. |
| 3 | Catalogs + parametros | PR 3 | ~900–1,300 lines. Depends on PR 1 (tenant plumbing); does not require PR 2. Gates #3, #4, #5. |
| 4 | Web ABMs | PR 4 | ~600–900 lines. Depends on PR 1–3 (consumes the API surface they expose). |

Each slice above satisfies the "independently mergeable, clear start/finish/verification/rollback" requirement: start = base branch state, finish = its own migration(s) + tests green, verification = its own unit/integration tests, rollback = its own down-migration(s) and route removal (proposal.md § Rollback Plan).

---

## Slice 1: Tenancy plumbing + org tables + RLS (PR 1)

**Start**: `main`. **Finish**: migration 1 applied, RLS proven, org tables seeded, isolation integration tests green. **Rollback**: down-migration 1 (additive only, nothing pre-existing touched).

### 1A. Apply-time verification (must resolve before schema work — ADR-6, ADR-9)

- [x] 1.1 Verify EF Core 10 keyed `SetQueryFilter` overload exists on pinned EF 10.0.10; report result. If absent, apply ADR-6 fallback (composed filter + explicit `.Where` re-application) and report the deviation. *(spec: tenant-organization / Tenant Isolation Enforcement)* — **Confirmed present**, no fallback. See apply report.
- [x] 1.2 Verify EF Core 10 does not force `IdTenant` nullable for the optional composite FK (`puntos_venta→empresas`, catálogo→`empresas`); report result. If it does, apply ADR-9 fallback (single-column FK + domain/RLS integrity) and report the deviation. *(spec: tenant-organization / Organization Hierarchy Tables)* — **Confirmed `IdTenant` stays `NOT NULL`**, no fallback. See apply report.

### 1B. Domain

- [x] 1.3 [P] Add `EntidadTenant : EntidadBase { IdTenant }` in `Ways.Domain/Common`. *(ADR-1)*
- [x] 1.4 [P] Add `Tenant`, `Empresa`, `PuntoVenta`, `EstadoTenant` in `Ways.Domain/Organizacion`. *(spec: tenant-organization / Organization Hierarchy Tables)*
- [x] 1.5 Add `ActorDeGestion`, `ValidarAlcanceDeTenant`, `RolesAsignablesPor` to `PoliticaDeRoles` (pure, DB-free) + unit tests (admin↔same tenant OK; admin→other tenant not found; admin→platform forbidden; platform root→any OK; assignable-roles split). *(spec: usuarios-tenant-scoping / PoliticaDeRoles Tenant Rule)*

### 1C. DB CHANGE GATE #1 — BLOCKING

- [x] 1.6 **STOP.** Present migration 1 (`Organizacion`) model summary to the user — tables, columns, AKs, composite FK, RLS functions/policies — and wait for explicit approval before generating anything. No exceptions (`CLAUDE.md`). — **Approved by the user 2026-07-31**, exactly as presented, plus an explicit decision to add RLS to `tenants` itself with the analogous policy (see 1.8's note).

### 1D. Infrastructure

- [x] 1.7 Implement `RlsMigrationBuilderExtensions.HabilitarRlsDeTenant(tabla)` (ENABLE + FORCE + policy block). *(ADR-15)*
- [x] 1.8 Generate migration 1 (`Organizacion`): `estado_tenant` enum, `tenants`/`empresas`/`puntos_venta`, AKs, composite FK, `app_tenant_actual`/`app_modo`/`app_es_plataforma`, RLS via 1.7 — only after 1.6 is approved. *(spec: tenant-organization / Organization Hierarchy Tables)* — Generated with `dotnet ef migrations add Organizacion`, then hand-added the RLS calls the scaffolder doesn't know how to emit: `CrearFuncionesDeContextoDeTenant()` + `HabilitarRlsDeTenant()` on **all three** tables including `tenants` (per gate approval decision 2), and the matching `DROP FUNCTION`s in `Down()`. File: `src/Ways.Infrastructure/Persistencia/Migraciones/20260801011312_Organizacion.cs`.
- [x] 1.9 [P] Implement `ITenantActual`, `TenantActualDeSesion` (scoped), `TenantActualFijo` (non-HTTP entry points). *(ADR-2)* — Suplantar/impersonation (ADR-16) deferred to when `ServicioDeAprovisionamiento` lands; not needed by this slice.
- [x] 1.10 [P] Implement `InterceptorDeContextoDeTenant : DbConnectionInterceptor` (`set_config` on connection open; `is_local: true` inside provisioning transactions). *(ADR-3)* — session-level `set_config` on connection open implemented; the `is_local: true` provisioning-transaction variant deferred with 1.9's Suplantar (not exercised by this slice).
- [x] 1.11 Wire `OnValidatePrincipal`: populate `TenantActualDeSesion` from claims, revalidate tenant `estado` (suspendido/baja ⇒ reject + sign-out). *(spec: tenant-organization / Tenant Suspension Enforcement)* — `ways:id_tenant` claim not yet emitted by login (slice 2); wired defensively, fails closed to `Ninguno` until slice 2 adds the claim. See apply report.
- [x] 1.12 Register named query filters `"BajaLogica"`/`"Tenant"` in `OnModelCreating` per 1.1 result. *(ADR-6)* — `Usuario`'s hand-written variant deferred to slice 2 (`usuarios.id_tenant` doesn't exist yet).
- [x] 1.13 Add `SaveChangesAsync` `IdTenant` stamping on Added + tamper rejection on Modified. — judgment-day batch 4: extended to all four public `SaveChanges*` entry points (sync included), plus a platform-mode check that `Added` rows carry a non-zero `IdTenant`.
- [x] 1.14 Add startup role check (`rolsuper`/`rolbypassrls`) in `InicializadorDeBaseDeDatos`: throw in Production, warn elsewhere. *(ADR-5)* — judgment-day batch 4: added the analogous ADR-3 connection-invariants guard (`Multiplexing`/`NoResetOnClose`) alongside it, via the new `InvariantesDeConexion` pure helper.

### 1E. Seed + tests

- [x] 1.15 Extend `InicializadorDeBaseDeDatos` with tenant 1 / empresa 1 / 2 locales seed, platform mode. *(spec: tenant-organization / Organization Hierarchy Tables, Scenario: Seed data present)* — code complete; the migration exists and the isolation suite now proves platform-mode inserts work end-to-end against real Postgres (`ElFiltroDeEfNuncaDevuelveFilasDeOtroTenant` et al. seed via the same platform-mode path). Seed literal names ("Ways" / "Local 1" / "Local 2") stay as placeholders per the gate approval (decision 3).
- [x] 1.16 Scaffold `tests/Ways.IntegrationTests`: `WebApplicationFactory` + `Testcontainers.PostgreSql`, two DB roles (migration owner + `ways_app` `NOSUPERUSER NOBYPASSRLS`). *(ADR-17)* — fixture runs the migration as `ways_owner` and provisions `ways_app` (`LOGIN NOSUPERUSER NOBYPASSRLS` + data-only `GRANT`s) directly against the container in `InitializeAsync`; the API host (`ConfigureWebHost`) connects as `ways_app`, never as the owner. Fixed in batch 3: `CrearContextoDeAplicacion` now also registers `InterceptorDeContextoDeTenant` (was missing, see 1.17).
- [x] 1.17 Integration tests — isolation core: EF filter blocks cross-tenant read; RLS blocks raw-SQL/`IgnoreQueryFilters` read; `WITH CHECK` rejects cross-tenant insert; fail-closed on unset GUC; no GUC leakage across pooled connections; policy-coverage query (ADR-15) returns zero rows (covering `tenants` too, per gate decision 2); `ways_app` has neither `rolsuper` nor `rolbypassrls`. *(spec: tenant-organization / Tenant Isolation Enforcement)* — **Verified in runtime, 7/7 green**, stable across two consecutive runs. Docker daemon came up mid-session; the first real run surfaced a genuine bug (see batch 3 in `apply-progress.md`): the test harness's `CrearContextoDeAplicacion` never registered `InterceptorDeContextoDeTenant`, so no `WaysDbContext` built through it ever ran `set_config` — GUCs stayed unset and even platform-mode seeding tripped `WITH CHECK`. **Confirmed test-harness bug, not a production bug**: `Ways.Infrastructure/DependencyInjection.cs` wires the interceptor correctly via DI. Fixed by adding `.AddInterceptors(new InterceptorDeContextoDeTenant(tenantActual))` to the helper, mirroring production wiring exactly. Judgment-day batch 4 added `WithCheckRechazaUnUpdateQueReasignaIdTenant`, the analogous `UPDATE`-reassignment case (was only covered for `INSERT`) — suite now 8/8.
- [x] 1.18 Regression: confirm existing `Ways.Domain.Tests`/`Ways.Application.Tests` are unedited and green. — Unedited. Full suite green: `Ways.Domain.Tests` 30/30, `Ways.Application.Tests` 14/14, `Ways.IntegrationTests` 7/7. 0 build warnings. Judgment-day batch 4 added tests on top (see `apply-progress.md`): full suite now 30/18/8, still 0 failures.

---

## Slice 2: usuarios retrofit + suspension + mail login (PR 2)

**Depends on**: Slice 1 (`usuarios.id_tenant` FK → `tenants`). **Start**: PR 1 merged/branch. **Finish**: migration 2 applied, mail login live, suspension enforced, tests green. **Rollback**: down-migration 2; login contract revert is a route-body change only.

### 2A. DB CHANGE GATE #2 — BLOCKING

- [x] 2.1 **STOP.** Present migration 2 (`UsuariosMultiTenant`) model summary — additive `id_tenant` column, `(id_tenant, usuario) NULLS NOT DISTINCT` index rebuild and why, the two login-mode policies and why — and wait for explicit approval. — **Approved by the user 2026-08-01, exactly as presented**: `id_tenant` column + FK, per-tenant unique index with `NULLS NOT DISTINCT`, RLS standard policy plus the two login-mode policies on `usuarios`, and the backfill policy (existing `root` → `id_tenant NULL`, any other existing user → tenant 1).

### 2B. Migration + backfill

- [x] 2.2 Generate migration 2 (only after 2.1 approved): `usuarios.id_tenant NULL` + FK, rebuild `ux_usuarios_usuario`, `usuarios` RLS policies (tenant + `usuarios_login_lectura`/`_actualiza`). *(spec: usuarios-tenant-scoping / id_tenant Column Semantics; usuario Uniqueness Is Scoped Per Tenant)* — Generated with `dotnet ef migrations add UsuariosMultiTenant`, then hand-added the RLS calls (same technique as migration 1): `HabilitarRlsDeTenant("usuarios")` + the two login-mode `CREATE POLICY` statements in `Up()`, matching drops (plus `DISABLE ROW LEVEL SECURITY`) in `Down()`. File: `20260801154718_UsuariosMultiTenant.cs`.
- [x] 2.3 Backfill in `InicializadorDeBaseDeDatos`: existing `root` stays `id_tenant NULL`; other existing users → tenant 1. *(ADR-14)* — `BackfillDeUsuariosAsync` added, runs after `SembrarOrganizacionAsync` (needs tenant 1 to exist): assigns the lowest-id tenant to every `Usuario` with `IdTenant == null && RolId != Root`. Idempotent (no-op on a fresh install and on a redeploy already backfilled). **Judgment-day batch 9 (round 2), CRITICAL fix**: the original implementation loaded the rows and assigned `IdTenant` through the `ChangeTracker`, which `WaysDbContext.EstamparTenant`'s tamper guard rejected unconditionally for any `Modified` entity with `IdTenant` touched — crashing the host on every real upgrade with a pre-existing non-root account (fresh installs escaped the bug because `huerfanos.Count == 0`). Rewritten as a set-based `ExecuteUpdateAsync`, which never enters the `ChangeTracker` and so never trips the guard, still passing RLS under platform mode (`WITH CHECK (app_es_plataforma() OR ...)`). Regression test: `InicializadorDeBaseDeDatosTests.ElBackfillNoRompeElArranqueYAsignaElTenant1AUnaCuentaHuerfana` (real Postgres, seeds an orphan non-root `Usuario` before the host boots).

### 2C. Application

- [x] 2.4 Update `ServicioDeAutenticacion` to resolve by `mail` instead of `usuario`; preserve anti-enumeration (same error, dummy-hash timing). *(spec: usuarios-y-login / Login Is By Mail; Anti-Enumeration Behavior Is Preserved)* — `SolicitudDeLogin(Mail, Password)`; lookup by `Mail`; same dummy-hash/error-message behavior, unchanged ordering, just re-keyed off mail.
- [x] 2.5 Add suspended-tenant check in `ServicioDeAutenticacion`, after password verification, same ordering as existing bloqueado/inactivo checks. *(spec: tenant-organization / Tenant Suspension Enforcement)* — Checked via a **second, platform-mode `IWaysDbContext`** (new keyed registration, `ClavesDeContexto.Plataforma`), not the request's own login-mode context — avoids needing a new RLS read policy on `tenants` just for this check. `tenant_suspendido`, 403.
- [x] 2.6 Add `root`-cannot-carry-tenant / `admin`-requires-tenant validation + admin-scoped-to-own-tenant rule wiring into `PoliticaDeRoles` call sites. *(spec: usuarios-tenant-scoping / Platform vs Tenant Role Meaning)* — New `PoliticaDeRoles.ValidarConsistenciaDeRolYAlcance` (pure), wired into `ServicioDeUsuarios.CrearAsync`/`ActualizarAsync`. `ValidarAlcanceDeTenant`/`ActorDeGestion` (slice 1, previously no call site) now wired into `ServicioDeUsuarios.BuscarAsync` — closes the judgment-day carried-forward INFO. `usuario` uniqueness check re-scoped per tenant (`ExigirDisponibilidadAsync`); fixed a latent cross-tenant leak in `ListarAsync`'s `incluirEliminados` path (bare `IgnoreQueryFilters()` → `IgnoreQueryFilters(["BajaLogica"])`), surfaced by adding tenant scoping to `Usuario`.
- [x] 2.7 Update `Ways.Web` login page: `usuario` field → `mail` field, client validation. *(spec: usuarios-y-login / Login Is By Mail)* — `Login.tsx` field is now `type="email"` labeled "Correo electrónico"; `AuthContext.iniciarSesion(mail, password)`; `UsuarioAutenticado.idTenant` added to `tipos.ts`.

### 2D. Tests

- [x] 2.8 [P] Unit tests: root/admin tenant-assignment validation, existing `PoliticaDeRoles` scenarios still pass unchanged. — 6 new tests on `ValidarConsistenciaDeRolYAlcance` in `PoliticaDeRolesTenantTests.cs`; all prior `PoliticaDeRoles*Tests` unedited and green (38/38 total domain suite).
- [x] 2.9 [P] Integration tests: suspension blocks new login + cuts active session; mail login for tenant user and platform root; anti-enumeration under mail; two tenants both provision `usuario = "admin"` without collision; duplicate platform `usuario` rejected (`NULLS NOT DISTINCT` proof). *(spec: usuarios-tenant-scoping; usuarios-y-login)* — `UsuariosYLoginTests.cs`, 7/7 green against real Postgres (migration 2 applied), stable across 3 consecutive runs. Also `FiltroDeUsuarioTests.cs` (5 tests, InMemory) and `ServicioDeAutenticacionTests.cs` (5 tests, InMemory) covering the same behaviors at the unit level. Judgment-day batch 8 added `ServicioDeUsuariosTests.cs` (7 tests, InMemory, incl. the CRITICAL cross-tenant-mail regression case), 1 tamper-guard test in `FiltroDeUsuarioTests.cs`, and 2 integration tests (existing-cookie re-login, cross-tenant mail 409 through the real HTTP API) — suite now 36/17. Judgment-day batch 9 (round 2) added: `InicializadorDeBaseDeDatosTests.cs` (backfill boot regression, real Postgres), `UsuariosRlsTests.cs` (3 raw-SQL RLS isolation tests mirroring `AislamientoDeTenantTests` for `usuarios`), and 2 more cases in `UsuariosYLoginTests.cs` (concurrent-creates hitting the `ManejadorDeErrores` 23505 backstop for real, and an HTTP-level cross-tenant 404 on `GET /api/usuarios/{id}`).
- [x] 2.10 Regression: existing doc 08 usuarios/login suite green, unedited. — No dedicated doc-08 automated suite existed before this slice (doc 08 predates SDD tracking, confirmed by searching `tests/`). Regression baseline instead: `Ways.Domain.Tests` 38/38, `Ways.Application.Tests` 28/28, `Ways.IntegrationTests` 15/15 (8 unedited slice-1 tests + 7 new slice-2 tests). 0 failures, 0 skipped, 0 build warnings. Judgment-day batch 8 (round 1): `Ways.Domain.Tests` 38/38, `Ways.Application.Tests` 36/36, `Ways.IntegrationTests` 17/17. Judgment-day batch 9 (round 2): `Ways.Domain.Tests` 38/38, `Ways.Application.Tests` 36/36, `Ways.IntegrationTests` 23/23 — 0 failures, 0 build warnings, stable across 2 consecutive full runs.

---

## Slice 3: Catalogs + parametros (PR 3)

**Depends on**: Slice 1 (tenant plumbing). **Start**: PR 1 (or PR 2 chain, per chosen strategy). **Finish**: migrations 3–5 applied, catalog machine + parametros resolution live, tests green. **Rollback**: down-migrations 3–5; new endpoints only.

### 3A. Domain + persistence machine

- [ ] 3.1 Add `CatalogoSimple : EntidadTenant { Nombre, Activo, IdEmpresa? }` in `Ways.Domain/Catalogos`. *(ADR-11)*
- [ ] 3.2 Add `ConfiguracionDeCatalogo<T>` shared EF config (table/columns/audit/index-pair) + abstract `ConfigurarPropio`. *(ADR-11, catalog index pair)*
- [ ] 3.3 [P] Add `Area`, `Marca`, `Grupo`, `MedioPago` (+ `comportamiento_medio_pago` enum) entities + thin configs. *(spec: auxiliary-catalogs / Catalog ABM Lifecycle)*
- [ ] 3.4 Add `Categoria` (self composite FK) + `ReglaDeCategorias.ValidarProfundidad`/`ValidarSinCiclo` + unit tests (depth 1-3 OK, 4 rejected, re-parent overflow rejected, cycle rejected, root OK). *(ADR-12; spec: auxiliary-catalogs / Categoria Depth Limit)*
- [ ] 3.5 [P] Add global fiscal entities: `CondicionFiscal`, `AlicuotaIva`, `TipoComprobante` (+ `clase_comprobante` enum), no `id_tenant`. *(spec: auxiliary-catalogs / Fiscal Catalogs Are Platform-Managed and Read-Only)*
- [ ] 3.6 Add `Parametro` entity + `ResolucionDeParametros` pure function (punto_venta ?? empresa ?? default) + unit tests. *(ADR-13; spec: parametros-operativos / Parameter Scope and Fallback)*
- [ ] 3.7 Add `ParametroConocido` typed key registry (key, CLR type, default, validation). *(ADR-13)*

### 3B. DB CHANGE GATE #3 — BLOCKING

- [ ] 3.8 **STOP.** Present migration 3 (`CatalogosDeTenant`) model summary — per-table columns, index pairs, self composite FK — and wait for explicit approval.

### 3C. Migrations 3–5 (each gated)

- [ ] 3.9 Generate migration 3 (`CatalogosDeTenant`): `areas`, `categorias`, `marcas`, `grupos`, `medios_pago` + enum, index pairs, policies — only after 3.8 approved.
- [ ] 3.10 **STOP — DB CHANGE GATE #4.** Present migration 4 (`CatalogosGlobales`) model summary — `[global]`, no `id_tenant`, no RLS — and wait for explicit approval.
- [ ] 3.11 Generate migration 4 (`CatalogosGlobales`): `condiciones_fiscales`, `alicuotas_iva`, `tipos_comprobante` — only after 3.10 approved.
- [ ] 3.12 **STOP — DB CHANGE GATE #5.** Present migration 5 (`Parametros`) model summary — table, two partial unique indexes, NULL-uniqueness reasoning (ADR-13) — and wait for explicit approval.
- [ ] 3.13 Generate migration 5 (`Parametros`): table + `ux_parametros_punto_venta`/`ux_parametros_empresa` + policy — only after 3.12 approved.
- [ ] 3.14 Seed three fiscal catalogs in `InicializadorDeBaseDeDatos`, platform mode.

### 3D. Application + API

- [ ] 3.15 Add `ServicioDeCatalogo<T, TListado, TAlta>` generic service (list/create/edit/soft-delete/get) + `virtual AplicarPropios`. *(ADR-11)*
- [ ] 3.16 [P] Add 4 thin subclasses (Area, Marca, Grupo, MedioPago) + Categoria's own subclass (escape hatch: depth/cycle validation via 3.4). *(ADR-11 escape hatch)*
- [ ] 3.17 Add `ServicioDeParametros` (resolution query + typed-key validation, documented default on miss). *(spec: parametros-operativos / all requirements)*
- [ ] 3.18 Add `MapearCatalogo<T>` endpoint helper; wire 5 catalog route groups, fiscal read-only `GET` endpoints, parametros endpoints. *(spec: auxiliary-catalogs; parametros-operativos)*

### 3E. Tests

- [ ] 3.19 [P] Integration tests: CRUD once per catalog through the shared route map; cross-tenant catalog id ⇒ 404; fiscal catalogs GET 200 / write 403; categoria depth 4 ⇒ 400. *(spec: auxiliary-catalogs)*
- [ ] 3.20 [P] Integration tests: `parametros` resolution end to end (punto_venta wins, empresa fallback, documented default on miss). *(spec: parametros-operativos)*

---

## Slice 4: Web ABMs (PR 4)

**Depends on**: Slices 1–3 (consumes their API surface). **Start**: prior slice branch/main per chosen chain strategy. **Finish**: all ABM screens functional against the API, smoke-verified. **Rollback**: new routes only, no existing screen touched.

- [ ] 4.1 [P] Add `catalogos.ts` field-descriptor API client + `tipos.ts` type additions.
- [ ] 4.2 Add generic `PaginaCatalogo` component driven by a field descriptor. *(ADR-11)*
- [ ] 4.3 Wire `/catalogos/:recurso` route + descriptors for `areas`, `marcas`, `grupos`, `medios_pago`; read-only views for the 3 fiscal catalogs. *(spec: auxiliary-catalogs)*
- [ ] 4.4 Add Categorias tree page (own service subclass, escape hatch — not the generic descriptor). *(ADR-11 escape hatch)*
- [ ] 4.5 [P] Add Tenants page: platform-only list/create/suspend. *(spec: tenant-organization / Platform-Only Creation, Tenant Suspension Enforcement)*
- [ ] 4.6 [P] Add Empresas page: platform creates, tenant admin edits descriptive fields only. *(spec: tenant-organization / Platform-Only Creation)*
- [ ] 4.7 [P] Add PuntosVenta page: same platform-create/tenant-edit pattern. *(spec: tenant-organization / Platform-Only Creation)*
- [ ] 4.8 Add tenant provisioning UI (platform-only form; shows generated admin password once, never persisted in plain text). *(spec: tenant-organization / Tenant Provisioning With Template Seed; ADR-16)*
- [ ] 4.9 Smoke-verify each ABM screen against its integration test expectations (no e2e harness this stage — ADR-17; flagged as follow-up).
- [ ] 4.10 Update `docs/10-modelo-de-datos.md` §1/§9 status notes and record the flow-A (subdomain login) extension point as still deferred.

---

## Dependency Summary

```
Slice 1 (tenancy + org + RLS)
   ├─▶ Slice 2 (usuarios + suspension + mail login)
   └─▶ Slice 3 (catalogs + parametros)
            Slice 2, Slice 3 ─▶ Slice 4 (web ABMs)
```

Within each slice, `[P]`-tagged tasks are parallelizable; all others are sequential (schema → infra → application → tests, gates always block the following migration task).
