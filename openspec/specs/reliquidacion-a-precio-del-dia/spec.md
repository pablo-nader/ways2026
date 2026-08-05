# Reliquidación a Precio del Día Specification

## Purpose

Defines F4 "Actualizar precios" (doc-01:394-402, doc-10:534-536): the
anti-inflation engine that re-indexes a cliente's fiado to the price of the
day at payment time. It walks not-yet-reliquidated `Consumo` movements,
re-prices their items against the cliente's CURRENT `id_lista_precio`,
reverts offer-line discounts, and writes exactly ONE `ActualizacionPrecios`
movement per run.

## Requirements

### Requirement: Eligibility Scan Covers Not-Yet-Reliquidated Consumos Only

Reliquidación MUST select every `Consumo` movement of the cliente not yet
marked reliquidated, regardless of age, and MUST skip movements already
marked by a prior run.

#### Scenario: A previously reliquidated consumo is skipped
- GIVEN a cliente with one reliquidated and one non-reliquidated consumo
- WHEN reliquidación runs
- THEN only the non-reliquidated consumo's items are re-priced

### Requirement: Re-Pricing Uses The Client's Current id_lista_precio

For each eligible consumo, reliquidación MUST re-price every underlying item
against the price currently in force for the cliente's CURRENT
`id_lista_precio` (via `listas_precio`/`precios`), not the lista recorded on
the item snapshot at sale time. Item snapshots are read-only inputs, never
mutated.

#### Scenario: Re-pricing reads the client's lista at reliquidación time
- GIVEN a cliente sold on lista 1 who is later moved to lista 2
- WHEN reliquidación runs
- THEN each line is re-priced against lista 2's current price, not lista 1

### Requirement: Offer-Line Discounts Are Reverted, Never Excluded

A line whose `items_comprobante_venta.id_oferta` is set MUST be re-priced to
the FULL current price with the discount annulled. It MUST NOT be excluded
from the walk, and MUST NOT be re-priced proportionally while preserving the
original discount ratio (doc-01:398 verbatim: *"Las líneas de oferta se
revierten, el descuento se anula"*).

**Worked example** — sold at list `100` with a `10` oferta discount (line
total `90`); current list price is now `120`:
- WRONG (ratio-preserving): `120 × (90/100) = 108` → delta `18`
- CORRECT (discount annulled): full current price `120` → delta
  `120 − 90 = 30`

#### Scenario: Offer line re-prices to the full current price
- GIVEN an offer line sold at `90` (list `100`, discount `10`) and a current
  list price of `120`
- WHEN reliquidación re-prices it
- THEN the line's delta is `30`, not `18`

#### Scenario: Non-offer line re-prices without any discount logic
- GIVEN a plain line sold at `100` and a current list price of `120`
- WHEN reliquidación re-prices it
- THEN the line's delta is `20`

### Requirement: One ActualizacionPrecios Movement Per Run, With Auditable Detail

A run with at least one eligible consumo MUST write exactly ONE
`actualizacion_precios` movement whose `importe` equals the sum of every
re-priced line's delta across every eligible comprobante, and whose
`detalle` carries a per-line audit (comprobante, item, old price, new price,
delta) sufficient to reconstruct the calculation.

#### Scenario: Two comprobantes, three lines, one movement
- GIVEN two eligible consumos totaling three lines with deltas `30`, `20`,
  and `-5`
- WHEN reliquidación runs
- THEN exactly one `ActualizacionPrecios` movement is written with
  `importe = 45`

### Requirement: Marker And Saldo Update Are Atomic With The Movement

In the same transaction: every eligible consumo MUST be marked reliquidated,
the `ActualizacionPrecios` movement MUST be inserted, and `Cliente.Saldo`
MUST be updated by the movement's `importe` via the same
`UPDATE ... RETURNING` pattern every other CC writer uses. A failure at any
step MUST leave saldo, marker, and ledger untouched.

#### Scenario: A fault-point failure rolls back the marker together with the movement
- GIVEN a reliquidación that fails while inserting the `ActualizacionPrecios`
  row
- WHEN the transaction aborts
- THEN no consumo is marked reliquidated and `Cliente.Saldo` is unchanged

### Requirement: A Run With No Eligible Consumos Is A No-Op

Reliquidación with zero eligible consumos MUST succeed with no
`ActualizacionPrecios` movement, no saldo change, and no marker write — this
is a successful no-op response, not an error.

#### Scenario: Running reliquidación twice in a row
- GIVEN a reliquidación that just ran and marked every consumo
- WHEN it is run again immediately
- THEN it succeeds with zero movements written and `Cliente.Saldo` unchanged

### Requirement: Reliquidación Is Irreversible; Correction Is A Compensating Ajuste

No endpoint MUST reverse or edit an `ActualizacionPrecios` movement. The only
correction path is a new, distinct `Ajuste` movement with its own detalle.

#### Scenario: No reversal endpoint exists for ActualizacionPrecios
- GIVEN an `ActualizacionPrecios` movement
- WHEN any client attempts to call a reversal/undo endpoint for it
- THEN no such endpoint exists (404)

### Requirement: Concurrent Reliquidación And Sale For The Same Cliente Are Serialized

Reliquidación MUST acquire the same cliente row lock the checkout
consumo-write path uses before computing and applying its saldo delta, so a
concurrent CC sale for the same cliente is serialized rather than
lost-updated or double-applied.

#### Scenario: A reliquidación and a concurrent CC sale race for the same cliente
- GIVEN a cliente targeted by a reliquidación and a CC sale at the same time
- WHEN both transactions commit
- THEN exactly one proceeds first, the other applies its delta against the
  post-first-commit saldo, and `Cliente.Saldo` reflects both changes with no
  lost update
