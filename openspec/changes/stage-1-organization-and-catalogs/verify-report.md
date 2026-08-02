# Verification Report: Stage 1 - Organization and Catalogs

Change: stage-1-organization-and-catalogs
Mode: openspec (repo-local)
Verified against: main @ b25cb0c (PRs #2, #4, #6, #8 merged)
Date: 2026-08-02

## Completeness (tasks.md)

All tasks across Slices 1-4 (incl. section 4B, the organization-backend scope extension)
are checked off. Verified against actual code, not just the checklist - see Correctness
below. No unchecked implementation tasks found.

## Build / Test Evidence (run by this verification, not copied from apply-progress)

- dotnet build Ways.slnx -> 0 errors, 0 warnings.
- dotnet test Ways.slnx ->
  - Ways.Domain.Tests: 61/61
  - Ways.Application.Tests: 85/85
  - Ways.IntegrationTests: 74/74 (real Postgres via Testcontainers, Docker daemon 29.5.3 reachable)
  - 0 failures, 0 skipped - matches the recorded baseline exactly.
- npx tsc -b (Ways.Web) -> clean, exit 0.

## Spec Compliance Matrix

### tenant-organization

| Requirement | Status | Evidence |
|---|---|---|
| Organization Hierarchy Tables | PASS | migration Organizacion; composite FK puntos_venta(id_empresa,id_tenant) to empresas(id_empresa,id_tenant) in PuntoVentaConfiguration.cs; seed via SembrarOrganizacionAsync |
| Platform-Only Creation | PASS | Politicas.SoloPlataforma, ServicioDeOrganizacion edit-only for tenant admins, AprovisionamientoEndpoints platform-only create |
| Tenant Provisioning With Template Seed | WARNING - see Findings #1 | ServicioDeAprovisionamiento.CrearTenantAsync, AprovisionamientoTests.cs (3/3 green) - tenant/empresa/puntos_venta/area/medios de pago all proven; price-list placeholder scenario NOT implemented |
| Tenant Suspension Enforcement | PASS | ServicioDeAutenticacion (login-time check), OnValidatePrincipal (session-cut), OrganizacionTests suspend-login-blocked-reactivate cycle (integration, real HTTP) |
| Tenant Isolation Enforcement | PASS | AislamientoDeTenantTests (8/8), CatalogosDeTenantRlsTests, UsuariosRlsTests, CatalogosGlobalesRlsTests - EF filter + RLS both proven at runtime, ways_app confirmed NOSUPERUSER NOBYPASSRLS |

### usuarios-tenant-scoping

| Requirement | Status | Evidence |
|---|---|---|
| id_tenant Column Semantics | PASS | migration UsuariosMultiTenant, backfill (BackfillDeUsuariosAsync, ExecuteUpdateAsync) |
| usuario Uniqueness Is Scoped Per Tenant | PASS | ux_usuarios_usuario (id_tenant, usuario) NULLS NOT DISTINCT; UsuariosYLoginTests proves both same-name-different-tenant and platform-NULL-group collision |
| Platform vs Tenant Role Meaning | PASS | PoliticaDeRoles.ValidarConsistenciaDeRolYAlcance + PoliticaDeRolesTenantTests |
| PoliticaDeRoles Tenant Rule | PASS | ValidarAlcanceDeTenant, wired into ServicioDeUsuarios.BuscarAsync/ServicioDeOrganizacion; 404-not-403 confirmed in PoliticaDeRoles.cs |

### usuarios-y-login (first baseline, ADDED-only per state.yaml note)

| Requirement | Status | Evidence |
|---|---|---|
| Tenant Column on Usuarios | PASS | additive migration, existing doc-08 columns untouched |
| Root and Admin Role Meaning Change | PASS | PoliticaDeRoles, ServicioDeAutenticacion |
| Login and Session Revalidation Respect Tenant State | PASS | OnValidatePrincipal tenant-estado revalidation |
| Login Is By Mail, Not By usuario (Flow B) | PASS | SolicitudDeLogin(Mail, Password) (code inspected), Login.tsx type=email/name=mail (code inspected) |
| Anti-Enumeration Behavior Is Preserved Under Mail-Based Login | PASS | ServicioDeAutenticacion.IniciarSesionAsync (code inspected): same error, discardable-hash path, DB round-trip leveling, account-state checks strictly after password verification |
| Subdomain-Based Login (Flow A) Is Deferred, Not Stage 1 | PASS (recorded deviation) | design.md ADR-7 explicit override note, state.yaml decision log; no flow-A code shipped, none expected |

### auxiliary-catalogs

| Requirement | Status | Evidence |
|---|---|---|
| Catalog ABM Lifecycle | PASS | ServicioDeCatalogo<T> + 4 subclasses, CatalogosTests |
| Categoria Depth Limit | PASS | ReglaDeCategorias.ProfundidadMaxima = 3 (code inspected), ReglaDeCategoriasTests (9 cases incl. judgment-day self-parent fix), ck_categorias_padre_no_self CHECK constraint in migration |
| Fiscal Catalogs Are Platform-Managed and Read-Only | PASS (recorded deviation: 404 not 403) | CatalogosEndpoints.cs (code inspected) - only 3 GET routes mapped for fiscal catalogs, no write route exists; HabilitarRlsDeCatalogoGlobal in migration CatalogosGlobales; CatalogosGlobalesRlsTests (6/6) |

### parametros-operativos

| Requirement | Status | Evidence |
|---|---|---|
| Parameter Scope and Fallback | PASS | ResolucionDeParametros (pure, unit-tested), ParametrosTests (integration, incl. deterministic-rendezvous race test for parametro_duplicado) |

## Findings

### WARNING

1. Tenant provisioning template's price-list placeholder is specified but not implemented,
   and the deviation was never elevated to a recorded user-approved spec exception.
   specs/tenant-organization/spec.md - Requirement "Tenant Provisioning With Template Seed" -
   states the system MUST create "one inactive general price-list placeholder" as part of
   provisioning, and the "Successful provisioning" scenario asserts it exists afterward. This
   is directly traceable to proposal.md's "Resolved product decisions #2" (user-approved
   2026-07-31: "one general price-list placeholder (inactive until stage 3)"). The shipped
   code (PlantillaDeAprovisionamiento.V1 / PlantillaV1.ItemsDiferidos,
   src/Ways.Domain/Organizacion/PlantillaDeAprovisionamiento.cs) deliberately does NOT create
   it - listas_precio does not exist yet (it is explicitly Out of Scope in the same
   proposal.md, which is an internal contradiction in the proposal itself: the template
   review decision asked for a placeholder in a table the same document excludes from stage
   1). design.md documents this as a "Scope gap" in its Risks table and apply-progress.md
   batch 4 (Slice 3) confirms it was reported, not silently dropped - but unlike ADR-7
   (login), ADR-10 (DeLaEmpresa deferral) or ADR-11 (RLS restoration), this gap was never
   surfaced through the project's own deviation-recording convention (no "overridden by
   user" / explicit approval entry in state.yaml). No test in AprovisionamientoTests.cs
   asserts the price-list placeholder - correctly, since it isn't built - which means the
   literal "Successful provisioning" scenario in the spec is unmet.
   Recommendation: before archive, either (a) get explicit user sign-off recorded in
   state.yaml/design.md the same way the other ADR overrides are recorded, or (b) amend
   specs/tenant-organization/spec.md's scenario to match the as-built template (drop the
   price-list clause, keep it as a documented deferred item), consistent with how ADR-10's
   DeLaEmpresa deferral was handled. This is not a security or correctness issue - it is an
   SDD-process gap between the spec's literal wording and a deliberate, technically sound
   scope reduction.

No CRITICAL findings. No further WARNINGs.

### SUGGESTION

1. docs/10-modelo-de-datos.md and docs/09-multi-tenancy.md were not re-audited line by line
   in this pass beyond the spot checks already covered by the spec compliance matrix above; a
   full doc-parity pass is optional before archive since apply-progress.md batch 11 already
   records doc updates (docs/10-modelo-de-datos.md section 1, docs/08-usuarios-y-login.md).
2. design.md's own Risks table item "Template's price-list / Consumidor Final cannot be
   created in stage 1" is the right place this was recorded - consider promoting it to a
   named ADR override (like ADR-7/10/11) purely for consistency of the project's own
   documentation convention, not because the current form is wrong.

## Deviations-vs-Recorded Check

| Deviation | Recorded? | Verdict |
|---|---|---|
| Per-tenant usuario + global mail, mail-based login (ADR-7 override) | Yes - state.yaml, design.md ADR-7, explicit "Overridden by product decision (2026-07-31)" | Consistent, correctly implemented |
| RLS restored on the 3 global fiscal catalogs (ADR-11 override, gate #4) | Yes - state.yaml DB CHANGE GATE #4 note, design.md ADR-11 override block | Consistent, correctly implemented, verified at runtime (CatalogosGlobalesRlsTests) |
| ADR-10 query.DeLaEmpresa(idEmpresa) deferred | Yes - state.yaml judgment-day slice 3 round 1 note, design.md ADR-10 deferral paragraph | Consistent - not implemented, matches spec (no scenario requires it) |
| Fiscal catalog write attempts return 404, not literal task wording "403" | Yes - tasks.md 3.19 note, apply-progress.md batch 4 (Slice 3) | Consistent - verified in code (CatalogosEndpoints.cs, no write route mapped), matches spec's own "404 or 403" scenario wording |
| Tenant provisioning template's price-list placeholder not created | Reported in design.md/apply-progress.md but not elevated to an explicit user-approved override | Inconsistent - see WARNING #1 |
| Organization backend (ServicioDeOrganizacion/OrganizacionEndpoints) added as scope extension inside Slice 4 | Yes - state.yaml, tasks.md section 4B, explicit "USER DECISION (2026-08-02)" | Consistent, correctly implemented |
| Slices 3 and 4 shipped as single PRs with size:exception (review workload guard) | Yes - tasks.md Review Workload Forecast, delivery decisions recorded | Consistent, per-project PR validation gate (judgment-day) was run each time |

## Judgment-Day Verdicts (as recorded, cross-checked against code state)

- Slice 1: APPROVED (3 rounds) - RLS UPDATE test fix confirmed present (AislamientoDeTenantTests.WithCheckRechazaUnUpdateQueReasignaIdTenant asserts SqlState == "42501", code inspected).
- Slice 2: APPROVED (4 rounds) - mail-uniqueness cross-tenant fix, backfill ExecuteUpdateAsync fix, both confirmed present in code.
- Slice 3: APPROVED (3 rounds) - self-parent CHECK constraint confirmed present in migration; deterministic-rendezvous race test confirmed present in ParametrosTests.cs.
- Slice 4: APPROVED (2 rounds) - snake_case error codes, categoria dropdown subtree filtering; not independently re-verified line-by-line in this pass (low risk, cosmetic/UX class of fixes).

## Verdict

PASS WITH WARNINGS - 0 CRITICAL, 1 WARNING (spec-vs-implementation gap on the provisioning
template's price-list placeholder, low severity, needs a paperwork fix not a code fix), 2
SUGGESTIONs (documentation polish, optional).

## Next Recommended

Resolve WARNING #1 (record the deviation formally, or amend the spec scenario) before
sdd-archive, per this project's own convention for every other deviation in this change.
This does not block archive on technical grounds - no security issue, no failing test, no
unchecked task - but it is inconsistent with how every other deviation in this exact change
was handled, and archive is the point where the spec becomes the historical baseline, so it
is the right moment to close this gap rather than carry it forward silently.
