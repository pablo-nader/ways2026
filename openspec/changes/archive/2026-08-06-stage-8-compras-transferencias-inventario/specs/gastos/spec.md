# Delta for Gastos

## Purpose Update (informational — apply manually at archive)

The Purpose clause "staged in etapa 6 per decision 1, WITHOUT
`id_comprobante_compra` — deferred to stage 8" is resolved: the deferred FK
lands in this stage.

## MODIFIED Requirements

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

## ADDED Requirements

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
