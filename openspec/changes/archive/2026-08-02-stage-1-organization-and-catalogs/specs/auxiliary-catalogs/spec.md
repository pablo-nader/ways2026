# Auxiliary Catalogs Specification

## Purpose

Defines ABM behavior for the doc 10 §1 padrones: `areas`, `categorias`,
`marcas`, `grupos`, `medios_pago` (tenant-scoped catalogs) and
`condiciones_fiscales`, `alicuotas_iva`, `tipos_comprobante`
(platform-global, read-only for tenants).

## Requirements

### Requirement: Catalog ABM Lifecycle

Each tenant-scoped catalog (`areas`, `categorias`, `marcas`, `grupos`,
`medios_pago`) MUST support list, create, edit, and soft-delete operations,
scoped by `id_tenant` with `id_empresa NULL` meaning shared across the
tenant's empresas.

#### Scenario: Create a catalog row

- GIVEN a tenant admin
- WHEN they create a new `marca` with a name
- THEN the row is persisted with the admin's `id_tenant` and `id_empresa = NULL`

#### Scenario: Soft delete hides but does not erase

- GIVEN an active `area` row
- WHEN the admin deletes it
- THEN `deleted_at` is set and the row no longer appears in the default list
- AND the row remains queryable via `IgnoreQueryFilters()` for audit

#### Scenario: Soft-deleted name is reusable

- GIVEN a soft-deleted `grupo` named "Ofertas"
- WHEN a new `grupo` is created with the same name
- THEN creation succeeds, per the partial unique index pattern (doc 08
  precedent)

### Requirement: Categoria Depth Limit

`categorias` MUST allow an unrestricted `id_categoria_padre` chain in the
schema, but the domain layer MUST reject any create or move operation that
would place a categoria deeper than 3 levels from its root.

#### Scenario: Depth within limit accepted

- GIVEN "Bebidas" (level 1) and "Gaseosas" (level 2)
- WHEN a categoria "Cola" is created under "Gaseosas" (level 3)
- THEN creation succeeds

#### Scenario: Depth exceeding limit rejected

- GIVEN "Cola" already sits at level 3
- WHEN a categoria "Cola 1.5L" is created under "Cola" (level 4)
- THEN the domain layer rejects the request with a validation error,
  independent of any UI check

### Requirement: Fiscal Catalogs Are Platform-Managed and Read-Only

`condiciones_fiscales`, `alicuotas_iva`, and `tipos_comprobante` MUST have
no `id_tenant` column (`[global]` scope per doc 10) and MUST expose
read-only endpoints to tenant users. No tenant-facing create/edit/delete ABM
exists for these in stage 1.

#### Scenario: Tenant reads fiscal catalogs

- GIVEN a tenant user
- WHEN they list `alicuotas_iva`
- THEN they receive the platform-wide rows, identical across all tenants

#### Scenario: Tenant write attempt rejected

- GIVEN a tenant admin
- WHEN they call a create/edit/delete endpoint for `tipos_comprobante`
- THEN no such endpoint exists / the request is rejected (404 or 403)
