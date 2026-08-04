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
optional `referencia`). It MUST return the emitted comprobante's `id`,
`numero` (formatted `PPPP-NNNNNNNN`), `estado`, totals, and items on success,
or a validation error identifying the specific rejected rule on failure.

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
