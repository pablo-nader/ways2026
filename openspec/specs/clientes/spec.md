# Clientes Specification

## Purpose

Defines the `clientes` table (doc 10 §2): full commercial-entity schema at rest,
the atomic per-tenant `numero` sequence, the protected Consumidor Final row, and
cliente ABM.

## Requirements

### Requirement: Cliente Schema At Rest

`clientes` MUST be catalog-scoped (`id_tenant`, `id_empresa NULL`, doc 09) with the
full doc 10 §2 shape: identity/contact fields, `tipo_documento`/`numero_documento`,
`id_condicion_fiscal NOT NULL`, `id_lista_precio NOT NULL`, and credit fields
`limite_credito NUMERIC(14,2) DEFAULT 0`, `credito_ilimitado BOOLEAN DEFAULT false`,
`saldo NUMERIC(14,2) DEFAULT 0`. No cuenta-corriente movement engine exists yet
(stage 7); `saldo` stays at its default outside Consumidor Final seeding.

#### Scenario: Create a cliente with default credit fields
- GIVEN a tenant admin creates a cliente without specifying credit fields
- WHEN the row is persisted
- THEN `limite_credito = 0`, `credito_ilimitado = false`, `saldo = 0`

#### Scenario: id_lista_precio and id_condicion_fiscal are required
- GIVEN a tenant admin submits a cliente missing `id_lista_precio` or `id_condicion_fiscal`
- WHEN the request is validated
- THEN it is rejected before reaching the database

#### Scenario: Invalid FK reference maps to 400
- GIVEN a create request references a non-existent `id_lista_precio`
- WHEN the insert reaches Postgres
- THEN 23503 is raised and `ManejadorDeErrores` maps it to 400 `referencia_invalida`

### Requirement: Atomic Per-Tenant Numero Assignment

`clientes.numero` MUST be assigned from an atomic per-tenant counter (same family
as doc 09's `numeraciones_comprobante`: `UPDATE ... RETURNING` inside the creating
transaction), guaranteeing no gaps or duplicates under concurrent creation.
`(id_tenant, numero)` MUST carry a unique index as the backstop.

#### Scenario: Concurrent creation produces no gaps or duplicates
- GIVEN two concurrent create requests for tenant 1
- WHEN both transactions commit
- THEN one receives `numero = N` and the other `N+1`, one 201 each, no 23505
  surfaced to either caller

#### Scenario: Unique backstop maps to 409
- GIVEN a non-standard write path bypasses the atomic counter
- WHEN two rows are inserted with the same `(id_tenant, numero)`
- THEN Postgres raises 23505 and `ManejadorDeErrores` maps it to 409

### Requirement: Consumidor Final Protected Row

Every tenant MUST have exactly one Consumidor Final cliente: `numero = 1`,
`id_condicion_fiscal` = CF, created automatically. The system MUST reject any
update or delete of this row through every API path, including the generic ABM
update/delete endpoints — the guard MUST live in the domain layer, not only the UI.

#### Scenario: Consumidor Final exists after provisioning
- GIVEN a newly provisioned tenant
- WHEN its clientes are queried
- THEN a Consumidor Final row exists with `numero = 1` and condición fiscal CF

#### Scenario: Update and delete attempts rejected
- GIVEN the Consumidor Final row of tenant 1
- WHEN a tenant admin calls the cliente update or soft-delete endpoint on it
- THEN the request is rejected with a domain validation error and the row is
  unchanged

### Requirement: numero_documento Has No Uniqueness Constraint

`clientes.numero_documento` MUST NOT carry a uniqueness constraint at any scope.
Duplicate values and `NULL` are both valid — a documented product decision
(legacy duplicate data; Consumidor Final and historical rows have no document),
not an oversight.

#### Scenario: Duplicate and NULL numero_documento accepted
- GIVEN a cliente exists with `numero_documento = "30712345678"`
- WHEN a second cliente is created with the same value, and a third with `NULL`
- THEN both creations succeed without a uniqueness error

### Requirement: Cliente ABM Lifecycle and Authorization

Clientes MUST support list/create/edit/soft-delete, gated by the same
authorization tier as stage-1 catalogs (`GestionDeCatalogo`: tenant `admin`
only — `root` and `vendedor` excluded).

#### Scenario: Admin creates and soft-deletes a cliente
- GIVEN a tenant admin
- WHEN they create a cliente and later soft-delete it
- THEN the row is persisted under the admin's `id_tenant`, and after deletion
  `deleted_at` is set and it no longer appears in the default list

#### Scenario: Vendedor blocked from writing
- GIVEN a user with the `vendedor` role
- WHEN they call the cliente create endpoint
- THEN the request is rejected with an authorization error

### Requirement: Tenant Isolation for Clientes

`clientes` MUST enforce the two-layer isolation guarantee (EF Core global query
filter + Postgres RLS without `BYPASSRLS`) established in stage 1.

#### Scenario: EF filter blocks cross-tenant read
- GIVEN a request scoped to tenant 1
- WHEN a query executes through the normal `DbContext` for a cliente of tenant 2
- THEN no rows are returned

#### Scenario: RLS blocks a read that bypasses EF
- GIVEN the app DB role has no `BYPASSRLS`
- WHEN a raw SQL query or `IgnoreQueryFilters()` reads tenant 2's clientes while
  `app.tenant_id = 1`
- THEN RLS returns zero rows
