# Gastos Specification

## Purpose

Defines `gastos` (doc 10 §5, staged in etapa 6 per decision 1, WITHOUT
`id_comprobante_compra` — deferred to stage 8 following the
`movimientos_stock.id_comprobante_compra` deferred-FK precedent): expense
capture against an open turno, categorías, required medio de pago, and its
effect on the arqueo. Retiros are never expressed as a gasto — the legacy's
`tipo = 95` magic number dies here.

## Requirements

### Requirement: Gasto Schema At Rest

`gastos` MUST be operativa-scoped and carry `fecha`, `id_punto_venta`,
`id_turno_caja`, `id_empleado`, `categoria_gasto` enum (`proveedor | sueldos
| viaticos | impuestos | servicios | otros`), `id_proveedor NULL`, `id_area
NULL`, `concepto`, `detalle`, `id_medio_pago NOT NULL`, `numero_factura
NULL`, `importe`. `id_comprobante_compra` does NOT exist in this stage.

#### Scenario: A gasto persists with its categoría and medio
- GIVEN an open turno at punto de venta 7
- WHEN a gasto of `categoria = servicios`, `importe = 300`, paid by
  transferencia, is submitted
- THEN a row is inserted with `id_turno_caja` equal to the open turno's id
  and `id_medio_pago` set to the transferencia medio

### Requirement: Gasto Requires An Open Turno

A gasto write MUST resolve the punto de venta's open turno server-side and
fail with `409 turno_no_abierto` when none exists, for symmetry with sales
and movimientos_caja.

#### Scenario: Gasto rejected with no open turno
- GIVEN punto de venta 7 has no open turno
- WHEN a gasto is submitted for punto de venta 7
- THEN it is rejected with `409 turno_no_abierto`

#### Scenario: Gasto succeeds with an open turno
- GIVEN an open turno at punto de venta 7
- WHEN a gasto is submitted
- THEN it is accepted and `id_turno_caja` is populated server-side, never
  client-supplied

### Requirement: Importe Must Be Positive

An `importe` of `0` or less MUST be rejected (legacy parity: the legacy
already rejects `importe = 0`).

#### Scenario: Zero-importe gasto is rejected
- GIVEN a gasto request with `importe = 0`
- WHEN it is validated
- THEN it is rejected before reaching the database

### Requirement: No Magic Tipo Encodes A Retiro As A Gasto

There MUST be no `categoria_gasto` value or write path that represents a
retiro de efectivo. Retiros MUST always be written to `movimientos_caja`.

#### Scenario: Gastos table never receives a retiro-equivalent row
- GIVEN the `categoria_gasto` enum
- WHEN it is inspected
- THEN it contains no value equivalent to the legacy's `tipo = 95` — retiros
  have no representation here

### Requirement: Gasto Authorization

Gasto endpoints MUST be gated by `Politicas.OperacionDePos` (Vendedor +
Supervisor + Admin).

#### Scenario: Vendedor records a gasto
- GIVEN a user with role Vendedor and an open turno
- WHEN they submit a valid gasto
- THEN the request succeeds
