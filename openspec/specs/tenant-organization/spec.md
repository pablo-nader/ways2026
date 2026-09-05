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

### Requirement: Organization Listings Project Owner Names, Never Raw Ids

`EmpresaListado` MUST carry the owning tenant's **name**, and `PuntoVentaListado` MUST carry both
the owning tenant's name and the owning empresa's razón social. Every field added to these DTOs
MUST have a consumer: `Empresas.tsx` MUST render the tenant name and `PuntosVenta.tsx` MUST render
the tenant name and the empresa razón social, in place of the raw integers rendered today. The
existing owner ids MUST remain in the DTOs — they are the filter keys — but MUST NOT be displayed
as the identity of the owner anywhere in the four root screens.

The names MUST be projected inside the existing list query. Each list endpoint MUST execute a
single database round trip; no per-row lookup is permitted.

#### Scenario: The empresas listing shows the tenant name
- GIVEN tenant 2 named "Comercio Sur" with one empresa
- WHEN a platform actor calls `GET /api/empresas`
- THEN the empresa entry carries `nombreTenant = "Comercio Sur"`
- AND `Empresas.tsx` renders "Comercio Sur", not `2`

#### Scenario: The puntos de venta listing shows both owner names
- GIVEN a punto de venta of empresa "Sur SRL" under tenant "Comercio Sur"
- WHEN a platform actor calls `GET /api/puntos-venta`
- THEN the entry carries `nombreTenant = "Comercio Sur"` and `razonSocialEmpresa = "Sur SRL"`
- AND `PuntosVenta.tsx` renders both names, not two integers

#### Scenario: Listing owner names costs one round trip
- GIVEN a tenant with 20 empresas
- WHEN `GET /api/empresas` is called
- THEN exactly one database round trip is executed — no N+1 per empresa

#### Scenario: No raw owner id is displayed
- GIVEN the four root screens rendered with data
- WHEN their descriptors are inspected
- THEN no cell presents `idTenant` or `idEmpresa` as the owner's identity

> Superseded by Reconciliación 10 (stage 20): the orphan filter *option* label carries an id
> suffix (`— (tenant 7)`), which is the D13 anomaly's handle — the owning tenant is soft-deleted
> and has no name to display — not an owner identity; no **cell** presents an id as the owner's
> identity, which is what this scenario protects.

### Requirement: The Tenants Listing Carries Live Child Counts

`TenantListado` MUST carry `CantidadEmpresas`, `CantidadPuntosVenta` and `CantidadUsuarios`, and
`Tenants.tsx` MUST render all three. The counts MUST exclude logically deleted rows: a child whose
`deleted_at` is set MUST NOT be counted. `CantidadUsuarios` MUST count only usuarios belonging to
that tenant; platform staff (`id_tenant IS NULL`) MUST NOT be counted under any tenant.

The counts MUST be produced within the same single round trip as the listing.

#### Scenario: Counts reflect the surviving children
- GIVEN a tenant with 2 empresas, 3 puntos de venta and 4 usuarios, none deleted
- WHEN a platform actor calls `GET /api/plataforma/tenants`
- THEN the tenant entry reports 2, 3 and 4
- AND `Tenants.tsx` renders those three numbers

#### Scenario: Logically deleted children are not counted
- GIVEN the same tenant after one of its empresas and one of its usuarios were logically deleted
- WHEN the listing is read again
- THEN the counts report 1 empresa and 3 usuarios

#### Scenario: Platform staff are counted under no tenant
- GIVEN a platform user with `id_tenant IS NULL`
- WHEN the tenants listing is read
- THEN no tenant's `cantidadUsuarios` includes that account

#### Scenario: Counting costs no extra round trip
- GIVEN five tenants
- WHEN `GET /api/plataforma/tenants` is called
- THEN exactly one database round trip is executed

### Requirement: The Root Screens Filter By Owner Over The Already-Loaded List

`Empresas.tsx`, `PuntosVenta.tsx` and `Usuarios.tsx` MUST offer a filter by tenant, and
`PuntosVenta.tsx` MUST additionally offer a filter by empresa. Filtering MUST operate on the
already-loaded list. No query parameter, no new endpoint and no server-side pagination is added by
this requirement.

The set of values offered by a filter MUST be derived from the rows the actor can already see, so a
filter never discloses the existence or name of an entity outside the actor's scope.

#### Scenario: Filtering empresas by tenant narrows the list
- GIVEN a platform actor viewing empresas of three tenants
- WHEN they select one tenant in the filter
- THEN only that tenant's empresas remain visible, with no additional network request

#### Scenario: Filtering puntos de venta by empresa narrows the list
- GIVEN a platform actor viewing puntos de venta of several empresas
- WHEN they select one empresa in the filter
- THEN only that empresa's puntos de venta remain visible

#### Scenario: A tenant admin's filter offers only their own tenant
- GIVEN an admin of tenant 1 viewing `GET /api/empresas`
- WHEN the tenant filter is rendered
- THEN it offers only tenant 1 — no other tenant's name is present in the response or in the DOM

#### Scenario: Clearing the filter restores the full loaded list
- GIVEN an active tenant filter
- WHEN the operator clears it
- THEN the full previously-loaded list is shown again

### Requirement: Platform Logical Deletion Surface For Tenant, Empresa And Punto De Venta

The system MUST expose `DELETE /api/plataforma/tenants/{id}`, `DELETE /api/empresas/{id}` and
`DELETE /api/puntos-venta/{id}`. Each MUST apply the semantics of the `bajas-de-organizacion`
capability: logical deletion only, structural minimum first, then the usage guard, then the
bounded same-instant cascade.

Authorization MUST reuse the policy of the group each route already belongs to —
`Politicas.SoloPlataforma` for tenant routes, `Politicas.GestionDeOrganizacion` for empresa and
punto de venta routes. **No new authorization policy is added**; `Politicas.cs` MUST remain
untouched. Note the deliberate asymmetry on puntos de venta: reading the list stays under
`Politicas.LecturaDePuntosVenta` (the POS selector needs it), while deleting does not.

Every existing route of this surface — provisioning, suspension, reactivation and every `PUT` —
MUST remain unchanged in behaviour and in authorization.

#### Scenario: A platform actor deletes a pristine tenant
- GIVEN a root user and a freshly provisioned tenant
- WHEN they call `DELETE /api/plataforma/tenants/{id}`
- THEN the deletion succeeds and the tenant disappears from the listing

#### Scenario: A tenant admin cannot reach the tenant delete route
- GIVEN a user with the tenant `admin` role
- WHEN they call `DELETE /api/plataforma/tenants/{id}`
- THEN the request is rejected by `Politicas.SoloPlataforma`

#### Scenario: A vendedor cannot delete a punto de venta they can list
- GIVEN a vendedor who can call `GET /api/puntos-venta` through `LecturaDePuntosVenta`
- WHEN they call `DELETE /api/puntos-venta/{id}`
- THEN the request is rejected by `Politicas.GestionDeOrganizacion`

#### Scenario: No new policy exists after the change
- GIVEN the diff of this change
- WHEN `src/Ways.Api/Seguridad/Politicas.cs` is inspected
- THEN it is byte-identical to its state before the change

#### Scenario: Suspension and reactivation are unchanged
- GIVEN the existing suspend and reactivate endpoints
- WHEN their pre-existing scenarios are exercised
- THEN they behave exactly as before, and neither writes nor reads `deleted_at`

### Requirement: Tenant Deletion Is The Only Writer Of EstadoTenant.Baja

Deleting a tenant MUST set `Estado = EstadoTenant.Baja` **and** `DeletedAt` atomically, in one
transaction. Writing only one of the two is a violation: `DeletedAt` alone leaves the documented
`Baja` value latent forever, and `Estado = Baja` alone leaves the tenant visible in the root
listing. No other operation MAY write `EstadoTenant.Baja` — suspension and reactivation MUST
continue to refuse to touch it.

#### Scenario: A deleted tenant carries both markers
- GIVEN a pristine tenant that is deleted
- WHEN the row is read with query filters ignored
- THEN `estado = 'baja'` and `deleted_at` is set, from the same transaction

#### Scenario: A deleted tenant's user cannot log in and gets a clean 403
- GIVEN a tenant that was deleted and one of its usuarios
- WHEN that usuario attempts to log in
- THEN the login is rejected with `403 tenant_suspendido` — not a crash, not a 500

> Superseded by Reconciliación 1 (stage 20): a cascade-deleted admin gets `401
> credenciales_invalidas`, not `403 tenant_suspendido` — the login lookup runs under the
> `"BajaLogica"` filter with no `IgnoreQueryFilters`, so the user is simply not found. The
> property this scenario protects (cannot log in, cleanly, no crash, no 500) holds; only the code
> differs. The `403 tenant_suspendido` branch stays reachable for a **suspended** tenant.

#### Scenario: Reactivation cannot resurrect a deleted tenant
- GIVEN a deleted tenant (`estado = 'baja'`, `deleted_at` set)
- WHEN the reactivate endpoint is called for it
- THEN it is refused with `404` — the `"BajaLogica"` filter hides the row from the lookup
- AND the pre-existing `409 tenant_dado_de_baja` guard is preserved unchanged as the backstop for
  any path that reaches a `Baja` tenant without the filter

#### Scenario: Suspension never writes Baja
- GIVEN an active tenant
- WHEN it is suspended
- THEN `estado = 'suspendido'` and `deleted_at IS NULL`
