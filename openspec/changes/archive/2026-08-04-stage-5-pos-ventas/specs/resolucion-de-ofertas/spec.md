# Delta for Resolución de Ofertas

## ADDED Requirements

### Requirement: OperacionDePos Authorization For POST /api/ofertas/resolver

`POST /api/ofertas/resolver` MUST be gated by `Politicas.OperacionDePos`
(Vendedor + Admin), replacing the previous `GestionDeCatalogo` (Admin-only)
gate. Resolution semantics are unchanged — this is an authorization-only
change, closing the stage-4 verify carryover.

#### Scenario: Vendedor can resolve ofertas for the POS cart
- GIVEN a user with role Vendedor
- WHEN they call `POST /api/ofertas/resolver` with a batch of cart lines
- THEN the request succeeds and returns resolved prices/ofertas as before

#### Scenario: Unauthenticated request is still rejected
- GIVEN no bearer token
- WHEN `POST /api/ofertas/resolver` is called
- THEN the request is rejected with 401
