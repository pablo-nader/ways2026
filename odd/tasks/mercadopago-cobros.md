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

- [x] E3-T1 — Backend charges: `medios_pago.proveedor`; table `cobros_mercadopago`; device endpoints create / read (refresh
  from Mercado Pago) / cancel a QR charge.
- [x] E3-T2 — Backend sale: `PagoDeVenta.IdCobroMercadoPago`; the sale validates and links the approved charge (unique).
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

E3-T3: web. The medio ABM shows `proveedor`; the POS charge modal with the on-screen QR, polling, cancel, auto close and print, sending `idCobroMercadoPago` in the payment row. See "E3-T2 progress" for the sale contract (codes, `numeroPreasignado` required, no client `referencia`).

## E3-T1 design (2026-11-01)

Branch `pnader/mercadopago-e3-t1`, stacked on E2 T6. Backend only.

- `medios_pago.proveedor varchar(30) NULL`, CHECK in (`mercadopago_qr`, `mercadopago_point`) and only on an `Electronico` medio
  (service validates first, typed 400); the medios ABM accepts and returns it (dto-contract-honesty); docs/10 updated.
- Table `cobros_mercadopago` (operativa: id_tenant + id_empresa, RLS): `id_cobro_mercadopago`, `id_tenant`, `id_empresa`,
  `id_punto_venta`, `id_dispositivo`, `id_medio_pago`, `numero_preasignado` (same type as `SolicitudDeVenta.NumeroPreasignado`),
  `intento int`, `tipo` (`qr` | `point`), `importe numeric(18,2)`, `external_reference varchar(64)`, `id_orden_mercadopago
  varchar(64)`, `estado` (`creando` | `pendiente` | `aprobado` | `cancelado` | `expirado` | `devuelto`; `creando` added in judgment-day round 1), `id_pago_mercadopago varchar(64)`,
  `referencia_pago_mercadopago bigint` (the numeric `reference_id`), `importe_pagado numeric(18,2)`, `qr_data text`,
  `expira_en`, `fecha_ultima_consulta`, `fecha_aprobacion`, `created_at`/`updated_at`. Partial UNIQUE (`id_punto_venta`,
  `numero_preasignado`) WHERE `estado IN ('creando','pendiente','aprobado')`: at most one live charge per sale number.
- `external_reference` = `W{tenant}P{pv}N{numero}I{intento}`; idempotency key = hash of the full request body (stored in `clave_idempotencia`; see round 1).
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
    `canceled` -> `cancelado`; `expired` -> `expirado`; `refunded` -> `devuelto` (only observed while the charge is pending; a refund after approval is E6's job, reconciliation).
  - `POST /api/pos/mercadopago/cobros/{id}/cancelar`: `pendiente` -> `POST /v1/orders/{id}/cancel`; a 409 from Mercado Pago
    refreshes the order instead (it may have been paid in the meantime, then the answer is `aprobado`).
- Mercado Pago 401/no connection reuse the E1 errors; `no_disponible` keeps the charge `pendiente`.

## E3-T1 progress (2026-11-01)

Done on `pnader/mercadopago-e3-t1`, commit `feat(mercadopago): el POS crea, consulta y cancela un cobro con QR dinámico` (the hash is in git log).

- `medios_pago.proveedor` (migration `CobrosMercadoPago`): `ProveedorDeCobro.Resolver` (domain) is the single rule; ABM field `Proveedor` is a `CampoOpcional<string?>` (absent keeps, null/blank clears) and the listing returns it to every role; 400 `proveedor_cobro_invalido` / `proveedor_solo_electronico`; CHECKs `ck_medios_pago_proveedor` and `ck_medios_pago_proveedor_solo_electronico` translated to the same codes. The POS snapshot (`MedioPagoDeInstantanea`) does NOT carry it yet (T3 decides).
- `cobros_mercadopago` model summary (docs/10 §7): id_cobro_mercadopago, id_tenant, id_empresa, id_punto_venta, id_dispositivo, id_medio_pago, numero_preasignado bigint, intento, tipo, importe numeric(18,2), external_reference, id_orden_mercadopago, estado, id_pago_mercadopago, referencia_pago_mercadopago bigint, importe_pagado, qr_data, expira_en, fecha_ultima_consulta, fecha_aprobacion, timestamps. Composite FKs to empresa, punto de venta, dispositivo and medio (all RESTRICT), RLS, partial unique `ux_cobros_mercadopago_vivo_numero` and unique `ux_cobros_mercadopago_intento` (both 23505 -> 409 `cobro_mercadopago_en_curso`). Golden inventory updated (6 lines, all `marcado`: a PV, empresa, medio or tenant with charges is "in use"). No audit rows: the table is itself the record of each attempt.
- Endpoints (`OperacionDePos` + `RequiereDispositivo`, always 200): `POST /api/pos/mercadopago/cobros/`, `GET .../cobros/{idCobro}`, `POST .../cobros/{idCobro}/cancelar`; response `{idCobro, estado, qrData, expiraEn, importe, importePagado, importeDistinto}`, `qrData` only while pending.
- Locks: creation takes `(id_punto_venta, 0x4D504351)`, GET/cancel take `(id_cobro, 0x4D504352)`; all held across the Mercado Pago call; non-retrying strategy; any `ErrorDominio` (401 reconnection, 409s, MP rejections) commits what was already saved before rethrowing.
- Decisions beyond the design: a pending charge past `expira_en` is refreshed from Mercado Pago before deciding reuse/409 (it may have expired or been paid); `intento` is `max + 1` over all charges of the number; same-amount reuse also requires the same medio (otherwise 409 `cobro_mercadopago_en_curso`); cancelling an `aprobado` charge is 409 `cobro_mercadopago_ya_aprobado`, cancelling a cancelado/expirado/devuelto one returns it as is; an order status Ways does not know (`created`, `at_terminal`) leaves the charge pending; `reference_id` is read tolerantly (number or string) and never blocks approving.
- Verification: build, `has-pending-model-changes` clean, Domain 2417, Application 1650, integration filter 359 (14 m). Mutation evidence: see the commit report (proveedor rule and CHECK, live-charge index, same-amount reuse, 2 s throttle, per-charge/per-PV locks, status mapping, cancel-409 refresh, device PV scoping, RLS).
- Open: single-clause mutation of each of the three device-PV conjuncts survives by design (pre-check + locked read are redundant, a charge never changes PV); the tests kill them only together. Nothing here was exercised against the real Mercado Pago API.

## Judgment-day E3-T1 round 1 (2026-11-01)

Both blind judges confirmed F1-F4 (F5 judge A only, F6 judge B only). The migration `CobrosMercadoPago` was not shipped, so it was edited in place (Designer + snapshot synced; `has-pending-model-changes` clean).

- F1 — `processed` approves only with a readable payment: `paid_amount` else `amount`, 0 < x <= max, 2 decimals. Otherwise the charge stays `pendiente` and a warning is logged (order and charge ids only); the next poll retries. `ImportePagado` is set on approval and `importeDistinto` follows from it.
- F2 — reserve-then-call: inside the PV lock the row is inserted as `creando` (with `external_reference`, `importe`, `id_medio_pago`, new `external_pos_id varchar(40)` and `clave_idempotencia varchar(64)` = `ways-cobro-` + SHA-256 of the full body) and committed in its own transaction; only then `POST /v1/orders` and the update to `pendiente`. `creando` counts as live in `ux_cobros_mercadopago_vivo_numero` and in the estado CHECK; `id_orden_mercadopago` became nullable with `ck_cobros_mercadopago_orden`. Create / GET / cancel of a `creando` row first re-send the ORIGINAL body (rebuilt from the stored fields, not from the current caja) with the stored key, then the normal rules apply (other importe -> 409 `cobro_mercadopago_en_curso`). The `qr_data` guard applies only to non-terminal statuses; terminal ones go through the status mapping. Superseded by round 2: a 4xx never cancels; the row follows the time rule of round 2. Create is a bounded loop (5 turns) of PV-locked transactions: a turn that reserves returns nothing and the next one completes it.
- F3 — every path that returns or mutates a live charge takes the per-charge lock and re-reads after it (create's reuse branch included). Lock order PV -> charge everywhere; GET/cancel of a `creando` row take the PV lock first. Terminal rows are returned without lock.
- F4 — `ServicioDeMediosPago` rejects (409 `medio_con_cobros_mercadopago_vivos`) changing the `proveedor`, leaving `Electronico` or deactivating a medio that has `creando`/`pendiente` charges (`FOR UPDATE` on the medio vs the reservation's `FOR KEY SHARE`, which also re-validates the medio under the lock). `aprobado` charges do not block.
- F5 — a 404 on `GET /v1/orders/{id}` for a pending charge past `expira_en` marks it `expirado`; before expiry it stays the 502.
- F6 — doc comments (entity, ODD design, docs/10 part 07) now say `devuelto` is only observed while pending; refunds after approval belong to E6.
- Known limits: a `creando` row whose re-send keeps failing with a non-definitive error (for example a 2xx with an invalid body) stays `creando` and keeps the number occupied until Mercado Pago answers; nothing here was exercised against the real API. The old test that injected a competing live row between the call and the save no longer applies (the reservation precedes the call); the 23505 -> 409 translation remains as a backstop.

## Judgment-day E3-T1 round 2 (2026-11-01, final)

Both judges confirmed the round-1 fix introduced R1-R3. Same unshipped migration, edited in place (`has-pending-model-changes` clean).

- R1 — completing a `creando` row sets `expira_en = ahora + 10 min` (the re-sent order carries a fresh `PT10M`); the normal expiry/refresh rule then runs on the just-completed row (create's `else if` became a second `if`).
- R2 + R3 — the "400/422 -> cancelado" rule is gone: a 4xx is not proof that no order exists. New time rule from the reservation instant (`created_at`, `reservadoEn`), in `ResolverCreandoAsync`, used by create, GET and cancel under the same locks:
  - `ahora < reservadoEn + 10 min`: re-send; any failure (4xx, 5xx, timeout, invalid 2xx) keeps it `creando` and answers the error as before.
  - `reservadoEn + 10 min <= ahora < reservadoEn + 25 min`: never re-sent (it could create a fresh payable order); create, GET and cancel answer 409 `cobro_mercadopago_en_curso` (charge being resolved, the cashier can use another medio).
  - `ahora >= reservadoEn + 2*10 min + 5 min (margin raised from 2 to 5 min after the final re-judgment: both judges suggested it for HTTP timeouts and clock skew)`: any order the row could have created has expired, so the row becomes `expirado` locally with no Mercado Pago call, freeing the number and the medio guard; create then opens the next `intento`.
  - `ck_cobros_mercadopago_orden` is now `estado IN ('creando','expirado') OR id_orden_mercadopago IS NOT NULL` (no `cancelado` without order is reachable); docs/10 part 07 and the entity comment updated.
- Suggestion — `CobroDeMercadoPago.VerificandoPago` (`verificandoPago`) is true when `estado` is `pendiente` and `qrData` is null (a recovered `processed` order without a readable payment): E3-T3 shows "verificando el pago" instead of a QR. The loop-exhaustion 409 message is now neutral.
- Tests (`MercadoPagoCobrosTests`, injected clock): 400/422 within the window keeps `creando` and stays recoverable; late completion renews `expira_en`; one instant before the window still re-sends; from the window on create/GET/cancel are 409 with no MP call (also one ms before the give-up instant); at the give-up instant GET/cancel/create expire the row without MP call and the number is charged again (`intento` 2); `verificandoPago` true/false.
- Mutation evidence (one per run, each killed): removing the `expira_en` renewal, `>=` -> `>` on the re-send window, `>=` -> `>` on the give-up instant, `VerificandoPago` forced false.
- Verification: build 0 errors, `has-pending-model-changes` clean, Domain 2417, Application 1650, integration filter 154 (3 m 35 s).

## Judgment-day E3-T1 verdict

- 2026-11-01: **APPROVED** after two fix rounds. Round 1 (both judges): approval without a readable paid amount, fragile
  lost-commit retry, reuse branch not re-read under lock, medio without a live-charge guard; fixed in 9dbab484 (reserve-then-call
  with a `creando` row, stored key and body). Re-judgment (both): stale expiry on late completion, `creando` rows stuck forever,
  4xx taken as "no order"; fixed in 95337690 (time bands from `created_at`: re-send within one vigencia, 409 en curso until
  2 vigencias + margin, then local `expirado`). Final re-judgment: both clean. Applied after it (both suggested): margin 2 -> 5 min.
- Accepted (judge B, documented): a re-send whose response is lost leaves no order id; that order is not payable by a customer
  because a dynamic QR exists only in the response Ways never showed, and it expires before the give-up instant.

## E3-T2 design (2026-11-01)

Branch `pnader/mercadopago-e3-t2`, stacked on E3-T1. Backend only.

- `PagoDeVenta` gains optional `IdCobroMercadoPago` (dto-contract-honesty: persisted or rejected). The sale's payment row gets
  a nullable FK to `cobros_mercadopago` (column `id_cobro_mercadopago` on the table that stores sale payments), and
  `cobros_mercadopago` gets nullable `id_comprobante_venta` (FK, UNIQUE): one charge pays at most one sale.
- `ServicioDeVentas.EmitirAsync` (inside its existing transaction and locks): a payment whose medio has `proveedor` must carry
  `IdCobroMercadoPago` (400 `cobro_mercadopago_requerido`; E5 will add the offline "sin verificar" alternative); a payment
  with `IdCobroMercadoPago` on a medio without proveedor -> 400. The charge, read under `FOR UPDATE`, must be: same tenant and
  punto de venta as the sale, same `numero_preasignado`, same `id_medio_pago`, `estado = aprobado`, `importe_pagado` equal to
  the payment's `Importe`, and not linked to another sale (409 codes: `cobro_mercadopago_no_aprobado`,
  `cobro_mercadopago_de_otra_venta`, `cobro_mercadopago_importe_distinto`, `cobro_mercadopago_ya_usado`). The payment's
  `Referencia` is set from the charge's Mercado Pago payment id.
- Idempotent retry of the same sale (same `NumeroPreasignado` and content) succeeds: a charge already linked to THIS sale is
  fine. Annulling a sale leaves the charge linked (the money was received; refunds are E6).
- The offline outbox carries `IdCobroMercadoPago` inside `SolicitudDeVenta` unchanged (no POS change in this task).

## E3-T2 progress (2026-11-01)

Done on `pnader/mercadopago-e3-t2`, commit `feat(ventas): la venta cobrada con Mercado Pago vincula su cobro aprobado` (hash in git log). Migration `CobroMercadoPagoLigadoALaVenta`.

- Model: `cobros.id_cobro_mercadopago` (nullable, FK `fk_cobros_cobro_mercadopago` composite with tenant, RESTRICT, index `ix_cobros_cobro_mercadopago`); `cobros_mercadopago.id_comprobante_venta` (nullable, FK `fk_cobros_mercadopago_comprobante_venta`, RESTRICT) with UNIQUE `ux_cobros_mercadopago_comprobante_venta` on `(id_comprobante_venta, id_tenant)` (composite so it also indexes the FK; the sale id is globally unique, so it is "one charge per sale"); new alternate key `ak_cobros_mercadopago_id_id_tenant`. 23505 -> 409 `cobro_mercadopago_ya_usado`; the FKs fall in the generic 23503 -> 400. docs/10 §4 and §7 updated. No golden-inventory change was needed (guards pass).
- Sale (`ServicioDeVentas.EmitirAsync`): `PagoDeVenta.IdCobroMercadoPago` (also `PagoEmitido`). Form rules in `ReglaDeCobroEnVenta` (domain): provider medio without charge 400 `cobro_mercadopago_requerido`; charge on a medio without provider 400 `..._en_medio_sin_proveedor`; charge without `NumeroPreasignado` 400 `..._sin_numero_preasignado`; client `Referencia` with a charge 400 `..._referencia_no_admitida`; same charge twice 400 `..._repetido`. In the transaction (step 1.6, after turno, before the comprobante): `FOR UPDATE` of the charge rows in ascending id, single read, then 400 `..._inexistente` or 409 `..._de_otra_venta` (PV, numero or medio differ), `..._no_aprobado`, `..._ya_usado`, `..._importe_distinto` (payment `Importe` must equal `importe_pagado`). After the comprobante insert the charge is linked and the payment row gets `id_cobro_mercadopago` and `referencia` = Mercado Pago payment id. Zero extra statements without charges.
- Lock order: the charge service takes advisory PV -> advisory charge -> charge row and never waits on sale locks; an approved charge is no longer modified, so the sale's row lock cannot close a cycle.
- Idempotent retry: `ExigirMismoContenido` compares `IdCobroMercadoPago`; the `Referencia` of a payment with charge is ignored (server-set). A concurrent identical pair ends 201 + (201 | 409 `cobro_mercadopago_ya_usado`), one sale.
- Credit notes: a charge is rejected (`..._no_admitido_en_devolucion`) but a provider medio is accepted as the refund medio without charge (refunds are E6). Decision to confirm.
- Other writers of `Cobro` rows: `ServicioDeCambioDeMedios` (409 `pago_mercadopago_no_editable` when any live payment has a charge; new payments go through the same form rule), `ServicioDeFacturacionDeRemitos` (form rule, no numero -> provider medio rejected), `ServicioDeCuentaCorriente` receipts (provider medio rejected). A structural guard (`EscritoresDePagosDeVentaEstructuralTests`) requires every `new Cobro` file to call the rule.
- Mutation evidence (one per run, all killed): PV, numero, medio, estado, importe, ya_usado, requerido, sin_proveedor, sin_numero, referencia, repetido, esDevolucion (domain test only), identity ignoring referencia, identity ignoring the charge, link skipped, referencia from the MP payment, id persisted on the payment, `FOR UPDATE` removed (race test), cambio-de-medios guard, its form rule, receipts, remitos.
- Not exercised: nothing against the real Mercado Pago API; POS bundle/offline engine unchanged (T3 sends the field).

## Judgment-day E3-T2 round 1 (2026-11-01)

Both blind judges confirmed E3T2-1; two small items were accepted from one judge each. The E3-T2 migration `CobroMercadoPagoLigadoALaVenta` is unshipped, so it was edited in place (Designer + snapshot synced; `has-pending-model-changes` clean).

- E3T2-1 — a charge could become `aprobado` with `id_pago_mercadopago` NULL (`AplicarOrden` stored null for an empty or over-long payment id) and the sale then threw `InvalidOperationException` (500) with the number stuck. Fixed in three layers: (a) `processed` approves only with a readable amount AND a valid payment id (1..64 chars); otherwise the charge stays `pendiente`, a warning is logged (order and charge ids only) and the next poll retries; the response follows the same rule as the unreadable amount; (b) CHECK `ck_cobros_mercadopago_aprobado_con_pago` (`estado <> 'aprobado' OR id_pago_mercadopago IS NOT NULL`) in the E3-T2 migration, backstop 23514 -> 409 `cobro_mercadopago_sin_pago`, raw-SQL test; (c) `ReglaDeCobroEnVenta.ExigirCobroUtilizable` answers a typed 409 `cobro_mercadopago_sin_pago` for an approved charge without payment id (unit-tested; unreachable through the database once the CHECK holds, kept as a defence in the domain rule). The false "la aprobación lo exige" exception message in `EscriturasDeCobroMercadoPago` became a comment that points at the rule.
- A sale admits at most ONE Mercado Pago charge (one live charge per number, UNIQUE on the sale). `ExigirSinCobrosRepetidos` / `cobro_mercadopago_repetido` was replaced by `ExigirUnSoloCobro` -> 400 `venta_con_mas_de_un_cobro_mercadopago` (same charge twice or two different ones). Documented in the `PagoDeVenta` XML doc.
- Accepted limits (documentation only): numbering is per (PV, tipo de comprobante) while the charge carries only (PV, número); a mismatch needs the device to send the charge with a sale it did not charge, and the charge is consumed once (UNIQUE). Annulling a charged sale leaves the charge linked: refunds are E6.
- Tests: processed order with empty / over-long payment id stays `pendiente` and approves when the id appears (`MercadoPagoCobrosTests`); CHECK backstop with 23514 and the 409 translation; two different charges in one sale -> 400; unit tests for `ExigirUnSoloCobro` and the sin-pago 409. The raw-SQL helper that inserts `aprobado` rows now sets a payment id.
- Mutation evidence (one per run, all killed): `|| idPago is null` removed from `AplicarOrden`; CHECK SQL replaced by `true` in the migration; `Count() > 1` -> `> 2`; the `IdPagoMercadoPago is null` guard in `ExigirCobroUtilizable` disabled.
