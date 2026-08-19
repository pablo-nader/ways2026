# Delta for Auxiliary Catalogs

## MODIFIED Requirements

### Requirement: Fiscal Catalogs Are Platform-Managed and Read-Only

`condiciones_fiscales`, `alicuotas_iva`, and `tipos_comprobante` MUST have
no `id_tenant` column (`[global]` scope per doc 10) and MUST expose
read-only endpoints to tenant users. No tenant-facing create/edit/delete ABM
exists for these in stage 1. As of this stage, `tipos_comprobante` ships
`PRE` with `activo = false` — deactivated by an idempotent data statement on
already-migrated databases and, independently, by an explicit
`Activo = false` on `TiposComprobanteBase` for the seeder, so a fresh
install never reopens the hole (the seeder runs only against an empty
database, after migrations) — and gains `TXR` (`clase venta`, `letra 'X'`,
`signo +1`, `discrimina_iva false`, `es_fiscal false`, `afecta_stock false`,
`activo true`), the itemless consolidation type for remitos (see `remitos`
capability). Both rows remain subject to the same read-only rule as every
other row in the padrón.
(Previously: silent on `PRE`'s deactivation and `TXR`'s addition — both
introduced by stage 17.)

#### Scenario: Tenant reads fiscal catalogs

- GIVEN a tenant user
- WHEN they list `alicuotas_iva`
- THEN they receive the platform-wide rows, identical across all tenants

#### Scenario: Tenant write attempt rejected

- GIVEN a tenant admin
- WHEN they call a create/edit/delete endpoint for `tipos_comprobante`
- THEN no such endpoint exists / the request is rejected (404 or 403)

#### Scenario: A freshly seeded database has PRE inactive

- GIVEN a brand-new tenant database seeded after this stage's migrations
- WHEN `tipos_comprobante` is read for `codigo = 'PRE'`
- THEN `activo = false`

#### Scenario: TXR is present, read-only, and non-fiscal

- GIVEN any tenant
- WHEN they list `tipos_comprobante`
- THEN a `TXR` row is present with `afecta_stock = false, es_fiscal = false`,
  and no tenant-facing write endpoint reaches it
