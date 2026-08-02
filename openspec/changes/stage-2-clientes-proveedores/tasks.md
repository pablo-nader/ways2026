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

- [x] 1.1 **STOP.** Present the migration model summary (tables/columns/indexes/
  constraints/RLS for `clientes`, `proveedores`, `listas_precio`,
  `numeraciones_clientes`) **together with** the full backfill section (which
  pre-existing tenants get a CF cliente + General list) and wait for explicit
  approval before generating anything. *(resolved decision #3; CLAUDE.md)*
  — Presented in apply batch 1; **APPROVED by the user 2026-08-02**, exactly as
  presented (all 4 tables, enums, RLS, partial unique indexes incl. the two
  single-default guarantees on `listas_precio` and the tenant-wide `cuit`
  index, `ck_clientes_cf_protegido`, and the full backfill plan).

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

- [x] 1.7 Generate migration `ClientesYProveedoresEtapa2`: 4 tables,
  `tipo_documento`/`modo_lista` enums, `ux_clientes_numero`,
  `ux_proveedores_cuit`, `ux_listas_precio_default_compartido/empresa`,
  `ck_clientes_cf_protegido`, all FKs, `HabilitarRlsDeTenant` on all 4 tables,
  enum registration in `WaysDbContextFactory` + prod DI (per `comportamiento_medio_pago`
  precedent). *(design: Migration Sequencing)* — generated exactly as approved;
  also hand-named 4 FK-support indexes in snake_case (EF's default naming would
  have produced `IX_*` PascalCase, breaking the doc-10 convention) before
  generating. `dotnet ef migrations has-pending-model-changes` confirms clean.

### 1D. Numero counter + provisioning + backfill

- [x] 1.8 Add `AsignadorDeNumeroCliente.AsignarSiguienteAsync`/
  `AsegurarContadorAsync` (raw ADO.NET on the tx's `DbConnection`/`DbTransaction`,
  not `SqlQuery<T>`/`FromSqlRaw<T>` — reuse `VerificarRolSinBypassAsync`'s
  workaround). *(design decisions 2, 3)*
- [x] 1.9 Extend `PlantillaDeAprovisionamiento.V1` in place: add the Consumidor
  Final cliente item + General `listas_precio` item (closes the `ItemsDiferidos`
  gap) + unit tests. *(design decision 5)*
- [x] 1.10 Wire `ServicioDeAprovisionamiento.CrearTenantAsync`: ensure counter →
  assign `numero = 1` → insert CF cliente + General lista_precio inside the
  provisioning transaction. *(spec: tenant-organization / Tenant Provisioning With
  Template Seed)*
  — Wired in apply batch 2, after the migration landed (deferred from batch 1
  for the reason recorded there and in apply-progress.md).
- [x] 1.11 Add `InicializadorDeBaseDeDatos.BackfillDeClientesYListasPrecioAsync`:
  idempotent, skips a tenant that already has its CF/General row, runs after
  migrations. *(spec: tenant-organization / Backfill for Pre-Existing Tenants)*
  — Wired in apply batch 2. Runtime-verified: exercised for real by
  `ClientesProvisioningYBackfillTests` against Postgres real (Docker), including
  the idempotent-second-run assertion.

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
  Tenant Isolation)* — **runtime-verified**: `[Skip]` removed in apply batch 2,
  green against Postgres real (Docker), run twice for stability.
- [x] 1.15 [P] Integration: provisioning creates CF cliente (`numero = 1`, CF
  condición fiscal) + General lista_precio; backfill on a pre-existing tenant
  creates the same; backfill run twice is a no-op. *(spec: tenant-organization /
  Backfill for Pre-Existing Tenants; listas-precio-minimal / One Default List)*
  — **runtime-verified** in apply batch 2, after 1.10/1.11 were wired.
- [x] 1.16 Integration: direct raw-SQL `UPDATE clientes SET deleted_at = now()
  WHERE numero = 1` bypassing the service asserts 23514 → 409
  `consumidor_final_protegido`. *(backstop map)* — **runtime-verified** in apply
  batch 2.
- [x] 1.17 [P] Integration: one FK smoke test per new FK (`fk_clientes_*`,
  `fk_proveedores_*`), cross-tenant/nonexistent id → 23503/400. *(backstop map)*
  — **runtime-verified** in apply batch 2.
- [x] 1.18 Regression: existing Domain/Application/IntegrationTests suites
  unedited and green. — Final (apply batch 2): 69/69 Domain, 91/91 Application,
  91/91 Integration (baseline 74 + 17 newly-active test cases from the 10
  un-skipped `[Fact]`/`[Theory]` methods — theories with `MemberData`/`InlineData`
  expand to one result per row once active). Run twice, identical both times.
  One incidental pre-existing test-order fragility found and fixed in
  `CatalogosGlobalesRlsTests` (see apply-progress.md) — no production behavior
  changed by that fix, test-only.

---

## Slice 2: Clientes service + API + Web ABM (PR 2)

**Depends on**: Slice 1. **Start**: PR 1 merged/branch (`main`, PR #10). **Finish**: cliente
CRUD live through the API AND through a dedicated web screen, atomic numero proven under
concurrency, tests green. **Rollback**: new routes/screen only.

> **Re-scoping note (apply batch 4)**: the orchestrator directed this apply batch to deliver
> clientes as one vertical slice (service + API + web ABM together), not split by layer as
> originally planned (Slice 2 = service/API only, Slice 4 = both screens after both Slices 2
> and 3). The clientes-only portions of section 4A/4B below (`Clientes.tsx`, its routes/nav,
> `clientes.ts`/`tipos.ts` additions) were pulled forward and completed in this batch; the
> proveedores-only portions of Slice 4 remain pending until Slice 3 lands.

### 2A. Application

- [x] 2.1 Add `ServicioDeClientes` (list/create/edit/soft-delete): create calls
  `AsignadorDeNumeroCliente`, enforces `ReglaDeClientes.ValidarNoConsumidorFinal`
  on edit/delete, `GestionDeCatalogo` policy. *(spec: clientes / Cliente ABM
  Lifecycle and Authorization)* — `id_lista_precio` implemented as REQUIRED
  (not defaulted-when-omitted): spec.md's literal "id_lista_precio and
  id_condicion_fiscal are required... rejected before reaching the database"
  scenario was found to conflict with this task's original "defaults when
  omitted" wording (which only ever appeared here, not in design.md) —
  resolved in favor of spec.md as the higher-authority acceptance contract.
  See apply-progress.md batch 4 for the full resolution.
- [x] 2.2 Add cliente contracts (`AltaCliente`/`EdicionCliente`/`ClienteListado`
  + `ListaPrecioAsignable` reference type for the lista-precio selector).

### 2B. API

- [x] 2.3 Add `ClientesEndpoints`: list/create/edit/soft-delete,
  `GestionDeCatalogo` policy (tenant admin only). Also adds `GET
  /api/listas-precio` (read-only reference listing for the form's selector,
  same criterion as `/api/roles` in `UsuariosEndpoints` — not a `listas_precio`
  ABM, which stays out of scope this stage).

### 2C. Tests

- [x] 2.4 [P] Unit (InMemory): required `id_lista_precio`/`id_condicion_fiscal`
  validation, invalid-reference 400, CF guard blocks edit/delete of `numero = 1`,
  non-CF edit/delete allowed, cross-tenant 404. Default credit fields
  (0/false/0) and "vendedor blocked/admin allowed" moved to 2.5's integration
  tests — `CrearAsync` wraps `AsignadorDeNumeroCliente` + `BeginTransactionAsync`,
  neither supported by the InMemory provider (same reason
  `ServicioDeAprovisionamiento` has zero Application.Tests); authorization is
  endpoint-policy-only (`GestionDeCatalogo`), same as `ServicioDeCatalogo`, so
  it's not a service-level unit-test concern either.
- [x] 2.5 [P] Integration: concurrent create race (2 requests, tenant 1) →
  sequential distinct `numero`, no 23505 surfaced; duplicate/NULL
  `numero_documento` accepted; admin create→soft-delete round trip; vendedor
  403; cross-tenant cliente id → 404; default credit fields on create.
  *(spec: clientes / Atomic Per-Tenant Numero Assignment; numero_documento Has
  No Uniqueness Constraint)* — race test does NOT reuse 1.13's rendezvous
  interceptor: `AsignadorDeNumeroCliente`'s `UPDATE ... RETURNING` already
  serializes on Postgres's own row lock (confirmed without forcing anything in
  Slice 1 batch 3's `AsignadorDeNumeroClienteConcurrenciaTests`), so two
  `Task.WhenAll`-launched POSTs already race for real without needing a forced
  rendezvous. `ManejadorDeErrores`'s `_numero` exemption comment updated to
  reflect the race test now existing.
- [x] 2.6 Regression: Slice 1 suites unedited and green.

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

- [x] 4.1a Add dedicated `Clientes.tsx` ABM (not the generic catalog machine, per
  design decision 1): list/create/edit/soft-delete, credit fields in the form; CF
  row (`numero = 1`) rendered read-only with edit/delete disabled (defense in
  depth on top of the domain guard). *(spec: clientes / Cliente ABM Lifecycle;
  Consumidor Final Protected Row)* — done in Slice 2 (apply batch 4, re-scoped).
- [ ] 4.1b Add dedicated `Proveedores.tsx` ABM: list/create/edit/soft-delete.
  *(spec: proveedores / Proveedor ABM Lifecycle)* — pending, Slice 3/4.

### 4B. Wiring + smoke

- [x] 4.3a Wire clientes route (`/clientes`) + nav entry; add `tipos.ts`
  additions and `clientes.ts` API client. Done in Slice 2 (apply batch 4).
- [ ] 4.3b Wire proveedores route + nav entry; add `proveedores.ts` API client.
  Pending, Slice 3/4.
- [x] 4.4a Smoke-verify the clientes screen (`tsc -b`/`oxlint`/`vite build`
  clean). Done in Slice 2 (apply batch 4) — no live-host contract smoke this
  batch (no dev server/API host was started); relies on the backend
  integration-test coverage (task 2.5) proving the exact contract the screen
  consumes (`ClienteListado`/`AltaCliente`/`EdicionCliente`/
  `ListaPrecioAsignable` shapes match `Ways.Application.Clientes.Contratos`
  field-for-field).
- [ ] 4.4b Smoke-verify the proveedores screen. Pending, Slice 3/4.

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
