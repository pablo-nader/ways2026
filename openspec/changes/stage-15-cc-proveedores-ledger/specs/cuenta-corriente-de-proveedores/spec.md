# Cuenta Corriente de Proveedores Specification

## Purpose

Defines the proveedor ledger `movimientos_cuenta_corriente_proveedor` (doc-10
§8-adjacent): an append-only, immutable movement history with four tipos
(`apertura | compra | pago | ajuste`), the single write authority that keeps
`proveedores.saldo` a maintained cache of the ledger, the opening backfill
that provably reproduces the formula it retires, the four write paths
(apertura, confirm, gasto, anulación) and the manual ajuste, imputación a
comprobantes, the pinned lock order, and the queryable estado de cuenta —
Etapa 7's design applied to money owed to suppliers.

## Requirements

### Requirement: Ledger Row Is Immutable And Its Shape Depends On Tipo

`movimientos_cuenta_corriente_proveedor` MUST be operativa-scoped, carry no
`EntidadBase` columns (no `updated_at`, no soft delete), and snapshot
`saldo_resultante` at insert time. Each `tipo` MUST validate its own shape
before insert: `apertura` MUST have `id_punto_venta = NULL` and
`id_empleado = NULL` and neither `id_comprobante_compra` nor `id_gasto`;
`compra` MUST carry `id_comprobante_compra` and no `id_gasto`; `pago` MUST
carry `id_gasto`; `ajuste` MUST carry either `id_comprobante_compra`
(contramovimiento) or a caller-supplied `detalle` (manual), never both
purposes conflated.

#### Scenario: An apertura row has no punto de venta or actor
- GIVEN the opening backfill writes an `apertura` movement for a proveedor
- WHEN the row is inspected
- THEN `id_punto_venta IS NULL` and `id_empleado IS NULL`

#### Scenario: A compra movement carries its originating comprobante
- GIVEN a `compra` movement written at confirm time
- WHEN the row is inspected
- THEN `id_comprobante_compra` is populated and `id_gasto IS NULL`

### Requirement: Apertura Backfill Reproduces The Retired Formula Exactly

The one-time migration MUST write one `apertura` movement per proveedor
whose derived saldo (`Σ compras confirmadas no eliminadas − Σ gastos
categoria=proveedor con id_proveedor no eliminados`) is non-zero, using the
exact predicate the retired `saldo-de-proveedor` read used, respecting soft
deletes on both `comprobantes_compra` and `gastos`, and excluding
proveedor-categoria gastos with `id_proveedor IS NULL`. A proveedor whose
derived saldo is `0` MUST get no row. Re-running the migration MUST write no
additional row and change no saldo.

#### Scenario: The backfill reproduces the exact pre-migration saldo
- GIVEN a proveedor with a `2000` confirmada compra, a `500` anulada compra,
  a `300` borrador, a `700` linked gasto, and a `200` unlinked proveedor
  gasto, none soft-deleted
- WHEN the migration runs
- THEN the opening movement's `importe` equals `2000 − 700 − 200 = 1100`,
  matching what `saldo-de-proveedor` returned before the migration

#### Scenario: Soft-deleted compras and gastos are excluded from the opening asiento
- GIVEN a proveedor whose only activity is a `1000` confirmada compra with
  `deleted_at` set
- WHEN the migration runs
- THEN no `apertura` row is written and `proveedores.saldo` stays `0`

#### Scenario: A gasto with no id_proveedor never enters the opening asiento
- GIVEN a `categoria = proveedor` gasto with `id_proveedor IS NULL`
- WHEN the migration computes the derived saldo
- THEN that gasto's importe is excluded from every proveedor's opening
  asiento

#### Scenario: A proveedor with zero derived saldo gets no opening row
- GIVEN a proveedor whose confirmed compras exactly equal its linked gastos
- WHEN the migration runs
- THEN no `apertura` row is written and `proveedores.saldo` stays `0`

#### Scenario: Re-running the migration is a no-op
- GIVEN the migration has already run once
- WHEN it is applied again
- THEN no additional `apertura` row is written and no `proveedores.saldo`
  changes

### Requirement: The Apertura CHECK Rejects A Human-Shaped Opening Row

`ck_movimientos_cuenta_corriente_proveedor_apertura` MUST reject, at the
database level, any row where `tipo = 'apertura'` carries a non-NULL
`id_punto_venta` or `id_empleado`, and any row where `tipo <> 'apertura'`
carries either as NULL.

#### Scenario: A raw insert violating the apertura shape is rejected
- GIVEN a raw INSERT attempts `tipo = 'apertura'` with `id_punto_venta` set
- WHEN it reaches Postgres
- THEN it is rejected with SQLSTATE `23514`

### Requirement: Confirming A Compra Writes Exactly One Debt Movement

`ServicioDeCompras.ConfirmarAsync` MUST write exactly one positive `compra`
movement in its existing transaction, carrying `id_comprobante_compra` and
the comprobante's `total` as `importe`, with `proveedores` locked as the
transaction's last row lock before the ledger INSERT.

#### Scenario: Confirming a compra increases the proveedor's saldo
- GIVEN a proveedor with `saldo = 0`
- WHEN a compra of `total = 5000` is confirmed
- THEN exactly one `compra` movement of `importe = 5000` is written and
  `proveedores.saldo = 5000`

### Requirement: Anulación Writes A Reversing Ajuste, Never Reverses A Pago

`ServicioDeCompras.AnularAsync` MUST write exactly one negative `ajuste`
movement, of magnitude equal to the annulled compra's `compra` movement and
`id_comprobante_compra` set to it, in the same transaction as the existing
stock contramovimientos. Linked `gastos` and their `pago` movements MUST NOT
be reversed — "sin motor de reversión de gastos" stays true. The resulting
saldo MAY be negative ("saldo a favor") and MUST NOT be clamped to zero.

#### Scenario: Anulando an unpaid compra reverses only the debt
- GIVEN a confirmada compra of `1000` with no payment
- WHEN it is anulada
- THEN a `-1000` `ajuste` movement is written and `proveedores.saldo`
  returns to its pre-confirm value

#### Scenario: Anulando a fully-paid compra leaves a saldo a favor
- GIVEN a compra of `1000` (`compra +1000`) fully paid (`pago -1000`)
- WHEN it is anulada
- THEN a `-1000` reversing `ajuste` is written, the linked gasto and its
  `pago` movement remain untouched, and `proveedores.saldo = -1000`,
  surfaced as saldo a favor

### Requirement: A Proveedor-Categoria Gasto With id_proveedor Writes One Imputed Pago Movement

`ServicioDeGastos.InsertarGastoAsync` MUST write exactly one negative `pago`
movement, carrying `id_gasto` and — when the gasto has
`id_comprobante_compra` — the same value as the movement's imputación,
whenever the gasto has `categoria = proveedor` AND `id_proveedor IS NOT
NULL`. A gasto failing either condition MUST write no movement.

#### Scenario: A linked proveedor gasto writes an imputed pago
- GIVEN a confirmada compra of `1000` and a gasto of `categoria = proveedor`,
  `id_proveedor` set, `id_comprobante_compra` set to that compra,
  `importe = 1000`
- WHEN the gasto is inserted
- THEN exactly one `pago` movement of `importe = -1000` is written with
  `id_comprobante_compra` equal to the compra's id

#### Scenario: An unlinked proveedor gasto reduces the saldo without imputación
- GIVEN a gasto of `categoria = proveedor`, `id_proveedor` set, no
  `id_comprobante_compra`
- WHEN the gasto is inserted
- THEN a `pago` movement is written with `id_comprobante_compra IS NULL`,
  reducing the total saldo without settling any compra

#### Scenario: A non-proveedor or unlinked-proveedor gasto writes no movement
- GIVEN a gasto of `categoria = servicios`, or a `categoria = proveedor`
  gasto with `id_proveedor IS NULL`
- WHEN either is inserted
- THEN no ledger movement is written

### Requirement: Manual Ajuste Requires A Detalle Under A Dedicated Policy

A manual `ajuste` MUST be rejected if `detalle` is empty or missing, MUST
carry `id_comprobante_compra IS NULL` (distinguishing it from the anulación
contramovimiento), and MUST be gated by
`Politicas.SupervisionDeCuentaDeProveedor` (Supervisor + Admin) — Vendedor
MUST be rejected.

#### Scenario: A manual ajuste with no detalle is rejected
- GIVEN an ajuste request with an empty `detalle`
- WHEN it is submitted
- THEN it is rejected before any write

#### Scenario: Vendedor is blocked from posting a manual ajuste
- GIVEN a user with role Vendedor
- WHEN they attempt to post a manual ajuste
- THEN the request is rejected with `403`

#### Scenario: Supervisor posts a manual ajuste
- GIVEN a user with role Supervisor and a valid detalle
- WHEN they post an ajuste of `importe = -200`
- THEN it succeeds and the proveedor's saldo decreases by `200`

### Requirement: Saldo Is The Single-Write-Authority Cache Of The Ledger

`proveedores.saldo` MUST equal the sum of that proveedor's
`movimientos_cuenta_corriente_proveedor.importe` at any point in time,
updated only through the one raw
`UPDATE proveedores SET saldo = saldo + $1 ... RETURNING` inside
`EscriturasDeCuentaCorrienteProveedor`, with `saldo_resultante` taken from
that `RETURNING` value — never a tracked `proveedor.Saldo +=`, never
recomputed apart from the write that produced it.

#### Scenario: Saldo matches the sum across a mixed sequence
- GIVEN a proveedor with an apertura (`+1100`), a compra (`+5000`), a pago
  (`-3000`), and a manual ajuste (`-100`)
- WHEN `proveedores.saldo` is compared against the sum of that proveedor's
  movimientos
- THEN both equal `3000`

#### Scenario: A failed ledger write leaves saldo and the business operation untouched
- GIVEN a compra confirmation whose ledger INSERT is forced to fail
- WHEN the transaction aborts
- THEN no movement is written, `proveedores.saldo` is unchanged, and the
  compra's `estado` remains `borrador`

### Requirement: Per-Compra Payment Status Is Derived From Imputed Movements

A confirmed compra's payment status MUST be computed as
`SUM(importe) WHERE id_comprobante_compra = X` over the compra's own `+total`
movement plus every `pago`/`ajuste` movement imputed to it: `= total` ⇒
`impaga`; `<= 0` ⇒ `pagada`; otherwise `parcial`.

#### Scenario: A fully imputed compra is pagada
- GIVEN a confirmada compra of `1000` and a `pago` movement of `-1000`
  imputed to it
- WHEN its payment status is read
- THEN it is `pagada`

#### Scenario: A partially imputed compra is parcial
- GIVEN a confirmada compra of `1000` and a `pago` movement of `-400`
  imputed to it
- WHEN its payment status is read
- THEN the remaining sum is `600` and status is `parcial`

#### Scenario: An unimputed payment reduces the total saldo without settling any compra
- GIVEN a confirmada compra of `1000` with no imputed pago, and a separate
  unlinked `-500` pago movement for the same proveedor
- WHEN the compra's payment status and the proveedor's saldo are both read
- THEN the compra's status is `impaga` and the proveedor's saldo is reduced
  by `500`

### Requirement: Standard RLS And Tenant Isolation

`movimientos_cuenta_corriente_proveedor` MUST enforce the two-layer
isolation guarantee (a cloned tenant query filter + Postgres RLS `FORCE`d,
no `BYPASSRLS`), with `id_tenant` written explicitly by the writer rather
than via `EstamparTenant()`.

#### Scenario: RLS blocks a cross-tenant read
- GIVEN the app DB role has no `BYPASSRLS`
- WHEN a raw SQL query reads tenant 2's movimientos while `app.tenant_id = 1`
- THEN RLS returns zero rows

#### Scenario: An INSERT with a foreign id_tenant is refused
- GIVEN a raw INSERT attempts `id_tenant` different from
  `app_tenant_actual()`
- WHEN it reaches Postgres
- THEN it is refused with `42501`

### Requirement: Pinned Lock Order Serializes Concurrent Writers On The Same Proveedor

Every transaction that writes a ledger movement MUST take `proveedores` as
its LAST row lock, immediately before the ledger INSERT, following the
total order `turnos_caja → comprobantes_compra → lotes →
stock/stock_lotes → proveedores → ledger INSERT`.

#### Scenario: Confirming a compra and paying another race without deadlock
- GIVEN a proveedor with two compras, one unconfirmed
- WHEN a confirm of one compra and a payment of a different, already
  confirmed compra of the same proveedor are submitted concurrently
- THEN both commit, serialized on the proveedor row, with no deadlock

#### Scenario: Two concurrent payments to the same proveedor serialize
- GIVEN a proveedor with `saldo = 1000`
- WHEN two payments of `200` each are submitted concurrently
- THEN both commit and `proveedores.saldo = 600`, never a lost update

#### Scenario: Anulación and a payment to the same proveedor race without deadlock
- GIVEN a proveedor with a confirmada compra being anulada and another
  compra being paid concurrently
- WHEN both transactions race
- THEN both commit, serialized on the proveedor row, with no deadlock

### Requirement: Estado De Cuenta Lists Movements With A Running Balance And Date Filter

`GET /api/proveedores/{id}/cuenta-corriente` MUST return every movement
ordered by `fecha` DESCENDING, each carrying its own `saldo_resultante`
snapshot read directly (never recomputed), default to the last month when
no filter is supplied, accept an explicit `desde`/`hasta` range using the
client's real offset, and support `historico = true`. A proveedor with no
movements MUST return `saldo = 0` and an empty list, never a 404.

#### Scenario: The list's saldo_resultante matches the ledger at every row
- GIVEN a proveedor with three movimientos of known `saldo_resultante`
- WHEN the movement list is requested
- THEN each returned row's balance equals its stored `saldo_resultante`

#### Scenario: A date-boundary filter uses the client's real offset, not UTC
- GIVEN today is `2026-08-17T12:00:00Z` (fixed seed, mediodía UTC) and a
  movement recorded at `2026-08-17T23:30:00-03:00` (client-local, which is
  `2026-08-18T02:30:00Z`)
- WHEN `hasta = 2026-08-17T23:59:59-03:00` is requested — the client's real
  `-03:00` offset, never `Z`
- THEN the movement is included, because the boundary is evaluated against
  the offset the client actually sent

#### Scenario: A proveedor with no activity has an empty, valid estado de cuenta
- GIVEN a proveedor with no ledger movements
- WHEN estado de cuenta is requested
- THEN it returns `saldo = 0` and an empty movement list with `200`
