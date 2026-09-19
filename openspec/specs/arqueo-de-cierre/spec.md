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

The classic cierre (`POST /api/caja/turnos/{id}/cierre`) request contract
MUST accept only `(id_medio_pago, importe_declarado)` pairs — no field for a
total, a subtotal, or an expected amount MUST exist anywhere in the request
shape. `importe_esperado` MUST always be computed server-side from the
ledgers, never accepted as input. Cierre Por Retiro (below) is a SECOND
close mode with its own, narrower contract — it carries no declared counts
at all, only the closing withdrawal amount, which is itself never a total
(see Cierre Por Retiro Payload Carries Only The Withdrawal Amount).

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

### Requirement: A Proveedor Gasto Linked To A Compra Introduces No New Derivation Term

A `gasto` with `id_comprobante_compra` set MUST flow through the existing
`SUM(gastos.importe on that medio)` term of the per-medio `importe_esperado`
derivation exactly like any other gasto — `CalculadorDeArqueo` MUST NOT
gain a compra-specific branch, term, or formula.

#### Scenario: A compra payment reduces esperado through the existing term only
- GIVEN a turno with `1500` in efectivo pagos and a `400` gasto
  (`categoria = proveedor`, linked to a confirmed compra) paid in efectivo
- WHEN the efectivo derivation runs
- THEN `importe_esperado` decreases by exactly `400` through
  `SUM(gastos.importe on that medio)`, with no separate compra term

#### Scenario: CalculadorDeArqueo source is unchanged by this stage
- GIVEN the `CalculadorDeArqueo` implementation before and after stage 8
  ships
- WHEN both versions are compared
- THEN they are byte-identical — no new branch or term was introduced for
  compra-linked gastos

## Cierre Por Retiro

A second close mode (`POST /api/caja/turnos/{id}/cierre-por-retiro`),
alongside the classic cierre above, structurally matching the owner's actual
practice: the cashier withdraws the counted cash and LEAVES the fondo
inicial in the drawer — nothing is counted at close. Both modes write
`arqueos_turno`/`movimientos_tesoreria` identically and are equally
irreversible; only how `importe_declarado` is sourced differs. The classic
cierre remains the web/arqueo mode (counted declarations) and is untouched
by this section.

### Requirement: Cierre Por Retiro Payload Carries Only The Withdrawal Amount

The cierre-por-retiro request contract MUST accept only
`(importe_retirado, observaciones?)` — `importe_retirado` is a MOVEMENT
amount (what the cashier physically takes out), never a total of sales, a
subtotal, or an expected/declared amount. No per-medio field MUST exist
anywhere in this request shape. `importe_retirado` MUST be `>= 0`; a
negative value MUST be rejected with `400 importe_retirado_invalido` before
reaching the database.

#### Scenario: A cierre-por-retiro request with only the withdrawal amount is accepted
- GIVEN a cierre-por-retiro request with `importeRetirado = 200`
- WHEN it is submitted
- THEN it is accepted and processed

#### Scenario: No request shape accepts a total or a per-medio count
- GIVEN the cierre-por-retiro endpoint's request contract
- WHEN it is inspected
- THEN it contains no `total`, `esperado`, `declarado`, or per-medio field —
  only the withdrawal amount and an optional observaciones

#### Scenario: A negative withdrawal amount is rejected
- GIVEN a cierre-por-retiro request with `importeRetirado = -1`
- WHEN it is validated
- THEN it is rejected with `400 importe_retirado_invalido` and the turno
  stays open

### Requirement: Cierre Por Retiro Declares The Fondo At The Anchor And The Esperado Elsewhere

`importe_esperado` per medio MUST be derived by the exact same formula as
the classic cierre (Importe Esperado Derivation Per Medio, above) — there is
no second formula. `importe_declarado` MUST be sourced by the server,
never the client: for the cash anchor medio (`ResolvedorDeMedioDeCajaFisica`),
`importe_declarado = fondo_inicial` (it physically stays in the drawer); for
every other arqueable medio, `importe_declarado = importe_esperado` (there
is nothing physical to count on a non-cash medio).

#### Scenario: The anchor is declared with the fondo inicial
- GIVEN a turno with `fondo_inicial = 500` and cash sales
- WHEN it closes by retiro
- THEN the cash anchor's `arqueos_turno` row has `importe_declarado = 500`

#### Scenario: A non-cash medio is declared with its own esperado
- GIVEN a turno with tarjeta sales of `300` and no tarjeta gastos
- WHEN it closes by retiro
- THEN the tarjeta `arqueos_turno` row has `importe_declarado = 300 =
  importe_esperado`, so its `diferencia` is always `0`

### Requirement: If Withdrawn, The Closing Retiro Is Inserted Before Deriving And Counts As Retiro/Ingreso

When `importe_retirado > 0`, the transaction MUST insert one
`movimientos_caja` row (`tipo = retiro`, `motivo = "Retiro de cierre de
turno"`, the closing employee) BEFORE reading the derivation's insumos, so
it is included in `SUM(movimientos_caja retiro)` exactly like any other
retiro of the turno — it reduces the anchor's `importe_esperado` and is
counted in the chained `movimientos_tesoreria` row's `ingreso`, same as the
classic cierre's tesorería chaining. When `importe_retirado = 0`, no
`movimientos_caja` row MUST be inserted (a physical retiro of `0` is not a
movement — it also could not satisfy `ck_movimientos_caja_importe`, which
requires `> 0` for `tipo <> apertura_cajon`).

#### Scenario: A positive withdrawal is inserted before deriving and reduces the anchor's esperado
- GIVEN a turno with `fondo_inicial = 500`, cash pagos of `1000`, and
  `importeRetirado = 200`
- WHEN it closes by retiro
- THEN a `movimientos_caja` row of `tipo = retiro, importe = 200` exists for
  the turno, and the anchor's `importe_esperado` includes `-200` from it

#### Scenario: A zero withdrawal inserts no movimiento
- GIVEN a cierre-por-retiro request with `importeRetirado = 0`
- WHEN it is processed
- THEN no new `movimientos_caja` row exists for the turno beyond whatever
  existed before the request

### Requirement: Cierre Por Retiro Is One Atomic, Irreversible Transaction Reusing The Cierre Lock

Cierre por retiro MUST run as a single transaction with the same statement-1
lock as the classic cierre (the guarded `UPDATE turnos_caja ... WHERE estado
= 'abierto'`, first statement, held to commit): 404 if the turno does not
exist, `409 turno_ya_cerrado` if it exists but is not `abierto`. It reuses
the classic cierre's arqueo-insertion and tesorería-chaining statements
verbatim (only the source of `importe_declarado` differs — see above). Any
failure at any step MUST roll back the entire transaction, leaving the
turno open with no partial `movimientos_caja`/`arqueos_turno`/
`movimientos_tesoreria` rows. No reapertura or arqueo-edit endpoint MUST
exist for this mode either.

#### Scenario: Closing an already-closed turno by retiro is rejected
- GIVEN a turno with `estado = cerrado`
- WHEN a cierre-por-retiro is requested for that turno
- THEN it is rejected with `409 turno_ya_cerrado`

#### Scenario: A failed cierre-por-retiro leaves the turno open with no side effects
- GIVEN a cierre-por-retiro whose arqueo insert fails
- WHEN the transaction aborts
- THEN the turno is still `abierto`, no `arqueos_turno` rows exist, the
  closing `movimientos_caja` retiro (if any) does not exist, and no
  `movimientos_tesoreria` row exists

### Requirement: Resumen De Cierre Is Available For Any Closed Turno, By Either Mode

`GET /api/caja/turnos/{id}/resumen-de-cierre` MUST return the same
derived summary for any `cerrado` turno regardless of which endpoint closed
it (classic cierre or cierre-por-retiro) — the derivation does not depend on
the close mode. It MUST be rejected with `409 turno_no_cerrado` while the
turno is still `abierto`, and with `404` if the turno does not exist or
belongs to another tenant. Its response MUST be identical to what
`POST .../cierre-por-retiro` returned for that same turno — this is what
lets a client reprint or recover after an ambiguous network failure on the
POST.

#### Scenario: The resumen is rejected before the turno closes
- GIVEN an open turno
- WHEN `GET .../resumen-de-cierre` is requested
- THEN it is rejected with `409 turno_no_cerrado`

#### Scenario: The resumen after closing matches what the POST returned
- GIVEN a turno just closed by `POST .../cierre-por-retiro`
- WHEN `GET .../resumen-de-cierre` is requested right after
- THEN every field of the response is identical to the POST's response

### Requirement: The Diferencia Invariant Ties The Retiro Summary To The Persisted Arqueo

The cierre-por-retiro response's `diferencia` (`totalRetiros −
(ventasEnEfectivoNetas − gastosEnEfectivo + refuerzos)`) MUST equal the
negative of the `diferencia` that `arqueos_turno` persists for the cash
anchor medio, because that medio is declared with the fondo inicial.

#### Scenario: The response diferencia is the negative of the anchor's persisted diferencia
- GIVEN a closed-by-retiro turno with cash pagos, gastos, a fondo inicial,
  and a closing retiro
- WHEN the response and the persisted `arqueos_turno` row for the cash
  anchor are compared
- THEN `response.diferencia == -(arqueo.importeEsperado -
  arqueo.importeDeclarado)`
