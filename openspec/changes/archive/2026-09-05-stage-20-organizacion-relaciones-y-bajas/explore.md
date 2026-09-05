# Explore — stage-20: organization relationships and logical deletion

Date: 2026-09-04
Trigger: owner testing the running application reported that the root (platform) admin UI shows
tenants, empresas, puntos de venta and usuarios as four unrelated flat lists, and that nothing can
be deleted or disabled except suspending a tenant.

This document records what was verified in the codebase, not what was assumed.

---

## 1. Reported symptoms, verified

### 1.1 Relationships are in the model but never projected

| Screen | What the DTO carries | What the screen renders |
|---|---|---|
| `Empresas.tsx` | `EmpresaListado.idTenant` (`api/tipos.ts:310-316`) | raw integer, `Empresas.tsx:156` — no tenant name |
| `PuntosVenta.tsx` | `PuntoVentaListado.idTenant`, `.idEmpresa` (`api/tipos.ts:320-331`) | raw integers, `PuntosVenta.tsx:216-217` — no names |
| `Usuarios.tsx` | `UsuarioListado` (`api/tipos.ts:23-32`) has **no** tenant/empresa/PV field at all | no tenant column exists |
| `Tenants.tsx` | `TenantListado` (`api/tipos.ts:301-306`) — id, nombre, estado, createdAt | no child counts, no navigation to children |

None of the three list screens offers a filter by tenant, and `PuntosVenta.tsx` offers no filter by
empresa. The hierarchy defined in `docs/09-multi-tenancy.md` (tenant -> empresa -> punto de venta)
is therefore invisible to a platform operator.

The data is present server-side; the gap is projection, not schema.

### 1.2 Deletion does not exist for organization entities

`OrganizacionEndpoints.cs:12-13` states that create and delete are platform-only via
`ServicioDeAprovisionamiento` (ADR-16), and that the absence of `POST`/`DELETE` in that file is
deliberate. However `AprovisionamientoEndpoints.cs` exposes **only** `POST /api/plataforma/tenants`.
There is no `DELETE` for tenant, empresa or punto de venta anywhere in the API surface. The delete
path the comment defers to was never implemented.

Additionally:

- `Empresa` (`src/Ways.Domain/Organizacion/Empresa.cs`) and `PuntoVenta` have no `Estado` field.
  Only `Tenant` has one (`EstadoTenant`, with suspend/reactivate endpoints), which is exactly the
  asymmetry the owner observed.
- `Usuario` **does** already support logical deletion: `DELETE /api/usuarios/{id}`
  (`UsuariosEndpoints.cs:57`), wired in `Usuarios.tsx:120-122` behind a "Baja" button, plus an
  `Inactivo`/`Bloqueado` state. The owner's report is inaccurate on this one point; the button is
  conditionally hidden for Root targets, for self, and when `puedeEditar` is false.

### 1.3 Soft-delete plumbing already exists

`EntidadBase` (`src/Ways.Domain/Common/EntidadBase.cs`) declares `CreatedAt`, `UpdatedAt` and
`DeletedAt` on every persisted entity, and `WaysDbContext.AplicarFiltroDeBajaLogica`
(`WaysDbContext.cs:426-447`) installs a `"BajaLogica"` query filter on every `EntidadBase`
descendant. Tenants, empresas and puntos de venta already have `deleted_at` columns and are already
filtered. **No migration is required to delete them logically** — only endpoints, a guard, and UI.

---

## 2. Dependency graph (input to the guard design)

### 2.1 Delete behaviour gives no protection

Every FK in `WaysDbContext`/`Configuraciones/*.cs` is `DeleteBehavior.Restrict`, and there is no
cascade anywhere. But **every delete in this codebase is logical** (`UPDATE ... SET deleted_at`),
which never triggers a Postgres FK check. `Restrict` therefore contributes zero protection to the
requested behaviour. Any "blocked when in use" rule must be an explicit application-level check.

### 2.2 Referencing entities

- **Tenant** — referenced by essentially every `EntidadTenant` descendant via `IdTenant`
  (~40 tables spanning structural config, user catalogs and operational records).
- **Empresa** — `PuntoVenta.IdEmpresa` (not null), `ArticuloEmpresa`, `CertificadoFiscal`,
  `Parametro` (not null); `Cliente`, `Proveedor`, `Oferta`, `ConfiguracionDeCatalogo<T>` (nullable,
  where null means "shared catalog row").
- **PuntoVenta** — 18 referencing FKs including `ComprobanteVenta`, `ComprobanteCompra`,
  `TurnoCaja`, `Stock`, `StockLote`, `MovimientoStock` (two FKs: `IdPuntoVenta` and
  `IdPuntoVentaDestino`), `NumeracionComprobante` and `NumeracionFiscal` (PK members), `Presupuesto`,
  `Remito`, `OrdenCompra`, `Gasto`, `MovimientoTesoreria`, `MovimientoCuentaCorriente`, `Auditoria`.
- **Usuario** — referenced as actor/employee by `Auditoria.IdActor` and by `IdEmpleado` /
  `IdEmpleadoApertura` / `IdEmpleadoCierre` on comprobantes, cajas, movimientos, presupuestos,
  remitos and ordenes de compra. `Usuario.IdTenant` is nullable (null = platform staff) and
  `Usuario` does not inherit `EntidadTenant`.

### 2.3 A provisioned tenant is not empty

`ServicioDeAprovisionamiento.CrearTenantAsync` (`ServicioDeAprovisionamiento.cs:28-170`) creates, in
one transaction: 1 tenant, 1 empresa, 1 punto de venta, 1 area ("General"), 2 medios de pago
("Efectivo", "Transferencia"), 1 lista de precios ("General", default), 1 cliente
("Consumidor Final", numero 1), 1 `numeraciones_clientes` counter row, and 1 `admin` usuario.

Therefore "never used" **cannot** mean "zero referencing rows". Every freshly provisioned tenant
already has nine dependent rows.

### 2.4 The discriminator: a single clock reading

`ServicioDeAprovisionamiento.cs:46` reads the clock **once** (`var ahora = reloj.Ahora;`) and stamps
that identical instant onto `CreatedAt`/`UpdatedAt` of every row it creates, including the `Tenant`
row itself (lines 53-54, 69-70, 79-80, 89-90, 104-105, 121-122, 142-143, 158-160).

This yields an exact, template-version-independent discriminator:

> An entity is **pristine** if and only if no dependent row exists whose `CreatedAt` is strictly
> greater than that entity's own `CreatedAt`.

Everything born with the provisioning template shares the tenant's timestamp and is excluded.
Anything the user created afterwards — an article, a client, a supplier, a sale, a second punto de
venta — is strictly later and counts as usage.

Caveat recorded: `numeraciones_clientes` is inserted by raw SQL in
`AsignadorDeNumeroCliente.AsegurarContadorAsync` and carries no `CreatedAt`; it is not an
`EntidadBase` and is therefore outside the discriminator entirely (it is a counter row, not user
data).

---

## 3. Existing conventions to follow

### 3.1 No existing delete checks dependents

`ServicioDeUsuarios.EliminarAsync` (`ServicioDeUsuarios.cs:266-286`),
`ServicioDeCatalogo<T,...>.EliminarAsync` (`ServicioDeCatalogo.cs:106-114`),
`ServicioDeClientes.EliminarAsync`, `ServicioDeProveedores.EliminarAsync`,
`ServicioDeArticulos.EliminarAsync` and `ServicioDeOfertas.EliminarAsync` all stamp `DeletedAt` with
no dependent check. Guards that do exist are about protected rows ("Consumidor Final") and role
policy, never about usage. This change introduces the first dependent guard in the codebase.

### 3.2 Error shape

`ManejadorDeErrores` (`src/Ways.Api/Seguridad/ManejadorDeErrores.cs:13-87`) maps `ErrorDominio` to
`(EstadoHttp, Message, Codigo)`. The idiomatic rejection shape is a pre-check throwing
`ErrorDominio.Conflicto("<codigo_snake_case>", "<mensaje>")` -> HTTP 409, as
`ServicioDeUsuarios.ExigirDisponibilidadAsync` does for `usuario_duplicado` / `mail_duplicado`.
The front end receives it as `ErrorApi { estado, codigo, mensaje }` (`api/cliente.ts:8-16, 60-63`).

Note for the `db-error-backstops` skill: that skill's SQLSTATE backstop does not apply here, because
no physical DELETE occurs and no Postgres constraint can fire. The application guard is the sole
line of defence, so its tests carry the whole burden.

### 3.3 Role policy on user deletion

`PoliticaDeRoles.ValidarPuedeIntervenirSobre` (`PoliticaDeRoles.cs:59-81`): actor must be Root or
Admin; a Root target may only be touched by Root and may never be deleted; self-deletion is
forbidden. `ValidarAlcanceDeTenant` (lines 85-108) returns 404 rather than 403 for out-of-scope
access, deliberately, to avoid an existence oracle (ADR-8).

### 3.4 Test layout

- Domain unit: `tests/Ways.Domain.Tests/<Area>/<Class>Tests.cs`
- Application unit: `tests/Ways.Application.Tests/<Area>/<Class>Tests.cs` — closest templates
  `Organizacion/ServicioDeOrganizacionTests.cs`, `Usuarios/ServicioDeUsuariosTests.cs`
- Integration: flat under `tests/Ways.IntegrationTests/`, `[Collection("Ways.IntegrationTests secuencial")]`
  — closest templates `OrganizacionTests.cs`, `BackstopClientesYProveedoresTests.cs`
- Web: colocated `*.test.tsx` next to the component (Vitest + RTL)

---

## 4. Options considered for the guard

| Option | Coverage | Failure direction | Verdict |
|---|---|---|---|
| Enumerate dependent tables by hand | ~40 `AnyAsync` calls for tenant alone | **Unsafe** — a table added in a future stage and forgotten makes a used tenant deletable, silently | rejected |
| Reflect over `EntidadTenant` descendants | tenant only; does not generalise to empresa/PV/usuario whose FK property names vary (`IdEmpleado`, `IdEmpleadoApertura`, `IdActor`, `IdPuntoVentaDestino`) | safe | partial, rejected |
| Query EF's own FK metadata via `IEntityType.GetReferencingForeignKeys()` | complete and automatic for all four entities, including FKs added later | **Safe** — an unmapped case blocks deletion rather than permitting it | **selected** |

The selected option follows a pattern already idiomatic in this codebase: `AplicarFiltroDeBajaLogica`
and `AplicarFiltroDeTenantEnTenant` (`WaysDbContext.cs:426-488`) already walk the EF model by
reflection to install behaviour uniformly.

---

## 5. Owner decisions taken during exploration

1. **Physical deletion is never permitted.** All deletion remains logical (`DeletedAt`), consistent
   with the `EntidadBase` convention "las bajas son siempre lógicas".
2. **Anything the user loaded counts as usage.** Not only operational records: articles, clients,
   suppliers and any other user-created row block deletion. Only the provisioning baseline does not.
   This replaced an earlier proposal that would have let a tenant with 3000 articles and zero sales
   remain deletable.
3. **Audit rows do not block.** `Auditoria.IdActor`/`IdPuntoVenta` are a trail *about* the entity,
   not data the user operated; and because deletion is logical the referenced row survives, so the
   trail keeps rendering. Recorded as an explicit carve-out, to be re-confirmed at spec time.

## 6. Open questions for the proposal phase

- Whether deleting a tenant should cascade the logical deletion to its children (otherwise orphaned
  empresas remain visible in the root list). Exploration recommends yes.
- Whether structural minimums should be enforced: a tenant must keep at least one empresa, an
  empresa at least one punto de venta. Exploration recommends yes, as a separate named 409.
- Whether `Usuario` deletion should gain the same dependent guard it currently lacks, or keep its
  present unguarded behaviour. Exploration recommends aligning it with the other three.

## 7. Out of scope

- Any change to `Estado`/suspension semantics for empresa or punto de venta. The owner asked for
  deletion, not disabling; `Tenant.Estado` stays as it is.
- Any schema migration. The change is designed to require none; if design discovers one is needed,
  the project's mandatory DB gate applies and work stops for owner approval.
- The unfinished `stage-18-etiquetas-y-consulta` change, which is blocked on a physical measurement
  by the owner and is untouched by this work.
