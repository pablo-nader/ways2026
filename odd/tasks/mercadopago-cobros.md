# Mercado Pago — in-person charges, offline approval and reconciliation (E3–E7)

## Objective

The POS charges with Mercado Pago (dynamic QR first, then Point). When Mercado Pago reports the payment approved, the sale
closes by itself and the ticket prints. Without network the cashier can approve manually and the payment stays flagged
"sin verificar" until reconciliation. Reconciliation matches Mercado Pago's release report against Ways (charges, fees, net,
withdrawals). Builds on E1 (OAuth per empresa) and E2 (stores, POS, Point terminals): `odd/tasks/mercadopago-conexion-oauth.md`,
`odd/tasks/mercadopago-sucursales-y-cajas.md`.

## Decisions (2026-11-01, orchestrator; within the owner's request)

- A medio de pago gets an optional `proveedor` (`mercadopago_qr` | `mercadopago_point`), only on `Electronico` medios. Selecting
  it in the POS starts a Mercado Pago charge instead of asking for a reference. The medio keeps its `comision_porcentaje` and
  `id_cuenta_tesoreria` for the estimate; the real fee comes from reconciliation (E6).
- Creating the order needs network (the device calls the API, the API calls Mercado Pago with the seller token). Offline, the
  POS offers manual approval (E5).
- The device learns the outcome by polling the API (every ~2 s while the QR is shown; the API refreshes from
  `GET /v1/orders/{id}` at most once per 2 s per charge). The webhook (E3-T4) updates the same row faster; polling stays the source
  of truth because the OAuth-seller webhook routing is unverified.
- The sale number is reserved before creating the order; `external_reference` = `W{tenant}P{pv}N{numero}` (letters/digits,
  ≤64) and the idempotency key derives from it, so a retry never creates a second order.
- On approval the POS fills the payment row (`IdCobroMercadoPago`), queues the sale as today and prints. The server accepts a
  payment with `IdCobroMercadoPago` only if that charge is approved, same empresa/PV, same amount, and not used by another sale.
- The QR is shown on screen (npm `qrcode`) and can also be printed with the existing ESC/POS `qr()`.

## Mercado Pago facts (verified 2026-11-01)

- `POST /v1/orders` (201), `X-Idempotency-Key` required. QR body: `type:"qr"`, `external_reference`, `total_amount` "123.45",
  `config.qr.{external_pos_id, mode:"dynamic"}`, optional `expiration_time` (ISO 8601 duration, default PT15M), `items[]`
  `{title, unit_price, quantity, unit_measure}`. Response `id` (ORD…), `status` created, `type_response.qr_data` (EMVCo).
- `GET /v1/orders/{id}`: `status` created | processed | canceled | refunded | expired; `transactions.payments[]
  {id, status, status_detail, amount, paid_amount, reference_id, payment_method}`. A rejected QR payment is not exposed: the
  order stays `created`.
- Cancel `POST /v1/orders/{id}/cancel` (idempotency key) only while `created`, else 409. Refund `POST /v1/orders/{id}/refund`.
- Point: `type:"point"`, `transactions.payments[{amount}]`, `config.point.{terminal_id, print_on_terminal}`; statuses add
  `at_terminal`, `action_required`, `failed`; 409 `already_queued_order_for_terminal`.
- Webhook: per application (panel), topic `order`; header `x-signature: ts=…,v1=<hex>`; manifest
  `id:{data.id};request-id:{x-request-id};ts:{ts};` HMAC-SHA256 with the app's secret; answer 200 within 22 s; then GET the order.
- Fees are not on the order: `GET /v1/payments/{reference_id}` (`fee_details`, `transaction_details.net_received_amount`,
  `money_release_date`, unverified for orders) or the release report (`SOURCE_ID`, `EXTERNAL_REFERENCE`, `NET_CREDIT_AMOUNT`,
  `MP_FEE_AMOUNT`, `FINANCING_FEE_AMOUNT`, `TAXES_AMOUNT`, `DESCRIPTION` payment/refund/payout…).

## Tasks

- [ ] E3-T1 — Backend charges: `medios_pago.proveedor`; table `cobros_mercadopago`; device endpoints create / read (refresh
  from Mercado Pago) / cancel a QR charge.
- [ ] E3-T2 — Backend sale: `PagoDeVenta.IdCobroMercadoPago`; the sale validates and links the approved charge (unique).
- [ ] E3-T3 — Web: the medio ABM shows `proveedor`; the POS charge modal with the on-screen QR, polling, cancel, auto close +
  print.
- [ ] E3-T4 — Webhook `POST /api/integraciones/mercadopago/webhook`: signature check, refresh the charge.
- [ ] E4 — Point: same charge with `type:"point"` on the PV's terminal.
- [ ] E5 — Offline manual approval flagged `sin_verificar`, carried in the offline queue.
- [ ] E6 — Reconciliation: import the release report, match charges (verified and unverified), real fees, net to the treasury
  account.
- [ ] E7 — Withdrawals/payouts from the report into treasury.

## Delivery strategy

`ask-on-risk`, standing chain `stacked-to-main`, one PR per task through judgment-day.

## Next step

E3-T1 design and implementation.

## E3-T1 design (2026-11-01)

Branch `pnader/mercadopago-e3-t1`, stacked on E2 T6. Backend only.

- `medios_pago.proveedor varchar(30) NULL`, CHECK in (`mercadopago_qr`, `mercadopago_point`) and only on an `Electronico` medio
  (service validates first, typed 400); the medios ABM accepts and returns it (dto-contract-honesty); docs/10 updated.
- Table `cobros_mercadopago` (operativa: id_tenant + id_empresa, RLS): `id_cobro_mercadopago`, `id_tenant`, `id_empresa`,
  `id_punto_venta`, `id_dispositivo`, `id_medio_pago`, `numero_preasignado` (same type as `SolicitudDeVenta.NumeroPreasignado`),
  `intento int`, `tipo` (`qr` | `point`), `importe numeric(18,2)`, `external_reference varchar(64)`, `id_orden_mercadopago
  varchar(64)`, `estado` (`pendiente` | `aprobado` | `cancelado` | `expirado` | `devuelto`), `id_pago_mercadopago varchar(64)`,
  `referencia_pago_mercadopago bigint` (the numeric `reference_id`), `importe_pagado numeric(18,2)`, `qr_data text`,
  `expira_en`, `fecha_ultima_consulta`, `fecha_aprobacion`, `created_at`/`updated_at`. Partial UNIQUE (`id_punto_venta`,
  `numero_preasignado`) WHERE `estado IN ('pendiente','aprobado')`: at most one live charge per sale number.
- `external_reference` = `W{tenant}P{pv}N{numero}I{intento}`; idempotency key = hash of (pv, numero, intento, importe).
- Device endpoints (device session; the charge's PV is the device's PV; 404 for another PV/tenant):
  - `POST /api/pos/mercadopago/cobros` `{idMedioPago, numeroPreasignado, importe}`: the medio must have proveedor
    `mercadopago_qr`; the PV needs a registered caja for the current seller (`mercadopago_caja_no_registrada`); a live
    `pendiente` for the same number and amount is returned as is; with another amount -> 409 `cobro_mercadopago_en_curso`; an
    `aprobado` for that number -> 409 `cobro_mercadopago_ya_aprobado`. Creates `POST /v1/orders` (`type:"qr"`,
    `config.qr.external_pos_id` = the caja's external_id, `mode:"dynamic"`, `expiration_time:"PT10M"`, `total_amount`, one
    item "Venta {numero}"). Returns `{idCobro, estado, qrData, expiraEn, importe}`.
  - `GET /api/pos/mercadopago/cobros/{id}`: a `pendiente` charge refreshes from `GET /v1/orders/{id}` at most once per 2 s
    (`fecha_ultima_consulta`) under a per-charge lock: `processed` -> `aprobado` (payment id, reference_id, paid amount,
    approval date; a paid amount different from `importe` is still `aprobado` but the response flags `importeDistinto`);
    `canceled` -> `cancelado`; `expired` -> `expirado`; `refunded` -> `devuelto`.
  - `POST /api/pos/mercadopago/cobros/{id}/cancelar`: `pendiente` -> `POST /v1/orders/{id}/cancel`; a 409 from Mercado Pago
    refreshes the order instead (it may have been paid in the meantime, then the answer is `aprobado`).
- Mercado Pago 401/no connection reuse the E1 errors; `no_disponible` keeps the charge `pendiente`.
