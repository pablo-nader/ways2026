# Delta for Usuarios Tenant Scoping

## ADDED Requirements

### Requirement: The Usuarios Listing Carries The Account's Tenant Identity

`UsuarioListado` — which today carries no tenant field at all — MUST gain `IdTenant` (nullable,
mirroring the column's own semantics) and `NombreTenant`. `NombreTenant` MUST be `null` if and
only if `IdTenant` is `null`; the API MUST NOT fabricate a display label. `Usuarios.tsx` MUST
render a tenant column that shows the tenant name for a tenant account and the literal
**"Plataforma"** for an account whose `IdTenant` is `null`, never an empty cell that reads like
missing data.

The name MUST be projected inside the existing list query: `GET /api/usuarios` MUST still execute a
single database round trip.

#### Scenario: A tenant account shows its tenant name
- GIVEN a `vendedor` of tenant 2 named "Comercio Sur"
- WHEN a platform actor calls `GET /api/usuarios`
- THEN that entry carries `idTenant = 2` and `nombreTenant = "Comercio Sur"`
- AND `Usuarios.tsx` renders "Comercio Sur" in the tenant column

#### Scenario: Platform staff render as Plataforma
- GIVEN the seeded `root` account with `id_tenant IS NULL`
- WHEN the listing is read
- THEN the entry carries `idTenant = null` and `nombreTenant = null`
- AND `Usuarios.tsx` renders "Plataforma" for that row

#### Scenario: The tenant column costs no extra round trip
- GIVEN a tenant with 30 usuarios
- WHEN `GET /api/usuarios` is called
- THEN exactly one database round trip is executed

#### Scenario: A tenant admin never enumerates another tenant's name
- GIVEN an admin of tenant 1
- WHEN they call `GET /api/usuarios` and apply the tenant filter
- THEN every returned row carries `idTenant = 1`, the filter offers only tenant 1, and no other
  tenant's name appears in the response or in the rendered screen

### Requirement: Usuario Deletion Gains The Usage Guard After PoliticaDeRoles, Never Instead Of It

`DELETE /api/usuarios/{id}` MUST keep every existing rule of
`PoliticaDeRoles.ValidarPuedeIntervenirSobre` — the actor must be Root or Admin, a Root target may
never be deleted, self-deletion is forbidden — and MUST keep
`PoliticaDeRoles.ValidarAlcanceDeTenant`'s deliberate `404`-not-`403` behaviour for an
out-of-scope target (ADR-8). The usage guard of the `bajas-de-organizacion` capability MUST be
applied **after** those checks and **before** the `DeletedAt` write, refusing with
`409 usuario_en_uso`. The audit record MUST still be written for a successful deletion. The route
and its policy (`Politicas.GestionDeUsuarios`) MUST NOT change.

The consequence is accepted and explicit: the `admin` account created by provisioning is pristine
until it operates, and stops being deletable the moment it does. The platform's lever for a
departing employee who has operated remains the existing `Inactivo` / `Bloqueado` state.

#### Scenario: A usuario who has sold cannot be deleted
- GIVEN a `vendedor` stamped as `id_empleado` on at least one comprobante de venta created after
  that usuario's own `created_at`
- WHEN an admin attempts to delete them
- THEN the request is refused with `409 usuario_en_uso`
- AND `deleted_at` is not written

#### Scenario: A never-used usuario is still deletable
- GIVEN a `vendedor` created yesterday who has opened no shift, sold nothing and appears on no
  document
- WHEN an admin deletes them
- THEN the deletion succeeds, `deleted_at` is stamped, and the audit record is written

#### Scenario: The provisioned admin is pristine until it operates
- GIVEN a freshly provisioned tenant whose `admin` shares the tenant's `created_at`
- WHEN a platform actor deletes that admin
- THEN the deletion succeeds
- AND after the same admin has opened a shift, the identical request is refused with
  `409 usuario_en_uso`

#### Scenario: Role policy is evaluated before the usage guard
- GIVEN a Root target that also has heavy usage
- WHEN a deletion is attempted
- THEN the refusal is the pre-existing `PoliticaDeRoles` error for a Root target, not
  `usuario_en_uso`

#### Scenario: Self-deletion is still forbidden regardless of usage
- GIVEN an admin with no usage at all
- WHEN they attempt to delete their own account
- THEN the pre-existing self-deletion refusal applies, unchanged

#### Scenario: An out-of-scope target stays a 404, never a usage disclosure
- GIVEN an admin of tenant 1 and a heavily used `vendedor` of tenant 2
- WHEN the admin issues a DELETE for that vendedor's id
- THEN the response is `404`, never `403` and never `409 usuario_en_uso`

#### Scenario: Audit rows referencing the usuario do not block
- GIVEN a usuario whose only dependents past their anchor are `auditoria` rows with
  `id_actor` pointing at them
- WHEN an admin deletes them
- THEN the deletion succeeds and the audit trail keeps rendering, because the row survives
  logically
