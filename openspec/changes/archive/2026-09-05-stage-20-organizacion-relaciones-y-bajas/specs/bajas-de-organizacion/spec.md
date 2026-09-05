# Bajas De Organizacion Specification

## Purpose

Defines what logical deletion means for the organization hierarchy (tenant, empresa, punto de
venta, usuario): the pristine/used discriminator and the single provisioning instant that makes it
exact; how the dependent set is discovered and why a hand-maintained list is forbidden; the three
classification buckets, the completeness rule and the two named carve-outs; the cascade rule and
its boundary; the structural minimums; the complete 409 code set; and the invariant that no row is
ever physically deleted.

This capability describes the deletion *semantics*. The API surface that exposes them lives in
`tenant-organization` and `usuarios-tenant-scoping`.

## Requirements

### Requirement: Deletion Is Always Logical, Never Physical

Deleting a tenant, empresa, punto de venta or usuario MUST be performed by stamping `DeletedAt`
(the `EntidadBase` soft-delete convention). The system MUST NOT issue any physical deletion —
`DELETE FROM`, `ExecuteDelete`, `ExecuteDeleteAsync`, `Remove(`, `RemoveRange(` — against
`tenants`, `empresas`, `puntos_venta` or `usuarios`. A physical delete on any of those four tables
is a violation of this specification, not an implementation choice.

Because the deletion is an `UPDATE`, no Postgres foreign-key constraint can ever fire against it:
`DeleteBehavior.Restrict` contributes zero protection, and the application-level guard specified
below is the sole line of defence.

#### Scenario: A deleted row survives in the database
- GIVEN a pristine tenant that is deleted through the platform surface
- WHEN the `tenants` table is read with the `"BajaLogica"` query filter ignored
- THEN the row still exists, with `deleted_at` set to the deletion instant

#### Scenario: The deleted row is invisible to every normal read
- GIVEN the same deleted tenant
- WHEN `GET /api/plataforma/tenants` is called
- THEN the tenant is absent from the listing

#### Scenario: A second deletion of the same row is a clean 404
- GIVEN an entity that has already been logically deleted
- WHEN a second DELETE is issued for the same id
- THEN the response is `404` (the row is invisible to the guard's own lookup), never `500`
- AND no second `deleted_at` write occurs

#### Scenario: The repository contains no physical delete over the four tables
- GIVEN the complete diff of this change
- WHEN the repository is scanned for `ExecuteDelete`, `Remove(`, `RemoveRange(` and `DELETE FROM`
  targeting `tenants`, `empresas`, `puntos_venta` or `usuarios`
- THEN zero matches are found

### Requirement: The Pristine Discriminator Is A Strict Timestamp Comparison Against The Entity's Own CreatedAt

An entity MUST be considered **pristine** if and only if no dependent row exists whose `CreatedAt`
is **strictly greater** than that entity's own `CreatedAt` (the *anchor*), subject to the bucket
rules below. The comparison MUST be strict (`>`), never `>=`: `ServicioDeAprovisionamiento` reads
the clock exactly once and stamps that identical instant on every provisioned row including the
tenant itself, so the whole provisioning baseline is equal to the anchor and MUST NOT block.

The discriminator MUST be template-version independent: it MUST NOT be expressed as a hardcoded
row count, a hardcoded table list, or a hardcoded knowledge of what the provisioning template
contains.

#### Scenario: A freshly provisioned tenant is pristine and deletable
- GIVEN a tenant provisioned seconds ago, with its empresa, punto de venta, area "General", two
  medios de pago, the General price list, the Consumidor Final cliente, its numeraciones_clientes
  counter row and its admin usuario
- WHEN the platform deletes that tenant
- THEN the deletion succeeds — all nine provisioned rows share the tenant's `created_at` and none
  is strictly greater than the anchor

#### Scenario: A dependent created at exactly the anchor instant does not block
- GIVEN a dependent row whose `created_at` is byte-identical to the entity's `created_at`
- WHEN the guard evaluates the entity
- THEN that row is not counted as usage — the comparison is `>`, not `>=`

#### Scenario: A dependent created one tick after the anchor blocks
- GIVEN a dependent row whose `created_at` is one tick later than the entity's `created_at`
- WHEN the guard evaluates the entity
- THEN the entity is not pristine and its deletion is refused

#### Scenario: Breaking the single-clock-reading property is detected
- GIVEN a change that made provisioning read the clock more than once, splitting the baseline into
  two instants
- WHEN the "a freshly provisioned tenant is deletable" regression test runs
- THEN it fails, naming the provisioned dependent that is now strictly later than the anchor

### Requirement: In Use Means Anything The Customer Created Beyond The Provisioning Baseline

"In use" MUST mean that the customer created or operated **anything** past the provisioning
baseline — catalog data as much as transactional records. Articles, clients, suppliers, price
lists, a second punto de venta, a second usuario, a sale, a stock movement: all of them MUST block.
The guard MUST NOT restrict itself to operational or transactional records.

#### Scenario: One article makes the tenant, its empresa and its punto de venta undeletable
- GIVEN a freshly provisioned tenant in which the customer loaded exactly one article, with no
  sale, no stock movement and no shift
- WHEN the platform attempts to delete the tenant, then the empresa, then the punto de venta
- THEN each attempt is refused with its own named 409: `tenant_en_uso`, `empresa_en_uso`,
  `punto_venta_en_uso`

#### Scenario: A catalog-only tenant with thousands of rows and zero sales is not deletable
- GIVEN a tenant with 3 000 articles and zero comprobantes
- WHEN the platform attempts to delete it
- THEN it is refused with `tenant_en_uso`

#### Scenario: A second punto de venta blocks its empresa
- GIVEN an empresa whose provisioned punto de venta was joined by a second one created later
- WHEN the platform attempts to delete the empresa
- THEN it is refused with `empresa_en_uso`

### Requirement: The Dependent Set Is Discovered From EF Metadata, Never From A Hand-Maintained List

The set of types and foreign keys that can block a deletion MUST be derived at runtime from the EF
Core model (`IEntityType.GetReferencingForeignKeys()`) for each of `Tenant`, `Empresa`,
`PuntoVenta` and `Usuario`. The system MUST NOT enumerate blocking tables in a hand-maintained
list, because a hand list fails unsafe: one forgotten table silently makes a used entity deletable.

Discovery MUST cover every referencing foreign key regardless of its property name or shape,
including secondary FKs to the same principal, FKs whose property name does not follow the
`Id<Entity>` pattern, and composite `(id, id_tenant)` keys.

#### Scenario: A secondary FK to the same principal is discovered
- GIVEN a `movimientos_stock` row created after the anchor that references a punto de venta only
  through `id_punto_venta_destino`
- WHEN that punto de venta's deletion is attempted
- THEN it is refused with `punto_venta_en_uso`

#### Scenario: Non-conventional FK property names are discovered
- GIVEN a `turnos_caja` row created after the anchor that references a usuario only through
  `id_empleado_cierre`
- WHEN that usuario's deletion is attempted
- THEN it is refused with `usuario_en_uso`

#### Scenario: A referencing type added by a future stage is covered without editing the guard
- GIVEN a new entity added to the EF model with a foreign key to `PuntoVenta`
- WHEN the guard runs
- THEN that new type participates in the evaluation with no change to the guard's own source

### Requirement: Every Referencing Type Is Classified Into Exactly One Bucket Or The Build Fails

The timestamp discriminator is **not** universally applicable: some referencing types do not
inherit `EntidadBase` and therefore have no `created_at` column at all (verified:
`Stock`, `ArticuloEmpresa`). Every type discovered by metadata MUST therefore be classified into
**exactly one** of three buckets:

| Bucket | Rule |
|---|---|
| 1 — timestamped | An `EntidadBase` descendant. Blocks iff a referencing row exists with `created_at > anchor` |
| 2 — untimestamped | Not an `EntidadBase` descendant. Blocks iff **any** referencing row exists |
| 3 — carve-out | An explicitly named type with a written reason. Never blocks |

An automated test MUST enumerate `GetReferencingForeignKeys()` for all four principals and assert
that every discovered type falls into exactly one bucket. A type that falls into **none** MUST fail
that test, naming the type. A type that falls into **more than one** MUST also fail it, naming the
type. The system MUST NOT skip, ignore or default an unclassified referencing type under any
circumstance — silently skipping it would let a used entity be deleted, which is the exact failure
this requirement exists to make impossible.

This completeness test is the mechanism that converts "the guard fails safe" from a claim into a
guarantee. It MUST NOT be weakened, conditionally skipped, or degraded.

#### Scenario: The completeness test covers all four principals
- GIVEN the current EF model
- WHEN the completeness test runs
- THEN it enumerates the referencing foreign keys of `Tenant`, `Empresa`, `PuntoVenta` and
  `Usuario`, and every discovered CLR type is classified into exactly one bucket

#### Scenario: An unclassifiable type turns the build red, naming it
- GIVEN a future stage adds a referencing entity the classification rules cannot place in any
  bucket
- WHEN the completeness test runs
- THEN it fails with a message naming that exact type — the type is never silently skipped

#### Scenario: A type claimed by two buckets turns the build red
- GIVEN a type that is both listed as a carve-out and matched by another bucket's rule
- WHEN the completeness test runs
- THEN it fails, because classification must be into exactly one bucket

#### Scenario: An untimestamped dependent blocks on mere existence
- GIVEN a punto de venta with one `stock` row (a type with no `created_at` column)
- WHEN its deletion is attempted
- THEN it is refused with `punto_venta_en_uso` — existence alone is usage for bucket 2

#### Scenario: An untimestamped type cannot be evaluated with the timestamp rule
- GIVEN a bucket-2 type such as `ArticuloEmpresa`
- WHEN the guard builds its evaluation for that type
- THEN no `created_at` comparison is applied to it, because the column does not exist

### Requirement: There Are Exactly Two Carve-Outs, Each With A Written Reason And Its Own Test

The carve-out bucket MUST contain exactly two members:

| Type | Reason |
|---|---|
| `Auditoria` | The audit trail is a record *about* the entity, not data the customer operated. Because deletion is logical the referenced row survives, so the trail keeps rendering |
| `NumeracionCliente` | The provisioning counter row (`numeraciones_clientes`), inserted by `AsignadorDeNumeroCliente`; it is not an `EntidadBase` and not customer data |

Audit rows MUST never block a deletion. Adding a third carve-out MUST require an explicit change to
this specification; the carve-out list MUST NOT grow silently.

#### Scenario: Audit rows alone do not block
- GIVEN a pristine tenant whose only dependents created after its anchor are `auditoria` rows
- WHEN the platform deletes the tenant
- THEN the deletion succeeds

#### Scenario: The provisioning counter row alone does not block
- GIVEN a pristine tenant whose only untimestamped dependent is its provisioned
  `numeraciones_clientes` row
- WHEN the platform deletes the tenant
- THEN the deletion succeeds

#### Scenario: The carve-out list is asserted to have exactly two members
- GIVEN the guard's carve-out list
- WHEN its test runs
- THEN it asserts the list contains exactly `Auditoria` and `NumeracionCliente` and nothing else

### Requirement: A Soft-Deleted Dependent Still Blocks

Usage MUST mean "the customer ever operated here", not "there is live data right now". A dependent
row that the customer created and later logically deleted MUST still block. The guard's evaluation
MUST therefore not be subject to the `"BajaLogica"` query filter.

#### Scenario: A deleted article still blocks its tenant
- GIVEN a tenant in which the customer created one article and then deleted it logically
- WHEN the platform attempts to delete the tenant
- THEN it is refused with `tenant_en_uso`

#### Scenario: Row-level security still applies to the guard
- GIVEN a tenant admin deleting an entity of their own tenant
- WHEN the guard evaluates dependents
- THEN it sees every dependent of that tenant and cannot under-count
- AND it can never observe a dependent belonging to another tenant

### Requirement: A Shared-Catalog Row With A NULL Owner Does Not Block

Rows whose owning foreign key is `NULL` by design — `Cliente`, `Proveedor`, `Oferta` and
`ConfiguracionDeCatalogo<T>` use `id_empresa IS NULL` to mean "shared catalog row" — MUST NOT block
the deletion of any specific empresa.

#### Scenario: A shared catalog row does not block an empresa
- GIVEN a shared catalog row with `id_empresa IS NULL`, created after the empresa's anchor
- WHEN that empresa's deletion is attempted
- THEN the shared row does not contribute to the usage verdict

### Requirement: Cascade Is Bounded To The Organization Projection And Shares One Instant

Deleting a tenant MUST logically delete, in the same transaction, its empresas, its puntos de venta
and its usuarios. Deleting an empresa MUST logically delete its puntos de venta. The parent and
every cascaded child MUST receive **the same** `DeletedAt` instant, obtained from a single clock
reading, so that a manual restore is unambiguous.

The cascade MUST NOT extend beyond those three child types. The rest of the provisioning template —
areas, medios de pago, listas de precio, the Consumidor Final cliente, numeraciones — MUST be left
untouched, because those rows are already invisible and unreachable once the tenant is gone and
`EstadoTenant.Baja` promises the data stays available for export.

#### Scenario: No orphan remains visible after a tenant is deleted
- GIVEN a pristine tenant with one empresa, one punto de venta and one admin usuario
- WHEN the platform deletes the tenant
- THEN `GET /api/empresas`, `GET /api/puntos-venta` and `GET /api/usuarios` return none of those
  rows for a platform actor — nothing points at a tenant that no longer resolves

#### Scenario: Parent and children share one deletion instant
- GIVEN the same deletion
- WHEN the four rows are read with query filters ignored
- THEN all four carry an identical `deleted_at` value

#### Scenario: The rest of the provisioning template survives
- GIVEN the same deletion
- WHEN `areas`, `medios_pago`, `listas_precio`, `clientes` and `numeraciones_clientes` of that
  tenant are read with query filters ignored
- THEN their rows are present with `deleted_at IS NULL`

#### Scenario: Deleting an empresa cascades only to its puntos de venta
- GIVEN a pristine empresa with one punto de venta, inside a tenant that has another empresa
- WHEN the platform deletes that empresa
- THEN the empresa and its punto de venta are logically deleted with the same instant, and the
  tenant and its usuarios are untouched

#### Scenario: An already-deleted child keeps its original deletion instant
- GIVEN a pristine tenant whose only empresa was already logically deleted earlier
- WHEN the platform deletes the tenant
- THEN the deletion succeeds and the already-deleted empresa keeps its **original** `deleted_at` —
  the cascade MUST NOT re-stamp a row that is already logically deleted

#### Scenario: The cascade never runs over used data
- GIVEN a tenant with any dependent past its anchor
- WHEN its deletion is attempted
- THEN it is refused before any cascade is attempted — the cascade only ever runs over a pristine
  provisioning baseline

### Requirement: Structural Minimums Are Checked Before The Usage Guard, With Their Own Named Codes

A tenant MUST always keep at least one empresa, and an empresa MUST always keep at least one punto
de venta. Deleting the last one MUST be refused with `ultima_empresa_del_tenant` and
`ultimo_punto_venta_de_la_empresa` respectively — never with a generic "in use" code, because the
two mean opposite things to the operator ("there is data here" versus "delete the parent instead").

The structural minimum MUST be evaluated **before** the usage guard. The count MUST consider only
rows that are not logically deleted.

#### Scenario: Deleting a tenant's only empresa is refused
- GIVEN a pristine tenant with exactly one empresa
- WHEN the platform attempts to delete that empresa
- THEN it is refused with `409 ultima_empresa_del_tenant`
- AND the tenant itself remains deletable, taking the empresa with it through the cascade

#### Scenario: Deleting one of two empresas succeeds
- GIVEN a tenant with two pristine empresas
- WHEN the platform deletes one of them
- THEN the deletion succeeds

#### Scenario: Deleting an empresa's only punto de venta is refused
- GIVEN an empresa with exactly one punto de venta
- WHEN the platform attempts to delete that punto de venta
- THEN it is refused with `409 ultimo_punto_venta_de_la_empresa`

#### Scenario: Already-deleted siblings do not count towards the minimum
- GIVEN a tenant that had two empresas, one of which was already logically deleted
- WHEN the platform attempts to delete the remaining empresa
- THEN it is refused with `409 ultima_empresa_del_tenant` — the deleted sibling is not counted as a
  surviving empresa

#### Scenario: The structural minimum wins over the usage verdict
- GIVEN a tenant's only empresa, which also has usage past its anchor
- WHEN its deletion is attempted
- THEN the response is `409 ultima_empresa_del_tenant`, not `409 empresa_en_uso`

### Requirement: The Complete 409 Code Set Is Exactly Six Codes

Every refusal introduced by this capability MUST be raised as
`ErrorDominio.Conflicto("<codigo_snake_case>", "<mensaje>")`, mapped to HTTP `409` and surfaced to
the front end as `ErrorApi { estado, codigo, mensaje }`. The complete set is:

| Code | Raised when |
|---|---|
| `tenant_en_uso` | The tenant has a blocking dependent past the provisioning baseline |
| `empresa_en_uso` | Idem, empresa |
| `punto_venta_en_uso` | Idem, punto de venta |
| `usuario_en_uso` | Idem, usuario |
| `ultima_empresa_del_tenant` | Deleting the tenant's only surviving empresa |
| `ultimo_punto_venta_de_la_empresa` | Deleting the empresa's only surviving punto de venta |

The message SHOULD name the blocking data in plain Spanish and MUST degrade to a generic phrase
when the blocking table has no label. The `codigo` MUST always be exact regardless of the message,
and the web MUST key its copy off `codigo`, never off `mensaje`.

#### Scenario: Each code fires on its exact condition and no other
- GIVEN one fixture per code, constructed to satisfy only that code's condition
- WHEN each deletion is attempted
- THEN each returns HTTP `409` with exactly its own `codigo`

#### Scenario: An unlabelled blocking table still yields the exact code
- GIVEN a blocking table with no entry in the message label dictionary
- WHEN the deletion is refused
- THEN the `codigo` is exact and the `mensaje` degrades to a generic phrase

#### Scenario: The web maps copy from the code
- GIVEN a `409` response carrying `codigo = "ultima_empresa_del_tenant"`
- WHEN the front end renders the failure
- THEN the copy it shows is selected by `codigo`, and changing `mensaje` does not change which copy
  is selected

### Requirement: Deletion Never Becomes A Cross-Tenant Existence Oracle

A tenant-scoped actor attempting to delete an entity outside their own tenant MUST receive `404`,
never `403` and never a `409` that discloses the entity's state. This preserves
`PoliticaDeRoles.ValidarAlcanceDeTenant`'s deliberate anti-existence-oracle behaviour (ADR-8).

#### Scenario: An out-of-scope delete is indistinguishable from a non-existent id
- GIVEN an admin of tenant 1 and an empresa belonging to tenant 2
- WHEN the admin issues a DELETE for that empresa's id
- THEN the response is `404`, identical in status and body shape to a DELETE for an id that does
  not exist at all

#### Scenario: An out-of-scope used entity does not leak its usage
- GIVEN an admin of tenant 1 and a heavily used empresa belonging to tenant 2
- WHEN the admin issues a DELETE for that empresa's id
- THEN the response is `404`, never `409 empresa_en_uso`
