# Consumo de Cuenta Corriente Specification

## Purpose

Defines the NARROW slice of stage 7 pulled forward by stage 5 (doc 10 §8):
`movimientos_cuenta_corriente` as a write-only ledger for `consumo` (on a
cuenta corriente sale) and `contramovimiento` (on anulación), the
credit-limit check, and `Cliente.Saldo` as the maintained cache the check
reads. Explicitly OUT of scope: reliquidación a precio del día (F4), CC
management UI/reporting, and `pago`/`ajuste`/`actualizacion_precios`
movement types (stage 7).

## Requirements

### Requirement: Movimiento Schema At Rest

`movimientos_cuenta_corriente` MUST be operativa-scoped (`id_tenant` +
`id_punto_venta`, doc 09), immutable once inserted, and MUST snapshot
`saldo_resultante` at insert time. Stage 5 writes only `tipo = consumo` (on
sale) and `tipo = ajuste`-shaped inverse rows used as the anulación
contramovimiento — no `pago` or `actualizacion_precios` row is ever produced
by this stage.

#### Scenario: Consumo snapshots the resulting saldo
- GIVEN a cliente with `saldo = 300` pays `200.00` by cuenta corriente
- WHEN the consumo movimiento is inserted
- THEN `importe = 200`, `saldo_resultante = 500`

### Requirement: Consumo Is Written Inside The Sale Transaction

When a pago's medio has `Comportamiento = CuentaCorriente`, checkout MUST
insert a `consumo` movimiento (`importe` = the pago's `importe`,
`id_comprobante_venta` set) and update `Cliente.Saldo` by the same amount, in
the same transaction as the comprobante, items, pagos, and stock writes.

#### Scenario: A CC sale updates Saldo atomically with the comprobante
- GIVEN a cliente with `saldo = 0`
- WHEN a `150.00` cuenta corriente sale is checked out
- THEN in the same transaction, the comprobante is emitted, a consumo
  movimiento of `150.00` is inserted, and `Cliente.Saldo = 150`

### Requirement: Credit-Limit Evaluation

A cuenta corriente pago MUST be rejected when `Cliente.Saldo + importe >
Cliente.LimiteCredito`, unless `Cliente.CreditoIlimitado = true`. Consumidor
Final MUST always be rejected for cuenta corriente, regardless of limit.

#### Scenario: Exact limit is accepted
- GIVEN `saldo = 700`, `limite_credito = 1000`, `credito_ilimitado = false`
- WHEN a cuenta corriente pago of `300.00` is submitted (`700 + 300 = 1000`)
- THEN it is accepted (limit is inclusive)

#### Scenario: One peso over the limit is rejected
- GIVEN `saldo = 700`, `limite_credito = 1000`, `credito_ilimitado = false`
- WHEN a cuenta corriente pago of `300.01` is submitted
- THEN it is rejected before any write

### Requirement: Anulación Produces A Contramovimiento

Anulación of a comprobante that included a cuenta corriente pago MUST insert
a contramovimiento (negative `importe`, equal magnitude to the original
consumo) and decrease `Cliente.Saldo` by the same amount, in the same
transaction as the comprobante's `estado` change and the inverse stock
movements.

#### Scenario: Anulación reverses Saldo exactly
- GIVEN a comprobante whose consumo raised `Cliente.Saldo` from `100` to
  `250`
- WHEN it is anulado
- THEN a contramovimiento of `-150` is inserted and `Cliente.Saldo = 100`

### Requirement: Saldo Is The Maintained Cache Of The Ledger

`Cliente.Saldo` MUST equal the sum of `movimientos_cuenta_corriente.importe`
for that cliente at any point in time — the same cache-of-the-ledger shape
`stock.cantidad` has over `movimientos_stock`.

#### Scenario: Saldo matches the sum after consumo and contramovimiento
- GIVEN a consumo of `200` followed by an anulación contramovimiento of
  `-200`
- WHEN `Cliente.Saldo` is compared against the sum of that cliente's
  movimientos
- THEN both equal `0`

### Requirement: No Reliquidación, No CC Management, No Pagos De Cuenta

Stage 5 MUST NOT implement reliquidación a precio del día (F4), a CC
management/reporting UI, or a "pago de cuenta corriente" endpoint that
reduces `Saldo` outside of an anulación contramovimiento. Those are stage 7.

#### Scenario: No pago-de-cuenta endpoint exists
- GIVEN a cliente with a positive `Saldo`
- WHEN any client attempts to call a CC-payment/reliquidación endpoint
- THEN no such endpoint exists (404)
