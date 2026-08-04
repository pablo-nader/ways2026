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
artículo independently of editing the artículo's other fields. Add/remove
stay gated by `GestionDeCatalogo`. Listing MUST return only active barcodes —
the same `BajaLogica`/global soft-delete filter used across the rest of the
ABM — and is now also reachable under `Politicas.OperacionDePos` (Vendedor +
Admin), since the POS needs it for scan lookup. A listing request against a
nonexistent or cross-tenant `id_articulo` MUST return the same uniform 404
(ADR-8) used by the add/remove paths.
(Previously: listing was gated by `GestionDeCatalogo` only — Vendedor was
blocked from every barcode operation, including read.)

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

#### Scenario: Vendedor blocked from adding or removing barcodes

- GIVEN a user with the `vendedor` role
- WHEN they call the barcode add or remove endpoint
- THEN the request is rejected with an authorization error

#### Scenario: Vendedor can list barcodes for POS scan lookup

- GIVEN a user with the `vendedor` role
- WHEN they call the barcode listing endpoint for an articulo of their tenant
- THEN the request succeeds

### Requirement: Scan Resolution Rule

The POS scan-resolution parser MUST resolve an input code by length:
`< 7` digits resolves against `articulos`' internal code (`codigo_interno`);
`>= 7` digits resolves against `codigos_barra.codigo`. Both paths MUST filter
`activo = true` only. The parser MUST accept `<cantidad>*<codigo>` syntax
(e.g. `3*7790001` loads 3 units), and MUST default an empty or `0` cantidad
to `1`. Re-scanning a code already in the cart MUST sum quantities rather
than adding a new line.

#### Scenario: Short code resolves by codigo_interno

- GIVEN an articulo with `codigo_interno = "42"` and no matching 7+ digit
  barcode
- WHEN the scan input `"42"` is resolved
- THEN it resolves to that articulo via `codigo_interno`

#### Scenario: Long code resolves by codigos_barra

- GIVEN an articulo with barcode `"7790001234567"`
- WHEN the scan input `"7790001234567"` is resolved
- THEN it resolves to that articulo via `codigos_barra`

#### Scenario: Quantity-prefixed syntax loads the given quantity

- GIVEN the scan input `"3*7790001234567"`
- WHEN it is resolved
- THEN 3 units of the matching articulo are added to the cart

#### Scenario: Re-scanning sums quantity instead of duplicating the line

- GIVEN a cart already containing 2 units of an articulo
- WHEN the same code is scanned again for 1 more unit
- THEN the cart shows a single line with quantity 3, not two lines

#### Scenario: Inactive articulo is not resolved

- GIVEN an articulo with `activo = false` and a matching barcode
- WHEN that barcode is scanned
- THEN resolution fails to find an active match

#### Scenario: Unknown code is rejected

- GIVEN a scan input matching no `codigo_interno` or `codigos_barra` row
- WHEN it is resolved
- THEN the scan is rejected with a "not found" result and no line is added

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
