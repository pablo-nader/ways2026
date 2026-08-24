# Comprobante Fiscal Specification

## Purpose

The CAE state machine, its three response states, the four invariants (no holes, idempotency,
terminality, inertia), the totals composition from the per-line alícuota snapshot, and the RG 4291
QR payload. This capability is what a fiscal comprobante may — and may never — do.

## Requirements

### Requirement: The CAE State Machine Has Three Response States, Not Two

`resultado_fiscal` MUST transition `pendiente → aprobado | aprobado_con_observaciones | rechazado`.
An approval carrying `Observaciones` (`aprobado_con_observaciones`) MUST be treated as a **valid,
terminal invoice** — never as a failure requiring re-emission.

#### Scenario: An approval with observations writes a CAE and is terminal
- GIVEN a `FECAESolicitar` response of `aprobado_con_observaciones` with a CAE and one observation
- WHEN the response is processed
- THEN the comprobante persists the CAE, its vencimiento, and the observations, with
  `resultado_fiscal = aprobado_con_observaciones`, and no further `FECAESolicitar` is ever issued
  for that number

#### Scenario: A rejected response writes no CAE and keeps the number reserved
- GIVEN a `FECAESolicitar` response with an excluding validation error and no CAE
- WHEN the response is processed
- THEN `resultado_fiscal = rechazado`, `cae` stays NULL, and the fiscal number remains bound to the
  `pendiente` comprobante — it is not released automatically

### Requirement: Invariant I1 — The Fiscal Series Has No Holes

A fiscal number, once assigned, MUST either be authorized or resubmitted with the **same** number.
It is released only by an explicit operator action while it is the top of its series.

#### Scenario: A failed emission does not advance the series
- GIVEN a `pendiente` comprobante holding fiscal number 42 whose WSFE call times out
- WHEN the emission transaction rolls back
- THEN `numeraciones_fiscales.proximo_numero` for that series is still 42, not 43 — a concurrent
  emission attempt for the same series is serialized behind it

### Requirement: Invariant I2 — FECompConsultar Precedes Any Non-Definitive Retry

Whenever a previous attempt for `(PtoVta, CbteTipo, CbteNro)` did not end in a definitive answer
(timeout, transport error, ambiguous response), the system MUST call `FECompConsultar` before any
subsequent `FECAESolicitar`. If ARCA already authorized the comprobante, its CAE MUST be adopted;
no second `FECAESolicitar` is issued for it.

#### Scenario: A timeout followed by a retry adopts the existing CAE
- GIVEN a `FECAESolicitar` call that times out after ARCA already authorized it
- WHEN the retry path resolves the `pendiente` comprobante
- THEN `FECompConsultar` is called first, finds the CAE, adopts it, and exactly **one**
  `FECAESolicitar` was issued across both attempts

### Requirement: Invariant I3 — An Emitted CAE Is Never Overwritten Or Deleted

`aprobado` and `aprobado_con_observaciones` MUST be terminal states. Correcting an already-emitted
invoice MUST require a nota de crédito, never a rewrite of the existing row.

#### Scenario: An attempt to re-request a CAE for an already-approved comprobante is refused
- GIVEN a comprobante with `resultado_fiscal = aprobado` and a CAE already persisted
- WHEN the retry endpoint (`POST /api/fiscal/comprobantes/{id}/reintentar`) is called for it
- THEN it is rejected — a terminal comprobante never re-enters `FECAESolicitar`

### Requirement: The Fiscal Emission Use Case Has Six Independent Named Gates, Including Invariant I4

*(Amended at verify: the shipped implementation carries SIX named pre-transaction 409 gates —
the original four plus `condicion_fiscal_receptor_no_mapeada` and the letter cross-check
`tipo_fiscal_letra_no_coincide` added by judgment-day slice-5 ronda 1/2 — see design.md D10 and
tasks.md Reconciliación 8. The count below is the binding one.)*

Fiscal emission MUST require, each as its own named 409 rather than a single boolean flag: the
empresa's `id_condicion_fiscal` is set, the punto de venta's `numero_fiscal` is set, an active
certificate exists for the empresa+ambiente, and the tipo comprobante is a valid fiscal type. With
no active certificate, the path MUST return `409 certificado_fiscal_ausente` and issue **zero**
network calls (invariant I4) — never a silent fallback.

#### Scenario: Emitting fiscal with no certificate performs zero network calls
- GIVEN an empresa with `id_condicion_fiscal` set and no active `certificados_fiscales` row
- WHEN `POST /api/fiscal/comprobantes` is called
- THEN it returns `409 certificado_fiscal_ausente` and the HTTP mock recorded zero requests

#### Scenario: Emitting fiscal with an empresa lacking a condición fiscal is rejected explicitly
- GIVEN an empresa with `empresas.id_condicion_fiscal IS NULL`
- WHEN `POST /api/fiscal/comprobantes` is called
- THEN it returns `409 empresa_sin_condicion_fiscal` — never a silently-defaulted letter

### Requirement: Totals Composition Maps The Per-Line Snapshot Into ARCA's Fields

`ComposicionDeTotalesFiscales` MUST derive `ImpNeto`, `ImpIVA`, and `Iva[]` only from
`items_comprobante_venta`'s existing per-line alícuota snapshot for real alícuotas (0%, 10.5%, 21%,
27%). Exento and No gravado amounts MUST go to `ImpOpEx` and `ImpTotConc` respectively and MUST
NEVER appear in `Iva[]` (decision 11). `ImpTotal` MUST equal
`ImpNeto + ImpIVA + ImpOpEx + ImpTotConc + ImpTrib` exactly.

#### Scenario: A mixed invoice routes exento and no-gravado outside Iva[]
- GIVEN an invoice with lines at 21%, 10.5%, Exento, and No gravado
- WHEN totals are composed
- THEN `Iva[]` contains only the 21% and 10.5% entries, the Exento amount is in `ImpOpEx`, the No
  gravado amount is in `ImpTotConc`, and `ImpTotal` sums exactly

### Requirement: The RG 4291 QR Payload Uses A Synthetic codAut In 19a

The QR payload MUST contain the 13 RG 4291 fields (`ver`, `fecha`, `cuit`, `ptoVta`, `tipoCmp`,
`nroCmp`, `importe`, `moneda`, `ctz`, `tipoDocRec`, `nroDocRec`, `tipoCodAut`, `codAut`), base64-
encoded into `https://www.afip.gob.ar/fe/qr/?p=<base64>`. Because no real CAE exists until 19b,
`codAut` MUST be synthetic, structurally correct in shape.

#### Scenario: The QR payload matches a hand-computed vector
- GIVEN a comprobante with a synthetic CAE and its full totals
- WHEN the QR payload is built
- THEN it base64-encodes to the exact vector computed by hand for the same inputs

### Requirement: NO_RESP Condición Fiscal Is Rejected Until Confirmed In 19b

A receptor whose `condicion_fiscal` maps to `NO_RESP` MUST be rejected with a named 409 rather than
invoiced on an unconfirmed mapping guess (decision 11's flagged uncertainty).

#### Scenario: A NO_RESP receptor is rejected explicitly
- GIVEN a cliente with `condicion_fiscal = NO_RESP`
- WHEN a fiscal emission is attempted for that cliente
- THEN it is rejected with a named 409 instead of being invoiced with a guessed
  `CondicionIVAReceptorId`
