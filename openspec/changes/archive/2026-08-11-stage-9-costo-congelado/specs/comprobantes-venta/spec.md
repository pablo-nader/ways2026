# Delta for Comprobantes de Venta

## MODIFIED Requirements

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
`id_alicuota_iva`, `porcentaje_iva`, `costo_unitario`, `costo_es_estimado` at
emission time. No endpoint MUST ever update an item after emission — a
reprint MUST NOT re-join `articulos`, `precios`, or `ofertas`.
(Previously: the frozen list did not include `costo_unitario` /
`costo_es_estimado`; added by stage 9.)

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

## ADDED Requirements

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
