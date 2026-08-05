# Turnos de Caja Specification

## Purpose

Defines `turnos_caja` (doc 10 §7): the apertura/cierre lifecycle, the
one-open-turno-per-punto-de-venta invariant enforced by a partial unique
index, `fondo_inicial`, `estado_turno` transitions, and the rule that a turno
is always server-resolved and never client-supplied.

## Requirements

### Requirement: Turno Schema At Rest

`turnos_caja` MUST be operativa-scoped (`id_tenant` + `id_punto_venta`, doc
09) with `id_empleado_apertura`, `id_empleado_cierre` nullable,
`fecha_apertura`, `fecha_cierre` nullable, `fondo_inicial numeric(14,2) NOT
NULL DEFAULT 0`, `estado_turno` enum (`abierto | cerrado`), `observaciones`.

#### Scenario: Apertura creates an open turno with its fondo
- GIVEN a Vendedor opens a turno at punto de venta 7 with `fondo_inicial = 500`
- WHEN the apertura is persisted
- THEN a row exists with `estado = abierto`, `fondo_inicial = 500`, and
  `fecha_cierre NULL`

#### Scenario: Cierre populates the closing fields
- GIVEN an open turno
- WHEN it is closed by employee 12
- THEN `estado = cerrado`, `fecha_cierre` is set, and `id_empleado_cierre = 12`

### Requirement: One Open Turno Per Punto De Venta

The database MUST enforce at most one `abierto` turno per punto de venta via
a partial unique index (`ux_turnos_caja_abierto (id_punto_venta) WHERE estado
= 'abierto'`) against a plain INSERT — no advisory lock, no read-then-insert
window. A `23505` violation on that index MUST map to `409 turno_ya_abierto`.

#### Scenario: A second apertura at an already-open punto de venta is rejected
- GIVEN punto de venta 7 already has an open turno
- WHEN another apertura is requested for punto de venta 7
- THEN it is rejected with `409 turno_ya_abierto`

#### Scenario: Concurrent aperturas race to exactly one winner
- GIVEN two concurrent apertura requests for the same punto de venta with no
  open turno
- WHEN both transactions commit
- THEN exactly one INSERT succeeds and the other fails with `23505`, mapped
  to `409 turno_ya_abierto`

#### Scenario: Aperturas at different puntos de venta are independent
- GIVEN punto de venta 7 has an open turno
- WHEN an apertura is requested for punto de venta 8
- THEN it succeeds

### Requirement: Apertura And Cierre Authorization

Both apertura and cierre MUST be gated by `Politicas.OperacionDePos`
(Vendedor + Supervisor + Admin) — legacy parity, since the legacy has no role
gate on caja. **Flagged at the DB Change Gate**: tightening cierre alone to
Supervisor + Admin is offered as a one-line change, not assumed.

#### Scenario: Vendedor opens and closes a turno
- GIVEN a user with role Vendedor
- WHEN they call apertura and later cierre on their own tenant's punto de
  venta
- THEN both requests succeed (authorization-wise)

#### Scenario: A role outside OperacionDePos is rejected
- GIVEN a user with `RolConocido.Root`
- WHEN they call apertura or cierre
- THEN authorization fails

### Requirement: Turno Is Always Server-Resolved, Never Client-Supplied

Every write that attaches to "the current turno" (sales, gastos,
movimientos_caja, cierre) MUST resolve the open turno server-side from
`idPuntoVenta` alone. No endpoint MUST accept an `idTurnoCaja` as client
input. When no open turno exists for the punto de venta, the write MUST fail
with `409 turno_no_abierto` before any other processing.

#### Scenario: No open turno blocks a dependent write
- GIVEN punto de venta 7 has no open turno
- WHEN a checkout, gasto, or movimiento de caja is requested for punto de
  venta 7
- THEN it is rejected with `409 turno_no_abierto`

#### Scenario: idTurnoCaja is not an accepted request field
- GIVEN the apertura/checkout/gasto/movimiento request contracts
- WHEN they are inspected
- THEN none of them accepts a client-supplied turno identifier
