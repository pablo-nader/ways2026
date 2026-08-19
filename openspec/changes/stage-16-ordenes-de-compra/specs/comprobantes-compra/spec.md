# Delta for Comprobantes de Compra

## ADDED Requirements

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
