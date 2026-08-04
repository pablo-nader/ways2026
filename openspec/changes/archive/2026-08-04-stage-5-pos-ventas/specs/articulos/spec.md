# Delta for Articulos

## MODIFIED Requirements

### Requirement: Articulo ABM Lifecycle and Authorization

Artículos MUST support create/edit/soft-delete, gated by `GestionDeCatalogo`
(tenant `admin` only — `root` and `vendedor` excluded). Listing/lookup
(including by `codigo_interno` and barcode, for cart pricing) MUST also be
reachable under `Politicas.OperacionDePos` (Vendedor + Admin).
(Previously: list/create/edit/soft-delete were all gated by
`GestionDeCatalogo` only.)

#### Scenario: Admin creates and soft-deletes an articulo

- GIVEN a tenant admin
- WHEN they create an artículo and later soft-delete it
- THEN it is persisted under the admin's `id_tenant`, and after deletion
  `deleted_at` is set and it no longer appears in the default list

#### Scenario: Vendedor blocked from writing

- GIVEN a user with the `vendedor` role
- WHEN they call the artículo create endpoint
- THEN the request is rejected with an authorization error

#### Scenario: Vendedor can list/lookup articulos for the POS cart

- GIVEN a user with the `vendedor` role
- WHEN they call the artículo lookup endpoint for their tenant
- THEN the request succeeds
