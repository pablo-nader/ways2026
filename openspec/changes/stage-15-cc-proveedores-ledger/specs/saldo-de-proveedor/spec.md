# Delta for Saldo de Proveedor

## MODIFIED Requirements

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

## REMOVED Requirements

### Requirement: Saldo Is A Derived Read, Never Persisted

(Reason: superseded — `proveedores.saldo` is now a maintained cache of
`movimientos_cuenta_corriente_proveedor`, kept an invariant by a single
write authority instead of recomputed on every read.)
(Migration: replaced by the `cuenta-corriente-de-proveedores` capability's
"Saldo Is The Single-Write-Authority Cache Of The Ledger" requirement.)

### Requirement: Saldo Is An Approximation, Not An Invariant

(Reason: the saldo becomes an invariant. The behavior this requirement
documented — an unlinked proveedor gasto reducing the total saldo without
settling any compra — is preserved, but now as a stated, tested property of
the ledger rather than an unmanaged approximation.)
(Migration: replaced by the `cuenta-corriente-de-proveedores` capability's
ledger-invariant and imputación requirements.)
