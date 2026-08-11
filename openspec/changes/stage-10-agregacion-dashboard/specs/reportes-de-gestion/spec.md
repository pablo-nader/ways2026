# Reportes De Gestión Specification

## Purpose

Defines the read-only aggregation contract under `/api/reportes/*`: which
comprobantes count, with which sign, bucketed by which business day, and
gated by which role. Direct parameterized SQL over existing tables — no
schema change, no materialized view (proposal decision 1).

## Requirements

### Requirement: LecturaDeReportes Policy Gates The Volume/Operational Reports

`Politicas.LecturaDeReportes` MUST admit `RolConocido.Supervisor` and
`RolConocido.Admin` only. `Vendedor` and `Root` MUST be rejected. It gates
every `/api/reportes/*` route except `rentabilidad` and `comisiones`.

#### Scenario: Vendedor is rejected on every reportes route
- GIVEN a user with role Vendedor
- WHEN they call `GET /api/reportes/ventas/resumen`
- THEN the response is 403

#### Scenario: Supervisor is accepted on volume reports
- GIVEN a user with role Supervisor
- WHEN they call `GET /api/reportes/ventas/por-vendedor`
- THEN the response is 200

### Requirement: Business-Day Bucketing Resolved Through The Punto De Venta's Timezone

Every date-ranged report MUST accept `desde`, `hasta`, and `granularidad`
(`dia` | `semana` | `mes`) and bucket rows via `date_trunc(<granularidad>,
fecha AT TIME ZONE <zona_horaria>)`, where `zona_horaria` resolves punto de
venta → empresa → default (`America/Argentina/Buenos_Aires`) through the
existing `ServicioDeParametros` precedence. `semana` buckets MUST start on
Monday (ISO week).

#### Scenario: A late-evening sale lands on its own business day
- GIVEN a comprobante emitted at `2026-08-05T22:30:00-03:00` (01:30 UTC on
  the 6th) at a punto de venta with `zona_horaria = America/Argentina/Buenos_Aires`
- WHEN `ventas/resumen` buckets by `dia`
- THEN the sale appears in the August 5th bucket, not August 6th

#### Scenario: Weekly bucket starts on Monday
- GIVEN comprobantes on Sunday 2026-08-09 and Monday 2026-08-10
- WHEN `ventas/resumen` buckets by `semana`
- THEN the Sunday sale falls in the ISO week ending Aug 9, and the Monday
  sale opens the next ISO week

### Requirement: Net Sales Has No Sign Branch

`GET /api/reportes/ventas/resumen` MUST compute net sales as
`SUM(comprobantes_venta.total)` over rows where `tipos_comprobante.clase =
'venta'`, `comprobantes_venta.estado <> 'anulado'`, and `deleted_at IS
NULL`. No sign branching on comprobante type: NCX totals are already
negative (negative `cantidad`), so the sum is net of returns by
construction.

#### Scenario: An NCX reduces net sales without a sign branch
- GIVEN a TX for $1000 and an NCX against it for -$300 (both `emitido`)
- WHEN `ventas/resumen` sums the period
- THEN net sales = $700

#### Scenario: Anulado, soft-deleted, and cross-tenant rows are excluded
- GIVEN a $999,999 comprobante that is `anulado`, another that is soft-deleted
  (`deleted_at` set), and another belonging to a different tenant
- WHEN `ventas/resumen` runs for the caller's tenant and period
- THEN none of the three amounts appear in the total

### Requirement: Ticket Promedio Excludes NCX From Both Sides

`ticket_promedio` MUST equal TX total ÷ TX count, where TX = comprobantes
whose `tipos_comprobante.clase = 'venta'` and are NOT a nota de crédito.
NCX rows MUST be excluded from both the numerator and the denominator. Net
sales and `ticket_promedio` MUST be reported side by side, never one
derived from the other.

#### Scenario: NCX is excluded from ticket promedio on both sides
- GIVEN three TX of $100, $200, $300 and one NCX of -$50 in the period
- WHEN `ventas/resumen` computes `ticket_promedio`
- THEN it equals $200 (`600 / 3`), not `550 / 4`

### Requirement: Ventas Breakdown Endpoints By Punto De Venta, Vendedor, Medio De Pago

`GET /api/reportes/ventas/por-punto-venta`, `/por-vendedor`, and
`/por-medio-pago` MUST apply the same net-sales semantics and bucketing as
`ventas/resumen`, grouped respectively by `id_punto_venta`, `id_empleado`
(the emitting vendedor), and `pagos_comprobante.id_medio_pago`. Each row
MUST report its own subtotal, not a percentage of an implicit whole.

#### Scenario: Grouping by vendedor sums each empleado's TX independently
- GIVEN vendedor A emitted $500 and vendedor B emitted $700 in the period
- WHEN `ventas/por-vendedor` runs
- THEN it returns two rows: A = $500, B = $700, no cross-total row

### Requirement: Top Artículos Ranks By Net Quantity And Revenue

`GET /api/reportes/articulos/top` MUST rank `items_comprobante_venta` rows
joined to comprobantes matching the net-sales filter (clase venta, estado
`<>` anulado, `deleted_at IS NULL`), summing `cantidad` and `total` per
`id_articulo`, ordered by revenue descending by default.

#### Scenario: An NCX line reduces its article's ranking figures
- GIVEN articulo 42 sold 10 units for $1000, then 2 units returned via NCX
  for -$200
- WHEN `articulos/top` runs for the period
- THEN articulo 42 shows `cantidad = 8`, `total = 800`

### Requirement: Compras Bucketed By Fecha De Recepción, Confirmada Only

`GET /api/reportes/compras/por-proveedor` MUST bucket by
`comprobantes_compra.fecha_recepcion` and include only rows with `estado =
'confirmada'` and `deleted_at IS NULL`. `borrador`, `anulada`, and
soft-deleted rows MUST be excluded.

#### Scenario: A borrador compra never appears
- GIVEN a compra in `estado = 'borrador'` with `fecha_recepcion` set
- WHEN `compras/por-proveedor` runs for that period
- THEN the compra is absent from every proveedor's total

### Requirement: Gastos Resumen

`GET /api/reportes/gastos/resumen` MUST sum `gastos.importe` bucketed by
`gastos.fecha`, scoped to `deleted_at IS NULL` and the caller's tenant,
optionally grouped by `categoria`.

#### Scenario: A soft-deleted gasto is excluded
- GIVEN a gasto of $5000 with `deleted_at` set
- WHEN `gastos/resumen` runs for its period
- THEN the $5000 is absent from the total

### Requirement: Raw SQL MUST Spell Out Soft-Delete And Estado Filters Explicitly

Every report query MUST include an explicit `deleted_at IS NULL` clause
and its relevant `estado`/`clase` filter in the SQL text itself. EF's
global query filters do NOT apply to raw SQL and MUST NOT be relied upon.

#### Scenario: A soft-deleted row with an inflated amount never appears
- GIVEN a soft-deleted comprobante with `total = 999999`
- WHEN any `/api/reportes/*` endpoint aggregates its period
- THEN the $999,999 never contributes to any returned figure

### Requirement: Tenant Isolation Holds On Raw SQL Via Connection-Level RLS

Every report query MUST run on the same `DbContext` connection so
`InterceptorDeContextoDeTenant`'s per-connection GUCs (`app.tenant_id`)
keep RLS enforcing isolation, proven per endpoint rather than assumed.

#### Scenario: A cross-tenant row is absent from every report
- GIVEN tenant A and tenant B each have comprobantes in the same period
- WHEN a user of tenant A calls any `/api/reportes/*` endpoint
- THEN tenant B's amounts never appear in the response
