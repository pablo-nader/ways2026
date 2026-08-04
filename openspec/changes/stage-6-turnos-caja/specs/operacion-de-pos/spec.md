# Delta for Operación de POS

## MODIFIED Requirements

### Requirement: Checkout Orchestration Contract

The checkout endpoint MUST accept `idPuntoVenta`, an optional `idCliente`
(defaults to Consumidor Final when omitted), a list of cart lines
(`idArticulo`, `cantidad`), and a list of pagos (`idMedioPago`, `importe`,
optional `referencia`). Before any pricing or oferta resolution runs, it
MUST resolve the open turno for `idPuntoVenta` and reject with `409
turno_no_abierto` if none exists. It MUST return the emitted comprobante's
`id`, `numero` (formatted `PPPP-NNNNNNNN`), `estado`, totals, and items on
success, or a validation error identifying the specific rejected rule on
failure.
(Previously: no turno precondition existed — checkout proceeded straight to
pricing.)

#### Scenario: Successful checkout returns the formatted numero
- GIVEN a valid cart and full efectivo payment at punto de venta `7`
- WHEN checkout completes
- THEN the response includes `numero` formatted as `"0007-00000001"` (or the
  next correlativo) and `estado = "emitido"`

#### Scenario: Omitted idCliente defaults to Consumidor Final
- GIVEN a checkout request with no `idCliente`
- WHEN it is validated
- THEN the sale is attributed to the tenant's Consumidor Final

#### Scenario: Rejected checkout identifies the failing rule
- GIVEN a checkout whose payment fails the tolerancia check
- WHEN it is rejected
- THEN the error response identifies the tolerancia rule, not a generic
  failure

#### Scenario: Selling with no open turno fails before any pricing work
- GIVEN punto de venta 7 has no open turno
- WHEN a checkout request is submitted with a 3-line cart
- THEN it is rejected with `409 turno_no_abierto` before any oferta
  resolution or price lookup runs

## ADDED Requirements

### Requirement: Caja Surface Lives Under OperacionDePos

The apertura, cierre, movimientos de caja (retiro / refuerzo / apertura de
cajón), gastos, and resumen parcial endpoints MUST be gated by
`Politicas.OperacionDePos` — the same policy that gates checkout and
anulación, not a separate tier.

#### Scenario: Vendedor accesses the caja surface
- GIVEN a user with role Vendedor
- WHEN they call apertura, movimiento, gasto, resumen parcial, or cierre
  endpoints for their own punto de venta
- THEN authorization succeeds (subject to the flagged decision 2 role
  tightening for cierre, offered at the DB Change Gate)

#### Scenario: A role outside OperacionDePos is rejected from the caja surface
- GIVEN a user with `RolConocido.Root`
- WHEN they call any caja endpoint
- THEN authorization fails
