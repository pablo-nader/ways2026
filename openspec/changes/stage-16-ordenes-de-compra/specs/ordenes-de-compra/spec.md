# Órdenes de Compra Specification

## Purpose

Defines `ordenes_compra` + `items_orden_compra` (doc-11:268-285, doc-10 §5-adjacent): the intention
placed on a proveedor before a `comprobantes_compra` exists. Owns the five-value lifecycle
(`borrador | enviada | recibida_parcial | cerrada | anulada`), the own document number assigned at
`enviar` from the existing numbering mechanism, the ligadura invariant that admits only matching
receptions, the derived (never stored) pending quantity, the `estado` as a pure, idempotent
projection of that derivation plus two human decisions (manual close, anulación), the anulación rule
expressed over the book, the informational price deviation, the pre-load from the reposición list,
and the authorization gate mirrored from `/api/compras`. Greenfield: the legacy never had an order
document (doc-01:203-208), so every rule here is a decision, not a port. The OC moves no stock, no
cost and no debt by itself — those effects live exclusively in the reception's linked
`comprobantes_compra`, owned by that capability.

## Requirements

### Requirement: Orden De Compra Schema At Rest

`ordenes_compra` MUST be operativa-scoped (`id_tenant` + `id_punto_venta`, doc 09), carry
`id_proveedor`, `id_empleado` (creator), `numero bigint NULL`, `fecha_emision`, `fecha_envio NULL`,
`fecha_esperada date NULL`, `fecha_cierre NULL`, `id_empleado_cierre NULL`, `observaciones`,
`estado estado_orden_compra NOT NULL`, and MUST inherit `EntidadBase` (mutable throughout
`borrador`, ordinary soft delete). `items_orden_compra` MUST reference its OC via FK and carry
`orden`, `id_articulo NOT NULL`, a `descripcion` snapshot, `cantidad_pedida numeric(12,3) > 0`, and
`costo_unitario_estimado numeric(14,4) NULL` (`>= 0` when present — never a fact, never `>` `0`).
Neither table MUST carry a `cantidad_recibida` column: the received quantity is always derived from
the reception book, never stored.

#### Scenario: A borrador OC is created with no items yet
- GIVEN an admin starts an OC for a proveedor at a punto de venta
- WHEN the borrador is created with no items
- THEN it persists with `estado = borrador` and `numero IS NULL`

#### Scenario: A raw insert with a non-positive cantidad_pedida is rejected
- GIVEN a raw INSERT into `items_orden_compra` supplies `cantidad_pedida = 0`
- WHEN it reaches Postgres
- THEN it is rejected via `ck_items_orden_compra_cantidad_positiva`, SQLSTATE `23514`

#### Scenario: A raw insert with a negative costo_unitario_estimado is rejected
- GIVEN a raw INSERT supplies `costo_unitario_estimado = -1`
- WHEN it reaches Postgres
- THEN it is rejected via `ck_items_orden_compra_costo_no_negativo`, SQLSTATE `23514`

### Requirement: Borrador Is Mutable, Full Replace-Set Under FOR UPDATE

A `borrador` OC MUST be incrementally editable — header fields and a full item replace-set — under
`SELECT … FOR UPDATE … WHERE estado = 'borrador'`, mirroring `ActualizarBorradorAsync`. Every other
`estado` MUST reject the edit endpoint.

#### Scenario: A replace-set adds and removes items in one request
- GIVEN a borrador OC with 3 items
- WHEN it is edited to remove one item and add two new ones
- THEN the persisted item set matches exactly the new set, with no stale row left over

#### Scenario: Editing a non-borrador OC is rejected
- GIVEN an OC with `estado = enviada`
- WHEN a client attempts the edit endpoint
- THEN it is rejected with `409`

### Requirement: Enviar Assigns The Own Number From Series 'OC', Per Punto De Venta

`POST /{id}/enviar` MUST assign `numero` via the existing `AsignadorDeNumeroComprobante` with
`tipo_comprobante = 'OC'`, consumed only at `enviar` (never at draft creation), and MUST stamp
`fecha_envio` together with `numero` in the same `UPDATE … WHERE estado = 'borrador' RETURNING`.
Only a `borrador` MAY be sent. Two concurrent `enviar` requests for OCs at the same punto de venta
MUST both succeed with two distinct numbers — never a `409` from the numbering mechanism.
`ux_ordenes_compra_numero`'s `23505` MUST resolve, by exact constraint name evaluated **above** the
generic `_numero` classifier branch, to `numero_de_orden_duplicado` rather than the
`ux_clientes_numero` family — the third occurrence of this ordering trap.

#### Scenario: Enviar assigns the next number for that punto de venta
- GIVEN a borrador OC at punto de venta 1 with no prior OC sent there
- WHEN `enviar` is called
- THEN `numero = 1`, `fecha_envio` is set, and `estado = enviada`

#### Scenario: Two concurrent enviar calls at the same punto de venta never collide
- GIVEN two distinct borrador OCs at the same punto de venta
- WHEN both are sent concurrently
- THEN both succeed with two distinct `numero` values and neither response is `409`

#### Scenario: Sending an already-enviada OC is rejected
- GIVEN an OC with `estado = enviada`
- WHEN `enviar` is called again
- THEN it is rejected with `409` and `numero` is not reassigned

#### Scenario: A raw duplicate-number insert resolves to the OC's own domain code
- GIVEN a raw out-of-band INSERT attempts a `numero` already used at the same punto de venta
- WHEN it reaches Postgres
- THEN `ManejadorDeErrores` reports SQLSTATE `23505` translated to `numero_de_orden_duplicado`, not
  a `ux_clientes_numero`-family code

### Requirement: Estado Is A Projection Of The Derived Reception Book Plus Human Decisions

The received quantity per artículo MUST be derived as `SUM(items_comprobante_compra.cantidad)`
over the OC's linked **confirmed** comprobantes, `GROUP BY id_articulo`, matched against
`SUM(cantidad_pedida) GROUP BY id_articulo` on the OC's own items — grouped on both sides,
never matched line-to-line. `estado` MUST be recomputed as: `anulada` if already `anulada`
(terminal); `cerrada` if `id_empleado_cierre IS NOT NULL` (manual, never revisited); `cerrada` if
every ordered artículo is fully received; `recibida_parcial` if something was received;
`enviada` otherwise. This projection MUST run inside the same transaction as both
`EjecutarConfirmarAsync` and `EjecutarAnulacionAsync` of a linked comprobante, via
`SELECT … FOR UPDATE` on the OC row (locked immediately after the comprobante header lock and
before `proveedores`) followed by a separate re-read of the derivation and an
`UPDATE … RETURNING` — never a single self-referential `UPDATE`. An artículo received but never
ordered, and an over-delivery, MUST NOT be treated as an error.

#### Scenario: Confirming a linked reception moves the OC to recibida_parcial
- GIVEN an enviada OC with one item of `cantidad_pedida = 100`
- WHEN a linked comprobante confirming `40` units is confirmed
- THEN, in the same transaction, the OC's `estado` becomes `recibida_parcial`

#### Scenario: Confirming the remainder closes the OC automatically
- GIVEN a `recibida_parcial` OC with `60` units still pending on its one item
- WHEN a second linked comprobante confirming the remaining `60` units is confirmed
- THEN the OC's `estado` becomes `cerrada` with `id_empleado_cierre IS NULL`

#### Scenario: Annulling the only reception of an automatically-closed OC returns it
- GIVEN an OC automatically closed by full reception, with `id_empleado_cierre IS NULL`
- WHEN the sole linked comprobante that closed it is anulada
- THEN the OC's `estado` returns to `enviada`

#### Scenario: Duplicate OC lines and an over-delivery do not block the derivation
- GIVEN an OC with two lines for the same artículo (`cantidad_pedida = 20` and `30`) and a linked
  confirmed comprobante receiving `70` units of that artículo
- WHEN the pending quantity is computed
- THEN both sides are grouped by `id_articulo` (`50` ordered vs `70` received) and the OC reports a
  received-not-ordered surplus of `20` with no error and `estado = cerrada`

#### Scenario: Two concurrent confirmations of two receptions of the same OC never overwrite each other
- GIVEN an enviada OC with two linked draft comprobantes covering different lines
- WHEN both are confirmed concurrently
- THEN both commit, no deadlock occurs, and the resulting `estado` reflects the sum of both
  receptions — never only one of them

### Requirement: Manual Close Is A Human Decision The Projection Never Reverts

`POST /{id}/cerrar` MUST transition `enviada` or `recibida_parcial` to `cerrada`, stamping
`fecha_cierre` and `id_empleado_cierre` (the acting employee) in the same
`UPDATE … WHERE estado IN ('enviada','recibida_parcial') RETURNING`. Once `id_empleado_cierre IS NOT
NULL`, the projection of the previous requirement MUST NOT move the OC out of `cerrada` for any
reason, including a later annulment of one of its receptions.

#### Scenario: A supplier that will not complete an order is closed manually
- GIVEN an enviada OC with a partially received item and no more deliveries expected
- WHEN an admin calls `cerrar`
- THEN `estado = cerrada`, `fecha_cierre` and `id_empleado_cierre` are both set

#### Scenario: A manually-closed OC does not reopen when its reception is annulled
- GIVEN an OC manually closed via `cerrar` (`id_empleado_cierre` set) with one confirmed reception
- WHEN that reception's comprobante is anulada
- THEN the OC's `estado` remains `cerrada`

#### Scenario: Closing an already-closed OC is rejected
- GIVEN an OC with `estado = cerrada`
- WHEN `cerrar` is called again
- THEN it is rejected with `409`

### Requirement: Anulación Is Governed By The Book, With A Defense-In-Depth Guard On Confirm

`POST /{id}/anular` MUST be allowed only when `estado IN ('borrador','enviada')` AND the derived
received quantity is zero for every artículo AND no linked comprobante remains `borrador` (still
confirmable) — otherwise `409 orden_compra_con_recepciones`. `anulada` MUST be terminal: the
projection MUST NEVER move an OC out of it. Independently, confirming a comprobante whose linked
OC is `anulada` MUST be refused with `409 orden_compra_anulada`, checked under the same OC row
lock the projection takes, closing the race between a concurrent annulment and confirmation.

#### Scenario: An OC whose only reception was later annulled can itself be annulled
- GIVEN an OC whose derived received quantity is zero because its one reception was confirmed and
  later anulada
- WHEN `anular` is called
- THEN it succeeds and `estado = anulada`

#### Scenario: An OC with an effective reception cannot be annulled
- GIVEN a `recibida_parcial` OC with a real (not-annulled) confirmed reception
- WHEN `anular` is called
- THEN it is rejected with `409 orden_compra_con_recepciones`

#### Scenario: An OC with a still-confirmable linked draft cannot be annulled
- GIVEN an enviada OC with a linked comprobante still in `borrador`
- WHEN `anular` is called
- THEN it is rejected with `409 orden_compra_con_recepciones`

#### Scenario: Confirming against an annulled OC is refused
- GIVEN an anulada OC with a linked comprobante still in `borrador`
- WHEN that comprobante is confirmed
- THEN it is rejected with `409 orden_compra_anulada` and no write occurs

### Requirement: Ligadura Invariant — An OC Accepts Only Matching Receptions

An `ordenes_compra` row MUST accept a link from a `comprobantes_compra` row only when both share the
same `id_tenant`, `id_proveedor`, and `id_punto_venta`. A link set while the comprobante is
`borrador` MUST be frozen once that comprobante transitions past `borrador`.

#### Scenario: A mismatched proveedor cannot link to the OC
- GIVEN an OC for proveedor A
- WHEN a compra draft for proveedor B attempts to set that OC as `id_orden_compra`
- THEN the request is refused before any write

#### Scenario: A mismatched punto de venta cannot link to the OC
- GIVEN an OC at punto de venta 1
- WHEN a compra draft at punto de venta 2 attempts to link to it
- THEN the request is refused before any write

#### Scenario: A confirmed comprobante's link cannot be changed
- GIVEN a compra confirmed with `id_orden_compra` set
- WHEN any client attempts to change that link afterward
- THEN no endpoint permits it — the comprobante is immutable past `borrador`

### Requirement: RLS And Tenant Isolation Are Standard On Both New Tables

`ordenes_compra` and `items_orden_compra` MUST enforce RLS `FORCE`d with no `BYPASSRLS`, scoped by
`app_tenant_actual()`.

#### Scenario: RLS blocks a cross-tenant read
- GIVEN the app DB role has no `BYPASSRLS`
- WHEN a raw query reads another tenant's `ordenes_compra` rows while a different tenant GUC is set
- THEN it returns zero rows

#### Scenario: An INSERT with a foreign id_tenant is refused
- GIVEN a raw INSERT attempts `id_tenant` different from `app_tenant_actual()`
- WHEN it reaches Postgres
- THEN it is refused with `42501`

### Requirement: Authorization Mirrors /api/compras Exactly

`/api/ordenes-compra` MUST be grouped under `Politicas.OperacionDePos`; `POST`, `PUT`, `enviar`,
`cerrar` and `anular` MUST additionally stack `Politicas.GestionDeCatalogo`. `Politicas.cs` MUST NOT
gain a new policy for this stage.

#### Scenario: Vendedor reads but cannot write an OC
- GIVEN a user with role Vendedor
- WHEN they list/read OCs (200) and then attempt create, `enviar`, `cerrar` or `anular`
- THEN the reads succeed and every write is rejected with `403`

#### Scenario: Admin exercises the full lifecycle
- GIVEN a user with role Admin
- WHEN they create, send, and close an OC
- THEN every request succeeds (authorization-wise)

### Requirement: Price Deviation Is Informational And Costs No Column

The read model MUST show, per line and per order, `costo_unitario_estimado` versus the effective
cost of the linked confirmed comprobantes' lines (via the existing
`CalculadorDeCompra.CalcularCostoEfectivoDesdeItem`), computed on read from existing data — no new
column, threshold, or table. It MUST NEVER block a confirmation. When
`costo_unitario_estimado IS NULL`, the deviation MUST report an explicit *no comparable* state,
never `0`.

#### Scenario: A price increase between order and invoice is surfaced, not blocked
- GIVEN an OC line estimated at `100` and a linked confirmed comprobante line effectively costed at
  `112`
- WHEN the OC detail is read
- THEN the deviation shows `+12%` and the earlier confirmation was not blocked by it

#### Scenario: A never-quoted line reports no comparable, never zero
- GIVEN an OC line with `costo_unitario_estimado IS NULL` and a linked confirmed reception
- WHEN the deviation is read
- THEN it reports an explicit *no comparable* state, not `0`

### Requirement: Pre-Load From The Reposición List Is Read-Only And Unidirectional

`POST /api/ordenes-compra` MUST accept an item list shaped from
`GET /api/reportes/stock/reposicion`'s rows, mapping `FilaDeReposicion.{IdArticulo, Sugerido} →
{IdArticulo, CantidadPedida}`, filtered by proveedor. Rows in the `"Sin proveedor"` group MUST NOT
be able to pre-load an OC. Rows with `sugerido = null` MUST be excluded, never defaulted to `0`.
Stage 13's endpoint and response shape MUST remain unchanged. The web's per-group *"Generar OC"*
action on the reposición screen MUST only be offered to a session whose role can write an OC
(Admin) — the reposición screen itself remains visible to every role that already reads it
(Supervisor, Admin).

#### Scenario: A pre-load excludes null-sugerido rows
- GIVEN a reposición list with one row where `sugerido = null`
- WHEN an OC draft is pre-loaded for that proveedor
- THEN that row is excluded from the resulting OC items, never defaulted to `cantidad_pedida = 0`

#### Scenario: The Sin proveedor bucket cannot produce an OC
- GIVEN reposición rows grouped under `"Sin proveedor"`
- WHEN the pre-load action is attempted for that group
- THEN no OC can be generated from it — there is no proveedor to send it to

#### Scenario: The reposición endpoint's shape is unchanged
- GIVEN `GET /api/reportes/stock/reposicion` before and after this stage
- WHEN both responses are compared for identical parameters
- THEN the response shape and figures are identical — Etapa 13 stays a read-only source

#### Scenario: The Generar OC action is Admin-gated in the web, the screen stays as-is for others
- GIVEN a Supervisor session viewing the reposición screen
- WHEN the per-group actions are rendered
- THEN no *"Generar OC"* button appears for any group, while every other part of the screen
  (filters, table, download) renders exactly as it does today
