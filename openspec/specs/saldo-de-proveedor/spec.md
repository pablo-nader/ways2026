# Saldo de Proveedor Specification

## Purpose

Defines the derived proveedor balance read (doc-10:408-409):
`Σ compras confirmadas − Σ gastos` linked to that proveedor, with per-compra
payment status from the linked gastos only. No table, no cache, no ledger —
explicitly an approximation, not an invariant, deliberately not following
stage 7's richer-ledger precedent because doc 10 forbids the extra table.

## Requirements

### Requirement: Per-Compra Payment Status From Linked Gastos Only

Each confirmed compra's payment status MUST be derived as
`pagado(X) = SUM(gastos.importe) WHERE gastos.id_comprobante_compra = X`
(the retired mechanism, still true for ALL time because a payment IS a
gasto) plus `SUM(-importe)` of `movimientos_cuenta_corriente_proveedor`
rows of tipo `ajuste` imputed to X. Movements of tipo `pago` are NEVER
counted — each mirrors a linked gasto already summed (counting both would
double-count). `pagado = 0` ⇒ `impaga`; `>= total` ⇒ `pagada`; otherwise
`parcial`. This formula is binding per the OD7 arbitration in
`state.yaml`: a ledger-only sum misreads pre-cutover compras (no `compra`
movement of their own — their debt lives inside the `apertura` asiento),
either as `pagada` (proposal shape) or by losing pre-cutover partial
payments (design shape).

#### Scenario: A fully paid compra
- GIVEN a confirmed compra of `1000` with one proveedor gasto of `1000`
  linked to it (whose `pago` movement is imputed to it)
- WHEN its payment status is read
- THEN it is `pagada`

#### Scenario: A pre-cutover compra keeps its true status
- GIVEN a compra confirmed BEFORE the ledger cutover (no `compra` movement
  of its own; its debt lives inside the `apertura` asiento) with one
  pre-cutover linked gasto of `400` against a total of `1000`
- WHEN its payment status is read
- THEN it is `parcial` — never `pagada` (the ledger-only sum would say so)
  and never `impaga` (a movement-imputed-only sum would lose the gasto)

#### Scenario: An unlinked gasto does not mark a compra as paid
- GIVEN a confirmed compra of `1000` and a separate `pago` movement to the
  same proveedor with `id_comprobante_compra IS NULL`
- WHEN the compra's payment status is read
- THEN it is `impaga` — the unimputed pago still reduces the proveedor's
  overall saldo, but does not mark this specific compra as paid

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
