# Delta for Articulos

## ADDED Requirements

### Requirement: GET /api/articulos Accepts Optional Category/Marca/Area Filters

`GET /api/articulos` MUST accept three additive, optional query params —
`idArea`, `idCategoria`, `idMarca` — alongside the existing `busqueda` and
`idEmpresa`. `idCategoria` MUST match the category and all of its
descendants (`categorias.id_categoria_padre` hierarchy). When all three are
absent, the response's behaviour, ordering, paging and the existing
`tamanio ∈ [1,200]` clamp MUST be byte-identical to the current listing.

#### Scenario: Filtering by idMarca narrows the listing
- GIVEN 40 artículos across 3 marcas, and marca A has 12
- WHEN `GET /api/articulos?idMarca={A}` is called
- THEN exactly the 12 artículos of marca A are returned

#### Scenario: idCategoria on a parent returns descendant artículos too
- GIVEN a three-level categoría tree where the top-level category has 5
  artículos and a grandchild category has 3
- WHEN `GET /api/articulos?idCategoria={top-level}` is called
- THEN all 8 artículos (5 direct + 3 descendant) are returned

#### Scenario: Absent filters leave the listing byte-identical
- GIVEN no `idArea`, `idCategoria`, or `idMarca` is supplied
- WHEN `GET /api/articulos` is called with the same `busqueda`/`idEmpresa`/
  paging as before this change
- THEN the response body, ordering, paging, and clamp behaviour are
  byte-identical to the pre-change listing

#### Scenario: Filters combine as AND
- GIVEN artículos matching `idArea` and a disjoint set matching `idMarca`
- WHEN both are supplied together
- THEN only artículos matching both filters are returned
