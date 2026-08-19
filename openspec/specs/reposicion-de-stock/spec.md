# Reposición De Stock Specification

## Purpose

Owns replenishment end to end (doc-11:165-188, proposal decisions 1, 3, 4,
6, 7): the semantics of `stock.minimo` (the reorder point) and
`stock.reposicion` (the restock target) on the existing per-`(articulo,
punto de venta)` `stock` row — `NULL` means unmanaged, the alert boundary
is inclusive (`cantidad <= minimo`) — the Admin-only write path and its
no-movement rule, the rotation figure and which `movimientos_stock` rows
count as consumption, the suggested-quantity formula and its honest nulls,
and the reposición report with its `/resumen` tile and `/export` sibling.
"Stock en tránsito" is documented here as intentionally **omitted** from
the suggestion formula (decision 4) — not a field, not hard-coded to zero.
As of Etapa 16, `ordenes_compra` now gives orders an `estado` and an
expected arrival (`fecha_esperada`), so the omission is no longer a
structural absence — it remains a **deliberate deferral** (Etapa 16
explore, Orchestrator Decision 5), with the reopen condition being the
first customer who over-orders because the report ignores what is
already on the way.

## Requirements

### Requirement: Minimo Is A Fixed, Owner-Set Reorder Point; NULL Means Unmanaged

`stock.minimo` MUST be the sole value the low-stock alert compares
against — never a value computed from rotation. A `stock` row with
`minimo IS NULL` MUST NEVER appear in the reposición report or alert,
regardless of `cantidad`. Rotation MAY produce `minimoSugerido` alongside
the field, but it MUST NOT be persisted to `minimo` by any automated path
— only an explicit Admin write via `PUT /api/stock/minimos` sets it.

#### Scenario: An articulo with no minimo never alerts, even at zero stock
- GIVEN articulo 10 at punto de venta 1 has `stock.cantidad = 0` and
  `stock.minimo = NULL`
- WHEN the reposición report is requested for that punto de venta
- THEN articulo 10 does not appear

#### Scenario: A catalog with no minimo configured anywhere returns zero alert rows
- GIVEN a punto de venta with hundreds of stocked articulos, none carrying
  a `minimo`
- WHEN the reposición report is requested
- THEN it returns zero rows

#### Scenario: minimoSugerido is never written to minimo automatically
- GIVEN articulo 10 has no `minimo` and a computable `minimoSugerido` from
  rotation
- WHEN the reposición report or its `/resumen` runs
- THEN `stock.minimo` for articulo 10 stays `NULL` — no write occurs
  outside `PUT /api/stock/minimos`

### Requirement: The Low-Stock Boundary Is Inclusive — cantidad <= minimo

An articulo whose `cantidad` equals its `minimo` MUST be classified as at
or below the reorder point (`estado = bajo`), not only when `cantidad` is
strictly less than `minimo`.

#### Scenario: An articulo exactly at its minimo appears in the report
- GIVEN articulo 10 has `minimo = 5` and `cantidad = 5`
- WHEN the reposición report runs
- THEN articulo 10 appears with `estado = bajo`

#### Scenario: An articulo one unit above its minimo does not appear
- GIVEN articulo 10 has `minimo = 5` and `cantidad = 6`
- WHEN the reposición report runs
- THEN articulo 10 does not appear

#### Scenario: minimo = 0 alerts only once stock is exhausted
- GIVEN articulo 11 has `minimo = 0`
- WHEN `cantidad = 0`
- THEN articulo 11 appears in the report; at `cantidad = 1` it does not

### Requirement: PUT /api/stock/minimos Writes Thresholds Without A Movement, Admin-Only

`PUT /api/stock/minimos` MUST be gated by `Politicas.GestionDeCatalogo`
stacked over `Politicas.OperacionDePos` (Admin only). It MUST create the
`stock` row with `cantidad = 0` when none exists for the `(articulo, punto
de venta)` pair, and MUST insert zero `movimientos_stock` rows regardless
of whether the row is created or updated. `minimo` and `reposicion`, when
supplied, MUST each be `>= 0` (`400 minimo_negativo`), MUST each carry at
most 3 decimal places (`400 minimo_invalido`), and `reposicion` MUST NOT
be below `minimo` when both are set (`400 reposicion_menor_que_minimo`) —
all validated before reaching the database. The 3-decimal guard applies to
both fields: without it, Postgres silently **rounds** a value the operator
typed into `numeric(12,3)` — the same discipline `cantidad_de_ajuste_invalida`
already enforces on ajuste/decomiso.

#### Scenario: Setting a minimo for an articulo with no stock row creates it without a movement
- GIVEN no `stock` row exists for `(articulo 20, punto de venta 1)`
- WHEN an Admin submits `PUT /api/stock/minimos` with `minimo = 10`
- THEN a `stock` row is created with `cantidad = 0, minimo = 10` and zero
  `movimientos_stock` rows are inserted

#### Scenario: A negative minimo is rejected before reaching the database
- GIVEN a request with `minimo = -1`
- WHEN it is validated
- THEN it is rejected with `400 minimo_negativo`

#### Scenario: A reposicion below minimo is rejected
- GIVEN a request with `minimo = 10, reposicion = 5`
- WHEN it is validated
- THEN it is rejected with `400 reposicion_menor_que_minimo`

#### Scenario: A minimo with more than 3 decimal places is rejected, not silently rounded
- GIVEN a request with `minimo = 10.1234`
- WHEN it is validated
- THEN it is rejected with `400 minimo_invalido`, never silently stored
  rounded to `10.123`

#### Scenario: A reposicion with more than 3 decimal places is rejected too
- GIVEN a request with `reposicion = 50.5678`
- WHEN it is validated
- THEN it is rejected with `400 minimo_invalido`

#### Scenario: A Supervisor reads but cannot write minimos
- GIVEN a user with role Supervisor
- WHEN they call `GET /api/reportes/stock/existencias` they receive `200`;
  WHEN they call `PUT /api/stock/minimos` they receive `403`

#### Scenario: A Vendedor is rejected from the write path
- GIVEN a user with role Vendedor
- WHEN they call `PUT /api/stock/minimos`
- THEN the response is `403`

### Requirement: Reposición Report Is The Alert And The Purchase Suggestion, Grouped By Proveedor Habitual, Never Dropping Unassigned Rows

`GET /api/reportes/stock/reposicion?idPuntoVenta=` MUST return every
`stock` row at that punto de venta where `minimo IS NOT NULL AND cantidad
<= minimo`, joined to `articulos`, grouped by
`articulos.id_proveedor_habitual`. Rows whose articulo has no
`id_proveedor_habitual` MUST be grouped under a `"Sin proveedor"` group and
MUST NEVER be omitted. Each row's suggested purchase quantity MUST be
`sugerido = reposicion IS NULL ? null : max(0, reposicion - cantidad)` —
`sugerido` MUST be `null`, never `0`, when `reposicion` is unset. The
formula MUST NOT subtract any "stock en tránsito" term. As of Etapa 16,
`ordenes_compra` gives orders an `estado` and an expected arrival
(`fecha_esperada`), and the pending quantity per artículo is now derivable
from that capability — so the omission is no longer a structural absence.
It remains a **deliberate deferral**: subtracting it is out of scope for
this formula, with the reopen condition being the first customer who
over-orders because the report ignores what is already on the way.
(Previously: justified the omission with "that concept is structurally
absent from this system's model (no order-with-state entity exists)" — no
longer true once Etapa 16 ships; the formula and every scenario below stay
byte-identical.)

#### Scenario: An articulo with no proveedor habitual appears under Sin proveedor
- GIVEN articulo 12 is below its minimo and has `id_proveedor_habitual =
  NULL`
- WHEN the reposición report runs
- THEN articulo 12 appears grouped under `"Sin proveedor"`, not omitted

#### Scenario: sugerido is null, never zero, when reposicion is unset
- GIVEN articulo 13 has `minimo = 10, reposicion = NULL, cantidad = 3`
- WHEN the reposición report runs
- THEN articulo 13's `sugerido` field is `null`

#### Scenario: sugerido computes the gap to the restock target
- GIVEN articulo 14 has `minimo = 10, reposicion = 50, cantidad = 20`
- WHEN the reposición report runs
- THEN articulo 14's `sugerido = 30`

#### Scenario: A Vendedor is rejected from the reposición report and its export
- GIVEN a user with role Vendedor
- WHEN they call the reposición report or its export
- THEN the response is `403`

#### Scenario: The formula stays byte-identical after Etapa 16 ships
- GIVEN the same stock/minimo/reposicion inputs as before Etapa 16, and an
  `ordenes_compra` row now derivable for that proveedor
- WHEN the reposición report runs
- THEN `sugerido` computes exactly as before — no "stock en tránsito" term
  is subtracted

### Requirement: Rotation Excludes Purchase-Reversal Anulaciones And Is Advisory-Only

The rotation figure MUST sum `movimientos_stock.cantidad` over `motivo =
venta` rows plus `motivo = anulacion` rows whose `id_comprobante_compra IS
NULL`, over a window of `dias_rotacion` days (`ParametroConocido`, default
`30`), resolved in the punto de venta's `zona_horaria`. `motivo IN
(ajuste, inventario, decomiso, transferencia, reclasificacion)` MUST NEVER
count as consumption. `minimoSugerido` MUST be computed as average daily
consumption times `dias_cobertura_objetivo` (`ParametroConocido`, default
`7`) and MUST be advisory: a wrong or absent rotation figure MUST NEVER
fire or suppress an alert, because the alert compares `cantidad` to the
stored `minimo` alone. Every route that surfaces a rotation-derived figure
— `GET /api/reportes/stock/reposicion` and `GET /api/reportes/stock/rotacion`
— MUST accept an optional `?dias=` query parameter that overrides the
resolved `dias_rotacion` for that single request, mirroring
`vencimientos?dias=` (stage 12) exactly, so the window is testable without
writing a `parametros` row.

#### Scenario: A purchase anulación is excluded from consumption
- GIVEN a mixed sequence containing `motivo = anulacion` reversing a sale
  (`id_comprobante_compra IS NULL`) and `motivo = anulacion` reversing a
  compra (`id_comprobante_compra = 12`)
- WHEN rotation is computed for the window
- THEN only the sale-reversal anulación contributes to consumption; the
  compra-reversal anulación does not

#### Scenario: ajuste, inventario, decomiso, transferencia and reclasificacion never count as consumption
- GIVEN a mixed sequence including one row of each of those five motivos
  alongside `venta` rows
- WHEN rotation is computed
- THEN only the `venta` rows contribute

#### Scenario: The rotation window resolves in the punto de venta's own zona horaria
- GIVEN a punto de venta with `zona_horaria =
  "America/Argentina/Buenos_Aires"` and `dias_rotacion = 30`
- WHEN rotation is computed near a UTC day boundary
- THEN the window's start and end instants are computed from "hoy"
  resolved in that zona_horaria, not UTC

#### Scenario: A zero-history articulo shows no suggestion rather than a suggestion of zero
- GIVEN articulo 15 has no `movimientos_stock` rows of type `venta` or
  qualifying `anulacion` in the window
- WHEN the reposición report computes `minimoSugerido`
- THEN `minimoSugerido` is `null`, not `0`

#### Scenario: A wrong rotation figure never gates the alert
- GIVEN articulo 16 has `minimo = 10, cantidad = 8` and an arbitrary
  rotation figure
- WHEN the reposición report runs
- THEN articulo 16 appears because `cantidad <= minimo`, independent of
  the rotation value

#### Scenario: An explicit dias override widens the reposición report's window
- GIVEN `dias_rotacion` resolves to `30` for the punto de venta, and a
  sale movement 45 days old that would fall outside the default window
- WHEN `GET /api/reportes/stock/reposicion?idPuntoVenta=7&dias=60` is
  requested
- THEN the movement falls inside the 60-day window and contributes to that
  articulo's rotation figures

### Requirement: GET /api/reportes/stock/rotacion Feeds The Suggested-Minimo Column, Never A Row For A Zero-History Articulo

`GET /api/reportes/stock/rotacion?idPuntoVenta[&dias]` MUST return one row
per articulo carrying at least one qualifying consumption movement in the
window, computed by the same consumption definition and window resolution
the reposición report uses — never a second definition. An articulo with
no qualifying movement in the window MUST be absent from the result set,
never present with a `minimoSugerido` of `0`. `dias` MUST be optional,
defaulting to the resolved `dias_rotacion` parametro.

#### Scenario: An articulo with no consumption history is absent, not zero
- GIVEN articulo 17 has no qualifying `venta`/`anulacion` movement in the
  window
- WHEN `GET /api/reportes/stock/rotacion?idPuntoVenta=7` is requested
- THEN articulo 17 does not appear in the response

#### Scenario: dias overrides the default window on the rotacion route too
- GIVEN `dias_rotacion` resolves to `30`, and a sale movement 45 days old
- WHEN `GET /api/reportes/stock/rotacion?idPuntoVenta=7&dias=60` is
  requested
- THEN the movement falls inside the window and the articulo appears with
  a `minimoSugerido` reflecting it

### Requirement: The Tablero Tile Reuses The Report Method, Never A Second Aggregation Query

`GET /api/reportes/stock/reposicion/resumen` MUST reuse the same method
that produces the full reposición report, returning only its counts — it
MUST NOT run a second, independent aggregation query. The tile MUST carry
three discriminating counts folded from the report's own rows: `bajoMinimo`
(the total row count), `sinStock` (rows where `cantidad <= 0`), and
`sinProveedor` (rows where the articulo has no `id_proveedor_habitual`).
`sinProveedor` is the actionable gap — load a proveedor for that articulo
— and MUST NOT be conflated with "no suggestion available"
(`sugerido is null`), which mixes two different causes (no proveedor vs.
no `reposicion` configured) behind one number.

#### Scenario: The tile's three counts equal the report's folded values
- GIVEN the reposición report returns 7 rows for a punto de venta, of
  which 2 have `cantidad <= 0` and 1 has no `id_proveedor_habitual`
- WHEN `GET /api/reportes/stock/reposicion/resumen` is requested for the
  same punto de venta
- THEN it reports `bajoMinimo = 7, sinStock = 2, sinProveedor = 1`

#### Scenario: sinProveedor counts the Sin proveedor group, not a missing suggestion
- GIVEN two rows below minimo: one with no `id_proveedor_habitual` and a
  `sugerido` of `30`, and another with a proveedor assigned but
  `sugerido = null` (`reposicion` unset)
- WHEN the tile is computed
- THEN `sinProveedor = 1` — only the row missing a proveedor counts,
  independent of whether `sugerido` is null

### Requirement: The Reposición Export Sibling Is Catalog-Bounded, Never Truncated

`GET /api/reportes/stock/reposicion/export?formato=xlsx` MUST follow the
`exportacion-de-reportes` contract, use the aggregated-by-catalog cap shape
(like `existencias`, not the growing-listado shape), and its figures MUST
equal the JSON endpoint's for identical parameters. At cap it MUST refuse
rather than silently truncate.

#### Scenario: The export's figures equal the JSON endpoint's
- GIVEN `GET /api/reportes/stock/reposicion?idPuntoVenta=7` returns 5 rows
  with a combined `sugerido` of `120`
- WHEN `GET /api/reportes/stock/reposicion/export?formato=xlsx` is
  requested for the same parameters
- THEN the workbook's rows sum to the same `120`

#### Scenario: The export refuses rather than truncates at cap
- GIVEN a catalog whose reposición rows exceed the export cap
- WHEN the export is requested
- THEN it responds with a refusal, never a silently truncated file

## Notes (non-normative, documented from design.md)

- **Rotation's denominator is the nominal window (`dias_rotacion`), not the
  number of days actually elapsed since the articulo's first movement.** An
  articulo received partway through the window shows a fraction of its
  real consumption rate until enough history accumulates. Accepted as-is —
  not a defect to fix in this stage — because rotation is advisory and
  never gates an alert (see "Rotation Excludes Purchase-Reversal
  Anulaciones And Is Advisory-Only" above).
- **`transferencia` movements never count as consumption, including the
  outbound leg of a transfer to another punto de venta.** Rotation is
  computed strictly from `movimientos_stock` local to the punto de venta
  being reported on, so a punto de venta that only ships stock elsewhere
  (a warehouse) shows zero rotation and no suggested minimo for that
  reason — a correct answer to "what did customers buy here", not a bug.
