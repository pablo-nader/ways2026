# Ofertas Specification

## Purpose

Defines the `ofertas` table (doc 10 §Ofertas, deviated per stage-4 decision 4):
catálogo-scoped discount rules keyed by exclusive scope (articulo/grupo/categoria),
exclusive benefit (precio_unitario/porcentaje/importe_fijo), vigencia windows,
`cantidad_minima` trigger, multi-lista targeting via `ofertas_listas`, and
precedence controls (`prioridad`, `acumulable`). Application/stacking math lives
in `resolucion-de-ofertas`; this spec covers schema, targeting, and ABM.

## Requirements

### Requirement: Ofertas Schema At Rest

`ofertas` MUST be catálogo-scoped (`id_tenant` NOT NULL, `id_empresa NULL` =
tenant-wide, doc 09 §Catálogo) with: `id_oferta`, `nombre citext NOT NULL`
(ticket label), `id_articulo`/`id_grupo`/`id_categoria` (exactly one NOT NULL),
`fecha_desde`/`fecha_hasta date NULL`, `hora_desde`/`hora_hasta time NULL`,
`dias_semana smallint[] NULL`, `cantidad_minima numeric(12,3) NULL`,
`precio_unitario`/`porcentaje`/`importe_fijo` (exactly one NOT NULL),
`prioridad int NOT NULL DEFAULT 0`, `acumulable boolean NOT NULL DEFAULT false`,
`activo`. `CHECK num_nonnulls(id_articulo, id_grupo, id_categoria) = 1` and
`CHECK num_nonnulls(precio_unitario, porcentaje, importe_fijo) = 1` MUST both
be enforced.

#### Scenario: Create an item-scope percentage oferta

- GIVEN a tenant admin submits `id_articulo`, `porcentaje = 20`, no vigencia limits
- WHEN the row is persisted
- THEN it succeeds with `id_grupo`/`id_categoria` NULL and `precio_unitario`/`importe_fijo` NULL

#### Scenario: Scope CHECK rejects zero or multiple scope columns

- GIVEN a create request with both `id_articulo` and `id_grupo` set, or with none set
- WHEN the insert reaches Postgres
- THEN 23514 is raised and `ManejadorDeErrores` maps it to 400 `oferta_alcance_invalido`

#### Scenario: Benefit CHECK rejects zero or multiple benefit columns

- GIVEN a create request with both `porcentaje` and `importe_fijo` set, or with none set
- WHEN the insert reaches Postgres
- THEN 23514 is raised and `ManejadorDeErrores` maps it to 400 `oferta_beneficio_invalido`

#### Scenario: Domain guard rejects invalid shapes before the database

- GIVEN a create request with an invalid scope or benefit shape
- WHEN the Domain factory validates it
- THEN it is rejected with a domain validation error before reaching Postgres
  (the CHECKs above are reachable only as defense-in-depth, e.g. raw SQL)

#### Scenario: Invalid scope reference maps to 400

- GIVEN a create request references a non-existent `id_articulo`, `id_grupo`,
  or `id_categoria`
- WHEN the insert reaches Postgres
- THEN 23503 is raised and `ManejadorDeErrores` maps it to 400 `referencia_invalida`

### Requirement: Vigencia Window Semantics

Each vigencia axis MUST be independently optional (NULL = unrestricted on that
axis) and, when set, inclusive on both ends: `fecha_desde <= fecha_consulta <=
fecha_hasta`; `hora_desde <= hora_consulta <= hora_hasta`; `dias_semana`
(ISO-8601, 1=Monday..7=Sunday) MUST contain the consulted weekday when set. A
momento matches only when ALL set axes match.

#### Scenario: All-NULL vigencia always matches

- GIVEN an oferta with `fecha_desde/hasta`, `hora_desde/hasta`, and
  `dias_semana` all NULL
- WHEN evaluated at any date, time, and weekday
- THEN it matches

#### Scenario: Boundary dates and hours are inclusive

- GIVEN an oferta with `fecha_desde = 2026-08-01`, `fecha_hasta = 2026-08-03`,
  `hora_desde = 10:00`, `hora_hasta = 14:00`
- WHEN evaluated at `2026-08-03 14:00`
- THEN it matches

#### Scenario: Outside any single axis excludes the match

- GIVEN the same oferta
- WHEN evaluated at `2026-08-04 12:00` (one day past `fecha_hasta`)
- THEN it does not match, regardless of the other axes

#### Scenario: dias_semana restricts to listed weekdays

- GIVEN an oferta with `dias_semana = {6,7}` (Saturday, Sunday) and no other
  vigencia limits
- WHEN evaluated on a Wednesday
- THEN it does not match

### Requirement: cantidad_minima Trigger Semantics

`cantidad_minima NULL` MUST mean the oferta applies regardless of quantity
("oferta directa"). When set, the oferta MUST match only when the requested
quantity is greater than or equal to `cantidad_minima`.

#### Scenario: NULL cantidad_minima always matches

- GIVEN an oferta with `cantidad_minima = NULL`
- WHEN 1 unit is requested
- THEN it matches

#### Scenario: Quantity below threshold excludes the match

- GIVEN an oferta with `cantidad_minima = 3`
- WHEN 2 units are requested
- THEN it does not match

#### Scenario: Quantity at threshold matches

- GIVEN an oferta with `cantidad_minima = 3`
- WHEN exactly 3 units are requested
- THEN it matches

### Requirement: Multi-Lista Targeting via ofertas_listas

`ofertas_listas` MUST be a junction (`id_oferta`, `id_lista_precio`) that
REPLACES doc 10's single `id_lista_precio NULL` column (stage-4 decision 4).
Zero rows for an oferta MUST mean it applies to every lista of the tenant,
including `derivada` ones. One or more rows MUST restrict it to exactly those
listas.

#### Scenario: No junction rows targets every lista

- GIVEN an oferta with no `ofertas_listas` rows
- WHEN resolution runs for the General lista and for a derivada lista
- THEN the oferta is a candidate in both

#### Scenario: Junction rows restrict targeting

- GIVEN an oferta with one `ofertas_listas` row pointing to the General lista
- WHEN resolution runs for the Mayorista lista
- THEN the oferta is not a candidate

#### Scenario: Junction row references must belong to the same tenant

- GIVEN a create request adds an `ofertas_listas` row referencing another
  tenant's lista
- WHEN the insert reaches Postgres
- THEN it is rejected with 400 `referencia_invalida` via a tenant-scoped
  existence pre-check

### Requirement: Oferta ABM Lifecycle and Authorization

Ofertas MUST support list/create/edit/soft-delete including `ofertas_listas`
management, gated by `GestionDeCatalogo` (tenant `admin` only — `root` and
`vendedor` excluded). Cross-tenant access to an existing oferta MUST return
the same 404 for "does not exist" and "belongs to another tenant" (ADR-8).

#### Scenario: Admin creates and soft-deletes an oferta

- GIVEN a tenant admin
- WHEN they create an oferta and later soft-delete it
- THEN it is persisted under the admin's `id_tenant`, and after deletion
  `deleted_at` is set and it no longer appears in the default list or as a
  resolution candidate

#### Scenario: Vendedor blocked from writing

- GIVEN a user with the `vendedor` role
- WHEN they call the oferta create or edit endpoint
- THEN the request is rejected with an authorization error

#### Scenario: Cross-tenant read/write is a uniform 404

- GIVEN tenant 1 requests, edits, or deletes an oferta belonging to tenant 2
- WHEN the request reaches the service
- THEN it is rejected with 404, identical to the "does not exist" case

### Requirement: Tenant Isolation for ofertas and ofertas_listas

`ofertas` and `ofertas_listas` MUST enforce the two-layer isolation guarantee
(EF Core global query filter + Postgres RLS without `BYPASSRLS`) established
in stage 1.

#### Scenario: EF filter blocks cross-tenant read

- GIVEN a request scoped to tenant 1
- WHEN a query executes through the normal `DbContext` for an oferta of tenant 2
- THEN no rows are returned

#### Scenario: RLS blocks a read that bypasses EF

- GIVEN the app DB role has no `BYPASSRLS`
- WHEN a raw SQL query or `IgnoreQueryFilters()` reads tenant 2's `ofertas`
  while `app.tenant_id = 1`
- THEN RLS returns zero rows
