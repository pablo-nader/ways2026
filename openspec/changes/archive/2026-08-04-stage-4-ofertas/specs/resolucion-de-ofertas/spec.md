# Resolución de Ofertas Specification

## Purpose

Defines the query-only, batch-first oferta resolution capability: given N
`(articulo, cantidad, lista, momento)` inputs, returns the final unit price
and the applied ofertas per input. Implements the precedence and
additive-over-original stacking rules (stage-4 decision 1, user-superseded
2026-08-03). Resolution MUST NOT write to `items_comprobante_venta`,
`ofertas`, `precios`, or any other table — stage 5 consumes the result.

## Requirements

### Requirement: Batch Input Shape

A single resolution call MUST accept multiple `(id_articulo, cantidad,
id_lista_precio, momento)` inputs and MUST resolve base prices and candidate
ofertas for the whole batch without a per-articulo query loop.

#### Scenario: Batch resolves many articulos in one call

- GIVEN 50 `(articulo, cantidad, lista, momento)` inputs across 3 listas
- WHEN resolution runs
- THEN 50 results are returned without issuing one price/oferta query per articulo

### Requirement: Candidate Matching

A candidate oferta for an input MUST satisfy ALL of: `activo = true`; scope
matches (`id_articulo` equals the input's articulo, OR `id_grupo` equals the
articulo's grupo, OR `id_categoria` equals the articulo's categoria **or any
ancestor of it** — an oferta on a parent categoria reaches articulos in its
subcategorias, user decision 2026-08-03, tree depth ≤ 3); `id_empresa` is NULL
or equals the input's empresa; lista targeting matches (per the `ofertas`
spec); vigencia matches at `momento` (per the `ofertas` spec);
`cantidad_minima` matches at `cantidad` (per the `ofertas` spec).

#### Scenario: Categoria-scoped oferta reaches subcategoria articulos

- GIVEN an oferta scoped to categoria "Bebidas" and an articulo whose categoria
  is "Gaseosas", a child of "Bebidas"
- WHEN resolution runs for that articulo
- THEN the oferta is a candidate for it (ancestor-chain match)

#### Scenario: Grupo-scoped oferta matches via the articulo's grupo

- GIVEN an oferta scoped to grupo "Bebidas" and an articulo belonging to that grupo
- WHEN resolution runs for that articulo
- THEN the oferta is a candidate

#### Scenario: Empresa-scoped oferta excludes other empresas

- GIVEN an oferta with `id_empresa` set to empresa A
- WHEN resolution runs for an input scoped to empresa B (same tenant)
- THEN the oferta is not a candidate

#### Scenario: No matching oferta leaves the price unchanged

- GIVEN an articulo with no active oferta matching its scope, lista, vigencia,
  or cantidad_minima
- WHEN resolution runs
- THEN the result is the original resolved price with an empty applied-ofertas list

### Requirement: Base Selection and Tie-Break

Among candidates with `acumulable = false`, the highest `prioridad` MUST win
as the base. A tie (equal `prioridad`) MUST resolve by greater effective
discount for that line; a further tie MUST resolve by lower `id_oferta`. If no
`acumulable = false` candidate exists, the base is the original resolved
price (no base discount) and only acumulables apply.

#### Scenario: Highest prioridad wins as base

- GIVEN two non-acumulable candidates, `prioridad 10` (−10%) and `prioridad 20` (−15%)
- WHEN resolution runs
- THEN the `prioridad 20` oferta is the base

#### Scenario: Equal prioridad ties break by greater discount

- GIVEN two non-acumulable candidates at `prioridad 10` on a $600 line: one
  −$50 fijo, one −10%
- WHEN resolution runs
- THEN the −$60 (10% of $600) oferta wins, being the greater discount

#### Scenario: Remaining tie breaks by lower id_oferta

- GIVEN two non-acumulable candidates at `prioridad 10` with an identical
  −10% discount, `id_oferta 5` and `id_oferta 9`
- WHEN resolution runs
- THEN `id_oferta 5` is the base

#### Scenario: Acumulable-only candidates apply with no base

- GIVEN only `acumulable = true` candidates match (no `acumulable = false`
  candidate)
- WHEN resolution runs
- THEN the original resolved price is the base and all matching acumulables
  stack on it per the stacking rule below

### Requirement: Additive-Over-Original Stacking

Each matching benefit (the base and every `acumulable = true` match) MUST
compute its discount independently against the ORIGINAL resolved unit price:
`porcentaje` → `original * pct/100`; `importe_fijo` → the fixed amount **per
unit** (the resolved output remains a reproducible unit price; a line of
quantity Q therefore discounts Q × importe_fijo — user decision 2026-08-03);
`precio_unitario` → `original - precio_unitario`. All discounts MUST be
summed, and the combined discount MUST be clamped so it never exceeds the
original price (final price floor = 0). Application order (descending
`prioridad`, then ascending `id_oferta`) is for the reported list only and
MUST NOT affect the computed amount.

#### Scenario: Base plus one acumulable

- GIVEN an original price of $1000, base = −20% (non-acumulable), one
  acumulable = −10%
- WHEN resolution runs
- THEN each discount computes against $1000 ($200 and $100), summed to $300,
  final price $700

#### Scenario: Multiple acumulables stack on the base

- GIVEN an original price of $1000, base = −20%, two acumulables = −10% and
  −$50 fijo
- WHEN resolution runs
- THEN discounts are $200 + $100 + $50 = $350, final price $650

#### Scenario: precio_unitario as the base

- GIVEN an original price of $1000, base = `precio_unitario = 750`
  (non-acumulable), one acumulable = −10%
- WHEN resolution runs
- THEN the base discount is $250 (1000 − 750), the acumulable discount is
  $100, summed $350, final price $650

#### Scenario: precio_unitario as an acumulable

- GIVEN an original price of $1000, base = −10% (non-acumulable), one
  acumulable = `precio_unitario = 600`
- WHEN resolution runs
- THEN the base discount is $100, the acumulable discount is $400
  (1000 − 600), summed $500, final price $500

#### Scenario: Combined discount over 100% clamps to zero

- GIVEN an original price of $1000, base = −80%, two acumulables = −30% and −20%
- WHEN resolution runs
- THEN the raw discounts sum to $1300, exceeding the original price; the
  combined discount is clamped to $1000 and the final price is $0

#### Scenario: Derivada lista price is the original base

- GIVEN a derivada lista's already-resolved price of $180 (10% off a $200
  base lista price) and an acumulable −10% oferta targeting that lista
- WHEN resolution runs
- THEN the discount computes as 10% of $180 ($18), final price $162 — the
  oferta never recomputes against the base lista's $200

### Requirement: Applied Ofertas Are Reported, Never Persisted

Each result MUST list every applied `id_oferta` (base and acumulables) and
the resolved final price. Resolution MUST NOT write to
`items_comprobante_venta`, `ofertas`, `precios`, or any other table.

#### Scenario: Result lists all applied ofertas

- GIVEN the "base plus one acumulable" scenario above
- WHEN resolution runs
- THEN the result includes both `id_oferta` values and the $700 final price

#### Scenario: Resolution performs no writes

- GIVEN any resolution call, matched or unmatched
- WHEN it completes
- THEN no row is inserted or updated in any table
