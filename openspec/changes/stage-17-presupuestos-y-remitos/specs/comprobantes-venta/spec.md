# Delta for Comprobantes de Venta

## ADDED Requirements

### Requirement: Checkout Refuses Any Tipo Comprobante With Afecta_Stock False, Unconditionally

`ServicioDeVentas.ResolverTipoComprobanteAsync` MUST reject any `tipos_comprobante` row with
`afecta_stock = false` with `400 tipo_comprobante_invalido`, independently of whether the request
carries product lines — the guard is unconditional on `afecta_stock`, never conditional on the
request's items. This closes the `PRE` finding (a seeded, active, `afecta_stock = false` type that
passed the resolver and would have decremented stock and consumed cuenta corriente exactly like a
`TX`) with a second, independent net alongside the catalog deactivation (`auxiliary-catalogs`
delta). The transaction lambda is NOT touched.

#### Scenario: A checkout with the seeded PRE type is refused
- GIVEN the seeded `PRE` tipo (now `activo = false`, `afecta_stock = false`)
- WHEN `POST /api/ventas` is submitted with `codigoTipoComprobante = "PRE"` and real product lines
- THEN it is rejected with `400 tipo_comprobante_invalido`, and no comprobante, stock movement, or
  cuenta corriente row is written

#### Scenario: An out-of-band active afecta_stock=false type is still refused (net 2, independent of net 1)
- GIVEN a raw out-of-band insert creates an **active**, non-fiscal, venta-class `tipos_comprobante`
  row with `afecta_stock = false`
- WHEN `POST /api/ventas` is submitted with that code and product lines
- THEN it is rejected with `400 tipo_comprobante_invalido` — proven independently of the catalog's
  own deactivation

#### Scenario: Removing either net alone still fails the other's test
- GIVEN the mutation-proof-tests contract
- WHEN the resolver clause is removed while `PRE` stays inactive, or `PRE` is reactivated while the
  resolver clause stays
- THEN the respective test for the surviving net still catches a phantom sale — neither net alone
  masks the other

### Requirement: A Comprobante MAY Carry A Linked Presupuesto Origen

`comprobantes_venta` MUST gain a nullable `id_presupuesto_origen`, with at most one comprobante per
presupuesto guaranteed by a partial unique index (`ux_comprobantes_venta_presupuesto_origen`). When
present, the presupuesto's own snapshot — never the request — supplies prices, discounts, IVA and
the customer (see `presupuestos` capability's conversion requirement).

#### Scenario: A sale from a presupuesto carries the link
- GIVEN a presupuesto `enviado`, non-expired
- WHEN it is converted into a sale
- THEN the resulting comprobante persists `id_presupuesto_origen` equal to the presupuesto's id

#### Scenario: A second comprobante for the same presupuesto is refused at the database
- GIVEN a presupuesto already linked to comprobante 501
- WHEN a raw INSERT attempts a second comprobante with the same `id_presupuesto_origen`
- THEN Postgres rejects it via `ux_comprobantes_venta_presupuesto_origen`, SQLSTATE `23505`

### Requirement: A TXR Comprobante Consolidates N Remitos, Carries Zero Items, And Writes No Stock

A comprobante of tipo `TXR` MUST carry **zero** `items_comprobante_venta` rows by construction and
MUST write **zero** `movimientos_stock` rows — the goods already left at remito time (see `remitos`
capability's consolidation requirement). Its printed detail MUST be the linked remitos' own frozen
items, joined in the read model.

#### Scenario: A TXR's item set is always empty
- GIVEN a consolidation of two remitos
- WHEN the resulting `TXR` comprobante is inspected
- THEN it has 0 rows in `items_comprobante_venta`

#### Scenario: The TXR's printed detail comes from its remitos
- GIVEN a TXR comprobante linking two remitos with 2 and 3 lines respectively
- WHEN its printed detail is read
- THEN it shows all 5 lines, sourced from `items_remito`, not from `items_comprobante_venta`

### Requirement: Annulling A TXR Comprobante Returns Its Remitos To Emitido

Annulling a `TXR` comprobante MUST, in the same transaction as the `estado = anulado` change, return
every linked remito to `emitido` and clear its `id_comprobante_venta`, and MUST reverse cuenta
corriente exactly as any other anulación. Because a TXR's item loop is empty by construction, this
path MUST create **zero** stock movements — the double-decrement and phantom-restock traps are
unreachable, not merely avoided. The un-link call MUST run only when the returned
`id_tipo_comprobante` (from `MarcarAnuladoAsync`'s existing `RETURNING`) is `TXR`; for every other
comprobante this path MUST emit zero extra statements.

#### Scenario: Annulling a TXR frees its remitos to be invoiced again
- GIVEN a TXR comprobante linking two facturado remitos
- WHEN it is anulado
- THEN both remitos return to `estado = emitido` with `id_comprobante_venta = NULL`, and zero
  `movimientos_stock` rows are created

#### Scenario: Annulling an ordinary TX does not run the un-link path
- GIVEN a TX comprobante with product lines
- WHEN it is anulado
- THEN the exact same statement sequence as before this stage executes — no remito lock, no remito
  update

## MODIFIED Requirements

### Requirement: RC Is Emittable Through The POS Surface, By Its Own Service

The `RC` `tipos_comprobante` row MUST be emittable through the POS surface, **by its own service**
(`ServicioDeCuentaCorriente`, with its own `ResolverTipoRcAsync`) — not through `ServicioDeVentas`'s
checkout entry point, which as of this stage refuses any `afecta_stock = false` tipo unconditionally
(see the ADDED checkout guard above). `RC` carries `es_fiscal = false`, `afecta_stock = false`,
`discrimina_iva = false`, `letra = NULL`, `signo = +1` — no fiscal receipt path is created.
(Previously: claimed RC flowed "through the same `ServicioDeVentas` checkout entry point as
TX/NCX" — false against the code even before this stage, `ServicioDeCuentaCorriente.cs:275-363`
always owned it end to end; the sentence would have read as a promise the resolver's new
unconditional guard breaks.)

#### Scenario: RC emission never touches fiscal fields
- GIVEN a valid RC payment request
- WHEN it is emitted
- THEN the persisted comprobante has `discrimina_iva = false`, no `neto_gravado`/`iva_total`, and
  `letra = NULL`

#### Scenario: RC does not reach the checkout's afecta_stock guard
- GIVEN an RC payment request submitted through `ServicioDeCuentaCorriente`
- WHEN it is processed
- THEN `ServicioDeVentas.ResolverTipoComprobanteAsync` is never invoked for it — the new guard has
  no effect on RC's own path
