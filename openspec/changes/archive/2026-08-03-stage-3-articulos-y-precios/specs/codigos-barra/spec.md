# Codigos de Barra Specification

## Purpose

Defines `codigos_barra` (doc 10 §3): N barcodes per artículo, tenant-unique,
one artículo per barcode with no overrides, and barcode add/remove
management.

## Requirements

### Requirement: Codigo De Barra Schema And Cardinality

`codigos_barra` MUST be tenant-wide (`id_tenant`) with `id_codigo_barra`,
`id_articulo NOT NULL` (tenant-scoped FK), `codigo citext NOT NULL`, `activo`.
An artículo MAY have zero or many barcodes.

#### Scenario: Create multiple barcodes for one articulo

- GIVEN a tenant admin's artículo has no barcodes
- WHEN two `codigos_barra` rows are added for it
- THEN both are persisted, each pointing to the same artículo

#### Scenario: Invalid articulo reference maps to 400

- GIVEN a create request references a non-existent `id_articulo`
- WHEN the insert reaches Postgres
- THEN 23503 is raised and `ManejadorDeErrores` maps it to 400
  `referencia_invalida`

### Requirement: Barcode Uniqueness Per Tenant

`codigos_barra.codigo` MUST be unique within its tenant via `UNIQUE (codigo,
id_tenant) WHERE deleted_at IS NULL` — one barcode belongs to exactly one
artículo of the tenant; no per-artículo overrides exist. Every
duplicate-code write path MUST map Postgres 23505 to a domain 409.

#### Scenario: Duplicate barcode within the same tenant is rejected

- GIVEN tenant 1's artículo A has barcode `"7791234567890"`
- WHEN tenant 1 attempts to add the same barcode to a different artículo
- THEN the request is rejected with 409 mapped from Postgres 23505

#### Scenario: Same barcode across different tenants is allowed

- GIVEN tenant 1 has an artículo with barcode `"7791234567890"`
- WHEN tenant 2 creates an artículo with the same barcode
- THEN both are created without a uniqueness conflict

#### Scenario: Concurrent creation race yields exactly one winner

- GIVEN two concurrent requests for tenant 1 adding the same new barcode to
  two different artículos
- WHEN both transactions commit
- THEN exactly one receives 201 and the other receives 409, asserted via the
  translated domain code, not just an exception type

### Requirement: Barcode Add/Remove/List Management

Tenant admins MUST be able to add, remove, and list the barcodes of an
artículo independently of editing the artículo's other fields, gated by
`GestionDeCatalogo`. Listing MUST return only active barcodes — the same
`BajaLogica`/global soft-delete filter used across the rest of the ABM —
and a listing request against a nonexistent or cross-tenant `id_articulo`
MUST return the same uniform 404 (ADR-8) used by the add/remove paths.

#### Scenario: Admin removes a barcode without affecting the articulo

- GIVEN an artículo with two barcodes
- WHEN a tenant admin removes one
- THEN the artículo persists unchanged and the remaining barcode still
  resolves it

#### Scenario: Listing returns only active barcodes

- GIVEN an artículo with two barcodes, one of which is later removed
- WHEN a tenant admin lists the artículo's barcodes
- THEN only the remaining barcode is returned, with its persisted `codigo`;
  the removed one is excluded

#### Scenario: Listing barcodes of a nonexistent or cross-tenant articulo returns 404

- GIVEN an `id_articulo` that does not exist, or that belongs to another
  tenant
- WHEN a tenant admin requests that artículo's barcode listing
- THEN the response is 404, the same uniform ADR-8 result the add/remove
  endpoints return for a nonexistent or cross-tenant `id_articulo`

#### Scenario: Vendedor blocked from managing or listing barcodes

- GIVEN a user with the `vendedor` role
- WHEN they call the barcode add, remove, or list endpoint
- THEN the request is rejected with an authorization error

### Requirement: Tenant Isolation for codigos_barra

`codigos_barra` MUST enforce the two-layer isolation guarantee (EF Core
global query filter + Postgres RLS without `BYPASSRLS`) established in stage
1.

#### Scenario: EF filter blocks cross-tenant read

- GIVEN a request scoped to tenant 1
- WHEN a query executes through the normal `DbContext` for a `codigos_barra`
  row of tenant 2
- THEN no rows are returned

#### Scenario: RLS blocks a read that bypasses EF

- GIVEN the app DB role has no `BYPASSRLS`
- WHEN a raw SQL query or `IgnoreQueryFilters()` reads tenant 2's
  `codigos_barra` while `app.tenant_id = 1`
- THEN RLS returns zero rows
