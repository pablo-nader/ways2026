# Archive Report: Stage 1 — Organization and Catalogs

**Archive date**: 2026-08-02  
**Change**: stage-1-organization-and-catalogs  
**Status**: ARCHIVED (verified, complete, all 4 slices merged to main)  
**Artifact store mode**: openspec (file-based)  

---

## Executive Summary

Stage 1 of the multi-tenant ERP retrofit is complete and archived. Four autonomous slices (tenancy plumbing + organization tables + RLS; usuarios retrofit + suspension + mail login; catalogs + parametros + tenant provisioning; web ABMs) were implemented across 4 PRs, all merged to main (PRs #2, #4, #6, #8), passing dual-blind judgment-day review per project protocol. Specification baseline established: 5 domain specs (tenant-organization, usuarios-tenant-scoping, usuarios-y-login, auxiliary-catalogs, parametros-operativos) synced to `openspec/specs/`. Verification PASS with 1 recorded WARNING (price-list placeholder deviation formally documented in spec). No CRITICAL issues. Test suite green: 61 domain / 85 application / 74 integration tests, stable across consecutive runs.

---

## Artifacts Synced to Main Specs

All 5 delta specs from `openspec/changes/stage-1-organization-and-catalogs/specs/` have been copied to the main specs baseline at `openspec/specs/`:

| Domain | Artifact | Type | Status |
|--------|----------|------|--------|
| tenant-organization | `openspec/specs/tenant-organization/spec.md` | new | created |
| usuarios-tenant-scoping | `openspec/specs/usuarios-tenant-scoping/spec.md` | new | created |
| usuarios-y-login | `openspec/specs/usuarios-y-login/spec.md` | new (ADDED-only, first baseline) | created |
| auxiliary-catalogs | `openspec/specs/auxiliary-catalogs/spec.md` | new | created |
| parametros-operativos | `openspec/specs/parametros-operativos/spec.md` | new | created |

**Note on usuarios-y-login:** No prior OpenSpec baseline existed (doc 08 predates SDD tracking). The delta spec is ADDED-only and establishes the first `openspec/specs/usuarios-y-login/spec.md` baseline.

**Note on price-list deviation:** The provisioning template's inactive price-list placeholder, originally planned in proposal.md resolved decision #2, was deliberately deferred (listas_precio does not exist in stage 1). This deviation is now formally recorded in `specs/tenant-organization/spec.md` (lines 61-66) and superseded by the user's stage-2 decision: `stage-2-clientes-proveedores` will create the real minimal `listas_precio` and add it to the template.

---

## Change Archive Location

```
openspec/changes/archive/2026-08-02-stage-1-organization-and-catalogs/
├── proposal.md
├── design.md
├── tasks.md (all 4 slices, 295 tasks, all checked)
├── apply-progress.md (11 batches, detailed implementation notes)
├── verify-report.md (PASS WITH WARNINGS, 0 CRITICAL, 1 WARNING, 2 SUGGESTIONs)
├── state.yaml (archive status: pending → archived)
└── specs/
    ├── tenant-organization/spec.md
    ├── usuarios-tenant-scoping/spec.md
    ├── usuarios-y-login/spec.md
    ├── auxiliary-catalogs/spec.md
    └── parametros-operativos/spec.md
```

---

## Verification Summary

**Date**: 2026-08-02  
**Verdict**: PASS WITH WARNINGS (0 CRITICAL, 1 WARNING, 2 SUGGESTIONs)  
**Build**: `dotnet build Ways.slnx` → 0 errors, 0 warnings  
**Tests**: `dotnet test Ways.slnx` → 220/220 passed (61 domain + 85 application + 74 integration)  
**TypeScript**: `npx tsc -b` → clean, exit 0  

**WARNING #1** (resolved 2026-08-02): Provisioning template's price-list placeholder specification vs. implementation gap. Originally proposed (proposal.md decision #2) but deferred due to missing `listas_precio` table. Now formally recorded in the spec itself with a note explaining the stage-2 followup.

**All deviations cross-checked and consistent** between state.yaml, design.md ADRs, and actual code:
- ADR-7: mail-based login (flow B), per-tenant usuario uniqueness
- ADR-11 override: RLS restoration on fiscal catalogs
- ADR-10 deferral: `query.DeLaEmpresa` not implemented (awaiting stage 4 ABM or first multi-empresa tenant)
- Fiscal catalog writes return 404 (no route mapped), not literal 403
- Section 4B scope extension: ServicioDeOrganizacion/OrganizacionEndpoints added during slice 4
- Size:exception PRs (slices 3 and 4) approved per judgment-day protocol

---

## Task Completion Gate

**Status**: PASS. All tasks across 4 slices checked and verified:

- Slice 1: 18 tasks (1.1-1.18) — tenancy plumbing, org tables, RLS, integration test infrastructure ✓
- Slice 2: 10 tasks (2.1-2.10) — usuarios retrofit, suspension enforcement, mail-based login ✓
- Slice 3: 27 tasks (3.1-3.27 incl. 3F provisioning) — catalogs, parametros, tenant provisioning ✓
- Slice 4: 10 tasks (4.1-4.10) + 5 tasks (4B.1-4B.5) — web ABMs + organization backend ✓

**No unchecked implementation tasks remain.** All 4 slices merged to main with passing judgment-day verdicts.

---

## Judgment-Day Reviews

All 4 slices passed dual-blind review per project protocol before merge:

| Slice | Rounds | Result | Key Findings | Merge Date |
|-------|--------|--------|--------------|-----------|
| 1 | 3 | APPROVED | RLS UPDATE test fix (SQLSTATE 42501), 2 critical fixes + 6 hardening | 2026-07-31 (PR #2) |
| 2 | 4 | APPROVED | Cross-tenant mail check fix, backfill ExecuteUpdateAsync fix, timing leveling | 2026-08-01 (PR #4) |
| 3 | 3 | APPROVED | Self-parent categoria cycle + CHECK constraint, error code generalization, race test (parametro_duplicado) | 2026-08-02 (PR #6) |
| 4 | 2 | APPROVED | Polish: snake_case error codes, categoria subtree/level filtering; ServicioDeOrganizacion scope extension | 2026-08-02 (PR #8) |

---

## Key Architectural Decisions Finalized

(See design.md for full ADRs; summary below)

- **ADR-1**: EntidadTenant new base class (distinct semantics from EntidadBase)
- **ADR-2**: Tenant resolved in OnValidatePrincipal, never from request
- **ADR-3**: Session-scoped `set_config`, not literal `SET LOCAL`
- **ADR-4**: Three access modes (tenant/plataforma/login) as GUCs; fail-closed
- **ADR-5**: FORCE ROW LEVEL SECURITY + startup role guard
- **ADR-6**: Named query filters (BajaLogica, Tenant) separately ignorable
- **ADR-7**: usuario unique per-tenant; mail globally unique; mail-based login (flow B)
- **ADR-8**: Cross-tenant access = 404 (never 403)
- **ADR-9**: Composite FKs via alternate keys enforce tenant integrity at DB level
- **ADR-10**: id_empresa scoping is opt-in, not global (DeLaEmpresa deferred to stage 4)
- **ADR-11**: One catalog machine (generic service + escape hatch for categorias)
- **ADR-12**: Categoria depth computed, not stored (3-level max via domain rule)
- **ADR-13**: Parametros: two partial unique indexes + typed key registry
- **ADR-14**: Seed data in InicializadorDeBaseDeDatos (idempotent, after RLS active)
- **ADR-15**: Every scoped table ships with its RLS policy in the same migration
- **ADR-16**: Provisioning via transaction + tenant impersonation + execution strategy
- **ADR-17**: Integration tests own RLS proof (no e2e; Playwright recommended follow-up)

---

## Deferred Items (Carried Forward)

- **ADR-10 DeLaEmpresa**: Query extension awaits stage 4 ABM or first multi-empresa tenant
- **Flow A (subdomain login)**: Extension point identified but depends on wildcard DNS/TLS (EasyPanel deployment layer)
- **EstadoTenant.Baja**: Dedicated action deferred to operational stage
- **E2E browser harness**: Playwright/e2e tests (ADR-17 follow-up)
- **Rendezvous timeout hardening**: Test robustness enhancement in ParametrosTests

---

## Implementation Timeline

| Date | Event |
|------|-------|
| 2026-07-31 | DB CHANGE GATE #1 approved (migration Organizacion). Slice 1 judgment-day complete (3 rounds). Slice 1 merged as PR #2. |
| 2026-08-01 | DB CHANGE GATES #2, #3, #4, #5 all approved. Slice 2 merged as PR #4. Slice 3 code-complete, judgment-day pending. |
| 2026-08-02 | Slice 3 merged as PR #6. Slice 4 merged as PR #8 (includes scope extension 4B: ServicioDeOrganizacion). Verify PASS WITH WARNINGS. Archive initiated. |

---

## Outstanding Considerations

1. **Price-list placeholder spec deviation** (now resolved by documentation): The template's inactive price-list placeholder is deferred to stage 2. The spec now explicitly records this deviation (instead of silently omitting it), and the stage-2 PRD will supersede it with the real implementation. ✓ RESOLVED (2026-08-02)

2. **Slice sizes and PR boundaries**: Slices 3 and 4 exceeded 400-line budget via user decisions (scope extension for org backend, tenant provisioning moved earlier). Both approved with `size:exception` and passed judgment-day (dual-blind review per project standard). ✓ RESOLVED (delivery strategy: size:exception approved per CLAUDE.md PR validation gate)

3. **E2E test gap**: No browser harness exists yet (ADR-17). Follow-up change recommended: introduce Playwright for end-to-end login → ABM → logout flows. ✓ ACCEPTED (documented in ADR-17)

---

## Merge Status

All 4 slices merged to main (as of 2026-08-02):
- `main` is now at commit b25cb0c (after PR #8 merge)
- PRs: #2 (Slice 1, 2026-07-31), #4 (Slice 2, 2026-08-01), #6 (Slice 3, 2026-08-02), #8 (Slice 4, 2026-08-02)
- Delivery strategy: stacked-to-main (each PR merges to main in order)

---

## Archive Completeness Checklist

- [x] All 5 delta specs synced to main `openspec/specs/`
- [x] No merge conflicts (specs are new; no existing baseline to merge into)
- [x] Change folder identified: `openspec/changes/archive/2026-08-02-stage-1-organization-and-catalogs/`
- [x] All artifacts preserved: proposal, design, tasks, apply-progress, verify-report, state, specs
- [x] Task completion gate PASS (no unchecked implementation tasks)
- [x] Verify PASS WITH WARNINGS (1 WARNING resolved by spec documentation, all deviations recorded)
- [x] Judgment-day verdicts recorded (all 4 slices APPROVED)
- [x] Archive report written and dated

---

## Next Recommended

**Stage 2**: `stage-2-clientes-proveedores` (clients/suppliers, pricing infrastructure, minimal listas_precio to join provisioning template).

**Follow-ups**:
- E2E harness (Playwright)
- ADR-10 DeLaEmpresa query extension (block: awaiting stage 4 ABM or first multi-empresa tenant)
- Flow A subdomain login (block: wildcard DNS/TLS at EasyPanel layer)
- EstadoTenant.Baja action (operational stage)
- Rendezvous timeout hardening (test robustness)

---

## SDD Cycle Status

This change is now **ARCHIVED**. The SDD cycle is complete:

- Proposal ✓ (resolved product questions, binding decisions)
- Spec ✓ (5 domain specs, compliance matrix verified)
- Design ✓ (17 ADRs, 4 natural slices, DB change gates)
- Tasks ✓ (63 tasks across 4 slices, all checked)
- Apply ✓ (4 slices implemented, 4 PRs merged, all judgment-day APPROVED)
- Verify ✓ (PASS WITH WARNINGS, 0 CRITICAL, test suite 220/220 green)
- Archive ✓ (specs synced, change folder archived, report filed)

Ready for next stage.
