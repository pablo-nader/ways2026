# Delta for Clientes

## MODIFIED Requirements

### Requirement: Cliente Schema At Rest

`clientes` MUST be catalog-scoped (`id_tenant`, `id_empresa NULL`, doc 09) with
the full doc 10 §2 shape: identity/contact fields, `tipo_documento`/`numero_documento`,
`id_condicion_fiscal NOT NULL`, `id_lista_precio NOT NULL`, and credit fields
`limite_credito NUMERIC(14,2) DEFAULT 0`, `credito_ilimitado BOOLEAN DEFAULT false`,
`saldo NUMERIC(14,2) DEFAULT 0`. `saldo` is a maintained cache of
`movimientos_cuenta_corriente` (see `consumo-cuenta-corriente` spec): it moves
inside the sale transaction when a cuenta corriente pago is used, and inside
the anulación transaction on reversal. It stays at its default outside those
write paths.
(Previously: "No cuenta-corriente movement engine exists yet (stage 7);
`saldo` stays at its default outside Consumidor Final seeding" — stage 5
activates the write paths.)

#### Scenario: Create a cliente with default credit fields
- GIVEN a tenant admin creates a cliente without specifying credit fields
- WHEN the row is persisted
- THEN `limite_credito = 0`, `credito_ilimitado = false`, `saldo = 0`

#### Scenario: id_lista_precio and id_condicion_fiscal are required
- GIVEN a tenant admin submits a cliente missing `id_lista_precio` or `id_condicion_fiscal`
- WHEN the request is validated
- THEN it is rejected before reaching the database

#### Scenario: Invalid FK reference maps to 400
- GIVEN a create request references a non-existent `id_lista_precio`
- WHEN the insert reaches Postgres
- THEN 23503 is raised and `ManejadorDeErrores` maps it to 400 `referencia_invalida`

#### Scenario: Saldo moves only through a cuenta corriente sale or its anulación
- GIVEN a cliente created with `saldo = 0`
- WHEN they never appear in a cuenta corriente pago
- THEN `saldo` stays `0` regardless of other sales made to them

### Requirement: Cliente ABM Lifecycle and Authorization

Clientes MUST support create/edit/soft-delete, gated by
`GestionDeCatalogo` (tenant `admin` only — `root` and `vendedor` excluded).
Listing/search MUST be reachable under `Politicas.OperacionDePos` (Vendedor +
Admin) as well, since the POS needs cliente lookup at checkout.
(Previously: list/create/edit/soft-delete were all gated by
`GestionDeCatalogo` only.)

#### Scenario: Admin creates and soft-deletes a cliente
- GIVEN a tenant admin
- WHEN they create a cliente and later soft-delete it
- THEN the row is persisted under the admin's `id_tenant`, and after deletion
  `deleted_at` is set and it no longer appears in the default list

#### Scenario: Vendedor blocked from writing
- GIVEN a user with the `vendedor` role
- WHEN they call the cliente create endpoint
- THEN the request is rejected with an authorization error

#### Scenario: Vendedor can search clientes for checkout
- GIVEN a user with the `vendedor` role
- WHEN they call the cliente list/search endpoint for their tenant
- THEN the request succeeds
