# Operación de POS Specification

## Purpose

Defines the new POS authorization tier (`Politicas.OperacionDePos`), the
explicit-`idPuntoVenta`-per-request posture (no server-side POS session), and
the cart-pricing integration contract that feeds checkout: cliente's
`IdListaPrecio` drives `ServicioDeOfertas.ResolverAsync` (batch), and applied
ofertas land on `items_comprobante_venta.id_oferta` + `descuento` — never a
phantom `OF...` line.

## Requirements

### Requirement: OperacionDePos Policy Admits Vendedor and Admin

`Politicas.OperacionDePos` MUST admit both `RolConocido.Vendedor` and
`RolConocido.Admin`. It gates the POS read surface (artículos, códigos de
barra, clientes, listas de precio, parámetros read-only, catálogos
fiscales/medios de pago, `POST /api/ofertas/resolver`) and the checkout/
anulación/stock-read endpoints. ABM write endpoints stay on
`GestionDeCatalogo` and are unaffected.

#### Scenario: Vendedor is admitted by the policy
- GIVEN a user with `RolConocido.Vendedor`
- WHEN a request checks `Politicas.OperacionDePos`
- THEN authorization succeeds

#### Scenario: Admin is admitted by the policy
- GIVEN a user with `RolConocido.Admin`
- WHEN a request checks `Politicas.OperacionDePos`
- THEN authorization succeeds

> Amended at judgment-day R1 of Slice 1 (2026-08-04, orchestrator decision under
> legacy-parity auto mode): the legacy admits admin/supervisor/vendedor to the
> selling flow (`tipoUser IN (2,3,4)`), so `Supervisor` joins `OperacionDePos`.
> Catalog WRITES remain Admin-only (`GestionDeCatalogo` unchanged).

#### Scenario: A role outside Vendedor/Supervisor/Admin is rejected
- GIVEN a user with `RolConocido.Root`
- WHEN a request checks `Politicas.OperacionDePos`
- THEN authorization fails (root administers tenants, does not operate them)

### Requirement: Explicit idPuntoVenta, No Server-Side POS Session

Every PUNTO-DE-VENTA-SCOPED POS request (read or write) MUST carry an explicit `idPuntoVenta`
parameter. The system MUST NOT persist or resolve a "current punto de venta"
from session state.

> Scope tightened at judgment-day R1 of Slice 2: identity-only lookups with no
> per-PV data (e.g. `GET /api/articulos/escaneo`) are tenant-scoped and carry no
> `idPuntoVenta`, per design decision 7.

#### Scenario: Same user operates two puntos de venta in sequence
- GIVEN a Vendedor with access to punto de venta A and B of the same tenant
- WHEN they submit a checkout with `idPuntoVenta = A` and then another with
  `idPuntoVenta = B`
- THEN both succeed, scoped to their respective punto de venta, with no
  cross-request state carried between them

#### Scenario: idPuntoVenta outside the caller's tenant is rejected
- GIVEN a Vendedor of tenant 1
- WHEN they submit `idPuntoVenta` belonging to tenant 2
- THEN the request is rejected (tenant isolation, not found)

### Requirement: Cart Pricing Has Exactly One Path

Cart line pricing MUST resolve through `cliente.IdListaPrecio` →
`ServicioDeOfertas.ResolverAsync` (batch) — the same engine stages 3–4
built. No per-client dual-price legacy hack MUST exist. Applied ofertas MUST
land as `items_comprobante_venta.id_oferta` + `descuento` on the line they
affected; no phantom `OF...` discount line MUST be created.

#### Scenario: Cart resolves through the cliente's lista
- GIVEN a cliente with `IdListaPrecio = 3` and a cart of 4 lines
- WHEN the cart is priced
- THEN all 4 lines resolve via `ServicioDeOfertas.ResolverAsync` against
  lista 3, in one batch call

#### Scenario: An applied oferta lands on its own line, not a phantom line
- GIVEN an articulo line with a matching acumulable oferta
- WHEN the item is persisted
- THEN `items_comprobante_venta.id_oferta` and `descuento` are set on that
  same line, and no separate `OF...`-coded line is created

### Requirement: Checkout Orchestration Contract

The checkout endpoint MUST accept `idPuntoVenta`, an optional `idCliente`
(defaults to Consumidor Final when omitted), a list of cart lines
(`idArticulo`, `cantidad`), and a list of pagos (`idMedioPago`, `importe`,
optional `referencia`). Before any pricing or oferta resolution runs, it
MUST resolve the open turno for `idPuntoVenta` and reject with `409
turno_no_abierto` if none exists. It MUST return the emitted comprobante's
`id`, `numero` (formatted `PPPP-NNNNNNNN`), `estado`, totals, and items on
success, or a validation error identifying the specific rejected rule on
failure.

#### Scenario: Successful checkout returns the formatted numero
- GIVEN a valid cart and full efectivo payment at punto de venta `7`
- WHEN checkout completes
- THEN the response includes `numero` formatted as `"0007-00000001"` (or the
  next correlativo) and `estado = "emitido"`

#### Scenario: Omitted idCliente defaults to Consumidor Final
- GIVEN a checkout request with no `idCliente`
- WHEN it is validated
- THEN the sale is attributed to the tenant's Consumidor Final

#### Scenario: Rejected checkout identifies the failing rule
- GIVEN a checkout whose payment fails the tolerancia check
- WHEN it is rejected
- THEN the error response identifies the tolerancia rule, not a generic
  failure

#### Scenario: Selling with no open turno fails before any pricing work
- GIVEN punto de venta 7 has no open turno
- WHEN a checkout request is submitted with a 3-line cart
- THEN it is rejected with `409 turno_no_abierto` before any oferta
  resolution or price lookup runs

### Requirement: Caja Surface Lives Under OperacionDePos

The apertura, cierre, movimientos de caja (retiro / refuerzo / apertura de
cajón), gastos, and resumen parcial endpoints MUST be gated by
`Politicas.OperacionDePos` — the same policy that gates checkout and
anulación, not a separate tier.

#### Scenario: Vendedor accesses the caja surface
- GIVEN a user with role Vendedor
- WHEN they call apertura, movimiento, gasto, resumen parcial, or cierre
  endpoints for their own punto de venta
- THEN authorization succeeds (subject to the flagged decision 2 role
  tightening for cierre, offered at the DB Change Gate)

#### Scenario: A role outside OperacionDePos is rejected from the caja surface
- GIVEN a user with `RolConocido.Root`
- WHEN they call any caja endpoint
- THEN authorization fails

### Requirement: SupervisionDeCuentaCorriente Policy Gates Reliquidación And Ajuste Manual

A new `Politicas.SupervisionDeCuentaCorriente` policy (Supervisor + Admin)
MUST gate reliquidación a precio del día and ajuste manual — Vendedor MUST
be rejected. This is the stage's one deliberate departure from legacy parity
(the legacy has no role gate on cuenta corriente at all).

#### Scenario: Supervisor can run reliquidación and post an ajuste
- GIVEN a user with role Supervisor
- WHEN they request reliquidación or post an ajuste for their tenant
- THEN both requests succeed (authorization-wise)

#### Scenario: Admin can run reliquidación and post an ajuste
- GIVEN a user with role Admin
- WHEN they request reliquidación or post an ajuste
- THEN both requests succeed (authorization-wise)

#### Scenario: Vendedor is rejected from reliquidación and ajuste manual
- GIVEN a user with role Vendedor
- WHEN they attempt reliquidación or an ajuste post
- THEN both are rejected with `403`

### Requirement: Pago A Cuenta And Estado De Cuenta Reads Live Under OperacionDePos

RC emission, RC anulación, and estado de cuenta reads (header + movement
list) MUST be gated by the existing `Politicas.OperacionDePos` (Vendedor +
Supervisor + Admin) — legacy parity, the cashier takes payments and looks up
accounts all day.

#### Scenario: Vendedor posts a pago a cuenta and reads estado de cuenta
- GIVEN a user with role Vendedor
- WHEN they post an RC payment and read estado de cuenta for their tenant
- THEN both requests succeed (authorization-wise)

#### Scenario: A role outside OperacionDePos is rejected from both
- GIVEN a user with `RolConocido.Root`
- WHEN they call RC emission or estado de cuenta
- THEN authorization fails

### Requirement: Compra, Transferencia And Conteo Write Paths Stack GestionDeCatalogo Over OperacionDePos

Compra borrador/confirmar/anular, transferencias, conteos de inventario,
and the price-application action MUST stack `Politicas.GestionDeCatalogo`
over `Politicas.OperacionDePos` — the same composition the existing manual
stock `ajuste` path uses (Admin-only). Compra list, compra detail, and the
proveedor saldo read MUST stay on `Politicas.OperacionDePos` alone. Paying
a compra is an ordinary gasto and keeps the gastos endpoint's existing
`Politicas.OperacionDePos` gate, unaffected by this stage.

#### Scenario: Admin performs every stage-8 write path
- GIVEN a user with role Admin
- WHEN they create/confirm/anular a compra, submit a transferencia, and
  submit a conteo
- THEN every request succeeds (authorization-wise)

#### Scenario: Vendedor is blocked from every stage-8 write path
- GIVEN a user with role Vendedor
- WHEN they attempt any compra write, a transferencia, or a conteo
- THEN every request is rejected with `403`

#### Scenario: Vendedor still reads the compra list and proveedor saldo
- GIVEN a user with role Vendedor
- WHEN they list compras and read a proveedor's saldo for their tenant
- THEN both requests succeed

#### Scenario: Paying a compra keeps the existing gastos gate
- GIVEN a user with role Vendedor and an open turno
- WHEN they submit a gasto linked to a compra
- THEN the request succeeds under the unchanged `OperacionDePos` gate — no
  new tier was introduced for payment

### Requirement: SupervisionDeCuentaDeProveedor Policy Gates The Proveedor Ajuste Manual

A new `Politicas.SupervisionDeCuentaDeProveedor` policy (Supervisor + Admin)
MUST gate the manual ajuste on the proveedor ledger — Vendedor MUST be
rejected. It is distinct from `SupervisionDeCuentaCorriente`, reserved for
the client-side ledger and a future cierre-de-caja tightening.

#### Scenario: Supervisor and Admin post a manual proveedor ajuste
- GIVEN a user with role Supervisor, and separately a user with role Admin
- WHEN each posts a manual ajuste with a valid detalle
- THEN both requests succeed (authorization-wise)

#### Scenario: Vendedor is rejected from the proveedor ajuste manual
- GIVEN a user with role Vendedor
- WHEN they attempt to post a proveedor ajuste
- THEN it is rejected with `403`

### Requirement: Proveedor Estado De Cuenta And Saldo Reads Stay Under OperacionDePos

Proveedor estado de cuenta and the existing `/saldo` read MUST stay gated
by `Politicas.OperacionDePos` (Vendedor + Supervisor + Admin) — the cashier
looks up what the tenant owes all day; only the ajuste write needs the
tighter policy.

#### Scenario: Vendedor reads estado de cuenta and saldo
- GIVEN a user with role Vendedor
- WHEN they read a proveedor's estado de cuenta and its saldo
- THEN both requests succeed (authorization-wise)
