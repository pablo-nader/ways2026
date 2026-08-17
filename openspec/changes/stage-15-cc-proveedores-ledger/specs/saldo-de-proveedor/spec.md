# Delta for Saldo de Proveedor

## MODIFIED Requirements

### Requirement: Per-Compra Payment Status From Linked Gastos Only

Each confirmed compra's payment status MUST be derived from
`movimientos_cuenta_corriente_proveedor` rows of tipo `pago` or reversing
`ajuste` whose `id_comprobante_compra` references it:
`SUM(importe) WHERE id_comprobante_compra = X` (the compra's own `+total`
movement plus every movement imputed to it) `= total` ⇒ `impaga`; `<= 0` ⇒
`pagada`; otherwise `parcial`.
(Previously: derived directly from `gastos.id_comprobante_compra` via a
`gastos` GROUP BY; now sourced from the ledger movements imputed to that
compra, written by `ServicioDeGastos` into
`movimientos_cuenta_corriente_proveedor` — the observable outcomes are
unchanged.)

#### Scenario: A fully paid compra
- GIVEN a confirmed compra of `1000` with one `pago` movement of `-1000`
  imputed to it
- WHEN its payment status is read
- THEN it is `pagada`

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
