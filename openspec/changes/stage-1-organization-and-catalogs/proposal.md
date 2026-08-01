# Proposal: Stage 1 — Organization and Catalogs

## Intent

Implement Etapa 1 of `docs/10-modelo-de-datos.md`: the tenant/empresa/punto_venta
hierarchy (`docs/09-multi-tenancy.md`), a tenant-aware `usuarios`, EF Core + Postgres
RLS isolation, and the auxiliary catalog tables every later stage depends on. Doc 09 is
explicit that tenancy scoping cannot be retrofitted later — every table created from now
on must be born scoped, or stages 2-8 (clients, articles, sales, cash, purchases) inherit
a single-tenant assumption that becomes expensive to unwind.

## Scope

### In Scope
- Organization tables: `tenants`, `empresas`, `puntos_venta` + seed of tenant 1 / empresa 1 / the 2 current locales
- `usuarios` retrofit: `id_tenant NULL` column, `root` → platform role, `admin` → tenant admin, tenant rule in `PoliticaDeRoles`
- Multi-tenant plumbing: `IdTenant` on `EntidadBase`/derived `EntidadTenant`, EF Core global query filter by convention, Postgres RLS policies, composite FKs
- Auxiliary catalogs (doc 10 §1): `areas`, `categorias` (hierarchical), `marcas`, `grupos`, `condiciones_fiscales`, `alicuotas_iva`, `tipos_comprobante`, `medios_pago`, plus `parametros` (doc 10 §9)
- Tenant provisioning: platform role only; new tenant seeded with a template (starter areas + payment methods, price-list placeholder, Consumidor Final placeholder note)
- ABM screens in `Ways.Web` for organization and catalog entities, following the existing usuarios/login pattern

### Out of Scope
- Legacy data migration (separate future change; stages are born empty)
- `clientes` / `proveedores` (stage 2)
- `articulos`, listas de precio, `precios`, `ofertas` (stages 3-4)
- AFIP/facturación electrónica beyond storing `codigo_afip` columns
- Fine-grained permissions (role-based only, per doc 08)
- Cross-subset catalog sharing (doc 09 documented and accepted limitation)

## Capabilities

### New Capabilities
- `tenant-organization`: tenants/empresas/puntos_venta CRUD, tenant provisioning with template seed, scoping enforcement
- `usuarios-tenant-scoping`: `id_tenant` retrofit, platform vs tenant role split, tenant rule in `PoliticaDeRoles`
- `auxiliary-catalogs`: areas, categorias, marcas, grupos, condiciones_fiscales, alicuotas_iva, tipos_comprobante, medios_pago ABM
- `parametros-operativos`: key/value `jsonb` parameters scoped to punto_venta with empresa fallback

### Modified Capabilities
- `usuarios-y-login` (doc 08, already implemented): `id_tenant` column, `PoliticaDeRoles` tenant rule, root/admin role meaning change

## Approach

1. **DB CHANGE GATE**: before generating any EF Core migration, present the full model
   summary (tables, columns, constraints, tenancy scoping) to the user and wait for
   explicit approval. Mandatory, no exceptions, applies to every implementer/sub-agent
   (project rule, `CLAUDE.md`).
2. Introduce `EntidadTenant` (derives `EntidadBase`) carrying `IdTenant`; apply an EF Core
   global query filter by convention, mirroring the existing `deleted_at` filter pattern.
3. Migrate `usuarios` additively: `id_tenant NULL`, keep the existing `root` seed as
   platform-scoped (`id_tenant NULL`), scope `admin` per tenant.
4. Sequence: `tenants`/`empresas`/`puntos_venta` first (no dependencies) → seed tenant 1 →
   retrofit `usuarios` FK → auxiliary catalogs (depend on scoping being live).
5. Enable Postgres RLS with `SET LOCAL app.tenant_id` per request; app DB role without `BYPASSRLS`.
6. Build ABM screens reusing the existing usuarios ABM pattern (list/create/edit/soft-delete).
7. Tenant provisioning endpoint: platform-role-only, transactional creation of tenant +
   empresa + seed catalog rows from a template.

## Affected Areas

| Area | Impact | Description |
|---|---|---|
| `src/Ways.Domain/Usuarios` | Modified | `id_tenant`, `PoliticaDeRoles` tenant rule |
| `src/Ways.Domain/Organizacion` (new) | New | tenants, empresas, puntos_venta entities |
| `src/Ways.Domain/Catalogos` (new) | New | areas, categorias, marcas, grupos, fiscal catalogs, medios_pago, parametros |
| `src/Ways.Infrastructure` | New/Modified | EF configs, migrations, RLS policies, `ITenantActual`, query filter convention |
| `src/Ways.Api` | New | organization + catalog + provisioning endpoints |
| `src/Ways.Web` | New | ABM screens for org + catalogs |

## Risks

| Risk | Likelihood | Mitigation |
|---|---|---|
| Tenant isolation bug leaks data cross-tenant | Med | Two independent layers: EF query filter + Postgres RLS (doc 09) |
| Migration touches `usuarios` (existing, tested) | Med | Additive nullable column only; no destructive change; existing tests re-run |
| Large PR / reviewer overload (schema + API + UI in one slice) | High | Chained PRs likely; flagged for delivery-strategy decision at `sdd-tasks` |
| RLS misconfiguration silently bypassed | Low | App DB role verified without `BYPASSRLS`; integration test connects as app role |

## Rollback Plan

Every migration is additive (new tables, nullable FK column) — revert via down-migration;
no destructive change to existing `usuarios` data. ABM screens and endpoints are new
routes, removable without touching existing login/usuarios flows. Tenant provisioning
endpoint is isolated and can be disabled independently.

## Dependencies

- DB Change Gate approval (blocking, before any migration is generated)
- Doc 08 usuarios/login implementation (existing, being retrofitted)

## Success Criteria

- [ ] `tenants`/`empresas`/`puntos_venta` exist, seeded with tenant 1 / empresa 1 / 2 locales
- [ ] `usuarios.id_tenant` retrofitted; `root` platform-scoped, `admin` tenant-scoped
- [ ] EF query filter + Postgres RLS both enforce tenant isolation (integration test attempts cross-tenant read)
- [ ] All auxiliary catalogs have working ABM (list/create/edit/soft-delete) in `Ways.Web`
- [ ] Platform role can provision a new tenant with template seed data
- [ ] Suspending a tenant blocks its users' logins and cuts active sessions on the next request
- [ ] Categoria depth > 3 is rejected server-side
- [ ] Unit tests cover `PoliticaDeRoles` tenant rule; integration tests cover catalog + org endpoints

## Resolved product decisions

Question round resolved with the user (2026-07-31). These are binding for spec/design/tasks:

1. **Empresa/punto_venta creation is platform-only.** Only the platform role creates
   tenants, empresas, and puntos_venta. The tenant admin edits their descriptive data
   (name, address, social links) but cannot create or delete them. This corrects the
   earlier assumption: the user explicitly rejected self-service creation.
2. **Single generic provisioning template.** Starter area "General", medios_pago
   Efectivo + Transferencia, one general price-list placeholder (inactive until stage 3).
   Vertical templates (kiosco/supermercado) are a future enhancement, not stage 1.
3. **Fiscal catalogs are platform-only.** `condiciones_fiscales`, `alicuotas_iva`, and
   `tipos_comprobante` are `[global]` per doc 10: platform-maintained (AFIP-driven),
   read-only for tenants. No tenant-facing ABM for them in this stage.
4. **Categoria depth: schema unlimited, UI capped at 3 levels.** `id_categoria_padre`
   stays unrestricted in the schema; the stage 1 ABM enforces a maximum depth of 3.
   The depth cap is a domain rule (validated server-side), not just a UI constraint.
5. **Tenant suspension is enforced now.** A suspended tenant blocks login for its users,
   and active sessions are cut on the next request (reusing the existing per-request
   account revalidation from doc 08). Added to Success Criteria.
