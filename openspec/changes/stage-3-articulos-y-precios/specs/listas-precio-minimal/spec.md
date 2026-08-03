# Delta for Listas de Precio

> Capability renamed at spec time (proposal-authorized): "Listas de Precio
> (Minimal)" becomes "Listas de Precio" — the table is no longer minimal
> after this stage. This delta stays under the existing
> `listas-precio-minimal` domain path so the archive merge targets the
> current main spec; `sdd-archive` MAY drop "Minimal" from the merged
> spec's title.

## ADDED Requirements

### Requirement: Derivada Mode Resolution And Validation

A `listas_precio` row with `modo = derivada` MUST require `id_lista_base NOT
NULL` and `porcentaje NOT NULL`; `modo = fija` MUST require both `NULL`. A
derivada lista's price is resolved at read time per the `precios` spec
("Derived List Price Resolution At Read Time") — no `precios` rows are ever
created for it.

#### Scenario: Creating a derivada lista requires base and porcentaje

- GIVEN a tenant admin creates a lista with `modo = derivada` but omits
  `id_lista_base` or `porcentaje`
- WHEN the request is validated
- THEN it is rejected before reaching the database

#### Scenario: id_lista_base must reference an existing lista of the tenant

- GIVEN a create request references a non-existent `id_lista_base`
- WHEN the insert reaches Postgres
- THEN 23503 is raised and `ManejadorDeErrores` maps it to 400
  `referencia_invalida`

### Requirement: Lista ABM Lifecycle and Authorization

Tenant admins MUST be able to create and edit `listas_precio` rows for both
`fija` and `derivada` modes, gated by `GestionDeCatalogo` (tenant `admin`
only — `root` and `vendedor` excluded). Deletion follows the same
soft-delete and protection pattern as other catalogs, subject to the
deactivation guard below.

#### Scenario: Admin creates a fija and a derivada lista

- GIVEN a tenant admin
- WHEN they create a `fija` lista and a `derivada` lista based on it
- THEN both are persisted under the admin's `id_tenant`

#### Scenario: Vendedor blocked from writing

- GIVEN a user with the `vendedor` role
- WHEN they call the lista create or edit endpoint
- THEN the request is rejected with an authorization error

### Requirement: Blocked Mode Switch Once History Exists

> Superseded wording (orchestrator decision, 2026-08-03, judgment-day round 1
> on stage-3 slice 4): an earlier draft also blocked the switch once a
> `derivada` lista "has ever been read-resolved" at a derived price. That
> clause was never designed nor implemented — design.md's Protection Rules
> table and tasks 4.1 scope the guard to `precios` history only, which a
> `derivada` lista never has by design (no `precios` rows are ever created
> for it, per "Derivada Mode Resolution And Validation" above). Tracking
> read-resolution was judged to carry no product value; this requirement now
> states the implemented behavior: `derivada` → `fija` switching is allowed
> unconditionally (only `fija` rows can accumulate `precios` history).

Switching a lista's `modo` (`fija` ↔ `derivada`) MUST be blocked once the
lista has any `precios` history. Only `fija` listas can have `precios`
history, so this guard is only ever reachable when switching away from
`fija`; switching `derivada` → `fija` is always allowed. Tenants MUST create
a new lista instead of switching a `fija` lista with history.

#### Scenario: Mode switch blocked after a price exists

- GIVEN a `fija` lista with at least one `precios` row
- WHEN a tenant admin attempts to change its `modo` to `derivada`
- THEN the request is rejected with a domain validation error

#### Scenario: Mode switch allowed before any price exists

- GIVEN a newly created `fija` lista with no `precios` rows yet
- WHEN a tenant admin changes its `modo` to `derivada` and supplies
  `id_lista_base`/`porcentaje`
- THEN the change succeeds

### Requirement: Blocked Deactivation While Referenced As Base

A lista referenced as `id_lista_base` by any active `derivada` lista MUST
NOT be deactivated (same protection spirit as Consumidor Final).

#### Scenario: Deactivation blocked while a derivada lista depends on it

- GIVEN the General lista is `id_lista_base` for an active derivada lista
- WHEN a tenant admin attempts to deactivate the General lista
- THEN the request is rejected with a domain validation error

#### Scenario: Deactivation allowed once no derivada lista depends on it

- GIVEN a lista that was previously a base but its only dependent derivada
  lista is now deactivated
- WHEN a tenant admin deactivates it
- THEN the request succeeds

## MODIFIED Requirements

### Requirement: listas_precio Schema At Rest

`listas_precio` MUST be catalog-scoped (`id_tenant`, `id_empresa NULL`, doc
09) with `id_lista_precio`, `nombre`, `es_default BOOLEAN`, `modo` (`fija` |
`derivada`), `id_lista_base NULL`, `porcentaje NULL`, `activo`. Both modes
are fully functional: `fija` lists hold `precios` history rows directly;
`derivada` lists compute their price at read time from `id_lista_base` and
`porcentaje` and never hold `precios` rows.
(Previously: only the `fija` shape was accepted; `derivada` columns existed
but were unused, no `precios` history existed yet.)

#### Scenario: Table accepts the fija shape

- GIVEN a tenant's General list
- WHEN it is queried
- THEN `modo = 'fija'`, `id_lista_base = NULL`, `porcentaje = NULL`

#### Scenario: Table accepts the derivada shape

- GIVEN a tenant's derivada lista
- WHEN it is queried
- THEN `modo = 'derivada'`, `id_lista_base` and `porcentaje` are both set

## REMOVED Requirements

### Requirement: listas_precio ABM Is Out of Scope This Stage

(Reason: this stage ships the full tenant-facing ABM for `listas_precio` —
create/edit for both modes.)
(Migration: see the new "Lista ABM Lifecycle and Authorization" requirement.)
