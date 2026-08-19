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
transitions `estado`, it is never deleted. `items_comprobante_compra`
additionally carries draft-time lot input `codigo_lote text NULL` and
`fecha_vencimiento date NULL` (captured while `estado = borrador`), guarded
by `ck_items_comprobante_compra_lote_input`
(`(codigo_lote IS NULL AND fecha_vencimiento IS NULL) OR fecha_vencimiento IS NOT NULL`
— a lot code without an expiry can never resolve to a valid `lotes` row),
and a resolved `id_lote integer NULL`, populated only at `Confirmar` and
NULL while the compra is a draft or for non-lot articulos.
(Previously: silent on lot input/resolution — the three columns did not
exist until stage 12.)

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
until stage 12.)

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
be anulada. For a lot-bearing item (`id_lote NOT NULL`), the reversal MUST
target that item's own `id_lote` snapshot, upsert `stock_lotes` for that lot
in the same transaction, and the negative-balance refusal MUST also apply at
the **lot** level: an anulación that would leave the item's own lot balance
negative MUST be refused with `409 compra_anulacion_stock_negativo`, even if
the articulo's aggregate `stock.cantidad` would stay non-negative.
(Previously: the negative-balance check was aggregate-only — a lot-level
underflow behind a sufficient aggregate was undetectable until stage 12.)

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

### Requirement: Anulación Is Attributable, Same Transaction As The Contramovimientos

Every anulación via `ServicioDeCompras.AnularAsync` MUST write an
`auditoria` row (`accion = "compra.anulacion"`, owned by the
`auditoria-de-operaciones` capability) in the same transaction as its stock
contramovimientos and the `estado` transition to `anulada`. A failure of
the audit write MUST fail the anulación exactly as the existing
negative-stock refusal does — no partial state, no reversal without
attribution.

#### Scenario: A compra anulación is attributable to its actor
- GIVEN a confirmada compra of 50 units, none yet sold
- WHEN an Admin anula it
- THEN the `auditoria` row's `id_actor` identifies that Admin, written in
  the same transaction as the `-50` `movimientos_stock` row

#### Scenario: An audit failure blocks the anulación, same as the negative-stock refusal
- GIVEN the audit writer is forced to fail during anulación of a
  confirmada compra with sufficient stock to reverse
- WHEN the transaction is attempted
- THEN `estado` remains `confirmada` and no `movimientos_stock`
  contramovimiento exists

### Requirement: Confirmar Writes A Compra Movement To The Proveedor Ledger

`ConfirmarAsync` MUST write exactly one positive `compra` movement to
`movimientos_cuenta_corriente_proveedor` in its existing transaction,
carrying `id_comprobante_compra` and the comprobante's `total` as `importe`,
with `proveedores` locked as the transaction's last row lock before the
ledger INSERT.

#### Scenario: Confirming a compra writes exactly one debt movement
- GIVEN a borrador of `total = 5000`
- WHEN it is confirmed
- THEN exactly one `compra` movement of `importe = 5000` is written
  alongside the existing stock and cost effects

### Requirement: Anulación Writes A Reversing Ajuste, Gastos Are Still Not Reversed

`AnularAsync` MUST write exactly one negative `ajuste` movement — of
magnitude equal to the original `compra` movement, `id_comprobante_compra`
set to the annulled compra — in the same transaction as the existing stock
contramovimientos. Linked `gastos` and their ledger `pago` movements MUST
NOT be touched, and the informational `gastosLigados` count stays unchanged.

#### Scenario: Anulación writes a reversing ajuste without touching linked gastos
- GIVEN a confirmada compra of `1000` with a linked gasto of `600`
- WHEN it is anulada
- THEN a `-1000` reversing `ajuste` is written, the linked gasto and its
  `pago` movement remain untouched, and the compra's `gastosLigados` count
  is unchanged

### Requirement: A Comprobante Compra MAY Carry A Linked Orden De Compra

`comprobantes_compra` MUST gain a nullable `id_orden_compra`. It MAY be set or changed only while
the comprobante is `borrador`, and the target `ordenes_compra` row MUST belong to the same
`id_tenant`, `id_proveedor`, and `id_punto_venta` as the comprobante — a cross-table rule the schema
cannot express, enforced in the service under `FOR SHARE` (`ExigirCompraLigableAsync`'s pattern).
Once the comprobante transitions past `borrador`, the link MUST be frozen. An unlinked comprobante
(`id_orden_compra IS NULL`) remains a fully legitimate, unrelated purchase — 100% of today's rows.

#### Scenario: A borrador draft links to a matching OC
- GIVEN a borrador compra for the same proveedor and punto de venta as an enviada OC
- WHEN the draft sets `id_orden_compra` to that OC
- THEN the link is accepted and persisted

#### Scenario: A draft cannot link to an OC of a different proveedor, PV or tenant
- GIVEN a borrador compra and an OC that disagrees on proveedor, punto de venta, or tenant
- WHEN the draft attempts to set that `id_orden_compra`
- THEN it is refused before any write, under the `FOR SHARE` guard

#### Scenario: The link is frozen once the compra is confirmed
- GIVEN a compra confirmed with `id_orden_compra` set
- WHEN any client attempts to change that link afterward
- THEN no endpoint permits it

### Requirement: Confirming A Linked Comprobante Refreshes The Orden De Compra In The Same Transaction

`EjecutarConfirmarAsync` MUST, when `id_orden_compra IS NOT NULL`, lock the OC row (`SELECT … FOR
UPDATE`, immediately after the comprobante header lock and before `proveedores`), re-read the
derived reception book, and `UPDATE … RETURNING` the OC's `estado` per the projection its own
capability defines — all inside the same transaction as the confirm. Confirming a comprobante
linked to an `anulada` OC MUST be refused with `409 orden_compra_anulada` and no write. For a
comprobante with `id_orden_compra IS NULL` — the unchanged case — `EjecutarConfirmarAsync` MUST
emit **zero** extra statements beyond its existing steps.

#### Scenario: Confirming a linked reception updates the OC in the same transaction
- GIVEN a borrador comprobante linked to an enviada OC, covering part of one pending item
- WHEN the comprobante is confirmed
- THEN the OC's `estado` becomes `recibida_parcial` in the same transaction that wrote the
  comprobante's stock and cost effects

#### Scenario: Confirming against an annulled OC is refused
- GIVEN a borrador comprobante linked to an `anulada` OC
- WHEN it is confirmed
- THEN it is rejected with `409 orden_compra_anulada` and none of the confirm's usual writes occur

#### Scenario: An unlinked confirm emits zero extra statements
- GIVEN a borrador comprobante with `id_orden_compra IS NULL`
- WHEN it is confirmed
- THEN the exact same statement sequence as before this stage executes — no OC lock, no OC read, no
  OC update

### Requirement: Annulling A Linked Comprobante Refreshes The Orden De Compra, Reopening Unless Manually Closed

`EjecutarAnulacionAsync` MUST, when `id_orden_compra IS NOT NULL`, apply the same lock → re-read →
`UPDATE … RETURNING` sequence as confirm, inside the same transaction as the stock
contramovimientos. An automatically-closed OC MUST be reopened to `recibida_parcial` or `enviada`
when its receptions no longer add up to a full delivery. A **manually**-closed OC
(`id_empleado_cierre IS NOT NULL`) MUST NOT be reopened by this path. For a comprobante with
`id_orden_compra IS NULL`, `EjecutarAnulacionAsync` MUST emit **zero** extra statements.

#### Scenario: Annulling a reception reopens an automatically-closed OC
- GIVEN an OC automatically closed by full reception (`id_empleado_cierre IS NULL`)
- WHEN its sole confirmed reception is anulada
- THEN the OC's `estado` returns to `enviada` in the same transaction as the stock reversal

#### Scenario: Annulling a reception does not reopen a manually-closed OC
- GIVEN an OC manually closed via `cerrar` (`id_empleado_cierre` set), with one confirmed reception
- WHEN that reception is anulada
- THEN the OC's `estado` remains `cerrada`

#### Scenario: An unlinked anulación emits zero extra statements
- GIVEN a confirmada comprobante with `id_orden_compra IS NULL`
- WHEN it is anulada
- THEN the exact same statement sequence as before this stage executes — no OC lock, no OC read, no
  OC update
