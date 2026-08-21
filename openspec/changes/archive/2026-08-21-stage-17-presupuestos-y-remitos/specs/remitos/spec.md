# Remitos Specification

## Purpose

Defines `remitos` + `items_remito` (doc-11:307-324, doc-10 §4-adjacent): a delivery note where the
stock leaves at `emitir`, invoiced later — alone or consolidated with others — into a non-fiscal
`TXR` comprobante. `ServicioDeRemitos` is the **fourth formal stock write site** (proposal decision
6), extending the `stock` capability's lock-order guarantee explicitly (see that capability's
delta). Owns the four-value lifecycle (`borrador | emitido | facturado | anulado`), its own number
assigned at `emitir` through the existing numbering mechanism (series `'REM'`), FEFO resolution and
stock exit at `emitir`, the reversal at `anular`, the product-only line rule, and the consolidated
invoicing contract: an itemless `TXR` comprobante linking N remitos, so the goods leave exactly
once. Greenfield: zero legacy paridad (explore.md:15).

## Requirements

### Requirement: Remito Schema At Rest

`remitos` MUST be operativa-scoped, carry `id_cliente`, `id_empleado`, `numero bigint NULL`,
`fecha_emision`, `fecha_salida NULL`, `direccion_entrega NULL`, `subtotal`/`descuento_total`/`total`,
`estado estado_remito NOT NULL`, `id_comprobante_venta NULL` (the consolidated invoice), and MUST
inherit `EntidadBase`. `ck_remitos_salida_completa` MUST enforce `numero` and `fecha_salida` arrive
together and that every state other than `borrador`/`anulado` has both. `ck_remitos_facturacion`
MUST enforce `(id_comprobante_venta IS NULL) = (estado <> 'facturado')` in both directions.
`items_remito` MUST reference its remito via FK, carry `orden`, `id_articulo NOT NULL` (**a remito
line MUST be a product** — `400` otherwise, a service is not loaded onto a truck), a `descripcion`
snapshot, `cantidad numeric(12,3) > 0`, the frozen price provenance, `costo_unitario`/
`costo_es_estimado` (frozen at exit — the stage-9 discipline: a cost is unrecoverable once the goods
left), and `id_lote NULL` (frozen FEFO for lot-effective articulos).

#### Scenario: A borrador remito is created with no items yet
- GIVEN an operator starts a remito for a cliente at a punto de venta
- WHEN the borrador is created with no items
- THEN it persists with `estado = borrador`, `numero IS NULL`

#### Scenario: A non-product line is rejected
- GIVEN a remito draft line references an articulo with `EsProducto = false`
- WHEN it is submitted
- THEN it is rejected with `400`

#### Scenario: A raw insert with a negative costo_unitario is rejected
- GIVEN a raw INSERT supplies `costo_unitario = -1`
- WHEN it reaches Postgres
- THEN it is rejected via `ck_items_remito_costo_no_negativo`, SQLSTATE `23514`

### Requirement: Borrador Is Mutable, Full Replace-Set Under FOR UPDATE

A `borrador` remito MUST be incrementally editable — header fields and a full item replace-set —
under `SELECT … FOR UPDATE … WHERE estado = 'borrador'`. Every other `estado` MUST reject the edit
endpoint.

#### Scenario: Editing a non-borrador remito is rejected
- GIVEN a remito with `estado = emitido`
- WHEN a client attempts the edit endpoint
- THEN it is rejected with `409`

### Requirement: Emitir Assigns The Own Number From Series 'REM', Resolves FEFO, And Is The Fourth Stock Write Site

`POST /{id}/emitir` MUST assign `numero` via `AsignadorDeNumeroComprobante` with
`tipo_comprobante = 'REM'`, stamping `fecha_salida` together with `numero` in the same
`UPDATE … WHERE estado = 'borrador' RETURNING`. In the same transaction, `ServicioDeRemitos` MUST,
for every line, resolve FEFO in the decide-then-commit read phase (unchanged rule, widened subject —
see `lotes-y-vencimientos` delta), insert one `movimientos_stock` row with `motivo = remito` and
`id_remito` set, and update `stock`/`stock_lotes` following the pinned lock order
`ORDER BY id_articulo, id_punto_venta, id_lote NULLS FIRST` — implemented **independently** of
`ServicioDeVentas`/`ServicioDeCompras`/`ServicioDeStock`, with its own concurrency test (see `stock`
capability's delta). `costo_unitario` MUST be frozen from today's `costo_nominal` at exit. Two
concurrent `emitir` requests at the same punto de venta MUST both succeed with two distinct numbers.
`ux_remitos_numero`'s `23505` MUST resolve, by exact constraint name, to `numero_de_remito_duplicado`
— the fifth occurrence of the `_numero` ordering trap.

#### Scenario: Emitting a remito moves stock with the remito's own motivo
- GIVEN a borrador remito with one line of 3 units of a non-lot articulo
- WHEN `emitir` is called
- THEN one `movimientos_stock` row of `-3` with `motivo = remito, id_remito = <this remito>` is
  inserted, and `stock.cantidad` decreases by 3

#### Scenario: A lot-effective line freezes FEFO on emitir
- GIVEN a remito line for a lot-effective articulo with two candidate lots
- WHEN `emitir` runs
- THEN the FEFO lot is resolved before the transaction opens and frozen onto
  `items_remito.id_lote`, and `stock_lotes` for that lot decreases accordingly

#### Scenario: A remito and a checkout competing for the same articulo and lot do not deadlock
- GIVEN a remito emitting from lot 7 of articulo 40 at punto de venta 1, and a concurrent checkout
  selling the same lot 7 of articulo 40
- WHEN both transactions run concurrently
- THEN both complete with no deadlock, because both build the identical ascending lock order

#### Scenario: A raw duplicate-number insert resolves to the remito's own domain code
- GIVEN a raw out-of-band INSERT attempts a `numero` already used at the same punto de venta
- WHEN it reaches Postgres
- THEN `ManejadorDeErrores` reports SQLSTATE `23505` translated to `numero_de_remito_duplicado`

### Requirement: A Remito Does Not Require An Open Turno To Be Emitted

A remito moves goods, not money: `emitir` MUST NOT require an open turno at the punto de venta.

#### Scenario: A warehouse can dispatch a remito with no till open
- GIVEN no open turno exists at a punto de venta
- WHEN a borrador remito there is emitted
- THEN the request succeeds

### Requirement: Anulación Reverses Stock With Inverse Movements And Is Rejected For A Facturado Remito

`POST /{id}/anular` MUST be allowed for `borrador` or `emitido`; a `facturado` remito MUST be
rejected with `409`. For an `emitido` remito, anulación MUST insert, in one transaction, the exact
inverse `movimientos_stock` rows (`motivo = anulacion`, same `id_remito`, same `id_lote` read from
the item snapshot — no re-derivation) and update `stock`/`stock_lotes` accordingly.

#### Scenario: Annulling an emitido remito reverses its exact movements
- GIVEN an emitido remito whose one line decremented stock by 3 and froze `id_lote = 7`
- WHEN it is anulado
- THEN a `+3` `movimientos_stock` row with `motivo = anulacion, id_remito = <this remito>,
  id_lote = 7` is inserted and both caches are restored

#### Scenario: A facturado remito cannot be annulled directly
- GIVEN a remito `estado = facturado`
- WHEN `anular` is called
- THEN it is rejected with `409`

### Requirement: Consolidated Invoicing Links N Remitos Into One Itemless TXR Comprobante

`POST /api/remitos/facturacion` MUST accept N remito ids of the same tenant, cliente and punto de
venta, all `emitido` and unlinked, lock them in ascending `id_remito` order, and emit one
comprobante of tipo `TXR` with `subtotal`/`descuento_total`/`total` summed from the remitos' frozen
items — the comprobante itself carries **zero items**. It MUST write pagos and, if cuenta corriente
is used, the `Consumo` movement through the existing `EscriturasDeCuentaCorriente`, and it MUST link
the remitos with one state-guarded `UPDATE … WHERE estado = 'emitido' RETURNING` whose row count
MUST equal the request's count — any mismatch is a race loser and MUST return `409`. It MUST write
**zero** `movimientos_stock` rows — the goods already left at remito time. It MUST require an open
turno, re-checked under `FOR SHARE` as its first statement (it takes money — unlike the remito
itself). A mixed-customer or already-invoiced set MUST be refused with `409`.

#### Scenario: Consolidating two remitos emits one itemless TXR
- GIVEN two emitido remitos of the same cliente and punto de venta, totaling `500.00`
- WHEN they are consolidated
- THEN one `TXR` comprobante is emitted with `total = 500.00`, zero items, and zero
  `movimientos_stock` rows; both remitos become `facturado` and link to it

#### Scenario: A race between two consolidation requests over the same remito yields one winner
- GIVEN one emitido remito requested by two concurrent consolidation calls
- WHEN both race
- THEN the state-guarded UPDATE's row count proves exactly one request links it; the other
  receives `409`

#### Scenario: A mixed-customer set is refused
- GIVEN two emitido remitos of different clientes
- WHEN a consolidation request names both
- THEN it is rejected with `409` before any write

#### Scenario: Consolidation requires an open turno
- GIVEN no open turno at the punto de venta
- WHEN a consolidation is attempted
- THEN it is rejected — the same gate the checkout applies

### Requirement: Authorization Mirrors /api/ventas Exactly — OperacionDePos, Nothing Stacked

`/api/remitos` and `/api/remitos/facturacion` MUST be grouped under `Politicas.OperacionDePos`
alone. `Politicas.cs` MUST NOT gain a new policy.

#### Scenario: Vendedor exercises the full remito lifecycle
- GIVEN a user with role Vendedor
- WHEN they create, emit, and consolidate remitos into an invoice
- THEN every request succeeds (authorization-wise)

### Requirement: RLS And Tenant Isolation Are Standard On Both Tables

`remitos` and `items_remito` MUST enforce RLS `FORCE`d with no `BYPASSRLS`, scoped by
`app_tenant_actual()`.

#### Scenario: RLS blocks a cross-tenant read
- GIVEN the app DB role has no `BYPASSRLS`
- WHEN a raw query reads another tenant's `remitos` rows while a different tenant GUC is set
- THEN it returns zero rows
