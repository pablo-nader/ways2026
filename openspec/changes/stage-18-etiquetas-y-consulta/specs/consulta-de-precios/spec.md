# Consulta de Precios Specification

## Purpose

Defines the salón price-lookup screen: scan → identity → resolved price,
composed entirely from the two existing endpoints
(`GET /api/articulos/escaneo`, `POST /api/ofertas/resolver`), zero new
endpoints, zero writes, zero persisted state, the offer-applies /
no-offer / unknown-code / no-price display rules, the idle auto-reset, and
the existing-session authorization gate (device-token access explicitly
deferred, OD2).

## Requirements

### Requirement: Zero New Endpoints, Two Existing Calls Only

The screen MUST resolve a scan using exactly the two existing endpoints —
`GET /api/articulos/escaneo` for identity, then `POST /api/ofertas/resolver`
for price — and MUST NOT introduce any new endpoint.

#### Scenario: A scan resolves in exactly two calls
- GIVEN a barcode is scanned into the input
- WHEN the screen resolves it
- THEN exactly one call to `GET /api/articulos/escaneo` and one call to
  `POST /api/ofertas/resolver` occur, and no other network call is made

### Requirement: Zero Writes, Zero Persisted State

The screen MUST issue no non-GET/resolver call and MUST NOT persist any
lookup history, per-user state, or session claim beyond the punto de venta
and lista selectors already remembered locally, matching the POS pattern.

#### Scenario: No write call is ever issued
- GIVEN any sequence of scans on the screen
- WHEN the session is inspected
- THEN no non-GET call other than `POST /api/ofertas/resolver` (itself
  read-only) is ever issued

### Requirement: Offer-Applied And No-Offer Display

When the resolved `Aplicadas` for the scanned artículo at its identified
quantity is non-empty, the screen MUST show `PrecioOriginal` struck through
and `PrecioFinal` prominently. When empty, it MUST show one price with no
strike.

#### Scenario: Scan with an active offer shows both prices
- GIVEN a scanned artículo with `Aplicadas.Count > 0`
- WHEN the resolution completes
- THEN the screen shows `PrecioOriginal` struck through and `PrecioFinal`
  prominent

#### Scenario: Scan with no active offer shows one price
- GIVEN a scanned artículo with an empty `Aplicadas`
- WHEN the resolution completes
- THEN the screen shows one price with no strike-through

### Requirement: Unknown Code And No-Vigent-Price Paths Never Show $0

An unknown code MUST show "no encontrado". An artículo identified but with
no vigent price in the selected lista MUST show "consultá en caja", never
`$0` — the single-item restatement of the label's no-price exclusion rule
(decision 6).

#### Scenario: Unrecognized code shows not-found
- GIVEN a scanned code that matches no `codigo_interno` or `codigo_barra`
- WHEN `GET /api/articulos/escaneo` resolves
- THEN the screen shows "no encontrado" and issues no price resolution call

#### Scenario: Identified artículo with no vigent price shows the fallback message
- GIVEN a scanned artículo with no vigent price for the selected lista
- WHEN `POST /api/ofertas/resolver` resolves with `PrecioOriginal = null`
- THEN the screen shows "consultá en caja" and never displays `$0`

### Requirement: Idle Auto-Reset

The screen MUST return to its idle state after approximately 20 seconds of
inactivity following a resolved lookup, with the scan input regaining focus.

#### Scenario: Screen resets after the idle timeout
- GIVEN a resolved lookup is displayed
- WHEN approximately 20 seconds pass with no new scan
- THEN the screen returns to idle and the scan input has focus

#### Scenario: A new scan before timeout cancels the pending reset
- GIVEN a resolved lookup is displayed and 10 seconds have passed
- WHEN a new scan arrives
- THEN the new result replaces the previous one and the idle timer restarts

### Requirement: OperacionDePos Authorization, No Device-Token Surface (OD2)

The screen MUST be reachable only under the existing
`Politicas.OperacionDePos` (Vendedor + Supervisor + Admin), the same
authenticated-session model as the rest of the POS. The screen MUST NOT read
any role claim, display any user identity, or store any per-user state
beyond the session already in effect. A login-less device-token surface is
explicitly OUT of scope for this stage.

#### Scenario: Vendedor can use the lookup screen
- GIVEN a user with role Vendedor, authenticated in the existing session
- WHEN they navigate to the price-lookup screen and scan
- THEN the lookup succeeds (authorization-wise)

#### Scenario: Root is rejected
- GIVEN a user with `RolConocido.Root`
- WHEN they attempt to reach the price-lookup screen
- THEN access is rejected

#### Scenario: No anonymous or device-token path exists
- GIVEN no authenticated session
- WHEN a request is made to either endpoint the screen consumes
- THEN the request is rejected with 401, with no alternative unauthenticated
  or device-scoped path available

> **Reopen condition**: the owner states that a salón device must serve
> traffic with nobody logged in. That reopens a device-token authorization
> surface as its own change with its own threat model; this requirement's
> "existing session only" clause is what that change would supersede.

### Requirement: Responsive, Large-Format Presentation

The screen MUST be usable on the store's device form factor (responsive
layout) with typography sized for at-a-distance reading of the resolved
price, and MUST keep the scan input auto-focused for keyboard-wedge input.

#### Scenario: Resolved price is legible at a glance
- GIVEN a resolved lookup
- WHEN the result renders
- THEN the price is displayed in a visually prominent size distinct from
  supporting text, and the input remains focused for the next scan
