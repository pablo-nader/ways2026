# Conteo de Inventario Specification

## Purpose

Defines the minimal per-articulo inventory count (doc-10:449-450, decision
1): the counted total as input — never a delta — with the server deriving
the signed adjustment under the stock row lock and writing it with
`motivo = inventario`, distinct from `ajuste` for traceability. Reuses
`AjustarAsync`'s transaction shape. Out of scope: any full-count
snapshot/freeze/variance workflow.

## Requirements

### Requirement: Conteo Input Is The Counted Total, Never A Delta

A conteo request MUST supply `cantidad_contada` (the physically counted
total) — it MUST NOT accept a delta or a signed adjustment. Under the same
stock-row lock `AjustarAsync` uses, the server MUST read the current
`stock.cantidad`, compute `delta = cantidad_contada − cantidad_actual`, and
use that server-derived delta for the movement.

#### Scenario: A count above the current cache produces a positive movement
- GIVEN `stock.cantidad = 40` for an articulo at a punto de venta
- WHEN a conteo of `cantidad_contada = 45` is submitted
- THEN a `+5` `movimientos_stock` row with `motivo = inventario` is
  inserted and `stock.cantidad = 45`

#### Scenario: A count below the current cache produces a negative movement
- GIVEN `stock.cantidad = 40`
- WHEN a conteo of `cantidad_contada = 33` is submitted
- THEN a `-7` `movimientos_stock` row with `motivo = inventario` is
  inserted and `stock.cantidad = 33`

#### Scenario: No endpoint accepts a client-supplied delta
- GIVEN the conteo request contract
- WHEN it is inspected
- THEN it carries only `cantidad_contada`, never a `delta` or `ajuste` field

### Requirement: Zero-Difference Conteo Writes No Ledger Row

When `cantidad_contada` equals the current `stock.cantidad`, the conteo
MUST be accepted as a no-op: no `movimientos_stock` row is inserted and the
cache does not change.

#### Scenario: A matching count writes nothing
- GIVEN `stock.cantidad = 40`
- WHEN a conteo of `cantidad_contada = 40` is submitted
- THEN it is accepted, no `movimientos_stock` row is inserted, and
  `stock.cantidad` stays `40`

### Requirement: Conteo Requires Observaciones And Is Distinct From Ajuste

A conteo MUST require a non-empty `observaciones`. `motivo = inventario`
MUST never be produced by the ajuste endpoint, and `motivo = ajuste` MUST
never be produced by the conteo endpoint — the two are separate write paths
kept distinct for traceability: an ajuste is an operator correction, a
conteo is a physical recount.

#### Scenario: Conteo without observaciones is rejected
- GIVEN a conteo request with empty `observaciones`
- WHEN it is validated
- THEN it is rejected before reaching the database

#### Scenario: A conteo movement is never tagged ajuste
- GIVEN a conteo that produces a movement
- WHEN the inserted row is inspected
- THEN `motivo = inventario`, never `ajuste`

### Requirement: Conteo Reuses AjustarAsync's Transaction Shape

The conteo write MUST insert its `movimientos_stock` row and upsert the
`stock` cache inside the same transaction, using the same `INSERT ... ON
CONFLICT DO UPDATE ... RETURNING` pattern whose row lock provides
serialization, with `EstrategiaSinReintento`.

#### Scenario: Concurrent conteos of the same articulo do not corrupt the cache
- GIVEN two concurrent conteo requests for the same articulo and punto de
  venta
- WHEN both transactions commit
- THEN the row lock serializes them and the final `stock.cantidad`
  reflects both counts applied in sequence, not a lost update

### Requirement: Authorization

The conteo write path MUST be gated by `Politicas.GestionDeCatalogo`
stacked over `Politicas.OperacionDePos` (Admin-only).

#### Scenario: Admin submits a conteo
- GIVEN a user with role Admin
- WHEN they submit a valid conteo with observaciones
- THEN the request succeeds

#### Scenario: Vendedor is blocked from conteo
- GIVEN a user with role Vendedor
- WHEN they call the conteo endpoint
- THEN the request is rejected with `403`
