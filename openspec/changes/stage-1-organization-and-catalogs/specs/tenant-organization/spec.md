# Tenant Organization Specification

## Purpose

Defines the tenant/empresa/punto_venta hierarchy (doc 09), platform-only
creation, tenant provisioning with a generic template, tenant suspension
enforcement, and the two-layer tenant isolation guarantee.

## Requirements

### Requirement: Organization Hierarchy Tables

The system MUST persist `tenants`, `empresas`, and `puntos_venta` following
doc 09's hierarchy, with `empresas.id_tenant` and `puntos_venta.(id_empresa,
id_tenant)` as composite foreign keys referencing `empresas (id_empresa,
id_tenant)`.

#### Scenario: Seed data present after migration

- GIVEN the stage 1 migration has run
- WHEN the organization tables are queried
- THEN tenant 1, empresa 1, and its 2 current puntos_venta exist as seeded rows

#### Scenario: Composite FK rejects cross-tenant reference

- GIVEN empresa 5 belongs to tenant 2
- WHEN a punto_venta row is inserted with `id_empresa = 5` and `id_tenant = 1`
- THEN the insert MUST fail the composite foreign key constraint

### Requirement: Platform-Only Creation

The system MUST restrict creation of tenants, empresas, and puntos_venta to
the platform role (`root`). Tenant admins MAY edit descriptive fields (name,
address, social links) of their own empresa/punto_venta but MUST NOT create
or delete them.

#### Scenario: Platform creates a tenant

- GIVEN a user with the `root` role
- WHEN they call the tenant creation endpoint with a name
- THEN a new tenant is created

#### Scenario: Tenant admin blocked from creating an empresa

- GIVEN a user with the tenant `admin` role
- WHEN they call the empresa creation endpoint
- THEN the request is rejected with an authorization error

#### Scenario: Tenant admin edits punto_venta descriptive data

- GIVEN a tenant admin and an existing punto_venta of their own tenant
- WHEN they update its address and WhatsApp link
- THEN the update succeeds and no new row is created

### Requirement: Tenant Provisioning With Template Seed

When the platform provisions a new tenant, the system MUST create, in a
single transaction, the tenant, its first empresa, and the generic template
seed: area "General", medios_pago Efectivo and Transferencia, and one
inactive general price-list placeholder.

#### Scenario: Successful provisioning

- GIVEN a platform user submits a new tenant name and empresa razón social
- WHEN provisioning completes
- THEN the tenant, empresa, area "General", medios_pago Efectivo/Transferencia,
  and the inactive price-list placeholder all exist
- AND all seeded rows carry the new tenant's `id_tenant`

#### Scenario: Provisioning failure rolls back

- GIVEN the template seed step fails partway
- WHEN the transaction is rolled back
- THEN no tenant, empresa, or partial catalog rows remain

### Requirement: Tenant Suspension Enforcement

The system MUST block login for users of a suspended tenant and MUST
invalidate their active sessions on the next request, reusing the
per-request account revalidation from doc 08.

#### Scenario: Suspended tenant blocks new login

- GIVEN a tenant with `estado = suspendido`
- WHEN one of its users attempts to log in
- THEN the login is rejected

#### Scenario: Suspension cuts an active session

- GIVEN a logged-in user whose tenant becomes suspended mid-session
- WHEN the user makes the next request
- THEN the session is invalidated and the user is redirected to login

### Requirement: Tenant Isolation Enforcement

The system MUST enforce tenant isolation through two independent layers: an
EF Core global query filter on every `IdTenant`-bearing entity, and Postgres
Row Level Security using an app DB role without `BYPASSRLS`.

#### Scenario: EF query filter blocks cross-tenant read

- GIVEN a request scoped to tenant 1
- WHEN a query for an entity belonging to tenant 2 executes through the
  normal DbContext
- THEN no rows are returned

#### Scenario: RLS blocks a read that bypasses EF

- GIVEN the app DB role has no `BYPASSRLS`
- WHEN a raw SQL query or an `IgnoreQueryFilters()` call attempts to read
  tenant 2 data while `app.tenant_id` is set to 1
- THEN Postgres RLS returns zero rows
