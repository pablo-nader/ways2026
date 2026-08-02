# Parametros Operativos Specification

## Purpose

Defines the doc 10 §9 key/value `parametros` table: operational settings
scoped to a punto_venta, with fallback to an empresa-level default.

## Requirements

### Requirement: Parameter Scope and Fallback

`parametros` MUST store `clave`/`valor jsonb` pairs with a nullable
`id_punto_venta`. `NULL` MUST represent the empresa-level default; a value
MUST represent a punto_venta-specific override.

#### Scenario: Resolve punto_venta-specific value

- GIVEN a `tolerancia_pago` row with `id_punto_venta = 1` and value `10`,
  and another with `id_punto_venta NULL` and value `5`
- WHEN punto_venta 1 resolves `tolerancia_pago`
- THEN it receives `10`

#### Scenario: Fallback to empresa default

- GIVEN only an `id_punto_venta NULL` row exists for `vuelto_maximo` with
  value `20`
- WHEN punto_venta 2 (no override) resolves `vuelto_maximo`
- THEN it receives `20`

#### Scenario: No value and no default

- GIVEN no row exists for `slots_tickets_espera` at either level for a
  given empresa
- WHEN a punto_venta resolves that key
- THEN the system returns a documented application default or an explicit
  "not configured" result, never a silent exception
