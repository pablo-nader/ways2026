# Design: Stage 1 — Organization and Catalogs

How to implement the tenancy model of `docs/09-multi-tenancy.md` and the stage-1 tables of
`docs/10-modelo-de-datos.md` inside this codebase. The architecture (shared database,
two isolation layers, scoping categories) is already decided by those docs — this document
decides **how it lands in Ways.Domain / Ways.Application / Ways.Infrastructure / Ways.Api /
Ways.Web**, and records the decisions that the docs left open.

Artifacts and identifiers are in English; database objects stay in the Spanish naming
convention of doc 10.

## Reading path

| If you are… | Read |
|---|---|
| Reviewing tenancy safety | *Tenant context lifecycle* + ADR-2 to ADR-8 + *Test strategy* |
| Writing the schema | *Data model shape* + *Migration sequencing* (the DB CHANGE GATE lives there) |
| Writing catalogs (API/UI) | *Catalog machinery* + ADR-11 |
| Estimating tasks | *Component map* + *Migration sequencing* |

---

## Architecture at a glance

The existing layering is preserved. Nothing new is introduced at the top level: tenancy is
a **cross-cutting concern implemented in Infrastructure**, exposed to Application as one
abstraction (`ITenantActual`), and invisible to the use cases.

```
Ways.Domain
  Common/       EntidadBase, EntidadTenant (new), CatalogoSimple (new)
  Usuarios/     PoliticaDeRoles (+ tenant rules), Usuario (+ IdTenant?)
  Organizacion/ Tenant, Empresa, PuntoVenta, EstadoTenant           (new)
  Catalogos/    Area, Categoria, Marca, Grupo, MedioPago,
                CondicionFiscal, AlicuotaIva, TipoComprobante,
                Parametro, ReglaDeCategorias, ResolucionDeParametros (new)

Ways.Application
  Abstracciones/ ITenantActual (new), IWaysDbContext (+ new DbSets)
  Catalogos/     ServicioDeCatalogo<T> + 5 thin subclasses, contracts  (new)
  Organizacion/  ServicioDeOrganizacion, ServicioDeAprovisionamiento   (new)
  Parametros/    ServicioDeParametros                                  (new)

Ways.Infrastructure
  Persistencia/  WaysDbContext (+ tenant filter, + SaveChanges stamping)
                 Configuraciones/ConfiguracionDeCatalogo<T> (new) + per-entity configs
                 Migraciones/ (5 new migrations)
  Multitenancy/  TenantActualDeSesion, InterceptorDeContextoDeTenant,
                 RlsMigrationBuilderExtensions                          (new)

Ways.Api
  Seguridad/     Politicas (+ plataforma, + gestión de catálogo),
                 ValidacionDeSesion (extracted from Program.cs)         (new)
  Endpoints/     OrganizacionEndpoints, CatalogosEndpoints,
                 AprovisionamientoEndpoints, ParametrosEndpoints        (new)

Ways.Web
  api/           catalogos.ts (descriptors), tipos.ts (+ types)
  paginas/       PaginaCatalogo (generic), Categorias (tree),
                 Tenants, Empresas, PuntosVenta                         (new)
```

**Boundary rule:** no use case ever reads or writes `IdTenant`. It is stamped on insert and
filtered on read by Infrastructure. A use case that needs to cross tenants must ask for it
explicitly (`IgnoreQueryFilters(["Tenant"])`) and can only do so under a platform session.

---

## Tenant context lifecycle

One choke point resolves the tenant, and everything downstream reads it.

```
1. Cookie arrives            → Authentication middleware
2. OnValidatePrincipal       → reads claims (usuario_id, id_tenant?, id_rol)
                             → TenantActualDeSesion.Establecer(modo, idTenant)
                             → revalidates account state  (existing behaviour)
                             → revalidates tenant estado  (NEW: suspendido/baja ⇒ RejectPrincipal + SignOut)
3. Any EF query opens a connection
                             → InterceptorDeContextoDeTenant.ConnectionOpenedAsync
                             → SELECT set_config('app.acceso', @modo, false),
                                      set_config('app.tenant_id', @id, false)
4. EF global query filter    → WHERE deleted_at IS NULL AND id_tenant = @tenantActual
5. Postgres RLS policy       → USING (id_tenant = app_tenant_actual() OR app_es_plataforma())
6. SaveChangesAsync          → stamps IdTenant on Added, rejects tampering on Modified
7. Connection returns to pool → Npgsql DISCARD ALL clears the GUCs
```

Three access modes exist, carried in `app.acceso`:

| Mode | Set when | Sees |
|---|---|---|
| `tenant` | authenticated user with `id_tenant` | only that tenant's rows |
| `plataforma` | authenticated user with `id_tenant IS NULL` (root) | all rows |
| `login` | the anonymous `POST /api/auth/login` request only | `usuarios` (SELECT/UPDATE) and nothing else |
| *(unset)* | anything else | **nothing** — policies fail closed |

---

## Decisions (ADR)

### ADR-1 — `EntidadTenant` is a new base class, not a column on `EntidadBase`

**Decision.** Add `EntidadTenant : EntidadBase { int IdTenant }`. `EntidadBase` is untouched.
`Usuario` and `Tenant` deliberately do **not** derive from it.

**Why.** Three different tenancy semantics exist and one class cannot express them:
`EntidadTenant` (`IdTenant NOT NULL`), `Usuario` (`IdTenant NULL` = platform staff), and
`Tenant` itself (its PK *is* the scope). Making `IdTenant` nullable on `EntidadBase` would
push a nullable check into every query filter and every FK, and would let a future table
ship with a silent `NULL` scope. Deriving from `EntidadTenant` becomes the visible, greppable
declaration that a table is scoped — the convention that applies the filter, the stamping and
the RLS coverage test all key on that type.

**Rejected.** (a) `IdTenant` on `EntidadBase` — nullable everywhere, no compile-time signal.
(b) An `ITenantScoped` marker interface — cannot carry the property with a shared mapping,
and EF configuration by convention over interfaces is more fragile than over a base type.

### ADR-2 — Tenant is resolved in `OnValidatePrincipal`, never from the request

**Decision.** `TenantActualDeSesion` (scoped, mutable, `internal set`) is populated inside the
cookie handler's `OnValidatePrincipal` from the `ways:id_tenant` claim, before any endpoint
runs. There is no header, query parameter or route segment carrying a tenant id anywhere in
the API.

**Why.** Doc 09: *"la sesión guarda `tenant_id` resuelto en el login; jamás viaja como
parámetro editable por el cliente"*. `OnValidatePrincipal` is the only hook that runs after
the cookie is decrypted and before endpoint execution, and it is already the place where the
account is revalidated per request — so the suspended-tenant rule (resolved decision 5) lands
in the same query with no extra round trip.

**Consequence.** `ITenantActual` is guaranteed populated for every authenticated request.
Non-HTTP entry points (`InicializadorDeBaseDeDatos`, `WaysDbContextFactory`, tests) register
`TenantActualFijo`, an immutable implementation constructed with an explicit mode.

**Rejected.** Resolving the tenant lazily inside `WaysDbContext` from `IHttpContextAccessor` —
it makes the DbContext depend on HTTP, and the resolution order versus the connection
interceptor becomes implicit.

### ADR-3 — `set_config(..., false)` on connection open, not literal `SET LOCAL`

**Decision.** `InterceptorDeContextoDeTenant : DbConnectionInterceptor` issues a
session-level `set_config` on `ConnectionOpenedAsync`. Inside an explicit transaction
(provisioning), the same helper is re-issued with `is_local = true`.

**Why.** Doc 09 writes `SET LOCAL app.tenant_id`, but `SET LOCAL` is a no-op outside a
transaction, and EF opens and closes a connection per query unless a transaction is active.
Session scope is correct here because Npgsql sends `DISCARD ALL` when a connection returns to
the pool, so the GUC cannot leak to the next tenant. Same guarantee, working mechanism.

**Invariants this creates** (each one is asserted by a test or a startup check):
- `NoResetOnClose` must stay `false` (default). If it is ever enabled, GUCs leak across tenants.
- Npgsql **multiplexing must stay disabled** (default). Multiplexing interleaves commands from
  different contexts on one physical connection and would break session-scoped GUCs.
- `TenantActualDeSesion.Establecer/Suplantar` re-applies the settings if a connection is
  already open on that context, so a mid-scope change never runs against a stale GUC.

**Slice-1 scoping.** `Suplantar`/impersonation (ADR-16) is deferred to when
`ServicioDeAprovisionamiento` lands and does not exist yet in this slice; `Establecer`
itself has no re-apply-on-open-connection logic today. The re-apply invariant above is the
target design, not yet an asserted current behavior — same deferral as tasks.md 1.9/1.10.

**Rejected.** (a) Wrapping every request in an explicit transaction to make `SET LOCAL` legal —
turns every read into a transaction and fights EF's execution strategy. (b) A separate
`NpgsqlDataSource` per tenant — unbounded pool growth on a multi-tenant SaaS.

### ADR-4 — Three access modes as GUCs; policies fail closed

**Decision.** Two SQL helpers, created in the first RLS migration:

```sql
CREATE FUNCTION app_tenant_actual() RETURNS integer LANGUAGE sql STABLE AS
$$ SELECT NULLIF(current_setting('app.tenant_id', true), '')::int $$;

CREATE FUNCTION app_modo() RETURNS text LANGUAGE sql STABLE AS
$$ SELECT COALESCE(NULLIF(current_setting('app.acceso', true), ''), 'ninguno') $$;

CREATE FUNCTION app_es_plataforma() RETURNS boolean LANGUAGE sql STABLE AS
$$ SELECT app_modo() = 'plataforma' $$;
```

Standard policy on every scoped table:

```sql
ALTER TABLE areas ENABLE ROW LEVEL SECURITY;
ALTER TABLE areas FORCE ROW LEVEL SECURITY;
CREATE POLICY areas_tenant ON areas
    USING      (app_es_plataforma() OR id_tenant = app_tenant_actual())
    WITH CHECK (app_es_plataforma() OR id_tenant = app_tenant_actual());
```

`current_setting(..., true)` (missing_ok) is mandatory: without it an unset GUC raises instead
of returning NULL, and `id_tenant = NULL` is NULL ⇒ no rows. Unset context therefore sees
nothing, which is the desired failure mode.

**Login is the one exception.** Authentication happens before the tenant is known, so `usuarios`
gets two extra permissive policies limited to the login mode:

```sql
CREATE POLICY usuarios_login_lectura     ON usuarios FOR SELECT USING (app_modo() = 'login');
CREATE POLICY usuarios_login_actualiza   ON usuarios FOR UPDATE USING (app_modo() = 'login')
                                                          WITH CHECK (app_modo() = 'login');
```

UPDATE is required because the login path writes `ultima_conexion`, `intentos_fallidos` and
the password rehash. INSERT and DELETE are not granted in login mode.

**Why.** Restricting the anonymous path to `usuarios`, SELECT+UPDATE only, is strictly less
privilege than the obvious alternative of running login under the platform mode.

**Residual risk (accepted, documented).** `app.acceso = 'plataforma'` is written by the
application, so RLS does not defend against a bug in our own platform code path. It cannot be
set from client input — the value derives only from the signed cookie claim, and the default is
"no access". The guarantee RLS buys is the one that matters: **a tenant session can never read
or write another tenant's row, whatever EF does.**

### ADR-5 — `FORCE ROW LEVEL SECURITY` plus a startup role check

**Decision.** Every scoped table gets `FORCE ROW LEVEL SECURITY`, and
`InicializadorDeBaseDeDatos` checks the connected role at startup:

```sql
SELECT rolsuper, rolbypassrls FROM pg_roles WHERE rolname = current_user;
```

Superuser or `BYPASSRLS` ⇒ **throw in Production**, log a warning elsewhere.

**Why.** Migrations run as the table owner, and Postgres skips RLS for the owner unless
`FORCE` is set — without it every policy in this design is decorative in production, where the
app *is* the owner. And a superuser bypasses RLS even with `FORCE`, silently. A managed
Postgres panel handing out a superuser DSN is a realistic accident; failing fast beats
discovering it through a data leak.

**Consequence for seeding.** DDL is unaffected by RLS, but data seeded by
`InicializadorDeBaseDeDatos` into scoped tables runs under the platform mode explicitly.

### ADR-6 — Named query filters: soft delete and tenant are separately ignorable

**Decision.** Use EF Core 10 named query filters. The convention loop in `OnModelCreating`
registers `"BajaLogica"` on every `EntidadBase` and `"Tenant"` on every `EntidadTenant`
(plus a hand-written variant for `Usuario` and `Tenant`, per ADR-1).

**Why.** Today `IgnoreQueryFilters()` is used to include soft-deleted rows (`ServicioDeUsuarios`,
`InicializadorDeBaseDeDatos`). With a single composed filter, that call would also drop the
tenant filter — turning "show me deleted users" into a cross-tenant read. Named filters make
`IgnoreQueryFilters(["BajaLogica"])` mean exactly what it says.

**Verification required at apply time.** The current code uses the low-level
`IMutableEntityType.SetQueryFilter(lambda)`. Confirm the keyed overload exists on the pinned
EF version (10.0.10) before relying on it. **Fallback if it does not:** compose both predicates
into one filter and replace every existing `IgnoreQueryFilters()` call site with an explicit
`.Where(e => e.IdTenant == ctx.Id)` re-application; RLS still backstops it. This fallback must
be reported, not applied silently.

### ADR-7 — `usuarios.usuario` is unique per tenant; `usuarios.mail` stays globally unique

> **Overridden by product decision (2026-07-31).** The original version of this ADR kept both
> `usuario` and `mail` globally unique and rejected per-tenant scoping. The product owner
> overrode that: `usuario` becomes per-tenant, `mail` stays global, and login moves to
> mail-based. This section records the new decision; the rejected alternative is kept below for
> traceability.

**Decision.** Two separate partial unique indexes, with different scopes:

```sql
CREATE UNIQUE INDEX ux_usuarios_usuario ON usuarios (id_tenant, usuario)
    NULLS NOT DISTINCT WHERE deleted_at IS NULL;
CREATE UNIQUE INDEX ux_usuarios_mail    ON usuarios (mail)
    WHERE deleted_at IS NULL;   -- unchanged from doc 08: still global
```

`usuario` is unique **within a tenant** — two different tenants can both have a user named
`admin`. `mail` is unique **across the whole system**, unchanged from doc 08.

**The platform-NULL gotcha, and the chosen mechanism.** `id_tenant` is nullable on `usuarios`
(ADR-1: platform staff carry `id_tenant NULL`). Postgres unique indexes treat every `NULL` as
a distinct value by default, so a plain `UNIQUE (id_tenant, usuario)` would let two platform
accounts both be named `root` — the index would never see a collision because `NULL ≠ NULL`.
**Chosen mechanism: `NULLS NOT DISTINCT`** (Postgres 15+; this project pins Postgres 17, see
`docker/Dockerfile` and `compose.dev.yml`). It folds every `id_tenant IS NULL` row into one
uniqueness group exactly as if `NULL` were a real value, so platform usernames are enforced
unique among themselves by the same index, with no second index and no trigger.

**Rejected alternative for the NULL case.** A dedicated partial unique index
(`UNIQUE (usuario) WHERE id_tenant IS NULL AND deleted_at IS NULL`) alongside the tenant-scoped
one — works, but is two indexes doing the job of one and needs the same
`WHERE id_empresa IS NULL` / `WHERE id_empresa IS NOT NULL` split already used for the catalog
tables (*Data model shape*) to actually deduplicate. `NULLS NOT DISTINCT` is available on the
pinned Postgres version and expresses the intent in one object.

### Login contract

| Flow | Tenant known from | Login accepts | Stage 1 |
|---|---|---|---|
| **A** — subdomain (`tenant.domain.com`) | Host header, before auth | `usuario` OR `mail` | **Designed, not implemented** — extension point below |
| **B** — bare domain (`domain.com`) | resolved *by* the mail itself (`mail` is globally unique) | `mail` only | **Implemented — this is the stage-1 login** |

Stage 1 ships flow B only: the login form field changes from "usuario" to "mail", and
`ServicioDeAutenticacion` resolves the account by `mail` (global lookup, no tenant context
needed yet — the account row carries its own `id_tenant`, same as today). Platform staff
(`root`, `id_tenant NULL`) also log in by mail; the seeded root account already has one
(`test@test.com`, doc 08), so no seed change is required. The anti-enumeration behavior of doc
08 — same error message for unknown account vs wrong password, dummy-hash verification when
the account does not exist, account-state check only after password verification — is
preserved unchanged; only the lookup column moves from `usuario` to `mail`, the comparison
stays `citext` (case-insensitive) either way.

`usuario` is not removed: it remains the per-tenant display/login-eligible handle for flow A
and for tenant-scoped ABM/UI purposes, and its uniqueness is enforced by the index above
starting stage 1, even though flow A does not consume it yet.

**Flow-A extension point (deferred, not stage 1).** Subdomain resolution needs a tenant hint
*before* a session exists — earlier in the pipeline than ADR-2's `OnValidatePrincipal`, which
only runs for an already-authenticated cookie. The identified hook is a new, narrow
pre-authentication middleware (`ResolucionDeTenantPorSubdominio`, not built in stage 1) that
would parse the `Host` header into a tenant hint available to `POST /api/auth/login`, so that
login can accept `usuario` scoped to that hint. This is deliberately **not** implemented now:
it depends on wildcard DNS/wildcard TLS at the hosting layer (EasyPanel), which is a deployment
concern orthogonal to this stage's schema/API/UI work. Building the middleware without that
infra would be dead code with no way to test the multi-subdomain case for real.

**Cost.** None functionally — flow B (mail) already resolves the tenant, so a same-named
`usuario` across tenants no longer blocks anything. The residual cost is that flow A stays
unavailable until the hosting/DNS work lands, so a user cannot yet log in with a bare
`usuario` without knowing their mail.

**Rejected (original ADR-7, superseded above).** Keeping both `usuario` and `mail` globally
unique. The reasoning at the time — "login is a single field and the tenant is derived from
the account, so a per-tenant discriminator would mean the tenant travels as a client parameter"
— missed that `mail`, already unique per doc 08, is itself a valid tenant-free discriminator:
routing login through `mail` gets tenant derivation without either a client-supplied
discriminator or a subdomain. The product owner overrode the original decision on that basis.

### ADR-8 — Cross-tenant access answers 404, never 403

**Decision.** A request for a row belonging to another tenant returns `no_encontrado` (404).
`PoliticaDeRoles` never raises `Prohibido` for a cross-tenant target. This includes a
tenant actor targeting a platform-scoped account (`idTenantObjetivo is null`): it also
answers 404, unified under the same rule instead of a separate `Prohibido`.

**Why.** 403 confirms the row exists; 404 does not. This is the same reasoning that already
makes `ServicioDeAutenticacion` return one message for "user does not exist" and "wrong
password". It also falls out naturally: the EF filter makes the row invisible, so
`BuscarAsync` throws `NoEncontrado` with no extra code. The rule exists to stop someone from
"improving" the error message later.

### ADR-9 — Composite FKs via alternate keys

**Decision.** Every scoped principal declares
`builder.HasAlternateKey(e => new { e.Id, e.IdTenant })`, and dependents map
`HasForeignKey(d => new { d.IdPrincipal, d.IdTenant }).HasPrincipalKey(p => new { p.Id, p.IdTenant })`
with `DeleteBehavior.Restrict` (bajas are logical).

Applies to: `puntos_venta → empresas`, `categorias → categorias` (self), and every
`catálogo → empresas` (`id_empresa` optional).

**Why.** Doc 09: a row of tenant 1 must not be able to reference a row of tenant 2 *even by
bug*. The alternate key is what makes the composite FK expressible.

**Two EF gotchas to expect at apply time:**
- An optional composite FK (`id_empresa NULL`, `id_tenant NOT NULL`) is optional because *one*
  FK property is nullable; Postgres `MATCH SIMPLE` then skips the check when `id_empresa IS NULL`,
  which is exactly the "shared across all empresas" semantics we want.
- If EF tries to make `IdTenant` nullable to satisfy an optional relationship, do **not** accept
  it. Fallback: drop that composite FK to a single-column FK and keep same-tenant integrity in
  the domain plus RLS. Report the deviation.

### ADR-10 — Empresa scoping is an explicit query extension, not a global filter

**Decision.** `id_tenant` is a global filter (isolation). `id_empresa` is **not**. Screens that
need the empresa view call an opt-in extension:

```csharp
query.DeLaEmpresa(idEmpresa)  // WHERE id_empresa IS NULL OR id_empresa = @e
```

**Why.** `id_empresa NULL` means "shared with all empresas of the tenant" — a visibility rule
with an OR, not an isolation rule. Expressing it as a global filter would make the tenant admin
unable to see and manage the shared rows alongside the private ones, and there is nothing to
protect: both sides are already inside the tenant boundary.

**Stage-1 consequence.** Since provisioning creates one empresa per tenant, every catalog row is
born with `id_empresa NULL`. `ITenantActual` carries only the tenant; the empresa/punto-venta
selection (legacy A2, `asignaciones_empleado`) is out of scope and lands with the operational
stages. `parametros` resolution therefore takes `idPuntoVenta` as an explicit argument today.

### ADR-11 — One catalog machine, three layers, with an escape hatch

**Decision.** Eight catalogs must not become eight copies. Factor at three levels:

| Layer | Shared piece | Per-catalog cost |
|---|---|---|
| Domain | `CatalogoSimple : EntidadTenant { Id, Nombre, Activo, IdEmpresa? }` | the extra properties only |
| Persistence | `ConfiguracionDeCatalogo<T>` maps table/columns/audit/indexes; abstract `ConfigurarPropio(builder)` | ~10 lines |
| Application | `ServicioDeCatalogo<T, TListado, TAlta>` with the 5 operations; `virtual AplicarPropios(entidad, datos)` | ~15 lines |
| API | `MapearCatalogo<T>(grupo, recurso, politica)` maps the 5 routes | 1 line |
| Web | `<PaginaCatalogo definicion={…}>` driven by a field descriptor, one route `/catalogos/:recurso` | ~15-line descriptor |

The shared persistence base also buys every catalog the non-obvious index pair described in
*Data model shape*, which is easy to get wrong eight times.

**Escape hatch (deliberate).** A catalog that outgrows the descriptor gets its own page and its
own service subclass. `categorias` already does — it is a tree with a depth rule, and forcing it
through the generic form would corrupt the abstraction for the other seven.

**Rejected.** (a) Eight hand-written pages — the actual cost is not the writing, it is that a
fix to the soft-delete confirm dialog then has to be applied eight times. (b) A fully data-driven
metamodel that also generates the schema — over-engineering; the schema is fixed by doc 10.

**Not part of the machine:** `tenants`, `empresas`, `puntos_venta` (real shapes, platform-only
writes) get explicit pages that reuse the form primitives but not the descriptor engine. The
three fiscal catalogs are `[global]`, platform-maintained, and expose read-only `GET` endpoints
only (resolved decision 3) — no ABM in this stage.

> **Overridden by user decision (2026-08-01, DB CHANGE GATE #4).** This ADR originally left the
> three fiscal catalogs with **no RLS at all** — protection by omission of write endpoints only
> (API-surface-only). The user restored defense-in-depth: `condiciones_fiscales`,
> `alicuotas_iva` and `tipos_comprobante` now get `ENABLE`/`FORCE ROW LEVEL SECURITY` too, with a
> permissive `FOR SELECT USING (true)` policy (global reference data — readable in every access
> mode, including `tenant`) plus a `FOR ALL` policy restricting every write command to
> `app_es_plataforma()`. The API surface stays exactly as designed above — read-only `GET` for
> tenants, no ABM — RLS is now a second, independent layer behind it, consistent with the
> two-layer isolation model the rest of this document uses for scoped tables. `RlsMigrationBuilderExtensions`
> gained `HabilitarRlsDeCatalogoGlobal(tabla)` for this pattern (migration 4,
> `CatalogosGlobales`), reusing the same identifier guard as `HabilitarRlsDeTenant`.

### ADR-12 — `categorias`: depth is a domain rule, computed, not stored

**Decision.** The schema keeps `id_categoria_padre` unrestricted (resolved decision 4). Depth is
enforced server-side by a pure domain rule fed by one recursive query:

```csharp
// Ways.Domain — no DB, unit-testable
public static class ReglaDeCategorias
{
    public const int ProfundidadMaxima = 3;
    public static void ValidarProfundidad(int nivelDelPadre, int alturaDelSubarbol);
    public static void ValidarSinCiclo(int idDestino, IReadOnlyCollection<int> descendientes);
}
```

Infrastructure supplies `nivelDelPadre` and `alturaDelSubarbol` with a single recursive CTE per
write. Re-parenting is validated with the same two facts: moving a 2-level subtree under a level-2
parent would produce level 4 and is rejected, and moving a node under its own descendant is a cycle.

**Why not store `nivel`.** A denormalized level has to be rewritten across the whole subtree on
every re-parent, and drifts the day someone writes a category by SQL. Catalogs here are dozens of
rows: a recursive CTE per write is free, and the rule stays a pure function that unit tests can
hammer without a database.

### ADR-13 — `parametros`: two partial unique indexes, and a typed key registry

**Decision.**

```sql
parametros (                                   -- [operativa con fallback a empresa]
    id_parametro, id_tenant NOT NULL, id_empresa NOT NULL,
    id_punto_venta NULL,                       -- NULL = default de la empresa
    clave citext NOT NULL, valor jsonb NOT NULL
);
CREATE UNIQUE INDEX ux_parametros_punto_venta ON parametros (id_tenant, id_empresa, id_punto_venta, clave)
    WHERE id_punto_venta IS NOT NULL AND deleted_at IS NULL;
CREATE UNIQUE INDEX ux_parametros_empresa     ON parametros (id_tenant, id_empresa, clave)
    WHERE id_punto_venta IS NULL     AND deleted_at IS NULL;
```

**Why two indexes.** A single unique index over a nullable column does not deduplicate: Postgres
treats each NULL as distinct, so two empresa-level defaults for the same key would both be
accepted. Splitting by `id_punto_venta IS NULL` closes the hole without depending on
`NULLS NOT DISTINCT`, and both indexes are directly usable by the resolution query.

**Resolution** is one query returning at most two rows per key, punto de venta winning:

```sql
SELECT DISTINCT ON (clave) clave, valor
FROM parametros
WHERE id_tenant = @t AND id_empresa = @e AND (id_punto_venta = @pv OR id_punto_venta IS NULL)
ORDER BY clave, (id_punto_venta IS NULL);   -- false (0) primero ⇒ gana el punto de venta
```

The precedence itself (`punto_venta ?? empresa ?? default`) is a pure function in Domain and is
unit-tested without a database.

**Typed keys.** A `ParametroConocido` registry declares key, CLR type, default value and
validation. A missing row returns the documented default instead of throwing, and the ABM renders
the right editor from the declared type. `jsonb` keeps the door open for structured values without
another migration.

### ADR-14 — Seed data lives in `InicializadorDeBaseDeDatos`, not in migrations

**Decision.** Migrations are schema-only. Tenant 1 / empresa 1 / the two current locales, the
three fiscal catalogs and the backfill of `usuarios.id_tenant` are seeded by the existing
idempotent initializer, extended with new `Sembrar…Async` steps under the platform mode.

**Why.** It is the pattern already in the repo (roles and root), it is idempotent by construction
so redeploys are safe, and it keeps `HasData`'s value-generation friction out of the picture. It
also means seeding runs *after* RLS is active, which is the configuration we want exercised on
every boot.

**Backfill rule.** The existing `root` stays `id_tenant NULL` (platform, per doc 09). Any other
existing user is assigned tenant 1.

### ADR-15 — Every scoped table ships with its policy in the same migration

**Decision.** A helper (`RlsMigrationBuilderExtensions.HabilitarRlsDeTenant("areas")`) emits the
`ENABLE` + `FORCE` + policy block, and is called in the very migration that creates the table.
An integration test asserts the invariant globally:

```sql
-- must return zero rows
SELECT c.relname
FROM pg_class c JOIN pg_attribute a ON a.attrelid = c.oid
WHERE a.attname = 'id_tenant' AND c.relkind = 'r'
  AND (NOT c.relrowsecurity OR NOT c.relforcerowsecurity
       OR NOT EXISTS (SELECT 1 FROM pg_policies p WHERE p.tablename = c.relname));
```

**Why.** A separate "add RLS later" migration guarantees a window where a scoped table is live
without a policy, and a table added in stage 2-8 would just be forgotten. The test turns the
convention into something that fails the build.

### ADR-16 — Provisioning: one transaction, tenant impersonation, execution strategy

**Decision.** `ServicioDeAprovisionamiento.CrearTenantAsync` runs as:

```csharp
await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
{
    await using var tx = await db.Database.BeginTransactionAsync(ct);

    // 1. tenants        — platform mode
    // 2. Suplantar(tenant.Id)  ⇒ set_config(..., is_local: true) sobre esta transacción
    // 3. empresas, puntos_venta, plantilla (áreas + medios de pago)
    // 4. usuario admin del tenant (password temporal, se devuelve una sola vez)

    await tx.CommitAsync(ct);
});
```

**Why the execution strategy wrapper.** `EnableRetryOnFailure` is already configured; with a
retrying strategy EF **throws** on a user-initiated `BeginTransaction` unless the whole block is
inside `ExecuteAsync`. This is the single most likely thing to break at apply time.

**Why impersonation.** Between step 1 and step 3 the code is a platform session writing
tenant-scoped rows. `Suplantar` switches both the EF filter/stamping value and the transaction's
GUC, so RLS `WITH CHECK` passes and the inserts are stamped with the new tenant. It is an
`IDisposable` scope, and it is the only sanctioned way to write on behalf of a tenant.

**Template (resolved decision 2).** `PlantillaDeAprovisionamiento.V1` — área "General", medios de
pago "Efectivo" (comportamiento `efectivo`) and "Transferencia" (`electronico`, requires
reference). Versioned so a future vertical template is added, not edited.

**Deferred template items.** The generic price-list placeholder and the "Consumidor Final"
customer are declared in the template as stage-3 / stage-2 extension points and are **not**
created in stage 1 — `listas_precio` and `clientes` do not exist yet. Provisioning a tenant today
therefore leaves those two gaps to be filled by the stage where the tables land.

**Tenant admin creation is part of provisioning.** Without it a freshly provisioned tenant has
nobody who can log in. The generated password is returned once in the response and never stored
in plain text; the account is created with the standard hasher.

### ADR-17 — Integration tests own the RLS proof; no e2e in this stage

**Decision.** New project `tests/Ways.IntegrationTests` using `WebApplicationFactory` +
`Testcontainers.PostgreSql`. The fixture provisions **two roles**: the owner that runs migrations,
and `ways_app` (`NOSUPERUSER NOBYPASSRLS`) that the application and the raw-SQL assertions use.

**Why two roles.** An RLS test that runs as a superuser passes vacuously. The proof has to come
from a connection that genuinely cannot bypass policies, and it has to be executed **without EF**
— otherwise the test only proves the query filter works.

**No e2e.** `Ways.Web` has no browser-test harness (`package.json` has vite/oxlint only).
Introducing Playwright is its own change with its own CI cost; bolting it on inside this slice
would inflate an already large PR. Recorded as the recommended follow-up.

---

## Data model shape

Scoping category per table, in the vocabulary of doc 09. **This table is the spine of every
DB CHANGE GATE summary.**

| Table | Category | Scope columns | Notes |
|---|---|---|---|
| `tenants` | root | `id_tenant` (PK) | `estado_tenant` enum: activo / suspendido / baja |
| `empresas` | tenant | `id_tenant` | AK `(id_empresa, id_tenant)` |
| `puntos_venta` | tenant | `id_tenant`, `id_empresa` | composite FK to empresas; AK `(id_punto_venta, id_tenant)` |
| `usuarios` | tenant, nullable | `id_tenant NULL` | NULL = platform staff; `usuario` unique per tenant incl. NULL group (ADR-7, `NULLS NOT DISTINCT`), `mail` unique globally |
| `areas` | catálogo | `id_tenant`, `id_empresa NULL` | `orden` |
| `categorias` | catálogo | `id_tenant`, `id_empresa NULL` | self composite FK, depth rule |
| `marcas` | catálogo | `id_tenant`, `id_empresa NULL` | — |
| `grupos` | catálogo | `id_tenant`, `id_empresa NULL` | `margen numeric(5,2) NULL` |
| `medios_pago` | catálogo | `id_tenant`, `id_empresa NULL` | `comportamiento_medio_pago` enum |
| `condiciones_fiscales` | **global** | — | platform-maintained, seeded; RLS read-all/write-plataforma (ADR-11 override, gate #4) |
| `alicuotas_iva` | **global** | — | platform-maintained, seeded; `nombre` unique (gate #4 decision); RLS read-all/write-plataforma |
| `tipos_comprobante` | **global** | — | `clase_comprobante` enum; seeded; RLS read-all/write-plataforma |
| `parametros` | operativa c/ fallback | `id_tenant`, `id_empresa`, `id_punto_venta NULL` | see ADR-13 |

### The catalog index pair (shared by `ConfiguracionDeCatalogo<T>`)

`UNIQUE (id_tenant, id_empresa, nombre)` does **not** work: `id_empresa IS NULL` makes every
shared row distinct, so duplicate shared names slip through. Same failure mode as `parametros`,
same fix:

```sql
CREATE UNIQUE INDEX ux_areas_nombre_compartido ON areas (id_tenant, nombre)
    WHERE id_empresa IS NULL     AND deleted_at IS NULL;
CREATE UNIQUE INDEX ux_areas_nombre_empresa    ON areas (id_tenant, id_empresa, nombre)
    WHERE id_empresa IS NOT NULL AND deleted_at IS NULL;
CREATE INDEX ix_areas_tenant ON areas (id_tenant);
```

Writing this once in the shared configuration is most of the justification for ADR-11.

### Enum handling

New Postgres enums: `estado_tenant`, `comportamiento_medio_pago`, `clase_comprobante`.
Register them **only** via `npgsql.MapEnum<T>("nombre")` in `AgregarInfrastructure`, never also in
`OnModelCreating` — the existing comment in `WaysDbContext` documents why (duplicate type creation
with alphabetically ordered values).

### `PoliticaDeRoles` — additive tenant rules

New pure functions, existing signatures untouched so the current unit tests stay green:

```csharp
public readonly record struct ActorDeGestion(RolConocido Rol, int Id, int? IdTenant)
{
    public bool EsDePlataforma => IdTenant is null;
}

public static void ValidarAlcanceDeTenant(ActorDeGestion actor, int? idTenantObjetivo);
public static IReadOnlyList<RolConocido> RolesAsignablesPor(RolConocido actor, bool esDePlataforma);
```

Rules encoded (all DB-free):

| Actor | May manage | Rationale |
|---|---|---|
| platform root (`IdTenant NULL`) | tenants, empresas, puntos de venta, tenant admins | doc 09: root administra tenants, no opera ninguno |
| tenant admin | only users with the same `id_tenant`; never a platform user | doc 09 tenant rule |
| tenant admin | cannot assign `admin` (unchanged) or `root` (unchanged) | the platform creates the tenant's admin at provisioning |
| anyone | cross-tenant target ⇒ `NoEncontrado`, never `Prohibido` | ADR-8 |

Suspended-tenant enforcement reuses the per-request revalidation (ADR-2) and adds a check in
`ServicioDeAutenticacion` **after** password verification — same ordering as the existing
bloqueado/inactivo checks, so a wrong password never reveals that a tenant exists.

---

## Migration sequencing and the DB CHANGE GATE

`CLAUDE.md` is unconditional: **before generating each migration, present the model summary and
wait for explicit approval.** Sub-agents return the summary; they do not generate or apply.

Five gates, each small enough to actually review:

| # | Migration | Contents | Gate summary must include |
|---|---|---|---|
| 1 | `Organizacion` | `estado_tenant` enum, `tenants`, `empresas`, `puntos_venta`, AKs, composite FK, RLS functions + policies | tables, columns, AKs, composite FK, policies |
| 2 | `UsuariosMultiTenant` | `usuarios.id_tenant NULL` + FK, `ux_usuarios_usuario` rebuilt as `(id_tenant, usuario) NULLS NOT DISTINCT`, `usuarios` policies (tenant + login) | the additive column, the per-tenant `usuario` index and the `NULLS NOT DISTINCT` reasoning (ADR-7), the two login policies and why |
| 3 | `CatalogosDeTenant` | `areas`, `categorias`, `marcas`, `grupos`, `medios_pago` (+ enum), index pairs, policies | per-table columns, index pairs, the self composite FK |
| 4 | `CatalogosGlobales` | `condiciones_fiscales`, `alicuotas_iva`, `tipos_comprobante` (+ `clase_comprobante`) | that these are `[global]` — no `id_tenant`, but **do** get RLS (ADR-11 override, gate #4): read-all, write-plataforma-only |
| 5 | `Parametros` | `parametros`, the two partial unique indexes, policy | the NULL-uniqueness reasoning of ADR-13 |

Ordering constraints:
1. The RLS helper functions must exist before any policy references them ⇒ migration 1 creates them.
2. Migration 2 must follow 1 (`usuarios.id_tenant` references `tenants`).
3. Seeding (tenant 1, locales, fiscal catalogs, `usuarios` backfill) runs in
   `InicializadorDeBaseDeDatos` after all migrations, under platform mode (ADR-14).
4. Everything is additive; each `Down` drops only what its `Up` created. No destructive change
   touches existing `usuarios` data.

---

## Test strategy

Per `CLAUDE.md`: unit at minimum, integration where feasible, and a task is not done until its
tests pass.

### Unit — `tests/Ways.Domain.Tests` (no database)

| Target | Cases |
|---|---|
| `PoliticaDeRoles` tenant rules | admin↔same tenant OK; admin→other tenant not found; admin→platform user forbidden; platform root→any tenant OK; assignable-roles split by platform/tenant |
| `ReglaDeCategorias` | depth 1-3 accepted, 4 rejected; re-parent that would push a subtree past 3 rejected; cycle rejected; root category accepted |
| `ResolucionDeParametros` | punto de venta wins over empresa; empresa used when no punto de venta row; declared default when neither; unknown key rejected |
| `PlantillaDeAprovisionamiento` | V1 contains exactly the área and the two medios de pago; deferred items are flagged, not silently dropped |

### Integration — `tests/Ways.IntegrationTests` (Testcontainers + `ways_app` role)

**Isolation (the core of this change):**
1. EF layer — tenant A session listing `areas` never returns tenant B rows.
2. RLS layer, **without EF** — raw `NpgsqlConnection` as `ways_app`, `app.acceso='tenant'`,
   `app.tenant_id=A`: `SELECT * FROM areas` returns only A. Repeat with
   `IgnoreQueryFilters(["Tenant"])` through EF: still only A. *This is the test that proves the
   second layer exists.*
3. Write attempt — `INSERT ... id_tenant = B` under tenant A context is rejected by `WITH CHECK`.
4. Fail-closed — unset GUC ⇒ zero rows, not an error and not everything.
5. GUC leakage — after a tenant A request, a connection taken from the pool reports
   `current_setting('app.tenant_id', true) IS NULL`.
6. Policy coverage — the `pg_class`/`pg_policies` query of ADR-15 returns zero rows.
7. Role guard — `ways_app` has neither `rolsuper` nor `rolbypassrls`.

**Endpoints:**
8. Catalog CRUD for each of the 5 tenant catalogs (the generic service is exercised once per
   catalog through the shared route map, not re-tested five times by hand).
9. Another tenant's catalog id ⇒ 404 (ADR-8).
10. Fiscal catalogs: GET 200 for a tenant, write verbs 403.
11. Provisioning: 403 for tenant admin, 201 for platform; the new tenant has empresa, punto de
    venta, the template rows and one admin; a failure mid-way leaves **nothing** behind
    (transaction proof).
12. Suspension: suspend tenant ⇒ that tenant's user is signed out on the next request, and login
    returns `tenant_suspendido`; other tenants unaffected.
13. Categoría with depth 4 ⇒ 400 with a domain error code.
14. Login by mail still works for the platform root (`test@test.com`, doc 08 seed), with the
    same anti-enumeration behavior (same error message, dummy-hash timing) now keyed on `mail`
    instead of `usuario` — regression on doc 08 under the flow-B contract (ADR-7).
15. Two different tenants can each provision a user named `usuario = "admin"` without collision;
    a second platform account also named the same as an existing platform account is rejected
    (`NULLS NOT DISTINCT` proof, ADR-7).

### Regression

The existing `Ways.Domain.Tests` and `Ways.Application.Tests` suites run unchanged. If any of
them needs editing, that is a signal the retrofit stopped being additive — report it rather than
adapting the test.

### E2E

Out of scope (ADR-17). Follow-up change: introduce a Playwright harness and cover
login → catalog ABM → logout.

---

## Risks and open items

| Item | Type | Handling |
|---|---|---|
| EF Core 10 keyed `SetQueryFilter` overload availability | Verify at apply | ADR-6 fallback; report, do not silently compose |
| EF making `IdTenant` nullable for an optional composite FK | Verify at apply | ADR-9 fallback; report the deviation |
| `BeginTransaction` throwing under `EnableRetryOnFailure` | Known trap | ADR-16 mandates the execution-strategy wrapper |
| Production DSN with a superuser role ⇒ RLS silently bypassed | Operational | ADR-5 startup check throws in Production |
| `app.acceso='plataforma'` is app-controlled | Accepted residual | Documented in ADR-4; tenant→tenant isolation is still absolute |
| Template's price-list / Consumidor Final cannot be created in stage 1 | Scope gap | Declared as stage-2/3 extension points in the template (ADR-16); a provisioned tenant is incomplete until then |
| Flow A (subdomain login, `usuario` at `tenant.domain.com`) not implemented in stage 1 | Accepted | ADR-7 login contract; extension point (`ResolucionDeTenantPorSubdominio`) identified but depends on wildcard DNS/TLS at the hosting layer (EasyPanel), a deployment concern out of this stage's scope |
| Slice size (schema + API + UI + tests) | Delivery | Already flagged in the proposal; resolve at the `sdd-tasks` review-workload guard. Natural cut points: (1) tenancy plumbing + org tables + RLS, (2) usuarios retrofit + suspension, (3) catalogs + parametros, (4) web ABM |
| Empresa / punto de venta selection in the session | Deferred | ADR-10; `parametros` takes an explicit `idPuntoVenta` until the operational stages land it |

---

## Next step

`sdd-tasks` — break this into work units, honouring the four cut points above and running the
Review Workload Forecast before any code is written. The first task of the implementation phase
is gate #1 of the migration sequence, which **stops and waits for the user**.
