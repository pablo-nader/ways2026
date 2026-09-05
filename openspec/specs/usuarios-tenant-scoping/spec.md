# Usuarios Tenant Scoping Specification

## Purpose

Defines the `usuarios.id_tenant` retrofit and the platform/tenant role split
introduced by doc 09 on top of the existing doc 08 `usuarios` table.

## Requirements

### Requirement: id_tenant Column Semantics

`usuarios` MUST carry a nullable `id_tenant` column. `NULL` MUST mean the
user is platform staff; a value MUST mean the user belongs to that tenant.

#### Scenario: Platform user has NULL id_tenant

- GIVEN the existing seeded `root` account
- WHEN the migration completes
- THEN `usuarios.id_tenant` for that account is `NULL`

#### Scenario: New tenant user is scoped

- GIVEN a tenant admin creates a `vendedor` user for their tenant
- WHEN the user is persisted
- THEN `usuarios.id_tenant` equals the creating admin's tenant

### Requirement: usuario Uniqueness Is Scoped Per Tenant

`usuarios.usuario` MUST be unique within its tenant scope, not globally. Two different
tenants MAY have a user with the same `usuario` value. Platform users (`id_tenant NULL`)
MUST be treated as one shared uniqueness group among themselves — the `NULL` scope MUST
NOT allow duplicate platform usernames, even though `id_tenant` is nullable on the column.
`usuarios.mail` is unaffected by this requirement and stays globally unique per doc 08.

#### Scenario: Same usuario in two different tenants is allowed

- GIVEN tenant 1 has a user with `usuario = "admin"`
- WHEN tenant 2 provisions a user with `usuario = "admin"`
- THEN both users are created without a uniqueness conflict

#### Scenario: Duplicate usuario within the same tenant is rejected

- GIVEN tenant 1 has a user with `usuario = "admin"`
- WHEN tenant 1 attempts to create another user with `usuario = "admin"`
- THEN the request is rejected as a uniqueness violation

#### Scenario: Duplicate usuario among platform staff is rejected

- GIVEN a platform user (`id_tenant NULL`) exists with `usuario = "root"`
- WHEN a second platform user is created with `usuario = "root"`
- THEN the request is rejected as a uniqueness violation, exactly as if `id_tenant` were a
  shared non-NULL value for both rows

#### Scenario: mail stays globally unique regardless of tenant

- GIVEN a user of tenant 1 with `mail = "a@x.com"`
- WHEN tenant 2 attempts to create a user with `mail = "a@x.com"`
- THEN the request is rejected as a uniqueness violation, unchanged from doc 08

### Requirement: Platform vs Tenant Role Meaning

The `root` role MUST remain platform-scoped (`id_tenant NULL`) and MUST NOT
be assignable with a non-NULL `id_tenant`. The `admin` role MUST be
tenant-scoped and MUST always carry a non-NULL `id_tenant`.

#### Scenario: root cannot be created with a tenant

- GIVEN a request to create a user with role `root` and `id_tenant = 1`
- WHEN the request is validated
- THEN it is rejected

#### Scenario: admin requires a tenant

- GIVEN a request to create a user with role `admin` and no `id_tenant`
- WHEN the request is validated
- THEN it is rejected

### Requirement: PoliticaDeRoles Tenant Rule

`PoliticaDeRoles` MUST enforce that an `admin` can only view and manage
users belonging to their own tenant. Platform (`root`) users are unaffected
by this rule and continue to manage tenant provisioning, not tenant users
directly.

#### Scenario: Admin manages own-tenant user

- GIVEN an admin of tenant 1 and a `vendedor` of tenant 1
- WHEN the admin edits that vendedor
- THEN the edit succeeds

#### Scenario: Admin blocked from another tenant's user

- GIVEN an admin of tenant 1 and a `vendedor` of tenant 2
- WHEN the admin attempts to view or edit that vendedor
- THEN the request is rejected as unauthorized

#### Scenario: Existing PoliticaDeRoles rules still apply

- GIVEN the existing rules (no one assigns `root`, admin can't touch other
  admins/root, no self-delete)
- WHEN those scenarios from doc 08 are exercised
- THEN they behave exactly as before, unchanged by the tenant rule

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

> Superseded by Reconciliación 9 (stage 20): the spec's `iff` holds for the platform-vs-tenant
> distinction (this scenario) and is deliberately FALSE for the soft-deleted-tenant orphan case —
> when the owning tenant is soft-deleted, `IdTenant` is non-null and `NombreTenant` is null, so the
> row renders as a visible anomaly instead of "Plataforma". `IdTenant`, never the name, is the
> discriminator a consumer must read.

#### Scenario: The tenant column costs no extra round trip
- GIVEN a tenant with 30 usuarios
- WHEN `GET /api/usuarios` is called
- THEN exactly one database round trip is executed

> Superseded by Reconciliación 8 (stage 20): `GET /api/usuarios` costs **2** database round trips,
> not 1 — it is the only paginated listing and its `CountAsync` predates this change. The property
> this scenario protects (the projection itself adds no round trip) is preserved and proven: the
> other three listings cost 1, and had the projection added one, usuarios would cost 3. The
> pagination was not changed to make this sentence literally true.

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
