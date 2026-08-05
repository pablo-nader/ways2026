# Arqueo de Cierre Specification

## Purpose

Defines `arqueos_turno` (doc 10 §7): the server-side per-medio-de-pago
expected-amount derivation, the declared counts the cashier submits, the
resulting diferencia, which medios get a row, and the one-transaction,
irreversible cierre — the structural answer to legacy bug D7 (client-supplied
totals). The same derivation powers the live resumen parcial.

## Requirements

### Requirement: Arqueo Schema At Rest

`arqueos_turno` MUST carry `id_turno_caja`, `id_medio_pago`,
`importe_esperado numeric(14,2)`, `importe_declarado numeric(14,2)`,
`diferencia numeric(14,2)` equal to `importe_esperado − importe_declarado`.

#### Scenario: Cierre writes one row per medio with activity
- GIVEN a turno with sales in efectivo and tarjeta
- WHEN it closes
- THEN two `arqueos_turno` rows exist, one per medio, each with its own
  esperado/declarado/diferencia

### Requirement: Cierre Payload Carries Only Declared Counts

The cierre request contract MUST accept only `(id_medio_pago,
importe_declarado)` pairs — no field for a total, a subtotal, or an expected
amount MUST exist anywhere in the request shape. `importe_esperado` MUST
always be computed server-side from the ledgers, never accepted as input.

#### Scenario: A cierre request with only declared counts is accepted
- GIVEN a cierre request listing `importe_declarado` per medio with activity
- WHEN it is submitted
- THEN it is accepted and processed

#### Scenario: No request shape accepts a total
- GIVEN the cierre endpoint's request contract
- WHEN it is inspected
- THEN it contains no `total`, `esperado`, or equivalent field — only
  per-medio declared counts

### Requirement: Importe Esperado Derivation Per Medio

For a medio with `Comportamiento = efectivo`, `importe_esperado` MUST equal
`fondo_inicial + SUM(pagos_comprobante.importe) − SUM(pagos_comprobante.vuelto)
− SUM(gastos.importe on that medio) − SUM(movimientos_caja retiro) +
SUM(movimientos_caja refuerzo)`, reading only `pagos_comprobante` of
comprobantes with `estado = emitido`. For any other non-`cuenta_corriente`
medio, `importe_esperado` MUST equal `SUM(pagos_comprobante.importe) −
SUM(gastos.importe on that medio)` — no fondo, vuelto, retiro, or refuerzo
term applies, since those are physical-cash-only concepts. An RC's
efectivo/tarjeta pagos participate in this `SUM(pagos_comprobante.importe)`
term exactly like a TX's pagos — RC introduces no new term and no separate
derivation.
(Previously: silent on RC; the formula is unchanged, RC simply flows through
the existing `pagos_comprobante` join like any other comprobante.)

#### Scenario: Efectivo expected includes fondo, pagos, vuelto, gastos, and movimientos
- GIVEN `fondo_inicial = 500`, efectivo pagos totaling `3000` with `120` in
  vuelto, `400` in gastos paid in efectivo, a `200` retiro, and a `100`
  refuerzo
- WHEN the derivation runs
- THEN `importe_esperado = 500 + 3000 − 120 − 400 − 200 + 100 = 2880`

#### Scenario: Electrónico expected is pagos net of its own gastos only
- GIVEN tarjeta pagos totaling `1500` and a `200` gasto paid by tarjeta
- WHEN the derivation runs
- THEN `importe_esperado = 1500 − 200 = 1300`

#### Scenario: Anulados are excluded from the derivation
- GIVEN a comprobante paid `500` in efectivo and later anulado
- WHEN the efectivo derivation runs for that turno
- THEN the anulado comprobante's pago does not contribute to
  `importe_esperado`

#### Scenario: An RC pago counts toward efectivo esperado like any other pago
- GIVEN a turno with a TX sale paid `1000` efectivo and an RC pago a cuenta
  of `300` efectivo
- WHEN the efectivo derivation runs
- THEN both contribute to the same `SUM(pagos_comprobante.importe)` term —
  `importe_esperado` includes both amounts with no separate RC line

### Requirement: Arqueo Rows Only For Medios With Activity, Never Cuenta Corriente

A medio gets an `arqueos_turno` row only if it has at least one
`pagos_comprobante` (non-anulado), `gastos`, or — for the efectivo medio —
`movimientos_caja` row or `fondo_inicial > 0` in the turno. Medios with
`Comportamiento = cuenta_corriente` MUST NEVER get an arqueo row, regardless
of activity — there is nothing physical to count.

#### Scenario: A medio with no activity gets no row
- GIVEN a turno with no transferencia activity
- WHEN it closes
- THEN no `arqueos_turno` row exists for the transferencia medio

#### Scenario: Cuenta corriente never produces a row
- GIVEN a turno with cuenta corriente pagos
- WHEN it closes
- THEN no `arqueos_turno` row exists for the cuenta corriente medio

### Requirement: Cierre Is One Atomic, Irreversible Transaction

Cierre MUST, in a single transaction: derive `importe_esperado` per medio,
insert the `arqueos_turno` rows with the declared counts, set `estado =
cerrado` + `fecha_cierre` + `id_empleado_cierre`, and chain one
`movimientos_tesoreria` row. Any failure at any step MUST roll back the
entire transaction, leaving the turno open. No reapertura or arqueo-edit
endpoint MUST exist.

#### Scenario: A failed cierre leaves the turno open with no side effects
- GIVEN a cierre that fails while chaining the tesorería row
- WHEN the transaction aborts
- THEN the turno is still `abierto`, no `arqueos_turno` rows exist, and no
  `movimientos_tesoreria` row exists

#### Scenario: Closing an already-closed turno is rejected
- GIVEN a turno with `estado = cerrado`
- WHEN a cierre is requested for that turno
- THEN it is rejected with `409 turno_ya_cerrado` — the turno exists but is
  no longer open, which is distinct from `turno_no_abierto` (no turno at
  all); the loser of two concurrent cierres MUST receive this code

### Requirement: Resumen Parcial Uses The Same Derivation As Cierre

The resumen parcial endpoint MUST call the same per-medio derivation cierre
uses — there MUST NOT be two formulas.

#### Scenario: Resumen parcial matches what cierre would compute
- GIVEN an open turno with sales, gastos, a retiro, and a refuerzo
- WHEN resumen parcial is requested mid-turno and cierre is requested
  immediately after with no further activity
- THEN the per-medio `importe_esperado` values are identical in both
