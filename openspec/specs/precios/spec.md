# Precios Specification

## Purpose

Defines the `precios` table (doc 10 §3): price history per `(articulo,
lista_precio)` with close-and-open updates, programmable future prices,
current-price-at-date resolution, derived-list read-time computation, and the
margin-based price suggestion helper.

## Requirements

### Requirement: Price History Never Overwrites

A price change for an `(articulo, lista_precio)` pair MUST close the
currently vigente row (`vigente_hasta` set) and insert a new row — the
`precio` column of an existing row MUST NOT be updated once persisted.
History rows MUST remain queryable indefinitely.

#### Scenario: Changing a price closes the old row and opens a new one

- GIVEN an articulo has a vigente price of $100 in the General lista
- WHEN a tenant admin sets a new price of $120 effective now
- THEN the $100 row's `vigente_hasta` is set to the new row's
  `vigente_desde`, and a new row with `precio = 120`, `vigente_hasta = NULL`
  is inserted

#### Scenario: Historical prices remain queryable

- GIVEN an articulo has had three prices over time in the same lista
- WHEN its price history is queried
- THEN all three rows are returned with their respective
  `vigente_desde`/`vigente_hasta`

### Requirement: Programmable Future Prices, At Most One Pending

A future price (`vigente_desde` after now) MAY be scheduled ahead of its
effective date. At most ONE pending (not-yet-effective) row MUST exist per
`(articulo, lista_precio)` at any time; scheduling a new future price MUST
replace the previously pending one, and the caller MUST confirm the
replacement before it takes effect.

#### Scenario: Scheduling a future price with none pending

- GIVEN an articulo has no pending future price in the General lista
- WHEN a tenant admin schedules `precio = 150` effective in 3 days
- THEN the pending row is created without affecting the currently vigente
  price

#### Scenario: Scheduling replaces the existing pending price

- GIVEN an articulo already has a pending price of $150 for next week
- WHEN a tenant admin schedules a new pending price of $160 for the same
  lista
- THEN the $150 pending row is replaced by the $160 one after the caller
  confirms

### Requirement: Current-Price Query Semantics By Date

A "current price" query for a given date MUST return the row where
`vigente_desde <= fecha` and (`vigente_hasta IS NULL` or `vigente_hasta >
fecha`). Point-in-time queries for a past date MUST resolve against history,
not the currently vigente row.

#### Scenario: Query at present date returns the active row

- GIVEN an articulo's current vigente price is $120
- WHEN the price is queried for today's date
- THEN $120 is returned, ignoring any pending future row

#### Scenario: Point-in-time query resolves a past price

- GIVEN an articulo had $100 last month and $120 today
- WHEN the price is queried for a date last month
- THEN $100 is returned

### Requirement: Derived List Price Resolution At Read Time

For a `lista_precio` with `modo = derivada`, the price MUST be computed at
read time as `precio_base * (1 ± porcentaje)` from the base lista's current
price — no `precios` row MUST ever be persisted for a derivada lista.

#### Scenario: Derived lista price follows the base lista automatically

- GIVEN a derivada lista at `-10%` over the General lista, whose current
  price is $100
- WHEN the derivada lista's price for that articulo is queried
- THEN $90 is returned, with no stored `precios` row for the derivada lista

#### Scenario: Base price change propagates without a write

- GIVEN the same derivada lista as above
- WHEN the General lista's price changes to $200
- THEN the derivada lista's resolved price becomes $180 with no additional
  write

### Requirement: Margin-Based Price Suggestion

The system MUST compute a suggested price from a base cost and a margin:
`grupos.margen` takes precedence when the articulo's grupo has one;
otherwise `proveedores.margen` (proveedor habitual) is used. Base cost is
`costo_nominal` when present, else `costo_lista * (1 - descuento_proveedor)`.
The suggestion MUST be presented for manual apply and MUST NOT be persisted
automatically.

#### Scenario: Grupo margin wins over proveedor margin

- GIVEN an articulo whose grupo has `margen = 30` and whose proveedor
  habitual has `margen = 20`
- WHEN a price suggestion is requested
- THEN it uses the grupo's 30% margin

#### Scenario: Falls back to proveedor margin without a grupo margin

- GIVEN an articulo with no grupo (or a grupo with `margen = NULL`) and a
  proveedor habitual with `margen = 15`
- WHEN a price suggestion is requested
- THEN it uses the proveedor's 15% margin

#### Scenario: Suggestion requires explicit apply

- GIVEN a computed price suggestion
- WHEN it is shown to the tenant admin
- THEN no `precios` row is created until the admin explicitly applies it

### Requirement: Batch Current-Price Resolution

The system MUST provide a batch current-price resolution path that accepts
multiple `(id_articulo, id_lista_precio)` pairs (or an articulo set × a lista
set) plus a single `momento`, and returns each pair's current-price-at-date
in one call, without issuing one query per lista or per articulo. This path
is additive: `PrecioVigenteAsync` and `PreciosVigentesAsync` (stage 3) MUST
keep their existing signatures and semantics unchanged.

#### Scenario: Batch resolves many articulo/lista pairs in one call

- GIVEN 20 articulos and 3 active listas (60 pairs)
- WHEN the batch price resolution path runs for today's date
- THEN 60 current prices are returned without a per-pair database round trip

#### Scenario: Derivada listas resolve within the same batch call

- GIVEN the batch input includes a derivada lista
- WHEN the batch path runs
- THEN its price is computed from its base lista's batch-resolved price, per
  "Derived List Price Resolution At Read Time"

#### Scenario: Existing single-articulo methods are unaffected

- GIVEN a caller still uses `PrecioVigenteAsync` or `PreciosVigentesAsync`
- WHEN either is invoked
- THEN behavior and return shape are unchanged from stage 3

### Requirement: Tenant Isolation for precios

`precios` MUST enforce the two-layer isolation guarantee (EF Core global
query filter + Postgres RLS without `BYPASSRLS`) established in stage 1.

#### Scenario: EF filter blocks cross-tenant read

- GIVEN a request scoped to tenant 1
- WHEN a query executes through the normal `DbContext` for a `precios` row
  of tenant 2
- THEN no rows are returned

#### Scenario: RLS blocks a read that bypasses EF

- GIVEN the app DB role has no `BYPASSRLS`
- WHEN a raw SQL query or `IgnoreQueryFilters()` reads tenant 2's `precios`
  while `app.tenant_id = 1`
- THEN RLS returns zero rows
