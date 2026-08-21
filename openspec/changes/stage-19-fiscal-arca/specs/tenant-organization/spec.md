# Delta for Tenant Organization

## ADDED Requirements

### Requirement: Empresa's Fiscal Condition Is Nullable, With No Honest Default

`empresas.id_condicion_fiscal` MUST be nullable, with a simple (non-composite) FK to
`condiciones_fiscales`. There MUST be no `NOT NULL DEFAULT` — the emisor's condition is a
real-world fact deciding letter A/B/C, and defaulting silently (e.g. to RI) would emit a wrong
letter. The fiscal path MUST refuse to emit with a named 409 until the value is set.

#### Scenario: A new empresa has no fiscal condition by default
- GIVEN a freshly provisioned empresa
- WHEN `id_condicion_fiscal` is read
- THEN it is NULL — no value is silently assumed

#### Scenario: Fiscal emission with an unset condición fiscal is rejected explicitly
- GIVEN an empresa with `id_condicion_fiscal IS NULL`
- WHEN a fiscal emission is attempted for that empresa
- THEN it is rejected with `409 empresa_sin_condicion_fiscal`

### Requirement: Punto De Venta's Fiscal Number Is Nullable And Unique Per Empresa

`puntos_venta.numero_fiscal` MUST be nullable, in range `1..99999`, and unique per empresa via
`ux_puntos_venta_numero_fiscal WHERE numero_fiscal IS NOT NULL`. The internal `id_punto_venta`
numbering (TX/NCX/TXR/RC/PRE/REM) MUST remain wholly unaffected — a punto de venta may operate
fiscal and non-fiscal simultaneously.

#### Scenario: A punto de venta with no fiscal number keeps operating non-fiscal traffic
- GIVEN a punto de venta with `numero_fiscal IS NULL`
- WHEN a TX sale is emitted through it
- THEN it succeeds exactly as before this stage — the internal series is untouched

#### Scenario: A numero_fiscal outside 1..99999 is rejected
- GIVEN a raw UPDATE setting `numero_fiscal = 100000`
- WHEN it executes
- THEN Postgres rejects it via `ck_puntos_venta_numero_fiscal_rango`, SQLSTATE `23514`
