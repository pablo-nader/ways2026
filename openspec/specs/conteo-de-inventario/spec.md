# Conteo de Inventario Specification

## Purpose

Defines the minimal per-articulo inventory count (doc-10:449-450, decision
1): the counted total as input — never a delta — with the server deriving
the signed adjustment under the stock row lock and writing it with
`motivo = inventario`, distinct from `ajuste` for traceability. Reuses
`AjustarAsync`'s transaction shape. Out of scope: any full-count
snapshot/freeze/variance workflow.

## Requirements

### Requirement: Conteo Input Is Exactly One Of A Counted Total Or A Per-Lot Breakdown, Never A Delta

`SolicitudDeConteo.Contada` (the physically counted aggregate total) MUST be
nullable (`decimal?`). A conteo request MUST supply **exactly one** of
`cantidad_contada` or a per-lot breakdown (`lotes: { idLote,
cantidadContada }[]`) — never both, never neither. Supplying both MUST be
rejected with `400 conteo_contada_y_lotes` before reaching the database: a
total silently discarded alongside a lot breakdown for a lot-controlled
articulo would be silent data loss dressed up as a valid request
(`dto-contract-honesty`). Supplying neither MUST be rejected with the same
`400 conteo_contada_y_lotes` — an empty request carries no counted value to
act on. Neither form MUST accept a delta or a signed adjustment: when
`cantidad_contada` is present, the server reads the current `stock.cantidad`
under the same stock-row lock `AjustarAsync` uses and computes
`delta = cantidad_contada − cantidad_actual` for the movement; when `lotes`
is present, each entry's delta is server-derived under that lot's own row
lock, exactly as `AjustarAsync`/`ContarAsync` already do one level up.
(Renamed from "Conteo Input Is The Counted Total, Never A Delta"; amended
post-design, decision 18: `Contada` widens from a required field to
`decimal?`, and the request contract becomes exactly-one-of `Contada` /
`Lotes` rather than "aggregate always, per-lot only for lot-effective
articulos" — the widening is source-compatible for every existing caller.)

#### Scenario: A count above the current cache produces a positive movement
- GIVEN `stock.cantidad = 40` for an articulo at a punto de venta
- WHEN a conteo supplying only `cantidad_contada = 45` (no `lotes`) is
  submitted
- THEN a `+5` `movimientos_stock` row with `motivo = inventario` is
  inserted and `stock.cantidad = 45`

#### Scenario: A count below the current cache produces a negative movement
- GIVEN `stock.cantidad = 40`
- WHEN a conteo supplying only `cantidad_contada = 33` is submitted
- THEN a `-7` `movimientos_stock` row with `motivo = inventario` is
  inserted and `stock.cantidad = 33`

#### Scenario: No endpoint accepts a client-supplied delta
- GIVEN the conteo request contract
- WHEN it is inspected
- THEN it carries only `cantidad_contada` or a per-lot counted total, never
  a `delta` or `ajuste` field

#### Scenario: A lot-effective articulo's conteo carries a per-lot counted total
- GIVEN articulo 40 is lot-effective with `L1 = 12` and `L2 = 28`
  (`stock.cantidad = 40`)
- WHEN a conteo is submitted with `lotes: [{ idLote: L1, cantidadContada: 10 },
  { idLote: L2, cantidadContada: 30 }]` and no `cantidad_contada`
- THEN the server derives `delta(L1) = -2` and `delta(L2) = +2` under each
  lot's own row lock, writes two `movimientos_stock` rows
  (`motivo = inventario`, `id_lote = L1, -2` and `id_lote = L2, +2`), and
  `stock.cantidad` stays `40` (the sum of the per-lot deltas)

#### Scenario: Supplying both cantidad_contada and lotes is rejected
- GIVEN a conteo request for a lot-effective articulo
- WHEN it supplies both `cantidad_contada = 40` and a `lotes` breakdown
- THEN it is rejected with `400 conteo_contada_y_lotes` before reaching the
  database, and no `movimientos_stock` row is written

#### Scenario: Supplying neither cantidad_contada nor lotes is rejected
- GIVEN a conteo request
- WHEN it supplies neither `cantidad_contada` nor a `lotes` breakdown
- THEN it is rejected with `400 conteo_contada_y_lotes` before reaching the
  database

### Requirement: Zero-Difference Conteo Writes No Ledger Row

When `cantidad_contada` equals the current `stock.cantidad`, the conteo
MUST be accepted as a no-op: no `movimientos_stock` row is inserted and the
cache does not change. For a lot-effective articulo, this rule MUST apply
**per lot**: a lot whose counted total equals its current
`stock_lotes.cantidad` MUST write no row for that lot, independent of
whether other lots in the same request differ.
(Previously: the no-op rule was stated only at the aggregate grain.)

#### Scenario: A matching count writes nothing
- GIVEN `stock.cantidad = 40`
- WHEN a conteo of `cantidad_contada = 40` is submitted
- THEN it is accepted, no `movimientos_stock` row is inserted, and
  `stock.cantidad` stays `40`

#### Scenario: A lot with no difference writes no row even when a sibling lot differs
- GIVEN articulo 40 is lot-effective with `L1 = 12` and `L2 = 28`
- WHEN a conteo is submitted with `L1 → 12` (matching) and `L2 → 30`
  (differing)
- THEN only one `movimientos_stock` row is inserted, for `L2`, and `L1`
  produces no row

### Requirement: Conteo Requires Observaciones And Is Distinct From Ajuste

A conteo MUST require a non-empty `observaciones`. `motivo = inventario`
MUST never be produced by the ajuste endpoint, and `motivo = ajuste` MUST
never be produced by the conteo endpoint — the two are separate write paths
kept distinct for traceability: an ajuste is an operator correction, a
conteo is a physical recount. A lot-effective conteo MUST also never
fabricate a balance into the sin-identificar lot to absorb a counting
difference — a counted delta belongs to the specific lot it was counted
against, never to an unidentified residue.
(Previously: silent on the lot dimension.)

#### Scenario: Conteo without observaciones is rejected
- GIVEN a conteo request with empty `observaciones`
- WHEN it is validated
- THEN it is rejected before reaching the database

#### Scenario: A conteo movement is never tagged ajuste
- GIVEN a conteo that produces a movement
- WHEN the inserted row is inspected
- THEN `motivo = inventario`, never `ajuste`

#### Scenario: A lot-effective conteo never writes into the sin-identificar lot to absorb a difference
- GIVEN articulo 40 is lot-effective with `L1 = 12`
- WHEN a conteo of `L1` counts `10` (a `-2` difference)
- THEN the `-2` movement carries `id_lote = L1`, never the sin-identificar
  lot's id

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

### Requirement: Conteo Of A Lot-Effective Articulo Counts Per Lot, With A Pre-Approved Refusal Fallback

For a lot-effective `(articulo, punto de venta)` pair, conteo MUST derive
one movement per lot from the per-lot counted totals, with the aggregate
delta equal to the sum of the per-lot deltas — this is also how the
physical count actually happens: perishables are counted by the date on the
shelf. If, for a given delivery slice, the per-lot conteo path is not yet
implemented, the endpoint MUST instead refuse a lot-effective articulo's
conteo cleanly with `409 conteo_lote_no_soportado` rather than accept an
aggregate-only total that would silently break the `stock_lotes` invariant —
this refusal is the pre-approved degradation (decision 11): a clean
refusal is strictly better than a silent divergence between `stock` and
`stock_lotes`.

#### Scenario: A lot-effective conteo derives the aggregate delta from per-lot deltas
- GIVEN articulo 40 is lot-effective with `L1 = 12`, `L2 = 28`
  (`stock.cantidad = 40`)
- WHEN a conteo counts `L1 → 15, L2 → 20`
- THEN `delta(L1) = +3`, `delta(L2) = -8`, and `stock.cantidad` moves by
  `-5` (their sum), matching the two per-lot `movimientos_stock` rows

#### Scenario: An aggregate-only conteo of a lot-effective articulo is refused when per-lot conteo is not shipped
- GIVEN articulo 40 is lot-effective and the deployed conteo endpoint only
  supports the pre-stage-12 aggregate contract
- WHEN a conteo request for articulo 40 supplies a single aggregate
  `cantidad_contada`
- THEN it is rejected with `409 conteo_lote_no_soportado`, and no
  `movimientos_stock` row is written

#### Scenario: An aggregate-only conteo of a lot-effective articulo is refused when per-lot conteo IS shipped
(Amended at slice-12 judgment-day, juez B FIX 1: the per-lot conteo path is
complete in this delivery slice, so the `409 conteo_lote_no_soportado`
degradation above never fires — but nothing had guarded the aggregate-only
path against a lot-effective articulo once per-lot support existed. Silently
accepting it would move `stock.cantidad` without ever touching
`stock_lotes`, breaking the third invariant — `SUM(stock_lotes.cantidad) =
stock.cantidad` — without any error surfaced. The honest refusal here is
`400 conteo_requiere_lotes`, distinct from the pre-approved `409`
degradation: this is a well-formed request against a fully-implemented
per-lot path, not a missing-feature fallback.)
- GIVEN articulo 40 is lot-effective (`stock.cantidad = 40`,
  `stock_lotes` sums to `40` across its lots) and the deployed conteo
  endpoint DOES support the per-lot contract
- WHEN a conteo request for articulo 40 supplies a single aggregate
  `cantidad_contada = 50` (no `lotes`)
- THEN it is rejected with `400 conteo_requiere_lotes` before any lock is
  acquired (the guard runs after the read-only articulo/parametro
  resolution SELECTs but before any row lock or write), no
  `movimientos_stock` row is written, and `stock.cantidad` stays `40`

#### Scenario: A per-lot conteo of an articulo WITHOUT lot-effective control is refused
(Amended at slice-12 judgment-day, juez B FIX 1 — inverse symmetry: a
`lotes` breakdown has no destination for an articulo that is not
lot-effective, same criterion as `lote_no_aplica` in
`ServicioDeStock.ResolverIdLoteEfectivoAsync`.)
- GIVEN articulo 41 is NOT lot-effective
- WHEN a conteo request for articulo 41 supplies a `lotes` breakdown (no
  `cantidad_contada`)
- THEN it is rejected with `400 conteo_no_aplica_lotes` before any lock is
  acquired, and no `movimientos_stock` row is written *(wording aligned at
  judge-A round: the guard follows the read-only resolution SELECTs, unlike
  the truly zero-DB exactly-one-of check)*
