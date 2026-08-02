# Listas de Precio (Minimal) Specification

## Purpose

Defines the minimal `listas_precio` table (doc 10 §3) needed so `clientes` has
a valid, non-null price list from day one: the table itself and one General
`es_default` list per tenant. `precios` and derived lists are out of scope
(stage 3).

## Requirements

### Requirement: listas_precio Schema At Rest

`listas_precio` MUST be catalog-scoped (`id_tenant`, `id_empresa NULL`, doc 09)
with `id_lista_precio`, `nombre`, `es_default BOOLEAN`, `modo` (`fija` |
`derivada`), `id_lista_base NULL`, `porcentaje NULL`, `activo`. No `precios`
rows and no `derivada` lists are created in this stage — the columns exist per
doc 10's full-model-upfront principle but are unused until stage 3.

#### Scenario: Table accepts the fija shape only in this stage
- GIVEN a tenant's General list
- WHEN it is queried
- THEN `modo = 'fija'`, `id_lista_base = NULL`, `porcentaje = NULL`

### Requirement: One Default List Per Tenant

Every tenant MUST have exactly one `listas_precio` row with `es_default = true`
(the General list), and `clientes.id_lista_precio` MUST be a `NOT NULL` foreign
key to `listas_precio`.

#### Scenario: General list exists after provisioning
- GIVEN a newly provisioned tenant
- WHEN its `listas_precio` are queried
- THEN exactly one row exists with `es_default = true`

#### Scenario: Cliente creation requires an explicit lista

> Superseded wording (verify, 2026-08-02): an earlier draft had cliente creation
> defaulting to the General list when `id_lista_precio` was omitted. That conflicted
> with `specs/clientes/spec.md` ("id_lista_precio and id_condicion_fiscal are
> required"), which won as the acceptance contract — see design.md:29 and
> apply-progress.md batch 4/5. This scenario now states the implemented behavior.

- GIVEN a tenant admin creates a cliente without specifying `id_lista_precio`
- WHEN the request is validated
- THEN it is rejected with 400 `id_lista_precio_requerido` (no defaulting occurs)

#### Scenario: Invalid id_lista_precio reference maps to 400
- GIVEN a create request references a non-existent `id_lista_precio`
- WHEN the insert reaches Postgres
- THEN 23503 is raised and `ManejadorDeErrores` maps it to 400 `referencia_invalida`

### Requirement: listas_precio ABM Is Out of Scope This Stage

No create/edit/delete endpoint for `listas_precio` MUST be exposed to tenants
in this stage; the General list is platform-seeded via provisioning and
backfill only. Reads MAY be exposed to support the cliente form's list
selector once more than one list exists (deferred: only one list exists today).

#### Scenario: No tenant-facing write endpoint exists
- GIVEN a tenant admin
- WHEN they attempt to call a create/edit/delete endpoint for `listas_precio`
- THEN no such endpoint exists (404)

### Requirement: Tenant Isolation for listas_precio

`listas_precio` MUST enforce the two-layer isolation guarantee (EF Core global
query filter + Postgres RLS without `BYPASSRLS`) established in stage 1.

#### Scenario: EF filter blocks cross-tenant read
- GIVEN a request scoped to tenant 1
- WHEN a query executes through the normal `DbContext` for a `listas_precio`
  row of tenant 2
- THEN no rows are returned

#### Scenario: RLS blocks a read that bypasses EF
- GIVEN the app DB role has no `BYPASSRLS`
- WHEN a raw SQL query or `IgnoreQueryFilters()` reads tenant 2's
  `listas_precio` while `app.tenant_id = 1`
- THEN RLS returns zero rows
