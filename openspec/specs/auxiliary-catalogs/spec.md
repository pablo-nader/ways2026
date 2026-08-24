# Auxiliary Catalogs Specification

## Purpose

Defines ABM behavior for the doc 10 §1 padrones: `areas`, `categorias`,
`marcas`, `grupos`, `medios_pago` (tenant-scoped catalogs) and
`condiciones_fiscales`, `alicuotas_iva`, `tipos_comprobante`
(platform-global, read-only for tenants).

## Requirements

### Requirement: Catalog ABM Lifecycle

Each tenant-scoped catalog (`areas`, `categorias`, `marcas`, `grupos`,
`medios_pago`) MUST support list, create, edit, and soft-delete operations,
scoped by `id_tenant` with `id_empresa NULL` meaning shared across the
tenant's empresas.

#### Scenario: Create a catalog row

- GIVEN a tenant admin
- WHEN they create a new `marca` with a name
- THEN the row is persisted with the admin's `id_tenant` and `id_empresa = NULL`

#### Scenario: Soft delete hides but does not erase

- GIVEN an active `area` row
- WHEN the admin deletes it
- THEN `deleted_at` is set and the row no longer appears in the default list
- AND the row remains queryable via `IgnoreQueryFilters()` for audit

#### Scenario: Soft-deleted name is reusable

- GIVEN a soft-deleted `grupo` named "Ofertas"
- WHEN a new `grupo` is created with the same name
- THEN creation succeeds, per the partial unique index pattern (doc 08
  precedent)

### Requirement: Categoria Depth Limit

`categorias` MUST allow an unrestricted `id_categoria_padre` chain in the
schema, but the domain layer MUST reject any create or move operation that
would place a categoria deeper than 3 levels from its root.

#### Scenario: Depth within limit accepted

- GIVEN "Bebidas" (level 1) and "Gaseosas" (level 2)
- WHEN a categoria "Cola" is created under "Gaseosas" (level 3)
- THEN creation succeeds

#### Scenario: Depth exceeding limit rejected

- GIVEN "Cola" already sits at level 3
- WHEN a categoria "Cola 1.5L" is created under "Cola" (level 4)
- THEN the domain layer rejects the request with a validation error,
  independent of any UI check

### Requirement: Fiscal Catalogs Are Platform-Managed and Read-Only

`condiciones_fiscales`, `alicuotas_iva`, and `tipos_comprobante` MUST have
no `id_tenant` column (`[global]` scope per doc 10) and MUST expose
read-only endpoints to tenant users. No tenant-facing create/edit/delete ABM
exists for these in stage 1. As of this stage, `tipos_comprobante` ships
`PRE` with `activo = false` — deactivated by an idempotent data statement on
already-migrated databases and, independently, by an explicit
`Activo = false` on `TiposComprobanteBase` for the seeder, so a fresh
install never reopens the hole (the seeder runs only against an empty
database, after migrations) — and gains `TXR` (`clase venta`, `letra 'X'`,
`signo +1`, `discrimina_iva false`, `es_fiscal false`, `afecta_stock false`,
`activo true`), the itemless consolidation type for remitos (see `remitos`
capability). Both rows remain subject to the same read-only rule as every
other row in the padrón.
(Previously: silent on `PRE`'s deactivation and `TXR`'s addition — both
introduced by stage 17.)

#### Scenario: Tenant reads fiscal catalogs

- GIVEN a tenant user
- WHEN they list `alicuotas_iva`
- THEN they receive the platform-wide rows, identical across all tenants

#### Scenario: Tenant write attempt rejected

- GIVEN a tenant admin
- WHEN they call a create/edit/delete endpoint for `tipos_comprobante`
- THEN no such endpoint exists / the request is rejected (404 or 403)

#### Scenario: A freshly seeded database has PRE inactive

- GIVEN a brand-new tenant database seeded after this stage's migrations
- WHEN `tipos_comprobante` is read for `codigo = 'PRE'`
- THEN `activo = false`

#### Scenario: TXR is present, read-only, and non-fiscal

- GIVEN any tenant
- WHEN they list `tipos_comprobante`
- THEN a `TXR` row is present with `afecta_stock = false, es_fiscal = false`,
  and no tenant-facing write endpoint reaches it

### Requirement: codigo_afip Is Populated Via A Double Net, With No Schema Change

`tipos_comprobante`, `condiciones_fiscales`, and `alicuotas_iva` already carry `codigo_afip smallint
NULL` since Etapa 1, left NULL on purpose. This stage MUST populate it via **two independent nets**:
an idempotent data statement (`WHERE codigo_afip IS NULL`) for already-migrated databases, and a
seed change for fresh databases — each net tested independently, since the seeder only runs against
an empty table after migrations.

#### Scenario: An already-migrated database gets codigo_afip via the data statement
- GIVEN an existing database with `tipos_comprobante.codigo_afip IS NULL` on `FA`
- WHEN the migration's data statement runs
- THEN `FA.codigo_afip = 1`, and no row is inserted, activated, or deactivated

#### Scenario: A fresh database gets codigo_afip via the seed alone
- GIVEN a brand-new database seeded from `TiposComprobanteBase`
- WHEN the seeder runs (data statement is a no-op — the table was empty at migration time)
- THEN `FA.codigo_afip = 1` is present from the seed net alone

#### Scenario: Removing either net alone still fails its own test
- GIVEN the double-net contract
- WHEN the data statement is removed while the seed stays, or vice versa
- THEN the respective scenario (already-migrated vs. fresh database) fails independently

### Requirement: Exento And No Gravado Keep codigo_afip NULL By Rule

`Exento` and `No gravado` rows in `alicuotas_iva` MUST keep `codigo_afip = NULL` — they are not
alícuotas. Their amounts belong in `ImpOpEx` and `ImpTotConc` respectively (see `comprobante-fiscal`
capability) and MUST NEVER be mapped as if they were an `AlicIva` entry.

#### Scenario: Exento and No gravado remain NULL after the double net runs
- GIVEN the data statement and seed net for `alicuotas_iva` have both run
- WHEN `Exento` and `No gravado` rows are inspected
- THEN both still have `codigo_afip IS NULL`
