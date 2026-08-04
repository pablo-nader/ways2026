# Delta for Precios

## ADDED Requirements

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
