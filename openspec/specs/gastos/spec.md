# Gastos Specification

## Purpose

Defines `gastos` (doc 10 §5): expense capture against an open turno,
categorías, required medio de pago, the optional `id_comprobante_compra`
link to the compra a proveedor gasto pays, and its effect on the arqueo.
Retiros are never expressed as a gasto — the legacy's `tipo = 95` magic
number dies here.

## Requirements

### Requirement: Gasto Schema At Rest

`gastos` MUST be operativa-scoped and carry `fecha`, `id_punto_venta`,
`id_turno_caja`, `id_empleado`, `categoria_gasto` enum (`proveedor | sueldos
| viaticos | impuestos | servicios | otros`), `id_proveedor NULL`, `id_area
NULL`, `concepto`, `detalle`, `id_medio_pago NOT NULL`, `numero_factura
NULL`, `importe`, `id_comprobante_compra NULL` (composite FK to
`comprobantes_compra`, `ON DELETE RESTRICT`).
(Previously: stated `id_comprobante_compra` does NOT exist in this stage —
the deferred FK lands here.)

#### Scenario: A gasto persists with its categoría and medio
- GIVEN an open turno at punto de venta 7
- WHEN a gasto of `categoria = servicios`, `importe = 300`, paid by
  transferencia, is submitted
- THEN a row is inserted with `id_turno_caja` equal to the open turno's id
  and `id_medio_pago` set to the transferencia medio

#### Scenario: A gasto links to the compra it pays
- GIVEN a confirmada compra of `total = 5000` to a proveedor
- WHEN a gasto of `categoria = proveedor`, `importe = 5000`,
  `id_comprobante_compra` set to that compra, is submitted
- THEN the gasto persists with the link, under the same open-turno gate as
  any other gasto

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

### Requirement: A Comprobante Compra Link Requires Categoria Proveedor

A gasto with `id_comprobante_compra NOT NULL` MUST have
`categoria = proveedor`. The open-turno gate and the `importe > 0` rule are
unchanged and apply identically to a compra-linked gasto.

#### Scenario: A non-proveedor categoria cannot link to a compra
- GIVEN a gasto request with `categoria = servicios` and
  `id_comprobante_compra` set
- WHEN it is validated
- THEN it is rejected before reaching the database

#### Scenario: A compra-linked gasto still requires an open turno
- GIVEN punto de venta 7 has no open turno
- WHEN a gasto linked to a compra is submitted for punto de venta 7
- THEN it is rejected with `409 turno_no_abierto`, same as any other gasto

### Requirement: A Compra-Linked Proveedor Gasto Writes A Pago Movement To The Ledger

A gasto with `categoria = proveedor` AND `id_proveedor NOT NULL` MUST write
exactly one negative `pago` movement to
`movimientos_cuenta_corriente_proveedor` inside `InsertarGastoAsync`'s
existing transaction, carrying `id_gasto` and — when `id_comprobante_compra`
is set — the same value as the movement's imputación. A gasto failing
either condition (a different categoria, or `categoria = proveedor` with
`id_proveedor IS NULL`) MUST write no movement — the same predicate the
retired `saldo-de-proveedor` formula used.

#### Scenario: A proveedor-categoria gasto with id_proveedor writes one pago movement
- GIVEN an open turno and a gasto of `categoria = proveedor`, `id_proveedor`
  set, `importe = 400`
- WHEN the gasto is inserted
- THEN exactly one `pago` movement of `importe = -400` is written in the
  same transaction

#### Scenario: A proveedor-categoria gasto with no id_proveedor writes no movement
- GIVEN a gasto of `categoria = proveedor` with `id_proveedor IS NULL`
- WHEN it is inserted
- THEN no ledger movement is written — the same case the retired formula
  already excluded from the saldo

#### Scenario: A gasto still requires an open turno regardless of the ledger write
- GIVEN punto de venta 7 has no open turno
- WHEN a `categoria = proveedor` gasto with `id_proveedor` set is submitted
- THEN it is rejected with `409 turno_no_abierto` and no ledger movement is
  written
