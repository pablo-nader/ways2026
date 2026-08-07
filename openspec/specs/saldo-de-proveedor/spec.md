# Saldo de Proveedor Specification

## Purpose

Defines the derived proveedor balance read (doc-10:408-409):
`Σ compras confirmadas − Σ gastos` linked to that proveedor, with per-compra
payment status from the linked gastos only. No table, no cache, no ledger —
explicitly an approximation, not an invariant, deliberately not following
stage 7's richer-ledger precedent because doc 10 forbids the extra table.

## Requirements

### Requirement: Saldo Is A Derived Read, Never Persisted

The proveedor saldo MUST be computed on read as `SUM(comprobantes_compra.total
WHERE estado = confirmada) − SUM(gastos.importe WHERE categoria = proveedor
AND id_proveedor = X)`. No saldo column, cache, or movement table MUST exist
for proveedores. Borradores and anuladas compras MUST be excluded from the
sum.

#### Scenario: Saldo reflects confirmed compras net of gastos
- GIVEN a proveedor with confirmed compras totaling `5000` and linked
  gastos totaling `3000`
- WHEN the saldo read runs
- THEN it returns `2000`

#### Scenario: Borradores and anuladas are excluded
- GIVEN a proveedor with a `1000` borrador, a `2000` confirmed compra, and
  a `500` anulada compra
- WHEN the saldo read runs
- THEN only the `2000` confirmed compra contributes to the total

#### Scenario: A proveedor with no activity has zero saldo
- GIVEN a proveedor with no compras and no gastos
- WHEN the saldo read runs
- THEN it returns `0`

### Requirement: Per-Compra Payment Status From Linked Gastos Only

Each confirmed compra's payment status MUST be derived from `gastos` rows
whose `id_comprobante_compra` references it — a compra with at least one
linked gasto totaling its `total` is `pagada`; with a partial linked total,
`parcial`; with none, `impaga`.

#### Scenario: A fully paid compra
- GIVEN a confirmed compra of `1000` with one linked gasto of `1000`
- WHEN its payment status is read
- THEN it is `pagada`

#### Scenario: An unlinked gasto does not mark a compra as paid
- GIVEN a confirmed compra of `1000` and a separate gasto to the same
  proveedor with no `id_comprobante_compra` set
- WHEN the compra's payment status is read
- THEN it is `impaga` — the unlinked gasto still reduces the proveedor's
  overall saldo, but does not mark this specific compra as paid

### Requirement: Saldo Is An Approximation, Not An Invariant

The saldo read MUST be documented as an approximation: an unlinked
proveedor gasto still reduces the total saldo (it is still money paid to
that supplier) even though it does not resolve any specific compra's
payment status. No reconciliation or imputación process MUST exist in this
stage.

#### Scenario: An unlinked gasto still reduces the total saldo
- GIVEN a proveedor with a `2000` confirmed compra and a `500` gasto to
  that proveedor with no `id_comprobante_compra`
- WHEN the saldo read runs
- THEN it returns `1500`, even though the compra individually still shows
  `impaga`

### Requirement: Authorization And Scoping

The saldo read MUST be gated by `Politicas.OperacionDePos` (Vendedor +
Admin) and scoped to the caller's tenant.

#### Scenario: Vendedor reads a proveedor's saldo
- GIVEN a user with role Vendedor
- WHEN they request the saldo of a proveedor in their tenant
- THEN the request succeeds

#### Scenario: Cross-tenant proveedor saldo is invisible
- GIVEN a tenant-1 user requests the saldo of a proveedor belonging to
  tenant 2
- THEN the request returns not-found (RLS/EF-filter isolation)
