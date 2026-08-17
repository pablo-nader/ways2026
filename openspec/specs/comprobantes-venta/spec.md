# Comprobantes de Venta Specification

## Purpose

Defines `comprobantes_venta` + `items_comprobante_venta` + `pagos_comprobante`
(doc 10 §4): schema at rest, checkout as one all-or-nothing transaction,
payment validation (legacy B6 parity, parametrized), snapshot immutability,
devoluciones as NCX, anulación with inverse movements, and the numbering
contract. No `restaurar` endpoint exists, ever.

## Requirements

### Requirement: Comprobante Schema At Rest

`comprobantes_venta` MUST be operativa-scoped (`id_tenant` + `id_punto_venta`,
doc 09) with `estado_comprobante` enum (`emitido | anulado`), `id_turno_caja`
referencing `turnos_caja` via FK — **required for every new sale**, resolved
server-side from the punto de venta's open turno and never client-supplied.
Comprobantes emitted before this stage keep `id_turno_caja = NULL`
permanently (decision 8: no backfill). `id_comprobante_asociado` nullable,
`UNIQUE (id_punto_venta, id_tipo_comprobante, numero)`. `items_comprobante_venta`
and `pagos_comprobante` MUST reference their comprobante via FK.
`items_comprobante_venta` additionally carries `costo_unitario numeric(14,2)
NULL` and `costo_es_estimado boolean NOT NULL DEFAULT false`, guarded by two
CHECKs: `ck_items_comprobante_venta_costo_no_negativo`
(`costo_unitario IS NULL OR costo_unitario >= 0`) and
`ck_items_comprobante_venta_estimado_con_costo`
(`NOT costo_es_estimado OR costo_unitario IS NOT NULL`).
(Previously: did not carry a cost column on the item; added by stage 9.)

#### Scenario: Every new sale carries the resolved open turno
- GIVEN a Vendedor with an open turno at punto de venta 7
- WHEN checkout completes
- THEN the persisted comprobante's `id_turno_caja` equals the open turno's
  id, not NULL

#### Scenario: Duplicate numero within the same punto de venta and tipo is rejected
- GIVEN a non-standard write path bypasses the atomic numeración allocator
- WHEN two rows are inserted with the same `(id_punto_venta, id_tipo_comprobante, numero)`
- THEN Postgres raises 23505 and `ManejadorDeErrores` maps it to 409

#### Scenario: Stage-5 NULL-turno comprobantes stay untouched
- GIVEN a comprobante emitted in stage 5 with `id_turno_caja NULL`
- WHEN the system is queried after stage 6 ships
- THEN the row still has `id_turno_caja NULL` — no backfill process ever runs

#### Scenario: A negative costo_unitario is unrepresentable
- GIVEN a raw write attempts `costo_unitario = -1.00` on an item row
- WHEN the insert/update executes
- THEN Postgres rejects it via `ck_items_comprobante_venta_costo_no_negativo`

#### Scenario: An estimated row with no cost is unrepresentable
- GIVEN a raw write attempts `costo_es_estimado = true` with `costo_unitario = NULL`
- WHEN the insert/update executes
- THEN Postgres rejects it via `ck_items_comprobante_venta_estimado_con_costo`

### Requirement: Snapshot Immutability of Items

Every `items_comprobante_venta` row MUST copy `descripcion`, `codigo_barra`,
`id_area`, `precio_unitario`, `id_lista_precio`, `id_oferta`, `descuento`,
`id_alicuota_iva`, `porcentaje_iva`, `costo_unitario`, `costo_es_estimado`,
and — for a lot-effective articulo line — `id_lote` at emission time. `id_lote`
MUST be frozen at emission and never re-derived; it is what makes anulación
exact (the reversal reads the item's own lot, with no lookup and no
ambiguity). No endpoint MUST ever update an item after emission — a
reprint MUST NOT re-join `articulos`, `precios`, `ofertas`, or `lotes`.
(Previously: the frozen list did not include `id_lote` — the column did not
exist until stage 12.)

#### Scenario: Reprint is unaffected by a later catalog change
- GIVEN a comprobante emitted with an item snapshot `precio_unitario = 150.00`
- WHEN the article's live price later changes to `180.00` and the ticket is reprinted
- THEN the reprinted line still shows `150.00`, unchanged

#### Scenario: No item update endpoint exists
- GIVEN an emitted comprobante
- WHEN any client attempts to call an item-edit endpoint
- THEN no such endpoint exists (404) — the only mutation on a comprobante is anulación

#### Scenario: Emission freezes the live costo_nominal onto the line
- GIVEN an articulo with `costo_nominal = 121.00`
- WHEN a TX line of 2 units for that articulo is emitted
- THEN the item persists `costo_unitario = 121.00`, `costo_es_estimado = false`

#### Scenario: An articulo with no cost produces an honest gap, never zero
- GIVEN an articulo with `costo_nominal = NULL`
- WHEN a line for that articulo is emitted
- THEN the item persists `costo_unitario = NULL`, `costo_es_estimado = false` —
  distinct from an articulo with `costo_nominal = 0`, which persists
  `costo_unitario = 0`

#### Scenario: A lot-effective line freezes its resolved lot onto the snapshot
- GIVEN a sale line for a lot-effective articulo resolves to `id_lote = 7`
  (FEFO-defaulted or supplied)
- WHEN the item is persisted
- THEN `items_comprobante_venta.id_lote = 7`, and a later price/lot change
  does not alter it on reprint

#### Scenario: A non-lot articulo's item never carries a lot
- GIVEN a sale line for an articulo with `controla_lote = false`
- WHEN the item is persisted
- THEN `items_comprobante_venta.id_lote` is NULL

### Requirement: RC Joins The POS-Emittable Tipos, Non-Fiscal

The `RC` `tipos_comprobante` row MUST be emittable through the same
`ServicioDeVentas` checkout entry point as `TX`/`NCX`, with
`es_fiscal = false`, `afecta_stock = false`, `discrimina_iva = false`,
`letra = NULL`, `signo = +1` — no fiscal receipt path is created.

#### Scenario: RC emission never touches fiscal fields
- GIVEN a valid RC payment request
- WHEN it is emitted
- THEN the persisted comprobante has `discrimina_iva = false`, no
  `neto_gravado`/`iva_total`, and `letra = NULL`

### Requirement: Checkout Is One All-Or-Nothing Transaction

`ServicioDeVentas` checkout MUST write the comprobante, its items, its pagos,
the resulting `movimientos_stock`, the `stock` cache update, the numeración
allocation, and (if cuenta corriente was used, or the comprobante is an
itemless RC pago a cuenta) the `movimientos_cuenta_corriente` row inside a
single database transaction. For an RC comprobante specifically, items and
`movimientos_stock` are empty by construction (`afecta_stock = false`) — the
transaction still covers the comprobante, its pagos, the numeración
allocation, and the single `Pago` movement. Any failure at any step MUST
roll back every write, including the numeración allocation.
(Previously: described only the CC-sale consumo case; did not cover the
itemless RC path.)

#### Scenario: Successful checkout commits every write together
- GIVEN a cart of 2 items and a full efectivo payment
- WHEN checkout completes
- THEN the comprobante, its 2 items, its 1 pago, 2 movimientos_stock rows, the
  updated stock cache, and the allocated numero are all visible in the same
  read

#### Scenario: A failure after stock decrement rolls back everything
- GIVEN a checkout that decrements stock successfully but then fails CC
  credit-limit validation
- WHEN the transaction aborts
- THEN no comprobante, item, pago, movimiento_stock, or numeración advance is
  visible — the numeración counter is unchanged

#### Scenario: RC checkout commits with zero items and one Pago movement
- GIVEN a valid RC payment of `200.00` efectivo
- WHEN it completes
- THEN the comprobante persists with 0 items, 1 pago, 0 movimientos_stock,
  and 1 `movimientos_cuenta_corriente` `Pago` row, all in the same
  transaction

### Requirement: Payment Validation Rejection Order

Checkout payment validation MUST apply the legacy B6 rejection order, reading
`tolerancia_pago` and `vuelto_maximo` through `ServicioDeParametros` (punto de
venta > empresa > default) — never hardcoded:

1. All medios sum to 0 and total > 0 → rejected.
2. `sum(pagos) + tolerancia_pago < total` → rejected.
3. `vuelto > vuelto_maximo` → rejected.
4. `vuelto > 0` on a pago whose medio has `AdmiteVuelto = false` → rejected.
5. Cuenta corriente payment beyond `LimiteCredito` (unless `CreditoIlimitado`)
   → rejected.
6. A pago on a medio with `RequiereReferencia = true` and no `referencia` →
   rejected.

#### Scenario: Payment within tolerancia is accepted
- GIVEN `tolerancia_pago = 10`, a total of `100.00`, and a single efectivo
  pago of `95.00`
- WHEN checkout validates payment
- THEN the sale is accepted (`95 + 10 >= 100`)

#### Scenario: Payment below tolerancia is rejected
- GIVEN `tolerancia_pago = 10`, a total of `100.00`, and a single efectivo
  pago of `85.00`
- WHEN checkout validates payment
- THEN it is rejected before any write (`85 + 10 < 100`)

#### Scenario: Vuelto over the parametrized maximum is rejected
- GIVEN `vuelto_maximo = 20`, a total of `50.00`, and an efectivo pago of
  `75.00` (vuelto `25.00`)
- WHEN checkout validates payment
- THEN it is rejected (`25 > 20`)

#### Scenario: Vuelto rejected on a medio without AdmiteVuelto
- GIVEN a tarjeta medio (`AdmiteVuelto = false`) paid `120.00` against a
  `100.00` total
- WHEN checkout validates payment
- THEN it is rejected — no vuelto may be returned on that medio

#### Scenario: Referencia required and missing is rejected
- GIVEN a transferencia medio (`RequiereReferencia = true`) with no
  `referencia` supplied
- WHEN checkout validates payment
- THEN it is rejected before any write

#### Scenario: Tolerancia and vuelto_maximo resolve per punto de venta
- GIVEN punto de venta A overrides `vuelto_maximo = 30` while the empresa
  default is `20`
- WHEN a sale at punto de venta A pays a vuelto of `25.00`
- THEN it is accepted, because the punto-de-venta override wins

### Requirement: Cuenta Corriente Payment Gating

A pago with medio `Comportamiento = CuentaCorriente` MUST be rejected when the
cliente is Consumidor Final, regardless of `LimiteCredito`. For any other
cliente, it MUST be rejected when `saldo + importe > limite_credito` unless
`credito_ilimitado = true`.

#### Scenario: Consumidor Final cannot pay by cuenta corriente
- GIVEN a cart totaling `100.00` and Consumidor Final as the cliente
- WHEN checkout includes a cuenta corriente pago
- THEN it is rejected before any write

#### Scenario: Credit limit exceeded is rejected
- GIVEN a cliente with `saldo = 800`, `limite_credito = 1000`,
  `credito_ilimitado = false`
- WHEN a cuenta corriente pago of `300.00` is submitted (`800 + 300 = 1100 > 1000`)
- THEN it is rejected before any write

#### Scenario: CreditoIlimitado bypasses the limit
- GIVEN a cliente with `saldo = 5000`, `limite_credito = 1000`,
  `credito_ilimitado = true`
- WHEN a cuenta corriente pago of `2000.00` is submitted
- THEN it is accepted

### Requirement: Numeración Allocation Is Atomic

Numero allocation MUST use `numeraciones_comprobante`'s `UPDATE ... SET
proximo_numero = proximo_numero + 1 ... RETURNING proximo_numero - 1` inside
the sale transaction, per `(id_punto_venta, id_tipo_comprobante)`. No number
is ever client-supplied. The visible format is `PPPP-NNNNNNNN`. `RC` MUST
allocate through the same per-`(id_punto_venta, id_tipo_comprobante)`
counter, independent from `TX`'s series.
(Previously: did not have an explicit scenario naming RC as an independent
series.)

#### Scenario: Concurrent sales at the same punto de venta get consecutive numbers
- GIVEN two concurrent checkouts at the same punto de venta and tipo TX
- WHEN both transactions commit
- THEN one receives `numero = N` and the other `N+1`, with no gap and no
  duplicate

#### Scenario: A rolled-back sale leaves an accepted gap
- GIVEN a checkout allocates `numero = 42` and then fails a later validation,
  rolling back
- WHEN the next successful sale at the same punto de venta and tipo runs
- THEN it receives `numero = 43` — the gap at 42 is accepted (non-fiscal TX/NCX)

#### Scenario: RC and TX numerar independently at the same punto de venta
- GIVEN TX is at `numero 50` and RC has never been emitted at punto de venta 7
- WHEN an RC is emitted there
- THEN it receives `numero = 1`, and the next TX still receives `numero = 51`

### Requirement: Devoluciones As NCX Comprobantes

A devolución MUST be emitted as a comprobante of tipo NCX (`signo` negative)
through the same checkout flow, with negative-quantity or negative-total
lines. `id_comprobante_asociado` is optional but MUST be populated when the
devolución references an original comprobante — this stage does NOT make it
mandatory for lot-controlled lines. For a lot-effective articulo, an NCX
line MUST carry an explicit `idLote`. The POS MUST suggest it — from the
associated comprobante's item snapshot when `id_comprobante_asociado` is
present, otherwise from the articulo's existing lots — but the suggestion is
never auto-applied without operator confirmation; the sin-identificar lot
remains a valid, always-available choice when the operator cannot identify
the physical lot. Returning into an expired lot MUST be permitted.
(Previously: silent on the lot dimension of NCX lines — no lot reference
existed on `items_comprobante_venta` until stage 12.)

#### Scenario: Standalone devolución without an original
- GIVEN a devolución with no referenced comprobante
- WHEN it is emitted
- THEN `id_comprobante_asociado` is NULL and the comprobante persists as NCX

#### Scenario: Devolución referencing an original
- GIVEN an original TX comprobante `id = 501`
- WHEN a devolución is emitted against it
- THEN the new NCX comprobante's `id_comprobante_asociado = 501`

#### Scenario: An NCX line for a lot-effective articulo requires idLote
- GIVEN articulo 40 is lot-effective
- WHEN an NCX line for articulo 40 is submitted with no `idLote`
- THEN it is rejected before reaching the database

#### Scenario: idLote is suggested from the associated comprobante's snapshot
- GIVEN an original TX comprobante `id = 501` whose item for articulo 40
  carries `id_lote = 7`
- WHEN a devolución referencing `id_comprobante_asociado = 501` is prepared
  in the POS
- THEN the suggested `idLote` for the returned line is `7`

#### Scenario: idLote is required even without an associated comprobante
- GIVEN a standalone devolución (no `id_comprobante_asociado`) for a
  lot-effective articulo
- WHEN the operator cannot identify the physical lot
- THEN the sin-identificar lot is accepted as `idLote`, and the request
  succeeds

#### Scenario: Returning into an expired lot is permitted
- GIVEN lot 9 of articulo 40 has `fecha_vencimiento` in the past
- WHEN a devolución line supplies `idLote = 9`
- THEN it is accepted — the returned units are honestly recorded as expired

### Requirement: Anulación Reverses Stock and CC, Never Restores by Editing, and Is Blocked By A Closed Turno

Anulación MUST reject with `409 turno_cerrado` when the comprobante's
`id_turno_caja` references a turno whose `estado = cerrado` — comprobantes
with `id_turno_caja NULL` (stage-5 era) are exempt from this gate (decision
5). Otherwise, in one transaction: set `estado = anulado`, insert inverse
`movimientos_stock` rows (opposite sign, `motivo = anulacion`) for every item
with `id_articulo NOT NULL`, and insert a `contramovimiento` in
`movimientos_cuenta_corriente` if the original comprobante produced a
`consumo` (CC sale) or a `pago` (RC) row — the reversal direction matches
the original row's sign. No `restaurar` endpoint MUST exist at any point.
For a lot-bearing item (`id_lote NOT NULL`), the inverse movement MUST carry
the **exact same `id_lote`** read from the item's own snapshot — no lookup,
no FEFO re-evaluation, no ambiguity — and MUST update that lot's
`stock_lotes` cache in the same transaction.
(Previously: the contramovimiento clause was scoped to a CC-sale consumo
only, and the inverse stock movement carried no lot dimension.)

#### Scenario: Anulación reverses stock movements
- GIVEN a comprobante whose sale decremented stock by 3 units of an articulo
- WHEN it is anulado
- THEN a new `movimientos_stock` row of `+3` with `motivo = anulacion` is
  inserted, and `stock.cantidad` reflects the reversal

#### Scenario: Anulación reverses a cuenta corriente consumo
- GIVEN a comprobante paid partly by cuenta corriente (`consumo = 200`)
- WHEN it is anulado
- THEN a `movimientos_cuenta_corriente` contramovimiento of `-200` is
  inserted and `Cliente.Saldo` decreases by `200`

#### Scenario: Anulación is idempotent-safe against double-anulación
- GIVEN a comprobante already `estado = anulado`
- WHEN a second anulación request is submitted
- THEN it is rejected with a domain validation error and no duplicate inverse
  movements are created

#### Scenario: No restaurar endpoint exists
- GIVEN an anulado comprobante
- WHEN any client attempts to call a restaurar/undo-anulación endpoint
- THEN no such endpoint exists (404)

#### Scenario: Anulación rejected when the comprobante's turno is closed
- GIVEN a comprobante whose `id_turno_caja` points to a turno with `estado =
  cerrado`
- WHEN anulación is requested
- THEN it is rejected with `409 turno_cerrado` and no stock/CC reversal is
  written

#### Scenario: Stage-5 NULL-turno comprobante stays anulable
- GIVEN a comprobante with `id_turno_caja NULL`
- WHEN anulación is requested
- THEN it succeeds — the closed-turno gate only fires when a turno exists
  and is closed

#### Scenario: RC anulación is blocked by a closed turno
- GIVEN an RC comprobante whose turno is now `cerrado`
- WHEN anulación is requested
- THEN it is rejected with `409 turno_cerrado`, same as any other comprobante

#### Scenario: Anulación of a lot-bearing sale reverses the exact lot
- GIVEN a sale item persisted `id_lote = 7`, having decremented
  `stock_lotes.cantidad` for lot 7 by 4
- WHEN the comprobante is anulado
- THEN the inverse `movimientos_stock` row carries `id_lote = 7` (read from
  the item snapshot, not re-derived), and `stock_lotes.cantidad` for lot 7
  increases by 4

### Requirement: OperacionDePos Authorization For Emission and Anulación

Emitting and anulando comprobantes MUST be gated by `Politicas.OperacionDePos`
(Vendedor + Admin). Reading comprobantes MUST be gated by the same policy.

#### Scenario: Vendedor can emit and anular
- GIVEN a user with role Vendedor
- WHEN they call checkout and later anulación on their own tenant's comprobante
- THEN both requests succeed (authorization-wise)

#### Scenario: Unauthenticated request is rejected
- GIVEN no bearer token
- WHEN checkout is called
- THEN the request is rejected with 401

### Requirement: Comprobante-Letter Resolution Stays Dormant

A pure, DB-free `ResolvedorDeLetraComprobante` Domain class MUST implement the
condición-fiscal-cross letter rule (fully unit-tested) but MUST NOT be wired
to any endpoint or write path in this stage — the POS only ever emits TX
(venta) and NCX (devolución), neither of which is fiscal
(`tipos_comprobante.es_fiscal = false`).

#### Scenario: The resolver is a pure function with no side effects
- GIVEN two condición fiscal inputs (emisor, receptor)
- WHEN `ResolvedorDeLetraComprobante` resolves the letter
- THEN it returns a value with no database read or write

#### Scenario: No endpoint exposes letter resolution
- GIVEN the POS API surface
- WHEN it is inspected for a letter-resolution endpoint
- THEN none exists — the class is reachable only from unit tests

### Requirement: Tenant and Punto de Venta Isolation

`comprobantes_venta`, `items_comprobante_venta`, and `pagos_comprobante` MUST
enforce the two-layer isolation guarantee (EF Core global query filter +
Postgres RLS without `BYPASSRLS`) for `id_tenant`, and every checkout/read
MUST require an explicit `idPuntoVenta` — there is no server-side "current
punto de venta" session state.

#### Scenario: RLS blocks a cross-tenant read
- GIVEN the app DB role has no `BYPASSRLS`
- WHEN a raw SQL query reads tenant 2's comprobantes while `app.tenant_id = 1`
- THEN RLS returns zero rows

#### Scenario: idPuntoVenta is required per request
- GIVEN a checkout request with no `idPuntoVenta`
- WHEN it is validated
- THEN it is rejected before reaching the database

### Requirement: Cost Snapshot Semantics, NCX Freeze, And No-Exposure

`costo_unitario` MUST be a three-state value per line: `(NOT NULL, false)` =
real snapshot, `(NOT NULL, true)` = backfilled approximation, `(NULL, false)`
= unknown cost. A nota de crédito (NCX) MUST freeze its own `costo_unitario`
at its own emission from the live `articulo.costo_nominal`, independent of
any original comprobante — it MUST NOT copy the original line's cost. On
every line, `costo_unitario` is stored unsigned per unit, exactly like
`precio_unitario`; the sign lives in `cantidad`. `costo_unitario` MUST NEVER
be exposed through `ItemEmitido`, `ComprobanteEmitido`, or any other
sale-facing API response or ticket/POS payload.

#### Scenario: An NCX freezes its own current cost with the sign reversing on its own
- GIVEN a TX sold an articulo at `costo_unitario = 100.00` and the articulo's
  `costo_nominal` later changes to `110.00`
- WHEN an NCX devolución for that articulo is emitted
- THEN the NCX item persists `costo_unitario = 110.00` (its own emission
  cost, not `100.00`) and `costo_unitario × cantidad` is negative, because
  `cantidad` is negative on the NCX

#### Scenario: The emit response never carries cost
- GIVEN a checkout that emits a comprobante with priced items
- WHEN the response DTO is inspected
- THEN `ItemEmitido` / `ComprobanteEmitido` contain no `costo_unitario` or
  `costo_es_estimado` field

### Requirement: One-Shot Backfill Marks Pre-Existing Rows As Estimated

The stage-9 migration MUST backfill every pre-existing `items_comprobante_venta`
row with `id_articulo NOT NULL`, a non-NULL `articulos.costo_nominal`, and
`costo_unitario IS NULL`, setting `costo_unitario` to that `costo_nominal` and
`costo_es_estimado = true`. Because every tenant table enforces `FORCE ROW
LEVEL SECURITY` and the application role has no `BYPASSRLS`, the backfill
MUST run with `SET LOCAL app.acceso = 'plataforma'` inside the migration
transaction — a plain `UPDATE` outside platform mode would match zero rows
and report success. The backfill MUST be idempotent by construction.

#### Scenario: Platform mode reaches every tenant's rows, not just one
- GIVEN pre-stage-9 item rows exist for both tenant A and tenant B,
  referencing priced articulos
- WHEN the migration's backfill runs under `app.acceso = 'plataforma'`
- THEN every reachable row of both tenant A and tenant B is updated to
  `costo_es_estimado = true`, proven by a multi-tenant fixture

#### Scenario: Re-running the backfill is a no-op
- GIVEN a row already backfilled with `costo_unitario` set and
  `costo_es_estimado = true`
- WHEN the backfill statement runs again
- THEN the row is unchanged, because `WHERE costo_unitario IS NULL` excludes
  it

### Requirement: FEFO Lot Is Decided In The Read Phase And Frozen On The Item

For a lot-effective articulo line, `idLote` on the checkout request MUST be
optional. When omitted, `ServicioDeVentas.EmitirAsync` MUST select the FEFO
lot in its decide-then-commit read phase, before the transaction opens —
never inside the retryable transaction lambda. When `idLote` is supplied,
the server MUST validate it and honour it. The per-lot `movimientos_stock`
write MUST occur inside the pinned stock-write lock order (`stock`
capability: `id_articulo, id_punto_venta, id_lote NULLS FIRST`).

#### Scenario: A cart with a lot-controlled and a non-lot articulo mixes both paths
- GIVEN a cart with one line for a lot-effective articulo (no `idLote`
  supplied) and one line for a non-lot articulo
- WHEN checkout runs
- THEN the FEFO lot for the first line is resolved before the transaction
  opens, the second line's stock decrement carries no lot dimension, and
  both writes commit in the same transaction

#### Scenario: A client that knows nothing about lots still transacts correctly
- GIVEN a legacy client submits a sale line for a lot-effective articulo
  with no `idLote` field in the payload
- WHEN checkout runs
- THEN the server silently applies its FEFO default and the sale succeeds

### Requirement: Expired Lot Sale Warns, Never Blocks

A sale or NCX line resolving (by FEFO default or explicit `idLote`) to a
lot whose `fecha_vencimiento` is in the past MUST be accepted. The response
MUST carry a warning flag identifying the expired line so the POS can
display it prominently. FEFO MUST pre-select a non-expired lot whenever one
exists with positive balance, so an expired-lot sale only happens when the
operator explicitly overrides the default or when no non-expired lot has
stock.

#### Scenario: A sale of an explicitly expired lot succeeds with a warning
- GIVEN lot 9 of articulo 40 has `fecha_vencimiento` in the past and
  positive balance, and it is the only lot with stock
- WHEN a sale line for articulo 40 is checked out
- THEN the sale succeeds and the response marks that line with an expired-lot
  warning

#### Scenario: FEFO prefers a non-expired lot when one has stock
- GIVEN articulo 40 has an expired lot with positive balance and a
  non-expired lot also with positive balance
- WHEN a sale line for articulo 40 omits `idLote`
- THEN the server selects the non-expired lot, and the response carries no
  expired-lot warning for that line

#### Scenario: A supplied idLote on a non-lot-effective line is rejected (Added at slice-7 judgment-day)
- GIVEN a sale line for an articulo that is NOT lot-effective (`controla_lote
  = false`, or the module is off for the empresa)
- WHEN the line carries a non-null `idLote`
- THEN the request is rejected with `400 lote_invalido` before reaching the
  database — the field has no destination on that line, so it is never
  silently ignored

### Requirement: Anulación Is Attributable Regardless Of Comprobante Composition

Every anulación via `EjecutarAnulacionAsync` MUST write an `auditoria` row
(`accion = "venta.anulacion"`, owned by the `auditoria-de-operaciones`
capability) in the same transaction as the `estado` transition to
`anulado` — independent of whether the comprobante carries product lines,
service-only lines, or a cuenta corriente movement to reverse. A failure of
the audit write MUST fail the anulación — `estado` MUST remain `emitido`
and no inverse `movimientos_stock`/`movimientos_cuenta_corriente` row MUST
exist. Previously, attribution existed only incidentally, through the
reversal ledgers' `id_empleado`, and was entirely absent for a
100%-servicio comprobante with no cuenta corriente to reverse.

#### Scenario: A 100%-servicio comprobante without cuenta corriente is attributable on anulación
- GIVEN a TX comprobante composed only of service lines (`id_articulo
  NULL` on every item) with no cuenta corriente pago
- WHEN it is anulado
- THEN an `auditoria` row naming the acting user exists, even though no
  `movimientos_stock` or `movimientos_cuenta_corriente` reversal row was
  written

#### Scenario: An audit failure blocks the anulación
- GIVEN the audit writer is forced to fail during anulación of a
  comprobante with 3 product lines and a cuenta corriente consumo
- WHEN the transaction is attempted
- THEN `estado` remains `emitido` and no inverse `movimientos_stock` or
  `movimientos_cuenta_corriente` row exists
