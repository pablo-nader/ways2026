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
`saldo_resultante` at insert time. All four `tipo_movimiento_cc` values now
have write paths: `consumo` (CC sale, stage 5), `pago` (RC pago a cuenta and
its anulación contramovimiento, stage 7), `ajuste` (manual correction, stage
7, plus the pre-existing anulación contramovimiento for a reversed
`consumo`), and `actualizacion_precios` (reliquidación, stage 7, exactly one
per run).
(Previously: only `consumo` and the `ajuste`-shaped anulación
contramovimiento had write paths; `pago` and `actualizacion_precios` were
reserved.)

#### Scenario: Consumo snapshots the resulting saldo
- GIVEN a cliente with `saldo = 300` pays `200.00` by cuenta corriente
- WHEN the consumo movimiento is inserted
- THEN `importe = 200`, `saldo_resultante = 500`

#### Scenario: Pago snapshots the resulting saldo
- GIVEN a cliente with `saldo = 500` pays an RC of `200.00`
- WHEN the pago movimiento is inserted
- THEN `importe = -200`, `saldo_resultante = 300`

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

Anulación of a comprobante that produced a `movimientos_cuenta_corriente`
row (a `consumo` from a CC sale, or a `pago` from an RC) MUST insert a
contramovimiento of equal magnitude and opposite sign to the original row,
and update `Cliente.Saldo` by the same amount, in the same transaction as
the comprobante's `estado` change and the inverse stock movements (when
applicable).
(Previously: scoped to a CC-sale consumo only; an RC's `pago` row had no
reversal path.)

#### Scenario: Anulación reverses Saldo exactly (consumo)
- GIVEN a comprobante whose consumo raised `Cliente.Saldo` from `100` to
  `250`
- WHEN it is anulado
- THEN a contramovimiento of `-150` is inserted and `Cliente.Saldo = 100`

#### Scenario: Anulación reverses an RC's Pago movement
- GIVEN an RC that dropped `Cliente.Saldo` from `500` to `300`
- WHEN it is anulado
- THEN a `+200` contramovimiento is inserted and `Cliente.Saldo = 500`

### Requirement: Saldo Is The Maintained Cache Of The Ledger

`Cliente.Saldo` MUST equal the sum of `movimientos_cuenta_corriente.importe`
for that cliente at any point in time — the same cache-of-the-ledger shape
`stock.cantidad` has over `movimientos_stock`. This invariant now spans all
four movement types — `consumo`, `pago`, `ajuste`, and
`actualizacion_precios` — not only `consumo` and its contramovimiento.
(Previously: proven only across `consumo` and its anulación
contramovimiento.)

#### Scenario: Saldo matches the sum after consumo and contramovimiento
- GIVEN a consumo of `200` followed by an anulación contramovimiento of
  `-200`
- WHEN `Cliente.Saldo` is compared against the sum of that cliente's
  movimientos
- THEN both equal `0`

#### Scenario: Saldo matches the sum across a mixed sequence
- GIVEN a cliente with a consumo (`+300`), a pago (`-100`), a manual ajuste
  (`-20`), and an actualizacion_precios (`+15`)
- WHEN `Cliente.Saldo` is compared against the sum of that cliente's
  movimientos
- THEN both equal `195`
