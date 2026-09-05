# Delta for Tenant Organization

## ADDED Requirements

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
