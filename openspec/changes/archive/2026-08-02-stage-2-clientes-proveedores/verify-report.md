# Verification Report

**Change**: stage-2-clientes-proveedores
**Version**: N/A (openspec, no semver)
**Mode**: Standard (no strict-TDD signal recorded in apply-progress.md)
**Verified against**: `main` @ 95a494e (PRs #10, #12, #14 merged)

## Completeness

| Metric | Value |
|--------|-------|
| Tasks total (Slices 1-4) | 34 numbered tasks (tasks.md) |
| Tasks complete | 34 |
| Tasks incomplete | 0 |

## Build & Tests Execution

**Build**: PASSED
```
dotnet build Ways.slnx
Compilacion correcta.
    0 Advertencia(s)
    0 Errores
```

**Tests**: PASSED - 326/326 (0 failed, 0 skipped)
```
dotnet test Ways.slnx
Ways.Domain.Tests:        69/69   Correcto
Ways.Application.Tests:  128/128  Correcto
Ways.IntegrationTests:   129/129  Correcto  (Docker/Testcontainers real Postgres)
```
Exact match to the baseline recorded in state.yaml/apply-progress.md.
One transient 23505 duplicate-key stack trace appears in the log during
AprovisionamientoTests.UnaFallaAMitadDeCaminoNoDejaNadaCreado - this is the test's own
intentional-failure/rollback assertion, not a real failure; run reports 0 failed.

**TypeScript**: PASSED
```
cd src/Ways.Web && npx tsc -b
(clean, no output)
```

**Coverage**: not measured (no coverage tooling configured in this repo).

## Spec Compliance Matrix

### specs/clientes/spec.md

| Requirement | Scenario | Test | Result |
|---|---|---|---|
| Cliente Schema At Rest | Default credit fields | ClientesEndpointsTests | COMPLIANT |
| Cliente Schema At Rest | id_lista_precio/id_condicion_fiscal required | ServicioDeClientesTests | COMPLIANT |
| Cliente Schema At Rest | Invalid FK -> 400 | ServicioDeClientesTests / BackstopClientesYProveedoresTests | COMPLIANT |
| Atomic Per-Tenant Numero Assignment | Concurrent creation, no gaps/dupes | ClientesEndpointsTests, AsignadorDeNumeroClienteConcurrenciaTests | COMPLIANT |
| Atomic Per-Tenant Numero Assignment | Unique backstop -> 409 | BackstopClientesYProveedoresTests | COMPLIANT |
| Consumidor Final Protected Row | CF exists after provisioning | AprovisionamientoTests, ClientesProvisioningYBackfillTests | COMPLIANT |
| Consumidor Final Protected Row | Update/delete rejected | ReglaDeClientesTests + ClientesEndpointsTests + BackstopClientesYProveedoresTests | COMPLIANT |
| numero_documento Has No Uniqueness Constraint | Duplicate + NULL accepted | ClientesEndpointsTests, ModeloDeClientesYProveedoresTests | COMPLIANT |
| Cliente ABM Lifecycle and Authorization | Admin create->soft-delete | ClientesEndpointsTests | COMPLIANT |
| Cliente ABM Lifecycle and Authorization | Vendedor blocked | ClientesEndpointsTests (403) | COMPLIANT |
| Tenant Isolation for Clientes | EF filter + RLS | ClientesYProveedoresRlsTests | COMPLIANT |

### specs/proveedores/spec.md

| Requirement | Scenario | Test | Result |
|---|---|---|---|
| Proveedor Schema At Rest | Create without cuit | ServicioDeProveedoresTests | COMPLIANT |
| Proveedor Schema At Rest | Invalid condicion fiscal -> 400 | ServicioDeProveedoresTests / BackstopClientesYProveedoresTests | COMPLIANT |
| cuit Uniqueness Is Scoped Per Tenant | Duplicate in same tenant rejected | ServicioDeProveedoresTests | COMPLIANT |
| cuit Uniqueness Is Scoped Per Tenant | Same cuit across tenants allowed | ServicioDeProveedoresTests, ProveedoresEndpointsTests | COMPLIANT |
| cuit Uniqueness Is Scoped Per Tenant | Concurrent race, one winner | ProveedoresEndpointsTests.LaCreacionConcurrenteConElMismoCuitDaExactamenteUnGanador | COMPLIANT |
| cuit Uniqueness Is Scoped Per Tenant | NULL cuit never collides | ServicioDeProveedoresTests | COMPLIANT |
| Proveedor ABM Lifecycle and Authorization | Admin create->soft-delete | ProveedoresEndpointsTests | COMPLIANT |
| Proveedor ABM Lifecycle and Authorization | Vendedor blocked | ProveedoresEndpointsTests (403) | COMPLIANT |
| Proveedor ABM Lifecycle and Authorization | Soft-deleted cuit reusable | ServicioDeProveedoresTests, ProveedoresEndpointsTests | COMPLIANT |
| Tenant Isolation for Proveedores | EF filter + RLS | ClientesYProveedoresRlsTests | COMPLIANT |

### specs/listas-precio-minimal/spec.md

| Requirement | Scenario | Test | Result |
|---|---|---|---|
| listas_precio Schema At Rest | fija-only shape this stage | ModeloDeClientesYProveedoresTests, ClientesProvisioningYBackfillTests | COMPLIANT |
| One Default List Per Tenant | General list exists after provisioning | AprovisionamientoTests, ClientesProvisioningYBackfillTests | COMPLIANT |
| One Default List Per Tenant | Cliente creation requires an explicit lista (scenario corrected 2026-08-02, see Issues > CRITICAL, resolved) | ServicioDeClientesTests.id_lista_precio_requerido | COMPLIANT |
| One Default List Per Tenant | Invalid id_lista_precio -> 400 | ServicioDeClientesTests / BackstopClientesYProveedoresTests | COMPLIANT |
| listas_precio ABM Is Out of Scope This Stage | No write endpoint exists | Source inspection: only GET /api/listas-precio exists, no POST/PUT/DELETE route | COMPLIANT (SUGGESTION: no explicit 404 regression test) |
| Tenant Isolation for listas_precio | EF filter + RLS | ClientesYProveedoresRlsTests | COMPLIANT |

### specs/tenant-organization/spec.md (delta)

| Requirement | Scenario | Test | Result |
|---|---|---|---|
| Tenant Provisioning With Template Seed (MODIFIED) | Successful provisioning incl. CF + General list | AprovisionamientoTests.AprovisionaUnTenantDePuntaAPuntaConLaPlantillaV1YElAdminPuedeIniciarSesion | COMPLIANT |
| Tenant Provisioning With Template Seed (MODIFIED) | Provisioning failure rolls back | AprovisionamientoTests.UnaFallaAMitadDeCaminoNoDejaNadaCreado | COMPLIANT |
| Backfill for Pre-Existing Tenants (ADDED) | Existing tenant gains CF + General list | ClientesProvisioningYBackfillTests | COMPLIANT |
| Backfill for Pre-Existing Tenants (ADDED) | Backfill idempotent | ClientesProvisioningYBackfillTests, BackfillPorArtefactoTests | COMPLIANT |
| Backfill for Pre-Existing Tenants (ADDED) | Backfill approved inside DB Change Gate | apply-progress.md batch 1/2 - gate presented and approved 2026-08-02 | COMPLIANT (process evidence) |

**Compliance summary**: 33/33 scenarios compliant (was 32/33 with 1 contradicted CRITICAL,
resolved in place 2026-08-02 — see Issues > CRITICAL).

## Correctness (Static Evidence)

| Requirement | Status | Notes |
|---|---|---|
| Migration ClientesYProveedoresEtapa2 | Implemented | 4 tables, composite fk_clientes_lista_precio/fk_*_empresa, ck_clientes_cf_protegido, ux_clientes_numero, ux_proveedores_cuit, ux_listas_precio_default_*, RLS on all 4 - matches design.md exactly |
| ManejadorDeErrores backstop map | Implemented | _cuit->cuit_duplicado, _numero->numero_duplicado, 23514->consumidor_final_protegido, 22003->valor_fuera_de_rango all present |
| AsignadorDeNumeroCliente | Implemented | Raw ADO.NET, UPDATE...RETURNING, opens via Database.OpenConnectionAsync() so the tenant interceptor fires |
| ReglaDeClientes CF guard | Implemented | Pure domain rule, called from ActualizarAsync/EliminarAsync before any mutation |
| Backfill idempotency (per-artifact) | Implemented | BackfillDeClientesYListasPrecioAsync evaluates CF-cliente and General-lista independently |
| Web ABMs | Implemented | Clientes.tsx/Proveedores.tsx exist, dedicated screens, routes wired in App.tsx, nav in Layout.tsx |

## Coherence (Design)

| Decision | Followed? | Notes |
|---|---|---|
| 1. Dedicated entities for clientes/proveedores, generic base for listas_precio | Yes | Cliente/Proveedor standalone; ListaPrecio : CatalogoSimple reuses ConfiguracionDeCatalogo<T> |
| 2. Numero atomicity via counter table | Yes | numeraciones_clientes + AsignadorDeNumeroCliente |
| 3. Raw ADO.NET for numero read, not SqlQuery<T>() | Yes | Confirmed in AsignadorDeNumeroCliente.cs |
| 4. CF protection: domain guard + DB CHECK | Yes | ReglaDeClientes + ck_clientes_cf_protegido both present |
| 5. Template extended in place (V1), not V2 | Yes | PlantillaDeAprovisionamiento.V1 extended, doc comment explains the ADR-16 exception |
| 6. ADR-10 DeLaEmpresa stays deferred | Yes | No new querying gap introduced |

## Deviations vs. Recorded (state.yaml / apply-progress.md)

All deviations explicitly recorded in state.yaml/apply-progress.md were checked against the
actual code and are consistently documented - none are undisclosed gaps:

| Recorded deviation | Verified in code | Status |
|---|---|---|
| id_lista_precio/id_condicion_fiscal required, not defaulted (spec.md wins over tasks.md/design.md:29 one-liners) | ExigirIdRequerido on both fields in ServicioDeClientes.CrearAsync/ActualizarAsync; design.md:29 carries a superseded note | Consistent - but see CRITICAL below: this resolution was never propagated to specs/listas-precio-minimal/spec.md's own contradicting scenario |
| PlantillaDeAprovisionamiento V1 extended in place, not bumped to V2 | Confirmed, doc comment explains the ADR-16 distinction | Consistent |
| cuit dedupe is format-sensitive (no digit-only canonicalization) | NormalizarCuit only trims + length-checks, no canonicalization | Consistent |
| Numeric bounds (margen/limite_credito) are service-only, no DB CHECK | No CHECK in the migration for margen/limite_credito; ExigirMargenValido/ExigirLimiteCreditoValido enforce at service level; generic 22003->400 backstop added | Consistent |
| Slice 4 re-scoped into Slices 2/3 (web ABMs pulled forward) | tasks.md shows Slice 4 tasks marked done with re-scoping notes; both screens exist and are wired | Consistent |
| Cross-tenant IdEmpresa pre-check symmetry (INFO carried from Slice 2 to Slice 3) | ExigirEmpresaValidaAsync present in both ServicioDeClientes and ServicioDeProveedores | Consistent, closed |

## Issues Found

### CRITICAL (resolved 2026-08-02, before archive)

1. ~~Uncorrected spec contradiction~~ — RESOLVED. specs/listas-precio-minimal/spec.md's
   "Cliente creation defaults to the General list" scenario (lines 36-39) was false as
   written and had no covering test: it claimed omitting `id_lista_precio` resolves to the
   tenant's `es_default` list, which directly contradicted specs/clientes/spec.md's own
   "id_lista_precio and id_condicion_fiscal are required" scenario, `ServicioDeClientes.
   ExigirIdRequerido`, and `ServicioDeClientesTests` — the actual, implemented, and tested
   behavior is that omitting `id_lista_precio` is REJECTED with 400
   `id_lista_precio_requerido` before reaching the database. Fixed in place (same treatment
   already given to design.md:29's equivalent contradiction): the scenario in
   specs/listas-precio-minimal/spec.md was renamed to "Cliente creation requires an explicit
   lista", given a "Superseded wording (verify, 2026-08-02)" note explaining the resolution
   in favor of specs/clientes/spec.md as the higher-authority acceptance contract, and its
   GIVEN/WHEN/THEN rewritten to match the implemented "required, no defaulting" contract.
   `ServicioDeClientesTests`'s existing required-field coverage now proves the corrected
   scenario directly (previously it only contradicted the old wording).
   File: openspec/changes/stage-2-clientes-proveedores/specs/listas-precio-minimal/spec.md:36-46

### WARNING

1. state.yaml's top-level phase.apply.status is stale. It reads
   "slice-3-done-ready-for-judgment-day", but the notes list in the same file records that
   Slice 3's judgment-day already reached an APPROVED verdict after 2 clean rounds, and PR #14
   (Slice 3) is merged to main per git log. Documentation-only drift (does not affect code
   correctness) but should be corrected before/at archive so the state file reflects the true
   final status.
   File: openspec/changes/stage-2-clientes-proveedores/state.yaml:7

2. RESOLVED at archive (2026-08-02). Merge implication for archive: the tenant-organization
   delta spec was written against the pre-archive stage-1 spec, and the archived baseline now
   lives at openspec/specs/tenant-organization/spec.md. That baseline file's "Tenant
   Provisioning With Template Seed" requirement showed the OLD scenario text (tenant + empresa
   + area + 2 medios de pago only - no Consumidor Final cliente, no General listas_precio row);
   it carried only a forward-pointing "Deviation recorded ... Superseded by the user's stage-2
   decision" note, not the actual merged scenario text. Fixed during sdd-archive: the
   requirement now carries the MODIFIED text (CF cliente + General listas_precio row in the
   scenarios), the stage-1 deviation note was preserved verbatim, and a follow-up "Fulfilled"
   note records that the template was extended in place (V1) rather than bumped to a new
   version, per design decision 5 — reconciling the delta's own "gains a new version (ADR-16)"
   wording with what was actually implemented. The ADDED "Backfill for Pre-Existing Tenants"
   requirement was also appended to the baseline.
   File: openspec/specs/tenant-organization/spec.md

### SUGGESTION

1. No explicit runtime test asserts the 404 for a nonexistent listas_precio write endpoint
   (specs/listas-precio-minimal/spec.md's "No tenant-facing write endpoint exists" scenario).
   Source inspection confirms no such route is registered (compliant by construction - ASP.NET
   Core 404s any unmapped route by default), but there is no test codifying this as an explicit
   regression guard. Low priority given the structural guarantee.

## Verdict

PASS (2026-08-02, post-fix). The one CRITICAL finding (specs/listas-precio-minimal/spec.md's
"Cliente creation defaults to the General list" scenario contradicting the implemented and
tested "required, no defaulting" behavior) was fixed in place before archive — see Issues >
CRITICAL. All code, tests (326/326 passing: Domain 69/69, Application 128/128,
IntegrationTests 129/129), spec compliance (33/33 scenarios), and design coherence checks are
clean. Ready for sdd-archive.

Original verdict (superseded by the fix above): FAIL - one CRITICAL, recorded verbatim for
audit trail. "specs/listas-precio-minimal/spec.md contains a scenario ('Cliente creation
defaults to the General list') that directly contradicts the implemented and tested behavior,
is contradicted by specs/clientes/spec.md's own scenario in the same change, and was never
reconciled the way the equivalent design.md:29 contradiction was. All code, tests (326/326
passing), and design coherence checks are otherwise clean; this is a spec-document correction,
not a code fix - recommend a direct doc edit (or a short sdd-apply pass) to reconcile
specs/listas-precio-minimal/spec.md before proceeding to sdd-archive."
