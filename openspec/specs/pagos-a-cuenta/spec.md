# Pagos a Cuenta Specification

## Purpose

Defines `RC` (Recibo de Cuenta), the pago a cuenta comprobante (doc-01 F4
"Ingresar pago", doc-10 §4/§8): an itemless, non-fiscal, non-stock
`tipos_comprobante` row that flows through the existing `ServicioDeVentas`
checkout machinery with physical (non-CC) medios and writes exactly one
negative `Pago` movement.

## Requirements

### Requirement: RC Tipo Comprobante Ships As An Idempotent Seed

The stage-7 migration MUST insert the `RC` row into `tipos_comprobante`
(`clase = venta`, `letra = NULL`, `signo = +1`, `discrimina_iva = false`,
`es_fiscal = false`, `afecta_stock = false`) via `INSERT ... WHERE NOT
EXISTS`, because `InicializadorDeBaseDeDatos` only seeds that table when
empty. The seed list MUST also gain `RC` for brand-new databases.

#### Scenario: RC resolves on an already-migrated database
- GIVEN a database already seeded through stage 6 (`tipos_comprobante`
  non-empty)
- WHEN the stage-7 migration runs
- THEN `RC` exists in `tipos_comprobante` afterward, with no duplicate row

#### Scenario: A fresh database seeds RC from the seed list
- GIVEN a brand-new database with an empty `tipos_comprobante`
- WHEN the initializer seeds the table
- THEN `RC` is present alongside `TX`/`NCX`

### Requirement: RC Comprobante Carries Zero Items And No Stock Effect

An RC comprobante MUST be emitted with zero `items_comprobante_venta` rows.
The RC request contract MUST NOT accept cart lines. The stock loop MUST be a
no-op for RC by construction (`afecta_stock = false`).

#### Scenario: RC emission persists no items
- GIVEN a valid RC payment request
- WHEN it is emitted
- THEN the resulting comprobante has zero `items_comprobante_venta` rows and
  zero `movimientos_stock` rows

### Requirement: RC Requires An Open Turno

RC MUST resolve the open turno server-side from `idPuntoVenta`, exactly like
checkout, and reject with `409 turno_no_abierto` before any other processing
when no open turno exists.

#### Scenario: RC with no open turno is rejected
- GIVEN punto de venta 7 has no open turno
- WHEN an RC payment is requested
- THEN it is rejected with `409 turno_no_abierto` before any write

#### Scenario: RC attaches the resolved open turno
- GIVEN an open turno at punto de venta 7
- WHEN RC is emitted
- THEN the persisted comprobante's `id_turno_caja` equals the open turno's id

### Requirement: RC Forbids Cuenta Corriente Medios And Consumidor Final

An RC pago MUST be rejected (`pago_a_cuenta_sin_medios_fisicos`) if any of
its medios has `Comportamiento = CuentaCorriente` — a debt cannot pay a debt.
An RC targeting Consumidor Final MUST be rejected
(`cliente_sin_cuenta_corriente`) before any write. `ValidadorDePagos` rules 5
(CF-blocks-CC) and 6 (credit limit) do not apply to RC.

#### Scenario: RC with a cuenta corriente medio is rejected
- GIVEN an RC payment where one pago's medio is `cuenta_corriente`
- WHEN it is submitted
- THEN it is rejected with `pago_a_cuenta_sin_medios_fisicos` before any write

#### Scenario: RC targeting Consumidor Final is rejected
- GIVEN Consumidor Final as the target cliente
- WHEN an RC payment is submitted
- THEN it is rejected with `cliente_sin_cuenta_corriente` before any write

#### Scenario: RC accepted with mixed physical medios
- GIVEN a cliente with debt and a payment split efectivo + tarjeta
- WHEN RC is submitted
- THEN it is accepted

### Requirement: RC Writes One Negative Pago Movement Atomically

Emitting RC MUST, in the same transaction as the comprobante and its
`pagos_comprobante`, insert exactly one `movimientos_cuenta_corriente` row
(`tipo = pago`, negative `importe` equal in magnitude to the RC total,
`id_comprobante_venta` set to the RC comprobante, `id_pago_comprobante NULL`)
and update `Cliente.Saldo` by the same amount via the same
`UPDATE ... RETURNING` pattern stage 5/6 use.

#### Scenario: RC emission writes one Pago movement and drops Saldo
- GIVEN a cliente with `saldo = 500`
- WHEN an RC of `200.00` (efectivo) is emitted
- THEN one `Pago` movement of `importe = -200` is inserted and
  `Cliente.Saldo = 300`, in the same transaction as the comprobante

#### Scenario: A failure after the comprobante insert rolls back everything
- GIVEN an RC checkout that fails while inserting `pagos_comprobante`
- WHEN the transaction aborts
- THEN no comprobante, Pago movement, or Saldo change is visible

### Requirement: Overpayment Produces Saldo A Favor, Never Rejected

An RC payment MUST NOT be rejected merely because it exceeds `Cliente.Saldo`;
`Cliente.Saldo` MAY become negative (saldo a favor) after the movement.
Vuelto on an RC's efectivo medio, if given, MUST still respect
`vuelto_maximo` through `ValidadorDePagos`, independent of the overpayment.

#### Scenario: Paying more than the outstanding saldo produces saldo a favor
- GIVEN `Cliente.Saldo = 100`
- WHEN an RC of `150.00` is emitted
- THEN it is accepted and `Cliente.Saldo = -50`

### Requirement: Anulación Reverses The Pago Movement

`AnularAsync`'s contramovimiento loop MUST extend to `Tipo == Pago`:
anulando an RC MUST insert a positive contramovimiento equal in magnitude to
the original Pago and restore `Cliente.Saldo`, in the same transaction as
`estado = anulado`. The existing `409 turno_cerrado` gate applies unchanged.

#### Scenario: Anulando an RC restores Saldo
- GIVEN an RC that dropped `Cliente.Saldo` from `500` to `300`
- WHEN it is anulado
- THEN a `+200` contramovimiento is inserted and `Cliente.Saldo = 500`

#### Scenario: Anulando an RC is rejected when its turno is closed
- GIVEN an RC comprobante whose turno is now `cerrado`
- WHEN anulación is requested
- THEN it is rejected with `409 turno_cerrado`

### Requirement: RC Gets Its Own Numeración Series

RC comprobantes MUST allocate `numero` via the existing
`(IdPuntoVenta, IdTipoComprobante)` `NumeracionComprobante` counter,
independent from TX's series.

#### Scenario: RC and TX numerar independently at the same punto de venta
- GIVEN TX is at `numero 50` and RC has never been emitted at punto de venta 7
- WHEN an RC is emitted there
- THEN it receives `numero = 1`, and the next TX still receives `numero = 51`
