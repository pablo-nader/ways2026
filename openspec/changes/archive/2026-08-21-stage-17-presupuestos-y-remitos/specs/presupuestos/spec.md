# Presupuestos Specification

## Purpose

Defines `presupuestos` + `items_presupuesto` (doc-11:307-324, doc-10 §4-adjacent): the sale-side
quote that moves neither stock nor cash, carries an expiry, is convertible into exactly one sale,
and keeps the price it offered. Structural mirror of stage 16's `ordenes_compra` (proposal decision
1): the checkout stays byte-identical by construction because a quote lives in its own table, never
in `comprobantes_venta`. Owns the four-value lifecycle (`borrador | enviado | convertido | anulado`),
its own number assigned at `enviar` through the existing numbering mechanism (series `'PRES'`), the
derived-never-stored `vencido` predicate resolved in the punto de venta's own zona horaria, the
frozen price snapshot that becomes the conversion's price authority, the 1:1 conversion guarantee,
and the authorization gate mirrored from `/api/ventas`. Greenfield: doc-01 and `alsina/` show zero
hits for `presupuesto` (explore.md:15), so every rule here is a decision, not a port. A presupuesto
reserves no stock — the honest residue is that a converted quote may drive stock negative, exactly
as the counter already allows.

## Requirements

### Requirement: Presupuesto Schema At Rest

`presupuestos` MUST be operativa-scoped (`id_tenant` + `id_punto_venta`, doc 09), carry
`id_cliente`, `id_empleado` (creator), `numero bigint NULL`, `fecha_emision` (from
`IRelojDelSistema`, no DB default), `fecha_envio NULL`, `vencimiento date NULL` (NOT NULL from
`enviado` onward), `subtotal`/`descuento_total`/`total`, `estado estado_presupuesto NOT NULL`, and
MUST inherit `EntidadBase`. `ck_presupuestos_envio_completo` MUST enforce that `numero`,
`fecha_envio` and `vencimiento` arrive together, and that every state other than `borrador`/`anulado`
has all three set. `items_presupuesto` MUST reference its presupuesto via FK, carry `orden`,
`id_articulo NOT NULL` (a free-concept line cannot be converted into a stock-moving sale line), a
`descripcion` snapshot, `cantidad numeric(12,3) > 0`, and the frozen price provenance
(`precio_unitario`, `descuento`, `id_lista_precio`, `id_oferta`, `id_alicuota_iva`,
`porcentaje_iva`) — deliberately narrower than `items_comprobante_venta`: no `id_area`, no
`codigo_barra`, no `costo_unitario` (a quote never froze a cost), no `id_lote` (nothing is
reserved).

#### Scenario: A borrador presupuesto is created with no items yet
- GIVEN an operator starts a presupuesto for a cliente at a punto de venta
- WHEN the borrador is created with no items
- THEN it persists with `estado = borrador`, `numero IS NULL`, and `vencimiento IS NULL`

#### Scenario: A raw insert with a non-positive cantidad is rejected
- GIVEN a raw INSERT into `items_presupuesto` supplies `cantidad = 0`
- WHEN it reaches Postgres
- THEN it is rejected via `ck_items_presupuesto_cantidad_positiva`, SQLSTATE `23514`

#### Scenario: numero, fecha_envio and vencimiento arrive together
- GIVEN a raw UPDATE sets `numero` on a presupuesto without also setting `fecha_envio` and
  `vencimiento`
- WHEN it reaches Postgres
- THEN it is rejected via `ck_presupuestos_envio_completo`, SQLSTATE `23514`

### Requirement: Borrador Is Mutable, Full Replace-Set Under FOR UPDATE

A `borrador` presupuesto MUST be incrementally editable — header fields and a full item
replace-set — under `SELECT … FOR UPDATE … WHERE estado = 'borrador'`. Every other `estado` MUST
reject the edit endpoint.

#### Scenario: A replace-set adds and removes items in one request
- GIVEN a borrador presupuesto with 3 items
- WHEN it is edited to remove one item and add two new ones
- THEN the persisted item set matches exactly the new set, with no stale row left over

#### Scenario: Editing a non-borrador presupuesto is rejected
- GIVEN a presupuesto with `estado = enviado`
- WHEN a client attempts the edit endpoint
- THEN it is rejected with `409`

### Requirement: Enviar Assigns The Own Number From Series 'PRES', Per Punto De Venta, And Requires An Expiry

`POST /{id}/enviar` MUST assign `numero` via the existing `AsignadorDeNumeroComprobante` with
`tipo_comprobante = 'PRES'`, MUST require and stamp a `vencimiento >= hoy(zona del punto de venta)`,
and MUST stamp `fecha_envio` together with both in the same
`UPDATE … WHERE estado = 'borrador' RETURNING`. Only a `borrador` MAY be sent. Two concurrent
`enviar` requests at the same punto de venta MUST both succeed with two distinct numbers — never a
`409` from the numbering mechanism. `ux_presupuestos_numero`'s `23505` MUST resolve, by exact
constraint name evaluated **above** the generic `_numero` classifier, to
`numero_de_presupuesto_duplicado` — the fourth occurrence of the `_numero` ordering trap.

#### Scenario: Enviar assigns the next number for that punto de venta
- GIVEN a borrador presupuesto at punto de venta 1 with no prior presupuesto sent there
- WHEN `enviar` is called with a future `vencimiento`
- THEN `numero = 1`, `fecha_envio` is set, and `estado = enviado`

#### Scenario: Two concurrent enviar calls at the same punto de venta never collide
- GIVEN two distinct borrador presupuestos at the same punto de venta
- WHEN both are sent concurrently
- THEN both succeed with two distinct `numero` values and neither response is `409`

#### Scenario: Enviar with a vencimiento in the past is rejected
- GIVEN a borrador presupuesto
- WHEN `enviar` is called with a `vencimiento` earlier than today in the punto de venta's zona
  horaria
- THEN it is rejected with `409` and no number is assigned

#### Scenario: A raw duplicate-number insert resolves to the presupuesto's own domain code
- GIVEN a raw out-of-band INSERT attempts a `numero` already used at the same punto de venta
- WHEN it reaches Postgres
- THEN `ManejadorDeErrores` reports SQLSTATE `23505` translated to `numero_de_presupuesto_duplicado`,
  not a `ux_clientes_numero`-family code

### Requirement: Vencido Is A Derived Predicate, Never Stored, Resolved In The Punto De Venta's Zona Horaria

`vencido(p) = p.estado = 'enviado' AND p.vencimiento < hoy(zona del punto de venta)` MUST be
computed on read, with no stored `vencido` state and no scheduler. *"Hoy"* MUST be resolved through
the punto de venta's own `zona_horaria` parametro, never `DateTime.UtcNow` — the same binding
criterion already imposed on the vencimientos report (`lotes-y-vencimientos/spec.md:318-320`).

#### Scenario: A presupuesto past its vencimiento reads as vencido
- GIVEN a presupuesto `enviado` with `vencimiento = 2026-08-10` and today (PV zona) is `2026-08-19`
- WHEN its detail is read
- THEN the read model reports `vencido = true`, with no stored column behind it

#### Scenario: Vencido is evaluated in the PV's own zona horaria, not UTC
- GIVEN a punto de venta at `zona_horaria = 'America/Argentina/Buenos_Aires'` (UTC-3) and a
  presupuesto with `vencimiento = 2026-08-19`
- WHEN it is read at `2026-08-19T23:30:00-03:00` (already `2026-08-20T02:30:00Z`)
- THEN `vencido` reports `false` — the PV's local day still governs, not the UTC day

### Requirement: Para-Venta Pre-Loads The POS For Display Only

`GET /{id}/para-venta` MUST return an `enviado`, non-`vencido` presupuesto's items shaped for POS
display, read-only — it MUST NOT write anything and MUST NOT be the price authority; the authority
is the conversion itself (see below).

#### Scenario: Para-venta of a vencido presupuesto is refused
- GIVEN an `enviado` presupuesto whose `vencimiento` has passed
- WHEN `/para-venta` is requested
- THEN it is rejected with `409 presupuesto_vencido`

### Requirement: Conversion Freezes The Presupuesto's Own Snapshot As The Price Authority

`SolicitudDeVenta` MAY carry `idPresupuestoOrigen`. When present: `lineas` MUST be absent or empty
(`400`, per `dto-contract-honesty` — a field that would be ignored is not accepted); the cliente
MUST be the presupuesto's own cliente, a conflicting `idCliente` MUST be refused rather than
silently overridden; `precio_unitario`, `descuento`, `id_lista_precio`, `id_oferta`,
`porcentaje_iva` and `id_alicuota_iva` MUST come frozen from `items_presupuesto`, never re-resolved
by `ServicioDeOfertas`; `costo_unitario` MUST be frozen from **today's** `costo_nominal`, not from
quoting time. The conversion MUST be refused with `409 presupuesto_vencido` when expired, or
`409 presupuesto_no_convertible` when not `enviado` or already `convertido`.

#### Scenario: Conversion honours the quoted price after a list-price change
- GIVEN a presupuesto quoted `precio_unitario = 100.00` for an articulo, and the list price later
  changes to `130.00`
- WHEN it is converted
- THEN the resulting comprobante's item persists `precio_unitario = 100.00`

#### Scenario: Conversion freezes today's cost, never the quoting-time cost
- GIVEN a presupuesto sent when `costo_nominal = 80.00`, later changed to `95.00`
- WHEN it is converted
- THEN the resulting comprobante's item persists `costo_unitario = 95.00`

#### Scenario: Lines in the request are rejected outright
- GIVEN a `POST /api/ventas` carries both `idPresupuestoOrigen` and a non-empty `lineas`
- WHEN it is validated
- THEN it is rejected with `400` before reaching the database

#### Scenario: An expired presupuesto cannot be converted
- GIVEN a presupuesto `enviado` whose `vencimiento` has passed
- WHEN a conversion is attempted
- THEN it is rejected with `409 presupuesto_vencido` and no sale is created

### Requirement: Conversion Is Terminal, Guaranteed 1:1 By A Partial Unique Index

`EscriturasDePresupuesto` MUST be the single `UPDATE presupuestos … WHERE estado = 'enviado'
RETURNING` transition authority to `convertido`, executed inside the sale transaction.
`ux_comprobantes_venta_presupuesto_origen` (partial unique on `(id_presupuesto_origen, id_tenant)
WHERE id_presupuesto_origen IS NOT NULL`) MUST make one comprobante per presupuesto a database
guarantee. Two concurrent conversions of the same presupuesto MUST yield exactly one `201` and one
`409 presupuesto_ya_convertido`. `convertido` MUST be terminal: once set, no path MUST revert it —
including a later annulment of the resulting sale, which does not reopen the presupuesto (the
frozen price must not become honourable again past its expiry).

#### Scenario: Two concurrent conversions of the same presupuesto never both succeed
- GIVEN one `enviado` presupuesto
- WHEN two conversion requests race
- THEN exactly one receives `201` and the other `409 presupuesto_ya_convertido`

#### Scenario: A raw out-of-band second link is rejected by the index
- GIVEN a presupuesto already linked to comprobante 501
- WHEN a raw INSERT attempts a second `comprobantes_venta` row with the same
  `id_presupuesto_origen`
- THEN Postgres rejects it via `ux_comprobantes_venta_presupuesto_origen`, SQLSTATE `23505`

#### Scenario: Annulling the resulting sale does not reopen the presupuesto
- GIVEN a presupuesto `convertido` into comprobante 501
- WHEN comprobante 501 is anulado
- THEN the presupuesto's `estado` remains `convertido`

### Requirement: A Presupuesto Reserves No Stock

A presupuesto MUST NOT hold, reserve, or otherwise reduce available stock for any other sale.
Converting an `enviado` presupuesto into a sale MAY drive stock negative — the same rule the
counter already allows.

#### Scenario: Sending a presupuesto does not touch stock
- GIVEN a borrador presupuesto with product lines
- WHEN it is sent (`enviado`)
- THEN no `movimientos_stock` row exists and `stock.cantidad` is unchanged

### Requirement: Anulación Is Rejected For A Convertido Presupuesto

`POST /{id}/anular` MUST be allowed only for `borrador` or `enviado`. A `convertido` presupuesto
MUST be rejected with `409` — its consequence (the sale) is annulled through the sale's own path,
never through the presupuesto.

#### Scenario: An enviado presupuesto is annulled before conversion
- GIVEN an `enviado`, non-expired presupuesto
- WHEN `anular` is called
- THEN `estado = anulado`

#### Scenario: A convertido presupuesto cannot be annulled directly
- GIVEN a `convertido` presupuesto
- WHEN `anular` is called
- THEN it is rejected with `409`

### Requirement: Authorization Mirrors /api/ventas Exactly — OperacionDePos, Nothing Stacked

`/api/presupuestos` MUST be grouped under `Politicas.OperacionDePos` alone, for both reads and
writes. `Politicas.cs` MUST NOT gain a new policy for this capability.

#### Scenario: Vendedor exercises the full presupuesto lifecycle
- GIVEN a user with role Vendedor
- WHEN they create, send, and annul a presupuesto
- THEN every request succeeds (authorization-wise)

### Requirement: RLS And Tenant Isolation Are Standard On Both Tables

`presupuestos` and `items_presupuesto` MUST enforce RLS `FORCE`d with no `BYPASSRLS`, scoped by
`app_tenant_actual()`.

#### Scenario: RLS blocks a cross-tenant read
- GIVEN the app DB role has no `BYPASSRLS`
- WHEN a raw query reads another tenant's `presupuestos` rows while a different tenant GUC is set
- THEN it returns zero rows

#### Scenario: An INSERT with a foreign id_tenant is refused
- GIVEN a raw INSERT attempts `id_tenant` different from `app_tenant_actual()`
- WHEN it reaches Postgres
- THEN it is refused with `42501`
