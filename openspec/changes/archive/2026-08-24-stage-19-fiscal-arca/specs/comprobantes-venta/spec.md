# Delta for Comprobantes de Venta

## ADDED Requirements

### Requirement: Fiscal Columns Are Additive, Nullable, And Coherence-Checked

`comprobantes_venta` MUST gain `cae`, `cae_vencimiento`, `resultado_fiscal`, and
`observaciones_fiscales`, all nullable. `resultado_fiscal IS NULL` MUST mean "not a fiscal
comprobante" — the state of 100% of existing and all TX/NCX/TXR/RC traffic, permanently.
`ck_comprobantes_venta_fiscal_coherente` MUST enforce: either all four columns are NULL, or
`resultado_fiscal` is set with `cae`/`cae_vencimiento` arriving together and present if and only if
`resultado_fiscal IN ('aprobado','aprobado_con_observaciones')`.

#### Scenario: An existing non-fiscal comprobante validates the new CHECK trivially
- GIVEN any comprobante row that existed before this migration
- WHEN the migration applies `ck_comprobantes_venta_fiscal_coherente`
- THEN it validates without modification — all four new columns are NULL on that row

#### Scenario: A rejected fiscal result cannot carry a CAE
- GIVEN a raw UPDATE setting `resultado_fiscal = 'rechazado'` and a non-NULL `cae` on the same row
- WHEN it executes
- THEN Postgres rejects it via `ck_comprobantes_venta_fiscal_coherente`, SQLSTATE `23514`

### Requirement: Fiscal Emission Is A Separate Write Site — Non-Fiscal Checkout Stays Byte-Identical

The fiscal emission use case MUST be a write site distinct from `ServicioDeVentas.EmitirAsync`.
`ServicioDeVentas` and its resolver MUST remain byte-identical for TX/NCX/TXR/RC: a non-fiscal sale
issues **zero** extra SQL statements as a result of this stage.

#### Scenario: A non-fiscal sale issues the exact same statement sequence as before this stage
- GIVEN a TX checkout request
- WHEN it is emitted after this stage ships
- THEN the recorded SQL statement sequence is identical to the pre-stage baseline — zero extra
  statements

#### Scenario: A fiscal type submitted to the ordinary POS endpoint is still refused
- GIVEN `codigoTipoComprobante = "FA"` (already seeded `activo = true, es_fiscal = true`)
- WHEN `POST /api/ventas` is submitted with that code
- THEN it is rejected with `400`, exactly as before this stage — decision 9's guard is narrowed for
  the fiscal path's own endpoint, never removed from the ordinary checkout

## MODIFIED Requirements

### Requirement: Comprobante-Letter Resolution Gets Its First Caller, Through The Fiscal Path Only

`ResolvedorDeLetraComprobante` MUST stay a pure, DB-free Domain class, and it now gains its **first
caller**: the fiscal emission use case, which resolves the letter from the empresa's (emisor) and
the cliente's (receptor) `id_condicion_fiscal`. It MUST NOT be wired to `ServicioDeVentas` or any
POS checkout endpoint — the POS continues to emit only TX (venta) and NCX (devolución), neither of
which is fiscal (`tipos_comprobante.es_fiscal = false`).
(Previously: the class had no caller anywhere and the requirement asserted no endpoint exposed
letter resolution at all; 19a gives it a caller restricted to the fiscal path.)

#### Scenario: The resolver is still a pure function with no side effects
- GIVEN two condición fiscal inputs (emisor, receptor)
- WHEN `ResolvedorDeLetraComprobante` resolves the letter
- THEN it returns a value with no database read or write

#### Scenario: An RI emisor and an RI receptor resolve to letter A end to end
- GIVEN an empresa with `id_condicion_fiscal = RI` and a cliente with `id_condicion_fiscal = RI`
- WHEN a fiscal emission is processed against mocks
- THEN the resolved letter is `A`

#### Scenario: An RI emisor and a Consumidor Final receptor resolve to letter B end to end
- GIVEN an empresa with `id_condicion_fiscal = RI` and a cliente with `id_condicion_fiscal = CF`
- WHEN a fiscal emission is processed against mocks
- THEN the resolved letter is `B`

#### Scenario: No POS checkout endpoint exposes letter resolution
- GIVEN the ordinary POS API surface (`/api/ventas`)
- WHEN it is inspected for a letter-resolution path
- THEN none exists — the resolver is reachable only from the fiscal endpoint and unit tests
