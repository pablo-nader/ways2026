# Mercado Pago: comisión real al aprobar el cobro

## Objective

When an integrated Mercado Pago cobro (medio `proveedor` `mercadopago_qr` / `mercadopago_point`) is approved through the
Orders webhook (or device polling, same code path), read the real fee from Mercado Pago and store it on the cobro. The turno
close credits the treasury account with that real fee when it is known, and the conciliation import only proposes the residual
difference, so the two paths never double-count.

## Problem / why

Today the close deducts an ESTIMATED commission (`medios_pago.comision_porcentaje`, e.g. Point 5 %, QR 0.968 %) and only the
conciliation report import (E6/E7) fixes the real commission per cobro and proposes a difference adjustment. Point fees vary by
debit/credit/installments, so the account is wrong until the report arrives.

## Evidence (verified)

- Orders API `GET /v1/orders/{id}`: `transactions.payments[]` has `id` (alphanumeric `PAY…`), `amount`, `paid_amount`, `status`,
  `reference_id` ("identifier of the processed payment"), `payment_method`; NO fee or net field.
  Sources: mercadopago.com.br/developers/pt/docs/qr-code/migrate-dynamic-qr-model-to-orders,
  …/docs/mp-point/payment-processing, …/docs/mp-point/resources/migrate-payment-intent-to-orders.
- Payments API `GET /v1/payments/{id}`: numeric `id`, `status`, `transaction_amount`, `fee_details[] {type, amount, fee_payer}`,
  `transaction_details.net_received_amount` (cross-border example: 100 = 90.96 net + 9.04 `mercadopago_fee` collector).
- Gap: timing of fee availability for Point/QR at `processed`, and whether `fee_details` includes IVA. Mitigation: a fee that
  cannot be read stays NULL (close falls back to the estimate) and the conciliation still corrects any residual against the
  report's FEE_AMOUNT.
- Code: approval in `ServicioDeCobrosMercadoPago.AplicarOrden` (stores `ReferenciaPagoMercadoPago` = numeric `reference_id`).
  Sale payments link to a cobro only at sale creation (`ServicioDeVentas.cs:1575`), after approval and before the close, so a
  fee stored at approval is always visible to the close. Medio change is blocked for MP-linked payments
  (`ServicioDeCambioDeMedios.cs:173`).

## Design

- New column `cobros_mercadopago.comision_informada numeric(18,2) NULL` (CHECK `>= 0`). Written ONLY when the cobro transitions to
  `aprobado`, never afterwards. Separate from `comision_real` (conciliation-owned, guarded by `ck_cobros_mercadopago_conciliacion`).
- Fee = Σ `fee_details[].amount` with `fee_payer = "collector"`, accepted only when the payment is `approved`, its
  `transaction_amount` equals the cobro's `importe_pagado`, and amounts are valid (≥ 0, ≤ amount). An empty `fee_details` counts as
  0 only when `net_received_amount == transaction_amount`; anything else unreadable → NULL. A failed call never blocks approval.
- A sale payment uses the informed fee only when it is the single live payment of an emitted comprobante for that cobro and its
  importe equals `importe_pagado` (one shared rule/query). Otherwise it is estimated as today.
- Close, compensation and reconstruction: per (turno, medio), commission = Σ informed fees + round(pct × (bruto − Σ importes of
  informed payments)); `porcentaje_aplicado` stays the medio's %. Retentions unchanged (on full bruto).
- Conciliation difference: a line with an informed fee has `estimada = comision_informada`; the % estimate is split only over
  the remaining lines of the group. Difference = real − estimada, as today.

## Scope

In: migration + entity/config, Payments DTO + capture, close/compensation/reconstruction, conciliation calculator/generator,
docs/10 part 07, unit + integration tests. Out: UI changes, re-fetching a fee after approval, retentions from MP.

## Delivery

Strategy `single-pr` (one cohesive behavior); branch `pnader/mp-comision-real`. Route: delegated direct (one writer, 2+
non-trivial files).

## Checklist

- [x] T1 Domain + schema: column, migration, calculators with informed fees, unit tests. (route: delegated; commit 8e29bdc2)
- [x] T2 Capture at approval: Payments DTO, fee rule, call in approval path; unit + integration (webhook) tests. (route: delegated; commit df202b01)
- [x] T3 Wiring: close, compensation, reconstruction, conciliation generator; integration tests. (route: delegated; commit 2f1f1b6e)
- [x] T4 docs/10 part 07 updated. (route: delegated; commit 2f1f1b6e)
- [ ] T5 judgment-day APPROVED, PR + auto-merge + Auto-fix.

## Acceptance

- Webhook approval with readable fee stores `comision_informada`; unreadable/failed fee leaves NULL and still approves.
- Close with an informed Point cobro writes commission = informed fee (+ % on the rest).
- Conciliation with report fee equal to the informed fee proposes no adjustment; a different fee proposes only the residual.

## Progress / evidence

Route: delegated direct (one writer; 2+ non-trivial files).

Model summary: `cobros_mercadopago.comision_informada numeric(18,2) NULL`, CHECK `ck_cobros_mercadopago_comision_informada`
(`IS NULL OR >= 0`), migration `20261109123321_ComisionInformadaMercadoPago` (DDL only, no backfill; written only by the approval path,
so no 409 translation).

- T1 `8e29bdc2`: `ComisionConocida` + 3-arg `CalculadorDeMovimientosDeCuenta.Calcular` (2-arg = `ComisionConocida(0,0)`, throws on
  impossible input); `PagoConciliadoParaComision.ComisionInformada` (optional last param), informed lines estimate = informed fee.
  RED: compile failure of the new tests; GREEN: Ways.Domain.Tests 2621/2621.
- T2 `df202b01`: `PagoDeMercadoPago` DTOs + `ComisionInformadaDeMercadoPago.Calcular` (unit tests, 18 cases);
  `ServicioDeCobrosMercadoPago.LeerComisionInformadaAsync` only when the state goes from not-aprobado to aprobado, catches
  `ErrorDominio` when `ct` is not cancelled. Fakes: `/v1/payments` answers 404 by default (existing tests untouched).
- T3/T4 `2f1f1b6e`: `ComisionesInformadasDePagos` (single rule) used by `AcreditarAsync`, `CompensarCambioDeMediosAsync` (cierre-rows
  branch; ajuste branch stays estimated), `ServicioDeReconstruccionDeCuentas` (preview + apply share the planner) and
  `GeneradorDePropuestasDeTesoreriaMercadoPago`. docs/10 part 07 updated.

Mutation evidence:
- `fee_payer == "collector"` → `true`: `UnaComisionQuePagaElCompradorNoCuenta` and
  `SinComisionesDelVendedorYSinUnNetoQueLoConfirmeNoHayComision` (payer-only case) fail; reverted, green.
- informed line estimate `ComisionInformada` → `Importe × % / 100`: the three informed-fee tests of
  `CalculadorDeDiferenciaDeComisionMercadoPagoTests` fail; reverted, green.
- close `Calcular(..., conocida)` → `default`: `ElCierreDescuentaLaComisionInformadaYElPorcentajeSoloSobreElResto` and
  `UnaComisionRealIgualALaInformadaNoProponeNada` fail; reverted, green.

Checks: build 0 errors; Ways.Domain.Tests 2621/2621; Ways.Application.Tests 1691/1691 (includes the structural guards);
new integration tests 9/9; integration MercadoPago|CuentasTesoreria|AislamientoDeTenant|SuperficieDeAutorizacion 619/619 (42 m) on 2f1f1b6e.

Next: T5 judgment-day, then PR.
