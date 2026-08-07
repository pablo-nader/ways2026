# Comprobantes de Compra Specification

## Purpose

Defines `comprobantes_compra` + `items_comprobante_compra` (doc 10 §5):
supplier-invoice identity via `numero_externo` (no correlativo propio), the
`borrador → confirmada → anulada` lifecycle, the confirm transaction (stock
entry + `costo_nominal` overwrite + cache), `precio_sugerido` as an audited
suggestion never auto-applied, anulación by contramovimientos with the
insufficient-stock refusal, and the compra-clase `tipos_comprobante` seed
that must never shadow a venta code. Greenfield: legacy C3 never persisted
(doc-01:203-208), so every rule here is a decision, not a port.

## Requirements

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
transitions `estado`, it is never deleted.

#### Scenario: A borrador is created with no items yet
- GIVEN an admin starts a compra for a proveedor at a punto de venta
- WHEN the borrador is created with no items
- THEN it persists with `estado = borrador` and no `movimientos_stock` row
  exists anywhere

#### Scenario: No delete endpoint exists for a comprobante compra
- GIVEN a comprobante compra in any estado
- WHEN any client attempts to call a delete endpoint
- THEN no such endpoint exists (404) — only state transitions are possible

### Requirement: Numero Externo Identity And Dedupe

`numero_externo` MUST be the document's identity — there is no correlativo
propio; `NumeracionComprobante` is never involved. The dedupe key
`UNIQUE (id_tenant, id_proveedor, id_tipo_comprobante, numero_externo)` MUST
ship as a PARTIAL index excluding rows with `estado = anulada` or
`numero_externo IS NULL`, so a mistyped invoice that was annulled can be
re-entered. `numero_externo` MAY be NULL while `borrador` but MUST be
non-NULL to confirm.

#### Scenario: The same invoice cannot be confirmed twice
- GIVEN a confirmada compra with `numero_externo = "0003-00012345"` for a
  proveedor and tipo
- WHEN a second borrador with the same proveedor, tipo and numero_externo is
  confirmed
- THEN it is rejected with `409 compra_duplicada`

#### Scenario: An annulled invoice number can be re-entered
- GIVEN a compra with `numero_externo = "0003-00012345"` that was confirmed
  and later anulada
- WHEN a new compra is created with the same proveedor, tipo and
  numero_externo
- THEN it is accepted — the partial index excludes the anulada row

#### Scenario: Confirming without a numero_externo is rejected
- GIVEN a borrador with `numero_externo = NULL`
- WHEN confirmar is requested
- THEN it is rejected with `400 compra_numero_externo_requerido` before any
  write

### Requirement: Borrador Is Mutable Because It Has No Ledger Effect

A `borrador` MUST be incrementally editable (header fields and item CRUD)
because it has produced no `movimientos_stock` row — this is the one
deliberate exception to the project's append-only posture, safe precisely
because nothing has moved yet. `confirmada` and `anulada` comprobantes MUST
be immutable: no header or item edit endpoint MUST succeed against them.

#### Scenario: Items can be added and removed while in borrador
- GIVEN a borrador compra
- WHEN items are added, edited and removed across several requests
- THEN each request succeeds and no `movimientos_stock` row is created by
  any of them

#### Scenario: A confirmada compra rejects an item edit
- GIVEN a compra with `estado = confirmada`
- WHEN a client attempts to edit one of its items
- THEN it is rejected with `409 compra_no_editable`

#### Scenario: An anulada compra rejects an item edit
- GIVEN a compra with `estado = anulada`
- WHEN a client attempts to edit one of its items
- THEN it is rejected with `409 compra_no_editable`

### Requirement: Confirmar Is One All-Or-Nothing Transaction

Confirmar MUST, in a single transaction: insert one `movimientos_stock` row
per item (`motivo = compra`, `cantidad = +cantidad`, `id_comprobante_compra`
populated) at the comprobante's punto de venta, upsert the `stock` cache for
each, overwrite `articulos.costo_nominal` only for items with
`actualiza_costo = true`, compute and store `precio_sugerido` per item via
the existing `SugeridorDePrecio`, and set `estado = confirmada`. Any failure
at any step MUST roll back every write, leaving `estado`, stock, cache and
`costo_nominal` untouched. Only a `borrador` MAY be confirmed.

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

### Requirement: Precio Sugerido Is A Suggestion, Never Auto-Applied

`precio_sugerido` MUST be computed per item at confirm time through the
existing pure `SugeridorDePrecio` and stored as an audit of the proposal —
confirmar MUST NEVER call `ServicioDePrecios.AbrirNuevoPrecioAsync` itself.
Applying the suggestion MUST be a separate, explicit action per item that
calls `AbrirNuevoPrecioAsync`, preserving price history.

#### Scenario: Confirm stores the suggestion without opening a new price
- GIVEN a confirmed item whose margin implies a higher sale price
- WHEN confirmar completes
- THEN `precio_sugerido` is populated on the item and no new `precios` row
  exists

#### Scenario: Applying the suggestion opens a new priced row
- GIVEN a confirmada compra item with a stored `precio_sugerido`
- WHEN the explicit apply-suggested-price action is called for that item
- THEN a new `precios` row opens through `AbrirNuevoPrecioAsync` and the
  previous price closes, preserving history

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
be anulada.

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

### Requirement: Compra-Clase Tipos Are Platform-Seeded Without Shadowing Venta Codes

Compra-clase `tipos_comprobante` rows MUST seed with globally-unique,
prefixed `codigo` values (`C-FA`, `C-FB`, `C-FC`; `clase = compra`,
`signo = +1`, `afecta_stock = true`, `es_fiscal = false`) via a dual path
(seed-list + idempotent migration insert guarded by `AND EXISTS`,
preventing the fresh-database seeder-skip bug). No compra `codigo` MUST
ever collide with an existing venta `codigo`, because
`ux_tipos_comprobante_codigo` is unique on `codigo` alone and venta
resolution looks up by `codigo` alone.

#### Scenario: Every venta code still resolves to its venta row after the seed
- GIVEN the compra-clase seed has run
- WHEN `ResolverTipoComprobanteAsync` resolves `"FA"`
- THEN it returns the venta `tipos_comprobante` row, not a compra row

#### Scenario: The seed does not break a fresh-database boot
- GIVEN a fresh database with no catalog rows yet
- WHEN the application boots and both the migration insert and the
  emptiness-guarded seeder run
- THEN every catalog row exists exactly once, including all compra-clase
  and venta-clase `tipos_comprobante`

### Requirement: Authorization

Borrador CRUD, confirmar, anular, and the price-application action MUST be
gated by `Politicas.GestionDeCatalogo` stacked over `Politicas.OperacionDePos`
(Admin-only). Compra list and compra detail reads MUST stay on
`Politicas.OperacionDePos` (Vendedor + Admin).

#### Scenario: Admin confirms and anula a compra
- GIVEN a user with role Admin
- WHEN they confirm a borrador and later anular the resulting compra
- THEN both requests succeed (authorization-wise)

#### Scenario: Vendedor is blocked from every compra write path
- GIVEN a user with role Vendedor
- WHEN they call borrador create, confirmar, anular or the
  price-application endpoint
- THEN every request is rejected with `403`

#### Scenario: Vendedor can read the compra list
- GIVEN a user with role Vendedor
- WHEN they list compras for their tenant
- THEN the request succeeds
