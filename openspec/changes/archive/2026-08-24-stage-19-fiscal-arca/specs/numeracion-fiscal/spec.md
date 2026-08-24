# Numeración Fiscal Specification

## Purpose

The fiscal number series, keyed by `(fiscal punto de venta, ARCA comprobante type)`, with the
opposite transactional discipline of the existing internal counter, and its reconciliation against
`FECompUltimoAutorizado`.

## Requirements

### Requirement: The Fiscal Number Is Assigned Inside The Emission Transaction

`AsignadorDeNumeroFiscal` MUST take the next `numeraciones_fiscales.proximo_numero` **inside** the
same transaction as the fiscal emission use case (`UPDATE … RETURNING`), and keep it bound to the
`pendiente` comprobante until the series resolves. This is the opposite discipline of
`AsignadorDeNumeroComprobante`, which commits its own number in a small transaction **before** the
caller's, and MUST NOT be reused for fiscal numbering.

#### Scenario: A rolled-back emission does not consume the fiscal number
- GIVEN an emission that fails after taking fiscal number 10 but before the WSFE call commits
- WHEN the transaction rolls back
- THEN `proximo_numero` for that series is still 10 on the next read — no number was burned

#### Scenario: numeraciones_comprobante is never written by the fiscal path
- GIVEN a fiscal emission end to end
- WHEN the statements it issues are inspected
- THEN none of them touches `numeraciones_comprobante` — the fiscal series lives exclusively in
  `numeraciones_fiscales`

### Requirement: Concurrent Emissions For The Same Fiscal Series Are Serialized

Two concurrent fiscal emissions for the same `(id_punto_venta, codigo_afip)` MUST be serialized —
one blocks on the row lock held by the other for the duration of the WSFE round trip, under a
bounded client timeout.

#### Scenario: Two concurrent requests for the same series never produce the same number
- GIVEN two concurrent `POST /api/fiscal/comprobantes` requests for the same fiscal punto de venta
  and the same ARCA comprobante type
- WHEN both execute
- THEN they receive two distinct, sequential fiscal numbers — never a duplicate

### Requirement: The Fiscal Point-Of-Sale-To-Row Map Is Injective

`ux_puntos_venta_numero_fiscal (id_tenant, id_empresa, numero_fiscal) WHERE numero_fiscal IS NOT
NULL` MUST guarantee that no two internal `puntos_venta` rows for the same empresa share the same
`numero_fiscal` — the map from ARCA's series key `(PtoVta, CbteTipo)` to `(id_punto_venta,
codigo_afip)` MUST be injective.

#### Scenario: Two internal points of sale cannot share the same fiscal number
- GIVEN punto de venta A already has `numero_fiscal = 3` for empresa 1
- WHEN a raw UPDATE attempts to set `numero_fiscal = 3` on punto de venta B for the same empresa
- THEN Postgres rejects it via `ux_puntos_venta_numero_fiscal`, SQLSTATE `23505`

### Requirement: Reconciliation Against FECompUltimoAutorizado Updates ultimo_autorizado_arca

The reconciliation path MUST write `ultimo_autorizado_arca` and `sincronizado_en` together (never
one without the other) from a `FECompUltimoAutorizado` response, using `IRelojDelSistema` for the
timestamp.

#### Scenario: Reconciling an empty series records 0, not NULL
- GIVEN a `FECompUltimoAutorizado` fixture answering `CbteNro = 0` for a never-used series
- WHEN reconciliation runs
- THEN `ultimo_autorizado_arca = 0` and `sincronizado_en` is set — both together, per the CHECK
  constraint

#### Scenario: ultimo_autorizado_arca and sincronizado_en never arrive alone
- GIVEN a raw INSERT/UPDATE on `numeraciones_fiscales`
- WHEN it sets `ultimo_autorizado_arca` to a non-NULL value while leaving `sincronizado_en` NULL
- THEN Postgres rejects it via `ck_numeraciones_fiscales_sincronizacion`, SQLSTATE `23514`
