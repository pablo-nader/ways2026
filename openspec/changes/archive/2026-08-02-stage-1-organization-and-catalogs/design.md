# Design: Stage 1 — Organization and Catalogs

How to implement the tenancy model of `docs/09-multi-tenancy.md` and the stage-1 tables of
`docs/10-modelo-de-datos.md` inside this codebase. The architecture (shared database,
two isolation layers, scoping categories) is already decided by those docs — this document
decides **how it lands in Ways.Domain / Ways.Application / Ways.Infrastructure / Ways.Api /
Ways.Web**, and records the decisions that the docs left open.

Artifacts and identifiers are in English; database objects stay in the Spanish naming
convention of doc 10.

## Reading path

| If you are… | Read |
|---|---|
| Reviewing tenancy safety | *Tenant context lifecycle* + ADR-2 to ADR-8 + *Test strategy* |
| Writing the schema | *Data model shape* + *Migration sequencing* (the DB CHANGE GATE lives there) |
| Writing catalogs (API/UI) | *Catalog machinery* + ADR-11 |
| Estimating tasks | *Component map* + *Migration sequencing* |

---

## Architecture at a glance

The existing layering is preserved. Nothing new is introduced at the top level: tenancy is
a **cross-cutting concern implemented in Infrastructure**, exposed to Application as one
abstraction (`ITenantActual`), and invisible to the use cases.

```
Ways.Domain
  Common/       EntidadBase, EntidadTenant (new), CatalogoSimple (new)
  Usuarios/     PoliticaDeRoles (+ tenant rules), Usuario (+ IdTenant?)
  Organizacion/ Tenant, Empresa, PuntoVenta, EstadoTenant           (new)
  Catalogos/    Area, Categoria, Marca, Grupo, MedioPago,
                CondicionFiscal, AlicuotaIva, TipoComprobante,
                Parametro, ReglaDeCategorias, ResolucionDeParametros (new)

Ways.Application
  Abstracciones/ ITenantActual (new), IWaysDbContext (+ new DbSets)
  Catalogos/     ServicioDeCatalogo<T> + 5 thin subclasses, contracts  (new)
  Organizacion/  ServicioDeOrganizacion, ServicioDeAprovisionamiento   (new)
  Parametros/    ServicioDeParametros                                  (new)

Ways.Infrastructure
  Persistencia/  WaysDbContext (+ tenant filter, + SaveChanges stamping)
                 Configuraciones/ConfiguracionDeCatalogo<T> (new) + per-entity configs
                 Migraciones/ (5 new migrations)
  Multitenancy/  TenantActualDeSesion, InterceptorDeContextoDeTenant,
                 RlsMigrationBuilderExtensions                          (new)

Ways.Api
  Seguridad/     Politicas (+ plataforma, + gestión de catálogo),
                 ValidacionDeSesion (extracted from Program.cs)         (new)
  Endpoints/     OrganizacionEndpoints, CatalogosEndpoints,
                 AprovisionamientoEndpoints, ParametrosEndpoints        (new)

Ways.Web
  api/           catalogos.ts (descriptors), tipos.ts (+ types)
  paginas/       PaginaCatalogo (generic), Categorias (tree),
                 Tenants, Empresas, PuntosVenta                         (new)
```

**Boundary rule:** no use case ever reads or writes `IdTenant`. It is stamped on insert and
filtered on read by Infrastructure. A use case that needs to cross tenants must ask for it
explicitly (`IgnoreQueryFilters(["Tenant"])`) and can only do so under a platform session.

---

(See full design.md in the change archive for complete ADRs and migration sequencing details.)
