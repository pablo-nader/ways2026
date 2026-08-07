# Proveedores Specification

## Purpose

Defines the `proveedores` table (doc 10 §2): full supplier schema, the unique
`cuit` per tenant, and proveedor ABM.

## Requirements

### Requirement: Proveedor Schema At Rest

`proveedores` MUST be catalog-scoped (`id_tenant`, `id_empresa NULL`, doc 09)
with the full doc 10 §2 shape: `razon_social`, `nombre_fantasia NULL`,
`cuit VARCHAR(13) NULL`, `id_condicion_fiscal NOT NULL`, contact fields
(`domicilio`, `telefono`, `email`, `vendedor`, `celular_vendedor`,
`supervisor`, `celular_supervisor`), `margen NUMERIC(5,2) NULL`, `observaciones`.

#### Scenario: Create a proveedor without cuit
- GIVEN a tenant admin creates a proveedor without a `cuit`
- WHEN the row is persisted
- THEN it succeeds with `cuit = NULL`

#### Scenario: Invalid condicion fiscal reference maps to 400
- GIVEN a create request references a non-existent `id_condicion_fiscal`
- WHEN the insert reaches Postgres
- THEN 23503 is raised and `ManejadorDeErrores` maps it to 400 `referencia_invalida`

### Requirement: cuit Uniqueness Is Scoped Per Tenant

`proveedores.cuit` MUST be unique within its tenant via a partial index
(`WHERE deleted_at IS NULL`), with `NULL` values allowed and not compared. A
duplicate `cuit` inside the same tenant is a data-entry error and MUST be
blocked; the same real-world supplier MAY exist with the same `cuit` across
different tenants. Every duplicate-`cuit` write path MUST map Postgres 23505 to
a domain 409 in `ManejadorDeErrores`.

#### Scenario: Duplicate cuit within the same tenant is rejected
- GIVEN tenant 1 has a proveedor with `cuit = "30712345678"`
- WHEN tenant 1 attempts to create another proveedor with the same `cuit`
- THEN the request is rejected with a 409 mapped from Postgres 23505

#### Scenario: Same cuit across different tenants is allowed
- GIVEN tenant 1 has a proveedor with `cuit = "30712345678"`
- WHEN tenant 2 creates a proveedor with the same `cuit`
- THEN both proveedores are created without a uniqueness conflict

#### Scenario: Concurrent creation race yields exactly one winner
- GIVEN two concurrent create requests for tenant 1 with the same `cuit`
- WHEN both transactions commit
- THEN exactly one receives 201 and the other receives 409, asserted via the
  translated domain code, not just an exception type

#### Scenario: NULL cuit never collides
- GIVEN tenant 1 has a proveedor with `cuit = NULL`
- WHEN tenant 1 creates a second proveedor with `cuit = NULL`
- THEN both are created without a uniqueness conflict

### Requirement: Proveedor ABM Lifecycle and Authorization

Proveedores MUST support list/create/edit/soft-delete, gated by the same
authorization tier as stage-1 catalogs (`GestionDeCatalogo`: tenant `admin`
only — `root` and `vendedor` excluded).

#### Scenario: Admin creates and soft-deletes a proveedor
- GIVEN a tenant admin
- WHEN they create a proveedor and later soft-delete it
- THEN the row is persisted under the admin's `id_tenant`, and after deletion
  `deleted_at` is set and it no longer appears in the default list

#### Scenario: Vendedor blocked from writing
- GIVEN a user with the `vendedor` role
- WHEN they call the proveedor create endpoint
- THEN the request is rejected with an authorization error

#### Scenario: Soft-deleted cuit is reusable
- GIVEN a soft-deleted proveedor with `cuit = "30712345678"` in tenant 1
- WHEN a new proveedor is created in tenant 1 with the same `cuit`
- THEN creation succeeds, per the partial unique index pattern

### Requirement: Tenant Isolation for Proveedores

`proveedores` MUST enforce the two-layer isolation guarantee (EF Core global
query filter + Postgres RLS without `BYPASSRLS`) established in stage 1.

#### Scenario: EF filter blocks cross-tenant read
- GIVEN a request scoped to tenant 1
- WHEN a query executes through the normal `DbContext` for a proveedor of
  tenant 2
- THEN no rows are returned

#### Scenario: RLS blocks a read that bypasses EF
- GIVEN the app DB role has no `BYPASSRLS`
- WHEN a raw SQL query or `IgnoreQueryFilters()` reads tenant 2's proveedores
  while `app.tenant_id = 1`
- THEN RLS returns zero rows

### Requirement: Proveedor Referenced By A Comprobante Compra Cannot Be Removed

The composite FK from `comprobantes_compra.id_proveedor` to `proveedores`
MUST be `ON DELETE RESTRICT`. A proveedor referenced by at least one
`comprobante_compra` MUST NOT be removable at the schema layer.

#### Scenario: A hard delete of a referenced proveedor is rejected at the schema layer
- GIVEN a proveedor referenced by a confirmada compra
- WHEN a hard delete of that proveedor row is attempted at the database
- THEN Postgres rejects it with a foreign key violation, mapped by
  `db-error-backstops` to a domain conflict

### Requirement: Proveedor Saldo Read Entry Point

Proveedor detail reads MUST expose a saldo entry point implementing the
`saldo-de-proveedor` derived read, gated by `Politicas.OperacionDePos`.

#### Scenario: Proveedor detail includes the derived saldo
- GIVEN a proveedor with confirmed compras and linked gastos
- WHEN their detail is read
- THEN the response includes the derived saldo per the
  `saldo-de-proveedor` specification
