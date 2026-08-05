# Estado de Cuenta Specification

## Purpose

Defines the estado de cuenta read model (legacy F4, doc-01:373-379): header
(saldo, acuerdo, disponibilidad), the movement list with running balance,
date filtering, and scoping — the screen `saldo` was deliberately kept
non-editable for.

## Requirements

### Requirement: Header Computes Disponibilidad Server-Side

The header MUST return `saldo`, `acuerdo` (`limite_credito`, or the literal
`"ilimitado"` when `credito_ilimitado = true`), and
`disponibilidad = acuerdo − saldo` (or `"ilimitado"` when
`credito_ilimitado`). No field MUST be client-suppliable.

#### Scenario: Disponibilidad for a limited-credit cliente
- GIVEN `saldo = 300`, `limite_credito = 1000`, `credito_ilimitado = false`
- WHEN the header is requested
- THEN `disponibilidad = 700`

#### Scenario: Disponibilidad shows ilimitado when credito_ilimitado
- GIVEN `credito_ilimitado = true`
- WHEN the header is requested
- THEN `acuerdo` and `disponibilidad` both read `"ilimitado"`

### Requirement: Movement List With Running Balance

The movement list MUST return every `movimientos_cuenta_corriente` row for
the cliente ordered by `fecha` DESCENDING (newest first — legacy F4 parity, doc-01:375; deterministic `id` tie-break), each carrying its own `saldo_resultante`
snapshot — the running balance MUST be read directly from that column, never
recomputed client-side.

#### Scenario: The list's saldo_resultante matches the ledger at every row
- GIVEN a cliente with three movimientos of known `saldo_resultante`
- WHEN the movement list is requested
- THEN each returned row's balance equals its stored `saldo_resultante`

### Requirement: Default Last Month Filter, Desde/Hasta, And Histórico

The endpoint MUST default to the last month of movements when no filter is
supplied, accept an explicit `desde`/`hasta` range, and support a `histórico`
mode returning the full ledger.

#### Scenario: No filter returns the last month
- GIVEN a cliente with movimientos spanning two years
- WHEN the movement list is requested with no filter
- THEN only movimientos from the last month are returned

#### Scenario: histórico returns the full ledger
- GIVEN the same cliente
- WHEN `historico = true` is requested
- THEN every movimiento is returned regardless of date

### Requirement: Tenant And Cliente Scoping

Estado de cuenta reads MUST enforce the two-layer isolation guarantee
(EF Core global query filter + Postgres RLS) for `id_tenant`, and MUST
require an explicit `idCliente` — there is no server-side "current cliente"
session state.

#### Scenario: RLS blocks a cross-tenant read
- GIVEN the app DB role has no `BYPASSRLS`
- WHEN a raw SQL query reads tenant 2's movimientos while `app.tenant_id = 1`
- THEN RLS returns zero rows

### Requirement: Empty State Returns Zero Movements, Not An Error

A cliente with no `movimientos_cuenta_corriente` rows MUST return `saldo =
0` and an empty movement list, never a 404.

#### Scenario: A brand-new cliente's estado de cuenta is empty but valid
- GIVEN a cliente with no CC activity
- WHEN estado de cuenta is requested
- THEN it returns `saldo = 0` and an empty movement list with `200`
