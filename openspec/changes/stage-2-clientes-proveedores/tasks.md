# Tasks: Stage 2 — Clientes y Proveedores

## Review Workload Forecast

| Field | Value |
|-------|-------|
| Estimated changed lines | ~2,250–3,150 total (incl. EF migration boilerplate) |
| 400-line budget risk | High |
| Chained PRs recommended | Yes |
| Suggested split | PR 1 → (PR 2 ∥ PR 3) → PR 4 |
| Delivery strategy | chained PRs, stacked-to-main (resolved, cached decision) |
| Chain strategy | stacked-to-main |

Decision needed before apply: No — resolved: chained PRs, stacked-to-main.
Chained PRs recommended: Yes
Chain strategy: stacked-to-main — each slice PR merges to main in order; Slice 1 alone
(~1,100–1,500 lines, migration-heavy, one combined migration per the DB CHANGE GATE) may
still exceed the 400-line budget with no further reasonable split — if so at apply-time,
record `size:exception` for that PR only, same precedent as stage 1's Slices 3/4. Slices
2 and 3 are independent (both depend only on Slice 1) and can run in parallel.
400-line budget risk: High (mitigated by the split below; Slice 1 may need `size:exception`)

### Suggested Work Units

| Unit | Goal | Likely PR | Notes |
|------|------|-----------|-------|
| 1 | Schema (4 tables, DB CHANGE GATE), domain entities, numero counter, provisioning template extension, backfill, backstop mappings | PR 1 | ~1,100–1,500 lines. Base: `main`. One combined migration under one gate (schema + backfill together, resolved decision #3). |
| 2 | Clientes service + API + tests | PR 2 | ~350–500 lines. Depends on PR 1. Independent of PR 3. |
| 3 | Proveedores service + API + tests | PR 3 | ~300–450 lines. Depends on PR 1. Independent of PR 2. |
| 4 | Web ABMs (clientes + proveedores) | PR 4 | ~500–700 lines. Depends on PR 2 + PR 3 (consumes both API surfaces). |

Each slice: start = base branch state, finish = its own tests green, verification = its
own unit/integration tests, rollback = down-migration (Slice 1 only) or new-routes-only
removal (Slices 2–4), per proposal.md § Rollback Plan.

---

## Slice 1: Schema, Domain Foundation & Provisioning (PR 1)

**Start**: `main`. **Finish**: migration applied, RLS proven on all 4 tables, CF/General
list backfilled + provisioned, tests green. **Rollback**: down-migration (additive only).

### 1A. DB CHANGE GATE — BLOCKING

- [ ] 1.1 **STOP.** Present the migration model summary (tables/columns/indexes/
  constraints/RLS for `clientes`, `proveedores`, `listas_precio`,
  `numeraciones_clientes`) **together with** the full backfill section (which
  pre-existing tenants get a CF cliente + General list) and wait for explicit
  approval before generating anything. *(resolved decision #3; CLAUDE.md)*
  — Presented in apply batch 1; awaiting explicit approval.

### 1B. Domain

- [x] 1.2 [P] Add `Cliente : EntidadTenant` (identity/contact, tipo_documento/
  numero_documento, credit fields, FKs) in `Ways.Domain/Clientes`. *(spec: clientes
  / Cliente Schema At Rest)*
- [x] 1.3 [P] Add `Proveedor : EntidadTenant` in `Ways.Domain/Proveedores`. *(spec:
  proveedores / Proveedor Schema At Rest)*
- [x] 1.4 [P] Add `ListaPrecio : CatalogoSimple` (`es_default`, `modo`,
  `id_lista_base?`, `porcentaje?`) reusing `ConfiguracionDeCatalogo<T>` EF base, no
  service/API. *(design decision 1; spec: listas-precio-minimal)*
- [x] 1.5 [P] Add `NumeracionCliente` entity (`id_tenant` PK, `proximo_numero`) + EF
  config. *(design table shapes)*
- [x] 1.6 Add pure `ReglaDeClientes.ValidarNoConsumidorFinal` (blocks edit/delete
  when `Numero == 1`) + unit tests (CF blocked, non-CF allowed). *(spec: clientes /
  Consumidor Final Protected Row)*

### 1C. Migration (only after 1.1 approved)

- [ ] 1.7 Generate migration `ClientesYProveedoresEtapa2`: 4 tables,
  `tipo_documento`/`modo_lista` enums, `ux_clientes_numero`,
  `ux_proveedores_cuit`, `ux_listas_precio_default_compartido/empresa`,
  `ck_clientes_cf_protegido`, all FKs, `HabilitarRlsDeTenant` on all 4 tables,
  enum registration in `WaysDbContextFactory` + prod DI (per `comportamiento_medio_pago`
  precedent). *(design: Migration Sequencing)*

### 1D. Numero counter + provisioning + backfill

- [x] 1.8 Add `AsignadorDeNumeroCliente.AsignarSiguienteAsync`/
  `AsegurarContadorAsync` (raw ADO.NET on the tx's `DbConnection`/`DbTransaction`,
  not `SqlQuery<T>`/`FromSqlRaw<T>` — reuse `VerificarRolSinBypassAsync`'s
  workaround). *(design decisions 2, 3)*
- [x] 1.9 Extend `PlantillaDeAprovisionamiento.V1` in place: add the Consumidor
  Final cliente item + General `listas_precio` item (closes the `ItemsDiferidos`
  gap) + unit tests. *(design decision 5)*
- [ ] 1.10 Wire `ServicioDeAprovisionamiento.CrearTenantAsync`: ensure counter →
  assign `numero = 1` → insert CF cliente + General lista_precio inside the
  provisioning transaction. *(spec: tenant-organization / Tenant Provisioning With
  Template Seed)*
  — DEFERRED to the migration batch: written and confirmed compiling in apply
  batch 1, then reverted (`clientes`/`listas_precio` don't exist yet; wiring this
  broke `AprovisionamientoTests`' 2 existing tests). See apply-progress.md.
- [ ] 1.11 Add `InicializadorDeBaseDeDatos.BackfillDeClientesYListasPrecioAsync`:
  idempotent, skips a tenant that already has its CF/General row, runs after
  migrations. *(spec: tenant-organization / Backfill for Pre-Existing Tenants)*
  — DEFERRED to the migration batch: same reason as 1.10, but higher blast
  radius (wiring it broke 63/74 integration tests — `EjecutarAsync` runs on every
  test's first `CreateClient()`). See apply-progress.md.

### 1E. db-error-backstops mapping

- [x] 1.12 Extend `ManejadorDeErrores` suffix classifier: `_cuit` →
  `cuit_duplicado` (409), `_numero` → `numero_duplicado` (409); add a new 23514
  case for `ck_clientes_cf_protegido` → 409 `consumidor_final_protegido`; confirm
  the existing generic `_nombre`/`_default`/`fk_` mappings already cover
  `listas_precio` and all new FKs. *(design: Backstop Map)*

### 1F. Hygiene (carried from stage 1)

- [x] 1.13 Add timeout + assert to the rendezvous `gate.Wait()` in
  `ParametrosTests.cs` (stage-1 INFO carried forward; hardens the pattern before
  Slices 2–3 reuse it for the numero/cuit race tests). *(stage-1 state.yaml INFO)*

### 1G. Tests

- [x] 1.14 [P] Integration: RLS proofs for all 4 new tables (EF filter blocks
  cross-tenant read; raw-SQL/`IgnoreQueryFilters` blocked), mirroring
  `AislamientoDeTenantTests`. *(spec: clientes/proveedores/listas-precio-minimal /
  Tenant Isolation)* — implemented, `[Skip]`-gated on the migration (task 1.7).
- [x] 1.15 [P] Integration: provisioning creates CF cliente (`numero = 1`, CF
  condición fiscal) + General lista_precio; backfill on a pre-existing tenant
  creates the same; backfill run twice is a no-op. *(spec: tenant-organization /
  Backfill for Pre-Existing Tenants; listas-precio-minimal / One Default List)*
  — implemented, `[Skip]`-gated on the migration AND on 1.10/1.11's wiring
  (same batch as the migration).
- [x] 1.16 Integration: direct raw-SQL `UPDATE clientes SET deleted_at = now()
  WHERE numero = 1` bypassing the service asserts 23514 → 409
  `consumidor_final_protegido`. *(backstop map)* — implemented, `[Skip]`-gated on
  the migration.
- [x] 1.17 [P] Integration: one FK smoke test per new FK (`fk_clientes_*`,
  `fk_proveedores_*`), cross-tenant/nonexistent id → 23503/400. *(backstop map)*
  — implemented, `[Skip]`-gated on the migration.
- [x] 1.18 Regression: existing Domain/Application/IntegrationTests suites
  unedited and green. — 69/69 Domain, 91/91 Application, 74 passed + 10 skipped
  Integration (0 regressions).

---

## Slice 2: Clientes service + API (PR 2)

**Depends on**: Slice 1. **Start**: PR 1 merged/branch. **Finish**: cliente CRUD live
through the API, atomic numero proven under concurrency, tests green. **Rollback**:
new routes only.

### 2A. Application

- [ ] 2.1 Add `ServicioDeClientes` (list/create/edit/soft-delete): create calls
  `AsignadorDeNumeroCliente`, defaults `id_lista_precio` to the tenant's
  `es_default` list when omitted, enforces `ReglaDeClientes.ValidarNoConsumidorFinal`
  on edit/delete, `GestionDeCatalogo` policy. *(spec: clientes / Cliente ABM
  Lifecycle and Authorization)*
- [ ] 2.2 Add cliente contracts (`ClienteListado`/`Alta`/`Edicion`).

### 2B. API

- [ ] 2.3 Add `ClientesEndpoints`: list/create/edit/soft-delete,
  `GestionDeCatalogo` policy (tenant admin only).

### 2C. Tests

- [ ] 2.4 [P] Unit (InMemory): default credit fields (0/false/0), required
  `id_lista_precio`/`id_condicion_fiscal` validation, CF guard blocks edit/delete
  of `numero = 1`, vendedor blocked, admin allowed.
- [ ] 2.5 [P] Integration: concurrent create race (2 requests, tenant 1) →
  sequential distinct `numero`, no 23505 surfaced (db-error-backstops race test
  for the atomic `ux_clientes_numero` path — reuse 1.13's hardened rendezvous);
  duplicate/NULL `numero_documento` accepted; admin create→soft-delete round
  trip; vendedor 403; cross-tenant cliente id → 404. *(spec: clientes / Atomic
  Per-Tenant Numero Assignment; numero_documento Has No Uniqueness Constraint)*
- [ ] 2.6 Regression: Slice 1 suites unedited and green.

---

## Slice 3: Proveedores service + API (PR 3)

**Depends on**: Slice 1 (independent of Slice 2). **Start**: PR 1 merged/branch.
**Finish**: proveedor CRUD live, cuit uniqueness proven under concurrency, tests
green. **Rollback**: new routes only.

### 3A. Application

- [ ] 3.1 Add `ServicioDeProveedores` (list/create/edit/soft-delete),
  `GestionDeCatalogo` policy. *(spec: proveedores / Proveedor ABM Lifecycle and
  Authorization)*
- [ ] 3.2 Add proveedor contracts.

### 3B. API

- [ ] 3.3 Add `ProveedoresEndpoints`.

### 3C. Tests

- [ ] 3.4 [P] Unit (InMemory): create without `cuit` succeeds, required
  `id_condicion_fiscal` validation, vendedor blocked.
- [ ] 3.5 [P] Integration: db-error-backstops race test for `ux_proveedores_cuit`
  (2 concurrent same-cuit creates → 1×201 + 1×409 via translated domain code, not
  exception type — reuse 1.13's hardened rendezvous); same cuit across 2 tenants
  allowed; `NULL` cuit never collides; soft-deleted cuit reusable; cross-tenant id
  → 404. *(spec: proveedores / cuit Uniqueness Is Scoped Per Tenant)*
- [ ] 3.6 Regression: Slice 1 suites unedited and green.

---

## Slice 4: Web ABMs (PR 4)

**Depends on**: Slices 2 + 3. **Start**: PR 2/PR 3 branch per chosen chain
strategy. **Finish**: both screens functional against the API, smoke-verified.
**Rollback**: new routes only, no existing screen touched.

### 4A. Screens

- [ ] 4.1 Add dedicated `Clientes.tsx` ABM (not the generic catalog machine, per
  design decision 1): list/create/edit/soft-delete, credit fields in the form; CF
  row (`numero = 1`) rendered read-only with edit/delete disabled (defense in
  depth on top of the domain guard). *(spec: clientes / Cliente ABM Lifecycle;
  Consumidor Final Protected Row)*
- [ ] 4.2 Add dedicated `Proveedores.tsx` ABM: list/create/edit/soft-delete.
  *(spec: proveedores / Proveedor ABM Lifecycle)*

### 4B. Wiring + smoke

- [ ] 4.3 Wire routes + nav entries; add `tipos.ts` additions and API clients
  (`clientes.ts`, `proveedores.ts`).
- [ ] 4.4 Smoke-verify both screens against integration test expectations
  (`tsc -b`/`oxlint`/`vite build` clean + contract smoke against a real API host,
  per ADR-17's no-e2e-harness gap, same approach as stage 1's 4.9).

---

## Dependency Summary

```
Slice 1 (schema + domain + provisioning + backfill)
   ├─▶ Slice 2 (clientes service + API)
   └─▶ Slice 3 (proveedores service + API)
            Slice 2, Slice 3 ─▶ Slice 4 (web ABMs)
```

Within each slice, `[P]`-tagged tasks are parallelizable; all others are
sequential (schema → infra → application → tests; the DB CHANGE GATE always
blocks the migration-generation task).
