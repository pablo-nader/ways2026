# Proposal: Stage 2 — Clientes y Proveedores

## Intent

Implement Etapa 2 of `docs/10-modelo-de-datos.md`: `clientes`, `proveedores`, and the
minimal `listas_precio` they depend on. Doc 10's staging table marks stage 2 as the
unlock for comprobantes (stage 5) — no sale or purchase can be modeled until a customer
and a supplier exist as first-class rows instead of free-text fields. This is a vertical
slice (schema + API + React ABM), the same shape stage 1 used.

## Scope

### In Scope
- `clientes`: full doc 10 §2 schema, including complete credit fields (`limite_credito`,
  `credito_ilimitado`, `saldo` cache — defaults 0/false/0; cuenta-corriente movements are
  stage 7). Visible `numero` assigned via an atomic per-tenant sequence (same pattern
  family as doc 09's `numeraciones_comprobante`). Consumidor Final: automatic protected
  row per tenant (`numero = 1`, condición fiscal CF, not editable/deletable) — seeded for
  existing tenants and added to the provisioning template.
- `proveedores`: full doc 10 §2 schema.
- `listas_precio` MINIMAL: table + the General list per tenant (`es_default`) + provisioning
  template inclusion. No `precios`, no derived lists (stage 3). `clientes.id_lista_precio`
  is `NOT NULL` against it from day one.
- ABM screens for clientes and proveedores, reusing the stage-1 generic catalog machine
  where the shape fits; clientes likely needs a dedicated screen given its field count
  (design decides).
- Both tables are catalog-scoped (`id_tenant` + `id_empresa NULL` sharing, per doc 09),
  standard RLS, `db-error-backstops` compliance planned for every new constraint from the
  start (23505/23503 mappings + race tests in the same work unit as each constraint).

### Out of Scope
- Precios / ofertas (stage 3)
- `movimientos_cuenta_corriente` (stage 7)
- Legacy data migration
- ADR-10 (`DeLaEmpresa` empresa-scoped query extension) — stays deferred per stage 1,
  unless design finds it genuinely necessary for empresa-scoped clientes; if so, **flag
  as an open question**, do not decide it inside this proposal.

## Capabilities

### New Capabilities
- `clientes`: cliente CRUD/ABM, full credit fields at rest (no CC movement engine yet),
  atomic per-tenant `numero`, protected Consumidor Final row.
- `proveedores`: proveedor CRUD/ABM.
- `listas-precio-minimal`: `listas_precio` table, one General/`es_default` list per tenant,
  no pricing engine.

### Modified Capabilities
- `tenant-organization` (stage 1): `PlantillaDeAprovisionamiento` gains the Consumidor
  Final customer and the General price list — both were declared as stage-2/3 extension
  points in stage 1's ADR-16 and explicitly deferred, not implemented, there.

## Approach

1. **Reuse, not redesign.** Stage 2 sits on stage-1 machinery as-is: `EntidadTenant`, the
   EF tenant query filter, `RlsMigrationBuilderExtensions.HabilitarRlsDeTenant`, the
   catalog index-pair pattern (shared vs. per-empresa uniqueness), the generic catalog
   machine (ADR-11) as a first option, the keyed platform `IWaysDbContext` for
   cross-tenant pre-checks, and the existing `ManejadorDeErrores` code-mapping contract.
   No new tenancy plumbing is proposed.
2. **DB CHANGE GATE**: present the full model summary (tables, columns, constraints,
   tenancy scoping) before generating each migration; wait for explicit approval
   (`CLAUDE.md`, unconditional).
3. `clientes` and `proveedores` land as catalog-scoped tables with the standard
   shared/per-empresa index-pair technique already established for `areas`/`marcas`/etc.
4. `numero` uses a small atomic counter table (same family as `numeraciones_comprobante`,
   doc 09) — exact shape is a design decision, not decided here.
5. Consumidor Final and the General price list become part of `PlantillaDeAprovisionamiento`
   (versioned bump, per ADR-16's "add a version, don't edit one") plus one idempotent
   backfill step in `InicializadorDeBaseDeDatos` for tenants provisioned before this stage.
6. Every new unique/FK constraint gets its `db-error-backstops` mapping and race test in
   the same work unit that introduces it — not retrofitted later.

## Affected Areas

| Area | Impact | Description |
|---|---|---|
| `src/Ways.Domain/Clientes` (new) | New | `Cliente` entity, credit fields, Consumidor Final domain rule |
| `src/Ways.Domain/Proveedores` (new) | New | `Proveedor` entity |
| `src/Ways.Domain/Catalogos` | Modified | minimal `ListaPrecio` entity |
| `src/Ways.Infrastructure` | Modified | EF configs, migrations, RLS policies, numero counter, `PlantillaDeAprovisionamiento` version bump |
| `src/Ways.Application` | Modified | `ServicioDeClientes`, `ServicioDeProveedores`, provisioning template extension |
| `src/Ways.Api` | New | `ClientesEndpoints`, `ProveedoresEndpoints` |
| `src/Ways.Web` | New | Clientes + Proveedores ABM screens |

## Risks

| Risk | Likelihood | Mitigation |
|---|---|---|
| Reviewer overload (schema + API + UI in one slice) | High | Chained PRs, stacked-to-main (session decision); exact cut points at `sdd-tasks` |
| Atomic `numero` sequence race under concurrent client creation | Med | `db-error-backstops` race test in the same work unit |
| Consumidor Final protection bypassed via generic update/delete path | Med | Explicit domain guard, not just UI hiding; covered by design + tests |
| Backfilling Consumidor Final / General list on existing tenants | Low-Med | Idempotent seeding pattern (ADR-14), same technique as stage 1 |
| `listas_precio` minimal shape needs rework once stage 3 (`precios`) lands | Low | Table matches doc 10 §3 exactly; only `precios`/derived lists are deferred |

## Rollback Plan

Every migration is additive (new tables, new FK from `clientes.id_lista_precio`); no
existing table is altered destructively. ABM screens and endpoints are new routes,
removable without touching stage-1 flows. The provisioning template version bump does
not remove the prior version's fields.

## Dependencies

- Stage 1 (merged/in review): `EntidadTenant`, RLS helper functions, generic catalog
  machine, keyed platform context, `ManejadorDeErrores` mapping contract, provisioning
  transaction (`ServicioDeAprovisionamiento`).
- DB Change Gate approval (blocking, before each migration).

## Success Criteria

- [ ] `clientes` and `proveedores` exist, RLS-covered, catalog-scoped per doc 09
- [ ] `numero` is assigned atomically per tenant with no gaps/dupes under concurrency
- [ ] Consumidor Final exists for every tenant (new and pre-existing), `numero = 1`, not
      editable or deletable through any API path
- [ ] `listas_precio` has one `es_default` General list per tenant; `clientes.id_lista_precio`
      is enforced `NOT NULL`
- [ ] ABM for clientes and proveedores: list/create/edit/soft-delete, working in `Ways.Web`
- [ ] Every new constraint has its `db-error-backstops` mapping + race test
- [ ] New tenant provisioning creates Consumidor Final + General list automatically

## Resolved product decisions (question round)

Resolved with the user (2026-08-02). Binding for spec/design/tasks:

1. **`proveedores.cuit` is UNIQUE per tenant** — partial index (`WHERE deleted_at IS NULL`,
   NULLs allowed): a duplicate CUIT within a tenant is a data-entry bug and gets blocked;
   the same real-world supplier CAN exist across different tenants. Ships with its
   23505 → 409 backstop and race test per `db-error-backstops` from day one.
2. **`clientes.numero_documento` has NO hard uniqueness** — legacy data contains duplicates,
   NULL documents are legitimate (Consumidor Final, historical rows), and a constraint here
   would block the future data migration. A soft UI warning may come later; documented
   decision, not an oversight.
3. **Consumidor Final backfill runs inside the normal DB Change Gate** — the clientes
   migration's gate summary MUST explicitly include the backfill section (which rows get
   created for which existing tenants) and the user approves schema + backfill together,
   same pattern as stage 1's usuarios backfill. Automatic idempotent initializer thereafter.
4. **ADR-10 (`DeLaEmpresa`) stays deferred** — reconfirmed; design may re-raise it only as
   an open question if empresa-scoped clientes prove genuinely necessary.
