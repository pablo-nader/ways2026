# Delta for Arqueo de Cierre

## MODIFIED Requirements

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
