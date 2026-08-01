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
