# Delta for Usuarios y Login

> No prior `openspec/specs/usuarios-y-login/spec.md` baseline exists in this
> repository — doc 08 was implemented before SDD tracking began. This delta
> is written as ADDED requirements documenting the stage 1 retrofit on top
> of the existing (previously undocumented-in-openspec) doc 08 behavior. At
> archive time, these ADDED requirements establish the first
> `openspec/specs/usuarios-y-login/spec.md` baseline.

## ADDED Requirements

### Requirement: Tenant Column on Usuarios

`usuarios` MUST gain a nullable `id_tenant` column, added additively
without altering any existing doc 08 column, constraint, or index.

#### Scenario: Existing usuarios unaffected

- GIVEN the existing `usuarios` table with its doc 08 columns and unique
  partial indexes
- WHEN the migration runs
- THEN all existing rows keep their data and existing login/ABM behavior is
  unchanged except for the new nullable column

### Requirement: Root and Admin Role Meaning Change

The `root` role MUST mean platform staff (`id_tenant NULL`) and the `admin`
role MUST mean tenant administrator (`id_tenant` set), replacing the
pre-tenancy single-tenant meaning of both roles.

#### Scenario: Root retains platform-wide access

- GIVEN the seeded `root` account
- WHEN it logs in
- THEN it operates without any tenant scoping, per its `id_tenant = NULL`

#### Scenario: Admin operates only within its tenant

- GIVEN an `admin` account with `id_tenant = 1`
- WHEN it accesses the usuarios ABM
- THEN it only sees and manages users of tenant 1

### Requirement: Login and Session Revalidation Respect Tenant State

The per-request account revalidation (doc 08) MUST also check the user's
tenant state and reject/cut sessions for users of a suspended tenant.

#### Scenario: Revalidation blocks a suspended tenant's user

- GIVEN an active session for a user whose tenant becomes suspended
- WHEN the per-request revalidation runs
- THEN the session is treated as invalid, same as a blocked/inactive user
  in doc 08

### Requirement: Login Is By Mail, Not By usuario (Flow B)

`POST /api/auth/login` MUST accept `{ mail, password }` and resolve the account by
`mail`, replacing the doc 08 `{ usuario, password }` contract. This is the flow-B login:
the request carries no tenant context, and `mail`'s global uniqueness (doc 08, unchanged)
is what resolves the account — and therefore the tenant — on its own. Platform staff
(`root`, `id_tenant NULL`) log in the same way, by `mail`. The login form field in
`Ways.Web` changes from "usuario" to "mail" accordingly.

#### Scenario: Tenant user logs in with mail

- GIVEN an active user of tenant 1 with `mail = "vendedor@tenant1.com"`
- WHEN they `POST /api/auth/login` with that mail and the correct password
- THEN the login succeeds and the session is scoped to tenant 1

#### Scenario: Platform root logs in with mail

- GIVEN the seeded `root` account with its seed mail (`test@test.com` by default, doc 08)
- WHEN it `POST /api/auth/login`s with that mail and the correct password
- THEN the login succeeds with no tenant scoping, exactly as before under the old
  `usuario`-based contract

#### Scenario: usuario field is no longer accepted at login

- GIVEN a valid account with a known `usuario` and password
- WHEN `POST /api/auth/login` is called with `{ usuario, password }` instead of
  `{ mail, password }`
- THEN the request does not authenticate by `usuario` — only `mail` resolves an account
  in stage 1

### Requirement: Anti-Enumeration Behavior Is Preserved Under Mail-Based Login

The doc 08 anti-enumeration guarantees MUST hold unchanged with `mail` as the lookup
column: the same error message for "mail not found" and "wrong password", a dummy hash
verification when the mail does not resolve to any account (so response timing does not
leak existence), and account-state disclosure (bloqueado/inactivo/suspendido) only after
password verification.

#### Scenario: Unknown mail and wrong password return the same error

- GIVEN a mail that has no matching account
- WHEN `POST /api/auth/login` is attempted with that mail and any password
- THEN the response is identical (message and shape) to a login attempt with a known
  mail and a wrong password

#### Scenario: Unknown mail still incurs a dummy hash verification

- GIVEN a mail that has no matching account
- WHEN `POST /api/auth/login` is attempted
- THEN the request verifies against a discardable hash before responding, so response
  timing does not distinguish an unknown mail from a known one

### Requirement: Subdomain-Based Login (Flow A) Is Deferred, Not Stage 1

Login by `usuario` at a tenant subdomain (`tenant.domain.com`, resolving the tenant from
the `Host` header before authentication) is a designed extension point and is explicitly
**out of scope for stage 1**. Its implementation depends on wildcard DNS/TLS at the
hosting layer (EasyPanel), a deployment concern separate from this change. Stage 1 MUST
NOT ship a partial or best-effort subdomain resolution; flow B (mail-based login at the
bare domain) is the only working login path delivered by this change.

#### Scenario: Subdomain login is not available in stage 1

- GIVEN the application deployed without wildcard subdomain routing (current state)
- WHEN a user attempts to reach a tenant-specific subdomain to log in with `usuario`
- THEN no such route or resolution exists — this remains a documented future requirement,
  not a stage-1 deliverable

#### Scenario: usuario remains a valid per-tenant field for future flow A

- GIVEN `usuarios.usuario` is unique per tenant (usuarios-tenant-scoping spec)
- WHEN flow A is implemented in a future stage
- THEN the per-tenant `usuario` uniqueness already in place is sufficient for it to
  resolve login by `usuario` within a known tenant, with no further schema change
