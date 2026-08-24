# Delta for Operación de POS

## ADDED Requirements

### Requirement: AdministracionFiscal Is A New Policy For Certificate And Fiscal-Identity Administration

`Politicas.AdministracionFiscal` (Admin only) MUST gate `/api/fiscal/certificados`,
`/api/fiscal/empresas/{id}/condicion-fiscal`, and `/api/fiscal/puntos-venta/{id}/numero-fiscal` —
a genuinely new risk tier (private key material and the emitter's legal identity), distinct from
`OperacionDePos`.

#### Scenario: A Supervisor is rejected from fiscal identity administration
- GIVEN a user with role Supervisor
- WHEN they call `PUT /api/fiscal/empresas/{id}/condicion-fiscal`
- THEN the request is rejected (authorization-wise)

### Requirement: Fiscal Emission Stays Under OperacionDePos, Not AdministracionFiscal

`/api/fiscal/comprobantes` (POST) and `/api/fiscal/comprobantes/{id}/reintentar` (POST) MUST stay
gated by the existing `Politicas.OperacionDePos` (Vendedor + Supervisor + Admin) — the letter, the
totals, and the CAE are all server-decided, so the risk being gated is not who presses the button.

#### Scenario: A Vendedor can attempt a fiscal emission (subject to the six gates)
- GIVEN a user with role Vendedor and an empresa with no active certificate
- WHEN they call `POST /api/fiscal/comprobantes`
- THEN the request is authorized (and then rejected with `409 certificado_fiscal_ausente` by the
  `comprobante-fiscal` gates — authorization and inertia are independent checks)
