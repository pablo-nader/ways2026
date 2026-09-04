# Proposal: Stage 20 — Organization relationships and usage-guarded logical deletion

## Intent

The owner tested the running application and reported two things about the platform (root) admin
surface. `explore.md` verified both against the code, and corrected one of them.

| Fact | Evidence | Consequence |
|---|---|---|
| **The hierarchy exists in the model and is never projected** | `EmpresaListado.IdTenant`, `PuntoVentaListado.IdTenant/.IdEmpresa` carry raw integers (`Contratos.cs:24,28-38`) and the screens render those integers (`Empresas.tsx:156`, `PuntosVenta.tsx:216-217`) | A platform operator reads `2` and cannot tell which tenant that is. Four flat lists, zero navigable relationship |
| **`UsuarioListado` carries no tenant at all** | `Usuarios/Contratos.cs:19-27` — `Id, Usuario, Mail, RolId, Rol, Estado, UltimaConexion, CreatedAt` | The root user list cannot even be **grouped** by tenant, let alone filtered. The data is one join away |
| **No DELETE exists for tenant, empresa or punto de venta** | `OrganizacionEndpoints.cs:12-13` defers alta/baja to `ServicioDeAprovisionamiento` (ADR-16), but `AprovisionamientoEndpoints.cs` exposes **only** `POST /api/plataforma/tenants` | The delete path the comment defers to was **never implemented**. The only lever is suspension, and only for tenants |
| **`EstadoTenant.Baja` is a latent value with zero writers** | The enum declares it (`EstadoTenant.cs:16`, *"los datos quedan para exportación"*), `Tenant.PuedeOperar` reads it, login rejects it (`ServicioDeAutenticacion.cs:141`), `CambiarEstadoTenantAsync` refuses to touch it (`ServicioDeOrganizacion.cs:67`), `Tenants.tsx:12` documents its absence — and **nothing ever assigns it** (`Estado = EstadoTenant.` grep: only `Activo`, twice) | Stage 17's *"PRE latente"* pattern, already in the tree. This stage is its writer |
| **The soft-delete plumbing is already installed** | `EntidadBase` gives `DeletedAt` to all four entities; `AplicarFiltroDeBajaLogica` (`WaysDbContext.cs:426-447`) installs the `"BajaLogica"` filter on every descendant; `estado_tenant` was created with `activo,baja,suspendido` in the **first** migration (`20260801011312_Organizacion.cs:18`) | **Zero migrations.** What is missing is endpoints, a guard, and UI |
| **The owner's report was wrong on one point** | `Usuario` **does** already delete logically: `DELETE /api/usuarios/{id}` (`UsuariosEndpoints.cs:57`) wired to a "Baja" button (`Usuarios.tsx:120-122`) | The gap there is not the endpoint — it is that the endpoint has **no dependent guard at all** |

Three costs the platform operator pays today. **Orientation**: with more than one tenant, the root
screens stop being administrable — you cannot answer *"which empresas does this tenant have?"*
without reading integers out of a database. **Reversibility**: a mistyped provisioning run, a
cancelled trial, a duplicated empresa are permanent; the platform accumulates rows nobody can
retire. **Safety**: the one deletion that does exist (`Usuario`) checks role policy and nothing
else — it will happily retire the employee whose id is stamped on ten thousand comprobantes.

**What stage 20 delivers.** The root surface shows the hierarchy it already owns, and the platform
gains a delete that is *safe by construction*: it refuses on anything the customer has actually
touched, and it discovers what "touched" means from EF's own model rather than from a list a human
has to remember to update.

## Binding owner decisions (settled — do not reopen)

| # | Decision | Consequence for this proposal |
|---|---|---|
| **B1** | **Physical deletion is never permitted.** All deletion is logical (`DeletedAt`), per `EntidadBase` | No `DELETE FROM` anywhere. No FK constraint can ever fire — see B5's corollary in Risks |
| **B2** | **"In use" means the customer loaded or operated *anything* beyond the provisioning baseline** — articles, clients, suppliers, a second punto de venta, not only transactional records. Only what `ServicioDeAprovisionamiento` created is pristine | A tenant with 3 000 articles and zero sales is **not** deletable. This replaced an earlier, narrower reading |
| **B3** | **The discriminator is `dependiente.CreatedAt > entidad.CreatedAt`**, valid because `ServicioDeAprovisionamiento.cs:46` reads the clock **once** (`var ahora = reloj.Ahora;`) and stamps that identical instant on every provisioned row, the `Tenant` included (`:53-54, 69-70, 79-80, 89-90, 104-105, 121-122, 142-143, 158-160`) | Template-version independent: the baseline is defined by a timestamp identity, never by a hardcoded row count |
| **B4** | **The dependent set comes from EF metadata** (`IEntityType.GetReferencingForeignKeys()`), never a hand-maintained table list | A hand list **fails unsafe** (one forgotten table makes a used entity deletable, silently). Metadata **fails safe** |
| **B5** | **Audit rows do not block.** `Auditoria.IdActor` and `Auditoria.IdPuntoVenta` are a trail *about* the entity, not data the customer operated — and because deletion is logical the referenced row survives, so the trail keeps rendering | One named carve-out, in code, with its reason and its own test (decision 4) |

## Scope

### In Scope

**Part A — project the hierarchy in the root UI**

- `EmpresaListado` gains `NombreTenant`; `PuntoVentaListado` gains `NombreTenant` and
  `RazonSocialEmpresa`; `UsuarioListado` gains `IdTenant` and `NombreTenant` (it carries **neither**
  today); `TenantListado` gains `CantidadEmpresas`, `CantidadPuntosVenta` and `CantidadUsuarios`.
- Filtering: by tenant on `Empresas.tsx`, `PuntosVenta.tsx` and `Usuarios.tsx`; additionally by
  empresa on `PuntosVenta.tsx`. Client-side over the already-loaded list (decision 7).
- The four screens render owner **names**, never raw ids.

**Part B — usage-guarded logical deletion**

- `DELETE /api/plataforma/tenants/{id}`, `DELETE /api/empresas/{id}`,
  `DELETE /api/puntos-venta/{id}` — logical, never physical.
- `InspectorDeUso`: one Application service that walks EF metadata, classifies every referencing FK
  into exactly one of three buckets (decision 4), and answers *"is this entity pristine?"* in a
  single round trip.
- Cascade on a pristine parent, scoped to the organization projection and the authentication
  surface (decision 2).
- Structural minimums with their own named 409s (decision 3).
- `Usuario` deletion gains the same dependent guard (decision 5), keeping every existing
  `PoliticaDeRoles` rule intact.
- Tenant deletion writes `Estado = EstadoTenant.Baja` **and** `DeletedAt` (decision 1).
- Six new `ErrorDominio.Conflicto` codes, their `ErrorApi` surfacing, and the web confirmation +
  message mapping.

### Out of Scope

- **Any schema migration.** The gate below is the contract: zero migrations, zero DDL, zero data
  statements. **Reopen condition**: if design discovers a schema change is genuinely required, work
  **stops** and the owner approves a model summary first (CLAUDE.md DB gate).
- **`Estado` / suspension for empresa or punto de venta.** The owner asked for deletion, not
  disabling. `Tenant.Estado` semantics are unchanged; `Empresa` and `PuntoVenta` gain no state field.
  **Reopen condition**: an explicit owner request.
- **A force/override delete** that bypasses the guard for a used entity. The platform's existing
  recourse for a customer it wants to stop serving is **suspension**, which already works.
  **Reopen condition**: the first real support case that needs it — at which point it needs an
  audit trail and a confirmation ceremony this stage is not designing blind.
- **Undelete / restore.** Rows are recoverable by hand (`deleted_at = NULL`), and `EstadoTenant.Baja`
  is deliberately not reachable from suspend/reactivate (`ServicioDeOrganizacion.cs:67-72`). A
  restore endpoint is its own change with its own guard question.
- **Retro-guarding the other soft deletes.** `ServicioDeCatalogo<T>`, `ServicioDeClientes`,
  `ServicioDeProveedores`, `ServicioDeArticulos` and `ServicioDeOfertas` all delete without a
  dependent check (`explore.md:118-123`). This stage builds the mechanism and applies it to the four
  organization entities only. **Reopen condition**: a later stage reuses `InspectorDeUso` — it is
  designed to be entity-agnostic precisely so that is a one-line adoption.
- **Server-side pagination or server-side filtering** of the four lists (decision 7).
- **Navigating from a tenant row into its children** (drill-down). Counts, not navigation.
- **The owner's reserved carryovers** — the `importe` CHECK micro-gate, the `articulos_empresas`
  replace-set gap, `stage-18-etiquetas-y-consulta` (blocked on a physical measurement). Untouched.

## Capabilities

### New Capabilities

- **`bajas-de-organizacion`** — what logical deletion means for the organization hierarchy: the
  pristine/used discriminator and why a single clock reading makes it exact; how the dependent set is
  discovered and why a hand list is forbidden; the three classification buckets and the two named
  carve-outs; the cascade rule and its boundary; the structural minimums; the complete 409 code set;
  and the invariant that no row is ever physically deleted.

### Modified Capabilities

- **`tenant-organization`** — **ADDED**: the read projection carries owner names and child counts;
  a platform-only logical deletion surface exists for tenant, empresa and punto de venta (the
  counterpart the existing *Platform-Only Creation* requirement never got); tenant deletion is the
  first and only writer of `EstadoTenant.Baja`. **UNCHANGED (asserted)**: provisioning, suspension /
  reactivation, tenant isolation, and every existing edit route.
- **`usuarios-tenant-scoping`** — **ADDED**: the usuarios listing DTO carries the account's tenant
  identity and name (platform actors only see more than one value); `Usuario` logical deletion is
  guarded by the same usage rule as the organization entities, **after** the existing
  `PoliticaDeRoles.ValidarPuedeIntervenirSobre` checks, never instead of them.

**Not modified**: every other capability. No policy is added or changed
(`Politicas.SoloPlataforma`, `Politicas.GestionDeOrganizacion`, `Politicas.LecturaDePuntosVenta` and
`Politicas.GestionDeUsuarios` cover the whole surface), and no operational path is touched.

## Approach

**Ask EF what points at this row, ask the clock whether the customer put it there, and refuse
loudly.** Three properties carry the whole stage:

1. **The dependent set is derived, not remembered.** `IEntityType.GetReferencingForeignKeys()`
   enumerates every FK pointing at `Tenant`, `Empresa`, `PuntoVenta` and `Usuario`, including the
   awkward ones a human list forgets — `MovimientoStock.IdPuntoVentaDestino`,
   `TurnoCaja.IdEmpleadoApertura`/`IdEmpleadoCierre`, and the composite `(id, id_tenant)` keys. This
   is the idiom the codebase already uses: `AplicarFiltroDeBajaLogica` and
   `AplicarFiltroDeTenantEnTenant` (`WaysDbContext.cs:426-486`) both walk the EF model by reflection
   to install behaviour uniformly.
2. **The baseline is a timestamp identity, not a count.** B3. `EXISTS (SELECT 1 FROM t WHERE fk = @id
   AND created_at > @ancla)` — nine provisioned rows share the tenant's `created_at` and are
   excluded; the first article the customer loads is strictly later and blocks.
3. **The guard fails safe in every direction.** A new table added by a future stage is discovered
   automatically. A dependent EF cannot classify **breaks a test at build time** rather than silently
   permitting a delete (decision 4). A dependent with no timestamp blocks on existence. There is no
   path in which forgetting something makes deletion *easier*.

**The corollary that must be stated out loud.** Because deletion is logical (B1), **no Postgres
constraint can ever fire** — every FK in `WaysDbContext` is `DeleteBehavior.Restrict`, and `Restrict`
contributes exactly zero protection against `UPDATE ... SET deleted_at`. The `db-error-backstops`
skill's SQLSTATE `23503` backstop **does not apply to this change**. The application guard is the
sole line of defence, and its tests carry the entire burden. This is recorded as the stage's top
risk, and it is why decision 4's completeness test is non-negotiable.

## Decisiones

Nine decisions: the explore's three open questions resolved, plus six the proposal had to take.

---

### 1 — **Tenant deletion writes `Estado = Baja` *and* `DeletedAt`, in one transaction. It is `EstadoTenant.Baja`'s first writer.**

`EstadoTenant.Baja` has a reader in three places and a writer in none (Intent table). Writing only
`DeletedAt` would leave the latent value latent forever, and would leave two contradictory notions of
"gone" in the same row. Writing only `Estado = Baja` would leave the tenant visible in the root list.

**Decision.** Both, atomically. `Tenant.PuedeOperar` (`Tenant.cs:20`) is already
`Estado == Activo && DeletedAt is null`, so the two agree by construction.

**The verified safety property.** A deleted tenant's users cannot authenticate, and the failure is a
clean 403, not a crash: `ServicioDeAutenticacion.cs:137-147` reads the tenant through the
`"BajaLogica"` filter, gets `null`, and the guard is `tenant is null || !tenant.PuedeOperar` →
`ErrorDominio("tenant_suspendido", …, 403)`. The `null` branch already exists and is already correct.

**Cost of reversing.** `UPDATE tenants SET deleted_at = NULL, estado = 'activo'` on one row.

---

### 2 — **Cascade: yes, and its boundary is "what the root UI shows plus what can authenticate". Nothing else. (Explore open question 1 — recommendation adopted, boundary added.)**

Deleting a tenant logically deletes **its empresas, its puntos de venta, and its usuarios**. Deleting
an empresa logically deletes **its puntos de venta**.

**Why cascade at all.** Without it, deleting tenant 4 leaves its empresa in `Empresas.tsx` pointing
at a tenant that no longer resolves — the root list would render an orphan, which is the exact defect
Part A exists to remove.

**Why the boundary stops there** — and this is the part the explore did not state. A wide cascade over
the whole provisioning template (`areas`, `medios_pago`, `listas_precio`, the *Consumidor Final*
client) would contradict the domain's own stated intent: `EstadoTenant.Baja` is documented as *"No se
elimina físicamente: los datos quedan para exportación"* (`EstadoTenant.cs:15`). Rows that are
invisible in the root UI and unreachable by any login are already inert; deleting them buys nothing
and costs the export promise.

**Why this is cheap to guarantee.** A tenant with a used child is **not deletable in the first
place** — every `EntidadTenant` descendant references the tenant, so any usage at all blocks at the
tenant. The cascade therefore only ever runs over the pristine provisioning baseline: one empresa,
one punto de venta, one admin. It is a small, provable set, not an unbounded sweep.

**One shared `DeletedAt` instant** for the parent and every cascaded child — one `reloj.Ahora`, the
`ServicioDeUsuarios.EliminarAsync:302` idiom (*"un único `momento` para la entidad Y el payload"*).
That instant is what makes a manual restore unambiguous.

**Cost of reversing.** `UPDATE … SET deleted_at = NULL WHERE deleted_at = '<the shared instant>'`.

---

### 3 — **Structural minimums: yes, with two separate named 409s, checked before the usage guard. (Explore open question 2 — adopted.)**

- A tenant must keep **at least one** empresa → `ultima_empresa_del_tenant`.
- An empresa must keep **at least one** punto de venta → `ultimo_punto_venta_de_la_empresa`.

**Why separate codes, not one generic "in use".** They mean opposite things to the operator.
*"In use"* says *"there is data here, this is not the row you want to delete."* *"Last empresa"*
says *"this row is fine to delete — you are trying to delete it the wrong way; delete the tenant."*
Collapsing them would send the operator hunting for data that does not exist.

**Ordering.** Structural minimum is checked **before** usage, because it is a cheap `COUNT` on one
table and its message is more actionable. A pristine single-empresa tenant is deleted through the
**tenant**, whose cascade (decision 2) takes the empresa with it — so the minimum never becomes a
prison.

**Deliberately not enforced here**: a minimum number of admins per tenant. Flagged in the question
round; `PoliticaDeRoles` already forbids self-deletion and Root-target deletion, so the reachable
failure is narrow, and inventing a role invariant in an autonomous run is worse than naming the gap.

**Cost of reversing.** Delete two pre-checks.

---

### 4 — **The dependent set is discovered from EF metadata (B4) and classified into exactly three buckets, with a completeness test that fails the build on an unclassified type.**

`GetReferencingForeignKeys()` returns the complete set. It does **not** tell us how to interrogate
each dependent, because **15 domain entity types do not inherit `EntidadBase` and therefore have no
`created_at` column at all** — `Stock`, `StockLote`, `MovimientoStock`, `MovimientoCaja`,
`MovimientoTesoreria`, `ArqueoTurno`, `MovimientoCuentaCorriente`,
`MovimientoCuentaCorrienteProveedor`, `Auditoria`, `ArticuloEmpresa`, `OfertaLista`,
`NumeracionCliente`, `NumeracionArticulo`, `NumeracionComprobante`, `NumeracionFiscal` (verified:
`Stock.cs` and `ArticuloEmpresa.cs` carry no timestamp property whatsoever). B3's discriminator is
**not universally applicable**, and the explore did not say so.

| Bucket | Rule | Why it is correct |
|---|---|---|
| **1 — timestamped** | `EntidadBase` descendant → blocks iff `EXISTS (fk = @id AND created_at > @ancla)` | B3 exactly |
| **2 — untimestamped** | not `EntidadBase` → blocks iff **any** row references the entity | Provisioning creates **no** row of any of these types **except** `numeraciones_clientes` (`explore.md:107-110`). Existence therefore already means usage, and it fails safe |
| **3 — carve-out** | an explicitly named type with a written reason and its own test | Exactly two members: `Auditoria` (B5) and `NumeracionCliente` (the provisioning counter, inserted by raw SQL in `AsignadorDeNumeroCliente.AsegurarContadorAsync`, not an `EntidadBase`, not customer data) |

**The completeness test is the safety mechanism, and it is the single most important test of the
stage.** It walks `GetReferencingForeignKeys()` for all four entities and asserts every discovered
type falls into exactly one bucket. A future stage that adds an untimestamped table gets a **red
test naming the type**, not a silent hole. Buckets 1 and 2 are derived from the model, so the only
hand-written artifact is the two-member carve-out list — and a forgotten carve-out over-blocks
(annoying) instead of under-blocking (unsafe).

**Nullable FKs need no special rule**: `Cliente`, `Proveedor`, `Oferta` and
`ConfiguracionDeCatalogo<T>` use `IdEmpresa NULL` to mean *"shared catalog row"*, and `fk = @id`
simply does not match `NULL`. A shared row correctly does not block an empresa's deletion.

**Cost of reversing.** The service has one caller per entity. Deleting it deletes the guard.

---

### 5 — **`Usuario` deletion gains the guard, positioned after `PoliticaDeRoles`, never instead of it. (Explore open question 3 — adopted.)**

`ServicioDeUsuarios.EliminarAsync` (`:292-317`) validates role policy and then stamps `DeletedAt`
with **no dependent check** — so today the platform can retire the employee stamped on
`ComprobanteVenta.IdEmpleado`, `TurnoCaja.IdEmpleadoApertura`/`IdEmpleadoCierre`, `Presupuesto`,
`Remito` and `OrdenCompra` rows.

**Decision.** Insert the same `InspectorDeUso` check **after** `ValidarPuedeIntervenirSobre` and
before the `DeletedAt` write, yielding `usuario_en_uso`. Every existing rule is preserved verbatim:
Root targets are still undeletable, self-deletion is still forbidden, the audit record is still
written, `ValidarAlcanceDeTenant`'s deliberate 404-not-403 (ADR-8, anti-existence-oracle) is
untouched.

**The one behavioural consequence, stated honestly.** The `admin` account created by provisioning has
the tenant's `created_at`, so it is pristine — until it logs in and does anything. This is correct
under B2: an admin who has operated is exactly the account whose history must not lose its actor. The
platform's remaining lever for a departing employee is the **existing** `Inactivo`/`Bloqueado` state,
which is what that state is for.

**Cost of reversing.** Remove one call.

---

### 6 — **One query, generated from metadata, executed as parameterised raw SQL — and its two side effects are exactly the semantics we want.**

`Tenant` has ~40 referencing FKs. Forty sequential `AnyAsync` round trips per delete attempt is
absurd; the guard composes **one** statement of `SELECT '<tabla>' … WHERE fk = @id [AND created_at >
@ancla] LIMIT 1` branches joined by `UNION ALL`, with an outer `LIMIT 1`. It returns the **name of
the first blocking table** or nothing. Precedent for raw ADO in this codebase:
`AsignadorDeNumeroComprobante` and `EscriturasDeCuentaCorriente`.

**Injection.** Identifiers come from EF metadata; entity ids and the anchor are **parameters**. No
user-supplied string ever reaches the statement.

**Side effect A — query filters are bypassed, and that is the intended rule.** Raw SQL does not apply
`"BajaLogica"`, so a **soft-deleted** dependent still blocks. Deliberate: B2 asks *"did the customer
ever operate here"*, not *"is there live data right now"*. A customer who loaded 3 000 articles and
then deleted them operated. It also fails safe.

**Side effect B — RLS still applies**, because it lives on the connection, not on EF. A tenant admin
deleting their own empresa sees every dependent of their own tenant, so the guard cannot under-count;
tenant deletion is platform-only and platform sees everything (`app_es_plataforma()`). Asserted by a
cross-tenant integration test.

**The one interface question for design.** `IWaysDbContext` exposes `DatabaseFacade Database` but not
`IModel Model` (`IWaysDbContext.cs:153`). The proposal's recommendation is to add `IModel Model
{ get; }` under the interface's own stated criterion — *"`DatabaseFacade` is the same EF Core
abstraction any `DbContext` already exposes, not a type from Infrastructure"* (`:150-152`) — which
`IModel` satisfies identically. **`sdd-design` confirms or replaces it with a dedicated port**; the
alternative is one extra abstraction, not a redesign.

**Cost of reversing.** One file.

---

### 7 — **Filtering is client-side over the already-loaded list. No new endpoint, no query parameter, no pagination.**

All four screens already `GET` the full list into `useState` with no paging
(`ServicioDeOrganizacion.ListarTenantsAsync` and siblings return `IReadOnlyList<T>` unbounded). At
platform scale — tenants, empresas, puntos de venta, staff accounts — that is tens of rows, not
thousands.

**Decision.** A `<select>` per screen filtering the loaded array. Zero API surface added by Part A
beyond the DTO fields.

**When this stops being true**, it stops being true for the whole screen (the unbounded list, not the
filter), and the answer is server-side pagination for all four at once — a different change with a
different shape. Naming that now prevents a half-server/half-client filter later.

**Cost of reversing.** The filter becomes a query parameter; the projections already carry the ids.

---

### 8 — **Owner names are projected by join in the existing `Select`, not denormalised and not fetched separately.**

`ListarEmpresasAsync` already projects into a DTO inside the `Select` (`ServicioDeOrganizacion.cs:92-96`);
adding `e.Tenant.Nombre` extends the same expression into one `LEFT JOIN`. Counts on `TenantListado`
are correlated sub-selects in the same statement. Two N+1 shapes are thereby avoided by construction,
and an integration test asserts a **single** round trip per list endpoint.

**`UsuarioListado` is the exception worth naming**: it gains **two** fields, `IdTenant` (which it has
never carried) and `NombreTenant`. `Usuario.IdTenant` is nullable — `null` means platform staff — so
the projection renders *"Plataforma"*, never an empty cell that reads like missing data.

**Cost of reversing.** Remove fields from a record and a `Select`.

---

### 9 — **Error messages name the blocked entity in plain Spanish and degrade to a generic phrase; the code is always exact.**

Decision 6 yields the blocking table's name. The message maps it through a small label dictionary
(`comprobantes_venta` → *"ventas"*, `articulos` → *"artículos"*, …) and falls back to *"datos
cargados"* for an unmapped table.

**Why the dictionary is not the hand-list B4 forbids.** B4's list would have decided **whether** to
block; this one only decides **how to word** an already-decided block. A missing entry costs a vaguer
sentence, never a wrong verdict — the failure directions are not comparable.

`ErrorDominio.Conflicto("<codigo_snake_case>", "<mensaje>")` → HTTP 409 → `ErrorApi { estado, codigo,
mensaje }` (`ManejadorDeErrores.cs:13-87`, `api/cliente.ts:8-16,60-63`), the
`usuario_duplicado`/`mail_duplicado` idiom. The **web** keys its copy off `codigo`, never off
`mensaje`.

**Cost of reversing.** Drop the dictionary; messages get generic.

---

## Modelo de datos propuesto — **CERO CAMBIOS DE SCHEMA**

> **DB CHANGE GATE (CLAUDE.md) — this section is the contract.** Any DDL or data statement proposed
> by a later phase is a **scope violation that reopens the gate**.

**Gate verdict proposed: ZERO migrations, ZERO tables, ZERO columns, ZERO enums, ZERO `ALTER`, ZERO
indexes, ZERO data statements, ZERO seed changes.**

Everything this change writes already exists in the schema:

| What the change writes | Where it already exists |
|---|---|
| `tenants.deleted_at`, `empresas.deleted_at`, `puntos_venta.deleted_at`, `usuarios.deleted_at` | `EntidadBase` (`EntidadBase.cs`), configured per entity (e.g. `TenantConfiguration.cs:33`) |
| The `"BajaLogica"` filter that hides them | `WaysDbContext.AplicarFiltroDeBajaLogica` (`:426-447`) |
| `tenants.estado = 'baja'` | Native pg enum `estado_tenant` created with **`activo,baja,suspendido`** in the first migration (`20260801011312_Organizacion.cs:18`) — the value is already in the database, only its writer was missing |
| Owner names and child counts | Read-only projections over existing FKs. A projection is not a schema change |

**Binding criteria for verify.**

1. **No new file** under `src/Ways.Infrastructure/Persistencia/Migraciones/`. The last migration of
   the tree at propose time **must still be the last one** at verify.
2. `dotnet ef migrations has-pending-model-changes` **clean**.
3. **Zero** `CREATE`/`ALTER`/`DROP`/`INSERT`/`UPDATE`-DDL statements introduced anywhere in the diff
   outside the guard's generated read-only `SELECT`.
4. `InicializadorDeBaseDeDatos.cs` **untouched**.

**Precedent**: stages 10, 11, 13 and 18 delivered complete stages with zero DDL.

## API surface

| Route | Method | Policy | Status |
|---|---|---|---|
| `/api/plataforma/tenants` | GET | `SoloPlataforma` | **Modified** — DTO gains three counts |
| `/api/plataforma/tenants/{id}` | **DELETE** | `SoloPlataforma` | **New** — logical, guarded, cascading |
| `/api/empresas` | GET | `GestionDeOrganizacion` | **Modified** — DTO gains `NombreTenant` |
| `/api/empresas/{id}` | **DELETE** | `GestionDeOrganizacion` | **New** |
| `/api/puntos-venta` | GET | `LecturaDePuntosVenta` | **Modified** — DTO gains two owner names |
| `/api/puntos-venta/{id}` | **DELETE** | `GestionDeOrganizacion` | **New** — note the asymmetry: read is `LecturaDePuntosVenta` (the POS selector uses it, `OrganizacionEndpoints.cs:64-66`), write is not |
| `/api/usuarios` | GET | existing | **Modified** — DTO gains `IdTenant` + `NombreTenant` |
| `/api/usuarios/{id}` | DELETE | existing | **Modified** — same route, guard added (decision 5) |
| `…/suspender`, `…/reactivar`, every `PUT` | — | — | **Unchanged**, asserted |

**Zero new policies.** All four DELETEs reuse the policy of the group they belong to.

### Error codes (complete set)

| Code | HTTP | Raised when |
|---|---|---|
| `tenant_en_uso` | 409 | The tenant has a dependent past the provisioning baseline |
| `empresa_en_uso` | 409 | Idem, empresa |
| `punto_venta_en_uso` | 409 | Idem, punto de venta |
| `usuario_en_uso` | 409 | Idem, usuario (decision 5) |
| `ultima_empresa_del_tenant` | 409 | Deleting the tenant's only empresa (decision 3) |
| `ultimo_punto_venta_de_la_empresa` | 409 | Deleting the empresa's only punto de venta |

Reused unchanged: `tenant_dado_de_baja` (409), the `PoliticaDeRoles` role errors, and
`ErrorDominio.NoEncontrado` (404) for an already-deleted row — the `"BajaLogica"` filter makes it
invisible, so a second DELETE is a clean 404, and **DELETE is therefore idempotent-safe**.

## Affected Areas

| Area | Impact | Description |
|---|---|---|
| `src/Ways.Application/Organizacion/Contratos.cs` | Modified | Three DTOs gain owner names; `TenantListado` gains three counts |
| `src/Ways.Application/Usuarios/Contratos.cs` | Modified | `UsuarioListado` gains `IdTenant` + `NombreTenant` |
| `src/Ways.Application/Organizacion/ServicioDeOrganizacion.cs` | Modified | Joined projections; three `EliminarAsync` methods; cascade; structural minimums |
| `src/Ways.Application/Organizacion/InspectorDeUso.cs` | **New** | The metadata walk, the three buckets, the generated statement (decisions 4 and 6) |
| `src/Ways.Application/Usuarios/ServicioDeUsuarios.cs` | Modified | One guard call inserted after `PoliticaDeRoles` (decision 5) |
| `src/Ways.Application/Abstracciones/IWaysDbContext.cs` | Modified | `IModel Model { get; }` — pending design confirmation (decision 6) |
| `src/Ways.Api/Endpoints/OrganizacionEndpoints.cs` | Modified | Three DELETE routes; the class doc-comment's *"acá no hay `POST` ni `DELETE` a propósito"* is corrected |
| `src/Ways.Api/Seguridad/Politicas.cs` | **Untouched** | Binding criterion |
| `src/Ways.Infrastructure/Persistencia/**` | **Untouched** except the `IModel` exposure | Binding criterion: no migration, no configuration change |
| `src/Ways.Web/src/api/tipos.ts` + `api/organizacion.ts` + `api/usuarios.ts` | Modified | DTO fields; three `eliminar*` calls |
| `src/Ways.Web/src/paginas/{Tenants,Empresas,PuntosVenta,Usuarios}.tsx` | Modified | Name columns, counts, filters, delete button + confirmation, 409 code→copy mapping |
| `src/Ways.Web/src/paginas/*.test.tsx` | **New** | **No test file exists for any of the four screens today** — Vitest coverage is created, not extended |
| `tests/Ways.Application.Tests/Organizacion/*` | New/Modified | `InspectorDeUsoTests` (including the completeness test), extended `ServicioDeOrganizacionTests` |
| `tests/Ways.IntegrationTests/*` | New/Modified | End-to-end delete/guard/cascade/RLS, single-round-trip assertions |
| `docs/09-multi-tenancy.md`, `docs/10-modelo-de-datos.md` | Modified | An "Etapa 20" note: deletion semantics and the guard. **No schema table changes** |

## Risks

| Risk | Likelihood | Mitigation |
|---|---|---|
| **The application guard is the *only* defence** — B1 means no FK constraint can fire, so `db-error-backstops`' SQLSTATE backstop does not apply and a guard bug is unbacked | **High if unmanaged** | Decision 4's completeness test (build-time detection of unclassified dependents); a per-bucket test; an integration test that deletes a used entity of each of the four kinds and asserts the exact 409 code; mutation-proof tests over the `>` in the discriminator |
| **A forgotten dependent silently permits a delete** | Low, by design | B4 + decision 4: the set is derived, and an unclassifiable type is a **red test**, not a hole |
| **Clock equality is not exactly equal** — if a future refactor made `ServicioDeAprovisionamiento` read `reloj.Ahora` more than once, the baseline splits and a fresh tenant becomes undeletable | Low | A regression test asserting that a freshly provisioned tenant is **pristine**, which fails the moment the single-reading property is broken. It guards `:46` from a distance |
| **Cascade deletes more than intended** | Low | Decision 2's boundary is three entity types, and it only ever runs over a pristine tenant; the shared-instant rule makes a manual restore exact |
| **A tenant becomes undeletable forever** after one article was loaded and deleted (decision 6, side effect A) | Med | Accepted and named: suspension remains the platform's lever, and a force-delete is a registered out-of-scope item with its reopen condition |
| **A tenant loses its last admin** and becomes unadministrable | Low | Flagged, not silently solved (decision 3, question round item 3). `PoliticaDeRoles` already blocks self- and Root-deletion |
| **~40 `UNION ALL` branches for a tenant are slow** | Low | Each branch is an indexed `fk = @id` with `LIMIT 1`, on an operation a platform operator performs a handful of times a year. Not on any hot path |
| **RLS makes the guard under-count for a tenant admin** | Low | Structurally impossible (all dependents share the actor's tenant); asserted by a cross-tenant integration test anyway |
| **Review budget** — two coherent but independent parts | Med | Five slices, stacked-to-main; Part A (1-2) and Part B (3-5) are independently mergeable and independently valuable |

## Rollback Plan

**No database artifact exists to roll back** — the stage ships zero migrations, so `git revert` is
the complete rollback at every level.

| Slice | Rollback |
|---|---|
| **1 — projection API** | Revert. DTO fields disappear; the web slice is not merged yet |
| **2 — projection web** | Revert. The screens return to rendering ids |
| **3 — the guard** | Revert. `InspectorDeUso` has **no caller** until slice 4 — this slice is inert by construction |
| **4 — deletion API** | Revert removes three routes and one guard call. Rows already soft-deleted stay soft-deleted and stay hidden; that is a **pre-existing, supported state** (`Usuario` has produced it since stage 1) |
| **5 — deletion web** | Revert removes buttons. The API still works; nobody can press it |
| **Whole stage** | `git revert` of the five merges. The only durable trace is any row an operator actually deleted, which is a `deleted_at` (and possibly `estado = 'baja'`) that a one-line `UPDATE` reverses — **the data was never destroyed (B1)** |

**There is no unrecoverable action in this stage.** That is the direct dividend of B1.

## Dependencies

- **Etapa 1** (archived) — the organization hierarchy, `EntidadBase`/`DeletedAt`, the `"BajaLogica"`
  filter, `estado_tenant` with `baja` already in it, RLS.
- **`ServicioDeAprovisionamiento`** (ADR-16) — the single-clock-reading property (B3) that the entire
  discriminator rests on.
- **`PoliticaDeRoles`** — consumed unchanged by decision 5.
- **EF Core `IEntityType.GetReferencingForeignKeys()`** — .NET 10 / EF Core, no new package.
- **`IRelojDelSistema` / `RelojFijo`** — the deletion instant and its tests.
- **No new NuGet package, no new web dependency, no migration, no external service.**
- Skills: `mutation-proof-tests`, `dto-contract-honesty`, `web-descriptor-tests`,
  `work-unit-commits`, `judgment-day` before every PR. **`db-error-backstops` is explicitly
  N/A** — see Approach.
- **Blocked on nothing.** No owner action is required beyond the DB-gate ratification of "zero
  schema".

## Success Criteria

- [ ] **Zero new migrations**; `has-pending-model-changes` clean; `InicializadorDeBaseDeDatos.cs`
      untouched.
- [ ] Each of the four root screens renders owner **names**; no raw owner id is displayed anywhere.
- [ ] `Usuarios.tsx` shows a tenant column, rendering *"Plataforma"* for `IdTenant is null`.
- [ ] `Tenants.tsx` shows empresa / punto de venta / usuario counts, and each list endpoint performs
      **one** database round trip (asserted, not assumed).
- [ ] Filtering by tenant works on empresas, puntos de venta and usuarios; by empresa on puntos de
      venta.
- [ ] A **freshly provisioned** tenant is deletable, and deleting it soft-deletes its empresa, its
      punto de venta and its admin usuario, all sharing **one** `deleted_at` instant, with
      `estado = 'baja'` on the tenant.
- [ ] Loading **one article** (no sale, no movement) makes that tenant, its empresa and its punto de
      venta undeletable, each with its own named 409 — **B2 proven, not asserted**.
- [ ] The completeness test enumerates `GetReferencingForeignKeys()` for all four entities and fails
      on any dependent type outside the three buckets.
- [ ] The two carve-outs are proven independently: an entity with **only** `auditoria` rows past the
      anchor is deletable; an entity with **only** its provisioned `numeraciones_clientes` row is
      deletable.
- [ ] A **soft-deleted** dependent still blocks (decision 6, side effect A), proven by a test.
- [ ] `ultima_empresa_del_tenant` and `ultimo_punto_venta_de_la_empresa` fire on their exact
      conditions and **not** on any other.
- [ ] Deleting a used `Usuario` returns `usuario_en_uso`, **and** every pre-existing
      `PoliticaDeRoles` behaviour (Root target, self, out-of-scope 404) is byte-identical.
- [ ] A second DELETE on an already-deleted row returns **404**, not 500.
- [ ] A deleted tenant's user cannot log in and receives **403 `tenant_suspendido`**, not a crash.
- [ ] **Zero physical deletes** in the whole diff — a repository scan for `ExecuteDelete`,
      `Remove(`, `RemoveRange(` and `DELETE FROM` on the four tables.
- [ ] Cross-tenant isolation holds for every new route (RLS read **and** write pair).
- [ ] Domain / Application / Integration / Vitest suites green.

## Plan de slices (tentative — `sdd-tasks` owns the final breakdown)

Stacked-to-main, one `judgment-day` round per slice (`protocolo-pr-solo-dev`).

| # | Branch | Content | ~lines |
|---|---|---|---|
| 1 | `feat/stage20-slice1-proyeccion-api` | The four DTOs gain owner names and counts; joined projections in `ServicioDeOrganizacion` and `ServicioDeUsuarios`; single-round-trip integration assertions | ~380 |
| 2 | `feat/stage20-slice2-proyeccion-web` | `tipos.ts`; name columns and counts on the four screens; tenant filter (×3) and empresa filter (×1); the **first** Vitest files for these screens | ~420 |
| 3 | `feat/stage20-slice3-inspector-de-uso` | `InspectorDeUso`: metadata walk, three buckets, two carve-outs, generated statement, `IModel` exposure; the completeness test and the per-bucket suite. **No caller — inert** | ~470 |
| 4 | `feat/stage20-slice4-bajas-api` | Three DELETE routes; `EliminarAsync` ×3; cascade; structural minimums; the `Usuario` guard; the six 409 codes; the label dictionary; end-to-end + RLS tests | ~480 |
| 5 | `feat/stage20-slice5-bajas-web` | Delete buttons + confirmation on the four screens; `codigo`→copy mapping; docs 09/10 note | ~330 |

Merge order `1 → 2 → 3 → 4 → 5`. **Slices 1-2 (Part A) and 3-5 (Part B) are independent**: Part A
ships standalone value if Part B stalls, and slice 3 is deliberately inert so the guard can be
reviewed on its own merits before anything can call it.

**Pre-approved degradation**, in priority order:

1. **If slice 4 overflows** — split `4a` (tenant + empresa + cascade + minimums) and `4b` (punto de
   venta + the `Usuario` guard).
2. **If slice 3 overflows** — split `3a` (metadata walk + buckets + completeness test) and `3b` (the
   generated statement + carve-outs).
3. **If slice 2 overflows** — split `2a` (names and counts) and `2b` (filters).
4. **Never degraded**: the completeness test, the two carve-out tests, the "one article blocks"
   test, and the zero-physical-delete scan. A guard without those is a guard nobody should trust.

**Review Workload Forecast (preliminary — `sdd-tasks` produces the binding one)**

- Estimated total: **~2 080 lines** across 5 slices, calibrated against stages 13-18 (which came in
  1.5-3× naive estimates because test depth inflates a slice). This stage's inflators are the guard's
  test matrix and the four new Vitest files; it has **no** schema, no concurrency and no protocol
  work, so the multiplier should sit at the low end. Realistic outturn: **5-6 PRs**.
- `Decision needed before apply: Yes` — the owner ratifies the **zero-schema** gate before slice 1.
- `Chained PRs recommended: Yes` — `chain_strategy: stacked-to-main`
- `800-line budget risk: Low` — every slice is estimated at roughly half the budget, and three split
  points are pre-authorized.
- `size:exception` anticipated: **No**.

## Tensiones con el explore

| # | Explore position | Verdict |
|---|---|---|
| 1 | Open question 1 — *"whether deleting a tenant should cascade … Exploration recommends yes"* | **Adopted, with the boundary the explore did not draw** (decision 2): cascade covers empresas, puntos de venta and usuarios — **not** the rest of the provisioning template, because `EstadoTenant.Baja`'s own doc-comment promises *"los datos quedan para exportación"* |
| 2 | Open question 2 — structural minimums, *"recommends yes, as a separate named 409"* | **Adopted, plus an ordering rule** (decision 3): the minimum is checked **before** usage, because its message is the more actionable one and the pristine single-empresa case is answered by the cascade |
| 3 | Open question 3 — *"whether `Usuario` deletion should gain the same dependent guard"* | **Adopted, with a position stated** (decision 5): the guard goes **after** `PoliticaDeRoles`, and the consequence — a provisioned admin stops being deletable the moment it operates — is named rather than discovered later |
| 4 | *"An entity is pristine iff no dependent row exists whose `CreatedAt` is strictly greater"* (`:100-102`) | **Ratified, and found to be incomplete.** **15 domain types have no `created_at` at all** (`Stock`, `ArticuloEmpresa`, `MovimientoStock`, the four `Numeracion*`, `Auditoria`, …), so the discriminator cannot be applied uniformly. Decision 4 adds the three buckets and the completeness test that makes the gap fail loudly |
| 5 | *"`numeraciones_clientes` … is not an `EntidadBase` and is therefore outside the discriminator entirely"* (`:107-110`) | **Ratified and promoted.** In decision 4's scheme it is not merely "outside" — it is the **only** untimestamped type provisioning creates, so it is one of exactly two named carve-outs, with its own test |
| 6 | `GetReferencingForeignKeys()` selected because *"an unmapped case blocks deletion rather than permitting it"* (`:161`) | **Ratified, and the property made verifiable** (decision 4): the fail-safe claim is only true if an unmapped case is **detected**, so the completeness test is what converts the argument into a guarantee |
| 7 | *"`Empresa` and `PuntoVenta` have no `Estado` field. Only `Tenant` has one"* (`:39-41`) | **Left as is**, per the explore's own out-of-scope §7 — and decision 1 uses the asymmetry productively: tenant deletion becomes `EstadoTenant.Baja`'s first writer, retiring a latent enum value instead of adding two new state fields |

**New material the explore did not raise**: that `EstadoTenant.Baja` is a **latent value with zero
writers** and this stage is its natural writer (decision 1); that soft-deleted login already degrades
correctly to 403 through the existing `tenant is null` branch (decision 1); that **15 types carry no
timestamp**, which breaks the uniform discriminator (decision 4); that raw SQL gives the *desired*
filter-bypass semantics for free while RLS still applies (decision 6); that `IWaysDbContext` exposes
`DatabaseFacade` but not `IModel`, and the interface's own doc-comment supplies the argument for
adding it (decision 6); that nullable `IdEmpresa` shared-catalogue rows need no special case
(decision 4); that **no Vitest file exists for any of the four screens**, so Part A creates coverage
rather than extending it; and that `db-error-backstops` is structurally inapplicable here, which
promotes the guard's tests from good practice to sole defence (Approach, Risks).

## Proposal question round

Execution mode is `auto`, so these were resolved rather than asked. Each records its assumption so a
correction is cheap. **None blocks spec or design.**

1. **Should a tenant be forced to keep at least one administrator?** Assumed **no rule added**
   (decision 3): `PoliticaDeRoles` already blocks self-deletion and Root targets, so the reachable
   failure is narrow. Inventing a role invariant autonomously is worse than naming the gap. If the
   owner wants it, it is one more pre-check and one more 409 (`ultimo_admin_del_tenant`).
2. **Should a customer who loaded data and then deleted it become deletable again?** Assumed **no**
   (decision 6, side effect A): usage is *"ever operated"*, not *"has live data"*. This is the
   strictest reading of B2 and the one that fails safe. Reversing it is one `AND deleted_at IS NULL`.
3. **Should the platform have a force-delete override?** Assumed **no** — suspension is the existing
   lever, and an override needs an audit ceremony this stage would be designing blind. Registered
   out-of-scope with its reopen condition.
4. **Should the tenant cascade sweep the whole provisioning template** (areas, medios de pago, lista
   de precios, Consumidor Final)? Assumed **no** (decision 2): those rows are invisible and
   unreachable once the tenant is gone, and the enum's own doc-comment promises the data stays for
   export.
5. **Should the root screens navigate from a tenant into its children?** Assumed **counts only** —
   drill-down is a navigation feature, not a relationship-projection one, and it would pull routing
   into a stage that otherwise touches four existing screens.
6. **Does the operator need to see already-deleted rows** (a "show deleted" toggle)? Assumed **no**:
   the `"BajaLogica"` filter hides them and `IgnoreQueryFilters(["BajaLogica"])` exists for the day
   the answer changes. A restore feature would be its own change (out of scope).
