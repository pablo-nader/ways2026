# Delta for Tenant Organization

## MODIFIED Requirements

### Requirement: Tenant Provisioning With Template Seed

When the platform provisions a new tenant, the system MUST create, in a
single transaction, the tenant, its first empresa, the generic template seed
(area "General" and medios_pago Efectivo and Transferencia), the Consumidor
Final cliente (`numero = 1`, condición fiscal CF), and the General
`listas_precio` row (`es_default = true`). `PlantillaDeAprovisionamiento`
gains a new version for this addition (ADR-16: add a version, do not edit
one); the prior version's fields are not removed.

(Previously: only tenant, empresa, area "General", and the two medios de pago
were seeded; Consumidor Final and the General price list were declared as
stage-2/3 extension points and deliberately not created, since `clientes` and
`listas_precio` did not exist yet.)

#### Scenario: Successful provisioning

- GIVEN a platform user submits a new tenant name and empresa razón social
- WHEN provisioning completes
- THEN the tenant, empresa, area "General", medios_pago Efectivo/Transferencia,
  Consumidor Final cliente, and General listas_precio row all exist
- AND all seeded rows carry the new tenant's `id_tenant`

#### Scenario: Provisioning failure rolls back

- GIVEN the template seed step fails partway (including the Consumidor Final
  or General list step)
- WHEN the transaction is rolled back
- THEN no tenant, empresa, or partial catalog/cliente/lista rows remain

## ADDED Requirements

### Requirement: Backfill for Pre-Existing Tenants

Tenants provisioned before this stage MUST receive their Consumidor Final
cliente and General `listas_precio` row via an idempotent backfill step in
`InicializadorDeBaseDeDatos` (same pattern as stage 1's `usuarios` backfill,
ADR-14), run after migrations. The migration's DB Change Gate summary MUST
explicitly include which rows get created for which existing tenants, and the
user approves schema and backfill together in the same gate — not as two
separate approvals.

#### Scenario: Existing tenant gains Consumidor Final and General list
- GIVEN a tenant provisioned under stage 1, with no `clientes` or
  `listas_precio` rows
- WHEN the backfill runs
- THEN the tenant has exactly one Consumidor Final cliente (`numero = 1`)
  and exactly one `es_default` listas_precio row

#### Scenario: Backfill is idempotent
- GIVEN a tenant that already has its Consumidor Final and General list
  (either from provisioning or a prior backfill run)
- WHEN the backfill runs again
- THEN no duplicate rows are created

#### Scenario: Backfill is approved inside the DB Change Gate
- GIVEN the clientes/listas_precio migration is ready to apply
- WHEN the gate summary is presented to the user
- THEN it names the affected pre-existing tenants and the rows to be created,
  and proceeds only after explicit approval covering both schema and backfill
