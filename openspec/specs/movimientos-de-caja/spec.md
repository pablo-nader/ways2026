# Movimientos de Caja Specification

## Purpose

Defines `movimientos_caja` (doc 10 §7): physical cash outside the sale —
retiro, refuerzo, and apertura de cajón (legacy F12) — each attached to the
punto de venta's open turno, with the motivo rules that replace the legacy's
`tipo = 95` magic number.

## Requirements

### Requirement: Movimiento Schema At Rest

`movimientos_caja` MUST be operativa-scoped via its turno and carry
`id_turno_caja`, `tipo_movimiento_caja` enum (`retiro | refuerzo |
apertura_cajon`), `importe numeric(14,2)`, `motivo text`, `id_empleado`,
`creado_el`. Rows are immutable — no update/delete endpoint MUST exist.

#### Scenario: A retiro persists against the open turno
- GIVEN an open turno at punto de venta 7
- WHEN a retiro of `200.00` with motivo "cambio de caja fuerte" is submitted
- THEN a row is inserted with `tipo = retiro`, `importe = 200.00`, and
  `id_turno_caja` equal to the open turno's id

### Requirement: Motivo Required For Retiro And Refuerzo

`retiro` and `refuerzo` movements MUST require a non-empty `motivo` —
money moving physically deserves a recorded reason (**flagged at the DB
Change Gate** as a deliberate tightening over the legacy, which never
required one for retiros).

#### Scenario: Retiro without motivo is rejected
- GIVEN a retiro request with an empty `motivo`
- WHEN it is validated
- THEN it is rejected with `400 movimiento_de_caja_sin_motivo` before reaching
  the database

#### Scenario: Refuerzo without motivo is rejected
- GIVEN a refuerzo request with an empty `motivo`
- WHEN it is validated
- THEN it is rejected with `400 movimiento_de_caja_sin_motivo`

#### Scenario: Retiro with a motivo is accepted
- GIVEN a retiro request with `motivo = "pago a proveedor en efectivo"`
- WHEN it is validated
- THEN it is accepted

### Requirement: Apertura De Cajón Follows Legacy F12 Parity

`apertura_cajon` MUST always persist `importe = 0` — a non-zero `importe`
supplied by the client MUST be rejected, not silently zeroed. `motivo` MUST
be at least 5 characters, mirroring legacy F12 (doc-01:157) rather than
inventing a new rule.

#### Scenario: Apertura de cajón with a non-zero importe is rejected
- GIVEN an apertura_cajon request with `importe = 50`
- WHEN it is validated
- THEN it is rejected — `importe` MUST be exactly `0` for this tipo

#### Scenario: Apertura de cajón with a short motivo is rejected
- GIVEN an apertura_cajon request with `motivo = "abc"` (3 characters)
- WHEN it is validated
- THEN it is rejected with `400 motivo_de_apertura_cajon_invalido`

#### Scenario: Apertura de cajón with a valid motivo is accepted
- GIVEN an apertura_cajon request with `motivo = "conteo inicial de turno"`
  (≥ 5 characters) and `importe = 0`
- WHEN it is validated
- THEN it is accepted

### Requirement: Movimiento Requires An Open Turno

Every movimiento_caja write MUST resolve the punto de venta's open turno
server-side and fail with `409 turno_no_abierto` when none exists.

#### Scenario: Movimiento rejected with no open turno
- GIVEN punto de venta 7 has no open turno
- WHEN a retiro is requested for punto de venta 7
- THEN it is rejected with `409 turno_no_abierto`

### Requirement: Movimiento Authorization

Movimiento endpoints MUST be gated by `Politicas.OperacionDePos` (Vendedor +
Supervisor + Admin).

#### Scenario: Vendedor records a movimiento
- GIVEN a user with role Vendedor and an open turno at their punto de venta
- WHEN they submit a refuerzo with a motivo
- THEN the request succeeds
