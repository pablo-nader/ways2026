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

Every movimiento_caja write reachable through `POST
/api/caja/turnos/{id}/movimientos` MUST resolve the turno server-side and
fail with `409 turno_no_abierto` when it is not `abierto`.

The ONE documented exception: the closing withdrawal that
`POST /api/caja/turnos/{id}/cierre-por-retiro` inserts (spec
arqueo-de-cierre: Cierre Por Retiro) is written by the SAME atomic
transaction that transitions the turno to `cerrado` — it is never reachable
through the movimientos endpoint, carries no client-supplied `idTurnoCaja`
either, and is inserted immediately after that transaction's own
statement-1 lock, deliberately AFTER the turno's estado has already flipped
within that same transaction. This is not a second write path a client can
reach with an arbitrary turno id; it is the cierre-por-retiro transaction's
own internal step.

#### Scenario: Movimiento rejected with no open turno
- GIVEN punto de venta 7 has no open turno
- WHEN a retiro is requested for punto de venta 7
- THEN it is rejected with `409 turno_no_abierto`

#### Scenario: The cierre-por-retiro closing withdrawal is not blocked by this requirement
- GIVEN a turno closing by retiro with `importeRetirado = 200`
- WHEN the cierre-por-retiro transaction inserts the closing
  `movimientos_caja` row
- THEN it succeeds even though the turno's estado already reads `cerrado`
  within that same transaction — this write never goes through `POST
  .../movimientos` and is not a counter-example to the requirement above

### Requirement: Movimiento Authorization

Movimiento endpoints MUST be gated by `Politicas.OperacionDePos` (Vendedor +
Supervisor + Admin).

#### Scenario: Vendedor records a movimiento
- GIVEN a user with role Vendedor and an open turno at their punto de venta
- WHEN they submit a refuerzo with a motivo
- THEN the request succeeds
