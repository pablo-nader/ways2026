# Delta for Comprobantes De Compra

## MODIFIED Requirements

### Requirement: Comprobante Compra Schema At Rest

`comprobantes_compra` MUST be operativa-scoped (`id_tenant` +
`id_punto_venta`, doc 09) carrying `id_proveedor`, `id_tipo_comprobante`
(clase = compra), `numero_externo citext NULL`, `fecha_comprobante`,
`fecha_recepcion`, `id_empleado`, `subtotal`, `descuento_total`,
`iva_total NULL`, `total`, `observaciones`, `estado estado_compra NOT NULL`
(`borrador | confirmada | anulada`). `items_comprobante_compra` MUST
reference its comprobante via FK and carry `id_articulo`, a `descripcion`
snapshot, `cantidad`, `bultos NULL`, `unidades_por_bulto NULL`,
`costo_unitario numeric(14,4)`, `descuento`, `id_alicuota_iva`,
`porcentaje_iva`, `total`, `actualiza_costo boolean DEFAULT true`,
`precio_sugerido NULL`. There is no baja lógica — the document only
transitions `estado`, it is never deleted. `items_comprobante_compra`
additionally carries draft-time lot input `codigo_lote text NULL` and
`fecha_vencimiento date NULL` (captured while `estado = borrador`), guarded
by `ck_items_comprobante_compra_lote_input`
(`(codigo_lote IS NULL AND fecha_vencimiento IS NULL) OR fecha_vencimiento IS NOT NULL`
— a lot code without an expiry can never resolve to a valid `lotes` row),
and a resolved `id_lote integer NULL`, populated only at `Confirmar` and
NULL while the compra is a draft or for non-lot articulos.
(Previously: silent on lot input/resolution — the three columns did not
exist until this stage.)

#### Scenario: A borrador is created with no items yet
- GIVEN an admin starts a compra for a proveedor at a punto de venta
- WHEN the borrador is created with no items
- THEN it persists with `estado = borrador` and no `movimientos_stock` row
  exists anywhere

#### Scenario: No delete endpoint exists for a comprobante compra
- GIVEN a comprobante compra in any estado
- WHEN any client attempts to call a delete endpoint
- THEN no such endpoint exists (404) — only state transitions are possible

#### Scenario: A borrador line captures lot input without resolving it
- GIVEN a borrador line for a lot-effective articulo supplies
  `codigo_lote = "L-002"`, `fecha_vencimiento = 2026-12-31`
- WHEN the line is saved
- THEN it persists `codigo_lote` and `fecha_vencimiento` as plain input, and
  `id_lote` stays NULL

#### Scenario: A lot code without an expiry is unrepresentable
- GIVEN a raw write attempts `codigo_lote = "L-003", fecha_vencimiento = NULL`
  on a draft item
- WHEN the insert executes
- THEN Postgres rejects it via `ck_items_comprobante_compra_lote_input`

### Requirement: Confirmar Is One All-Or-Nothing Transaction

Confirmar MUST, in a single transaction: insert one `movimientos_stock` row
per item (`motivo = compra`, `cantidad = +cantidad`, `id_comprobante_compra`
populated) at the comprobante's punto de venta, upsert the `stock` cache for
each, overwrite `articulos.costo_nominal` only for items with
`actualiza_costo = true`, compute and store `precio_sugerido` per item via
the existing `SugeridorDePrecio`, and set `estado = confirmada`. Any failure
at any step MUST roll back every write, leaving `estado`, stock, cache and
`costo_nominal` untouched. Only a `borrador` MAY be confirmed. For a
lot-effective articulo item carrying `codigo_lote`/`fecha_vencimiento`, the
same transaction MUST resolve (get-or-create) the `lotes` row against
`ux_lotes_articulo_codigo`, freeze `items_comprobante_compra.id_lote` to it,
insert the `movimientos_stock` row with that `id_lote`, and upsert
`stock_lotes` for that lot — all inside the same transaction as the
aggregate write. A get-or-create race under concurrent confirms MUST
self-resolve atomically: the `ON CONFLICT ... DO UPDATE ... RETURNING`
statement targets `ux_lotes_articulo_codigo` directly (design decision 4), so
Postgres serializes the race on the conflict target and the "loser" reuses
the winner's row — no exception surfaces on this path, and both confirms
succeed against the same lot. (Amended at slice-5 judgment-day: the original
wording claimed a `23505` backstop here; empirically no `23505` is ever
raised on the get-or-create path — that backstop belongs to the admin alta
path, a plain `INSERT` covered by `lotes-y-vencimientos`'s `409
lote_duplicado` scenario.)
(Previously: silent on the lot resolution step — confirm was aggregate-only
until this stage.)

#### Scenario: Confirmar writes stock, cache and cost together
- GIVEN a borrador with 2 items, one `actualiza_costo = true`
- WHEN it is confirmed
- THEN 2 `movimientos_stock` rows with `motivo = compra` exist, both stock
  caches increase by their item's `cantidad`, only the flagged item's
  `articulos.costo_nominal` changes, and `estado = confirmada`

#### Scenario: A failed confirm leaves everything untouched
- GIVEN a confirm that fails while upserting the second item's stock cache
- WHEN the transaction aborts
- THEN no `movimientos_stock` row from this confirm exists, no
  `costo_nominal` changed, and `estado` is still `borrador`

#### Scenario: Confirming an already-confirmada compra is rejected
- GIVEN a compra with `estado = confirmada`
- WHEN confirmar is requested again
- THEN it is rejected with `409 compra_ya_procesada` and no duplicate
  movements are written

#### Scenario: Confirmar get-or-creates a lot and freezes it onto the item
- GIVEN a borrador item for articulo 40 with `codigo_lote = "L-002"`,
  `fecha_vencimiento = 2026-12-31`, and no existing lot with that codigo
- WHEN the compra is confirmed
- THEN a `lotes` row is created (`codigo = "L-002"`), the item persists
  `id_lote` pointing to it, a `movimientos_stock` row of `motivo = compra`
  carries that `id_lote`, and `stock_lotes` for that lot increases by the
  item's `cantidad`

#### Scenario: Confirmar reuses an existing lot with a matching expiry
- GIVEN an active lot `(articulo 40, codigo "L-002", fecha_vencimiento = 2026-12-31)`
  already exists
- WHEN a borrador item for articulo 40 with the same `codigo_lote` and
  `fecha_vencimiento` is confirmed
- THEN the item resolves to the same `lotes` row, no new `lotes` row is
  created

#### Scenario: Confirmar rejects a conflicting expiry for an existing codigo
- GIVEN an active lot `(articulo 40, codigo "L-002", fecha_vencimiento = 2026-12-31)`
  already exists
- WHEN a borrador item for articulo 40 supplies `codigo_lote = "L-002"` with
  `fecha_vencimiento = 2027-02-01`
- THEN confirmar is rejected with `409 lote_vencimiento_incompatible` and no
  write occurs

#### Scenario: A concurrent get-or-create race self-resolves to one lot
- GIVEN two confirms for the same `(articulo 40, codigo_lote "L-002")` race
  each other
- WHEN both attempt to create the `lotes` row concurrently
- THEN the `ON CONFLICT` target on `ux_lotes_articulo_codigo` serializes the
  race inside Postgres, no exception surfaces, exactly one `lotes` row
  exists afterward, and both confirms succeed against that same lot
  *(amended at slice-5 judgment-day — see the requirement note above)*

### Requirement: Anulación Reverses By Contramovimientos, Refused When It Would Go Negative

Anulando a `confirmada` compra MUST, in one transaction, insert one
`movimientos_stock` row per item (`motivo = anulacion`, `cantidad` =
negation of the original compra movement, `id_comprobante_compra`
populated) and upsert the stock cache, then set `estado = anulada`. It MUST
be refused with `409 compra_anulacion_stock_negativo` naming the offending
articulos when any resulting stock cache would go negative — the goods
already left, so pulling them back would claim units that do not exist.
`articulos.costo_nominal` MUST NOT be reverted by anulación; a wrong cost is
corrected by editing the articulo directly. Only a `confirmada` compra MAY
be anulada. For a lot-bearing item (`id_lote NOT NULL`), the reversal MUST
target that item's own `id_lote` snapshot, upsert `stock_lotes` for that lot
in the same transaction, and the negative-balance refusal MUST also apply at
the **lot** level: an anulación that would leave the item's own lot balance
negative MUST be refused with `409 compra_anulacion_stock_negativo`, even if
the articulo's aggregate `stock.cantidad` would stay non-negative.
(Previously: the negative-balance check was aggregate-only — a lot-level
underflow behind a sufficient aggregate was undetectable until this stage.)

#### Scenario: Anulación reverses stock and restores the cache
- GIVEN a confirmada compra whose confirm added 50 units at a punto de venta
  with `stock.cantidad = 80`
- WHEN it is anulada
- THEN a `-50` `movimientos_stock` row with `motivo = anulacion` is
  inserted, `stock.cantidad = 30`, and `estado = anulada`

#### Scenario: Anulación refused when the goods were already sold
- GIVEN a confirmada compra added 50 units and 40 were since sold, leaving
  `stock.cantidad = 10`
- WHEN it is anulada
- THEN it is rejected with `409 compra_anulacion_stock_negativo` naming the
  articulo, and no movement is written

#### Scenario: Costo nominal is not reverted by anulación
- GIVEN a confirm overwrote `articulos.costo_nominal` to `150.00`
- WHEN the compra is later anulada (stock permitting)
- THEN `articulos.costo_nominal` remains `150.00`

#### Scenario: Anulando a borrador is rejected
- GIVEN a compra with `estado = borrador`
- WHEN anulación is requested
- THEN it is rejected with `409 compra_no_procesada` — a borrador has no
  ledger effect to reverse

#### Scenario: Anulación reverses a lot-bearing item into its exact lot
- GIVEN a confirmada compra item resolved `id_lote = 7` at confirm, having
  increased `stock_lotes.cantidad` for lot 7 by 50
- WHEN the compra is anulada and none of lot 7's units were since sold
- THEN the inverse `movimientos_stock` row carries `id_lote = 7`, and
  `stock_lotes.cantidad` for lot 7 decreases by 50

#### Scenario: Anulación refused by a lot-level underflow despite a sufficient aggregate
- GIVEN a confirmada compra added 50 units of lot 7, of which 40 were sold
  specifically from lot 7 (leaving `stock_lotes.cantidad = 10` for lot 7),
  while a separate lot 9 of the same articulo still holds 100 units
  (aggregate `stock.cantidad = 110`)
- WHEN the compra is anulada
- THEN it is rejected with `409 compra_anulacion_stock_negativo`, even
  though the aggregate is sufficient, because reversing lot 7's 50 units
  would leave it at `-40`

## ADDED Requirements

### Requirement: Expired Reception Is Refused

A borrador line supplying `fecha_vencimiento` in the past MUST be rejected
with `409 lote_vencido_en_recepcion` — receiving already-expired
merchandise is a supplier problem to resolve at the door, not a data entry
to accept. This check MUST fire when the line is saved or edited, not only
at confirm.

#### Scenario: A reception line with a past expiry is refused
- GIVEN today is `2026-08-12`
- WHEN a borrador line supplies `fecha_vencimiento = 2026-08-01`
- THEN it is rejected with `409 lote_vencido_en_recepcion` before reaching
  the database

#### Scenario: A future expiry is accepted
- GIVEN today is `2026-08-12`
- WHEN a borrador line supplies `fecha_vencimiento = 2026-12-31`
- THEN it is accepted
