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
single transaction, the tenant, its first empresa, the generic template seed
(area "General" and medios_pago Efectivo and Transferencia), the Consumidor
Final cliente (`numero = 1`, condición fiscal CF), and the General
`listas_precio` row (`es_default = true`).

> Deviation recorded (2026-08-02, verify): the originally-planned "inactive general
> price-list placeholder" was deliberately deferred because `listas_precio` does not
> exist in stage 1 (`PlantillaDeAprovisionamiento.ItemsDiferidos`). Superseded by the
> user's stage-2 decision: `stage-2-clientes-proveedores` creates the real minimal
> `listas_precio` (General list) and adds it to this template, which fulfils the
> original intent with a real row instead of a placeholder.
>
> Fulfilled (2026-08-02, archive of stage-2-clientes-proveedores): this requirement now
> reflects the stage-2 implementation directly (Consumidor Final cliente + General
> listas_precio row seeded by provisioning, and backfilled for pre-existing tenants — see
> the Backfill requirement below). Per apply-progress.md and design.md decision 5,
> `PlantillaDeAprovisionamiento` was extended in place (`V1`), not bumped to a new version —
> the stage-2 change proposal's original plan to add a versioned bump (ADR-16) was
> superseded by that design decision once implementation started; the prior version's
> fields were not removed either way.

#### Scenario: Successful provisioning

- GIVEN a platform user submits a new tenant name and empresa razón social
- WHEN provisioning completes
- THEN the tenant, empresa, area "General", medios_pago Efectivo/Transferencia,
  Consumidor Final cliente, and General listas_precio row all exist
- AND all seeded rows carry the new tenant's `id_tenant`

#### Scenario: Provisioning failure rolls back

- GIVEN the template seed step fails partway (including the Consumidor Final
  or General list step)
- WHEN the transaction is rolled back
- THEN no tenant, empresa, or partial catalog/cliente/lista rows remain

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

### Requirement: Backfill for Pre-Existing Tenants

Tenants provisioned before `stage-2-clientes-proveedores` MUST receive their
Consumidor Final cliente and General `listas_precio` row via an idempotent
backfill step in `InicializadorDeBaseDeDatos` (same pattern as stage 1's
`usuarios` backfill, ADR-14), run after migrations. The migration's DB Change
Gate summary MUST explicitly include which rows get created for which
existing tenants, and the user approves schema and backfill together in the
same gate — not as two separate approvals.

#### Scenario: Existing tenant gains Consumidor Final and General list
- GIVEN a tenant provisioned under stage 1, with no `clientes` or
  `listas_precio` rows
- WHEN the backfill runs
- THEN the tenant has exactly one Consumidor Final cliente (`numero = 1`)
  and exactly one `es_default` listas_precio row

#### Scenario: Backfill is idempotent
- GIVEN a tenant that already has its Consumidor Final and General list
  (either from provisioning or a prior backfill run)
- WHEN the backfill runs again
- THEN no duplicate rows are created

#### Scenario: Backfill is approved inside the DB Change Gate
- GIVEN the clientes/listas_precio migration is ready to apply
- WHEN the gate summary is presented to the user
- THEN it names the affected pre-existing tenants and the rows to be created,
  and proceeds only after explicit approval covering both schema and backfill

### Requirement: Empresa's Fiscal Condition Is Nullable, With No Honest Default

`empresas.id_condicion_fiscal` MUST be nullable, with a simple (non-composite) FK to
`condiciones_fiscales`. There MUST be no `NOT NULL DEFAULT` — the emisor's condition is a
real-world fact deciding letter A/B/C, and defaulting silently (e.g. to RI) would emit a wrong
letter. The fiscal path MUST refuse to emit with a named 409 until the value is set.

#### Scenario: A new empresa has no fiscal condition by default
- GIVEN a freshly provisioned empresa
- WHEN `id_condicion_fiscal` is read
- THEN it is NULL — no value is silently assumed

#### Scenario: Fiscal emission with an unset condición fiscal is rejected explicitly
- GIVEN an empresa with `id_condicion_fiscal IS NULL`
- WHEN a fiscal emission is attempted for that empresa
- THEN it is rejected with `409 empresa_sin_condicion_fiscal`

### Requirement: Punto De Venta's Fiscal Number Is Nullable And Unique Per Empresa

`puntos_venta.numero_fiscal` MUST be nullable, in range `1..99999`, and unique per empresa via
`ux_puntos_venta_numero_fiscal WHERE numero_fiscal IS NOT NULL`. The internal `id_punto_venta`
numbering (TX/NCX/TXR/RC/PRE/REM) MUST remain wholly unaffected — a punto de venta may operate
fiscal and non-fiscal simultaneously.

#### Scenario: A punto de venta with no fiscal number keeps operating non-fiscal traffic
- GIVEN a punto de venta with `numero_fiscal IS NULL`
- WHEN a TX sale is emitted through it
- THEN it succeeds exactly as before this stage — the internal series is untouched

#### Scenario: A numero_fiscal outside 1..99999 is rejected
- GIVEN a raw UPDATE setting `numero_fiscal = 100000`
- WHEN it executes
- THEN Postgres rejects it via `ck_puntos_venta_numero_fiscal_rango`, SQLSTATE `23514`
