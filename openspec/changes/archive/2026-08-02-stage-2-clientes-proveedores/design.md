# Design: Stage 2 — Clientes y Proveedores

## Technical Approach

Add 4 tables (`listas_precio`, `numeraciones_clientes`, `clientes`, `proveedores`) as
catalog-scoped (`id_tenant` + `id_empresa NULL`, doc 09), each with `HabilitarRlsDeTenant`.
`listas_precio` reuses the stage-1 generic catalog EF base (`CatalogoSimple` /
`ConfiguracionDeCatalogo<T>`) because its shape genuinely matches (name + flags); no
service/API this stage (no ABM in scope — stage 3 adds one against the same table, no
rework). `clientes` and `proveedores` do **not** reuse `ServicioDeCatalogo<T>` — see
Decision 1. `numero` is assigned by a small dedicated Application helper reused by
creation, provisioning bootstrap, and backfill (DRY, one atomic-assignment code path).

## Architecture Decisions

| # | Decision | Choice | Rejected alternative | Rationale |
|---|---|---|---|---|
| 1 | Generic catalog machine fit | `clientes`/`proveedores` get dedicated `Entidad`/EF config/`Servicio*`/screen. `listas_precio` reuses `CatalogoSimple`+`ConfiguracionDeCatalogo<T>` (EF layer only). | Force all 3 through `ServicioDeCatalogo<T>` (proposal's "proveedores likely fits closer" guess) | `ConfiguracionDeCatalogo<T>` hardcodes a nombre-based shared/private unique-index pair. `proveedores` dedupes by `cuit` tenant-wide (resolved decision #1), not by name/empresa-pair — forcing it through the base class would add a wrong uniqueness rule. `clientes` has no single "nombre" identity (nombre+apellido+razón social) and its numero is service-assigned, not user input. `listas_precio`'s shape (nombre+flags, standard dedupe) genuinely matches, so only that one reuses the base. |
| 2 | Numero atomicity | Counter table `numeraciones_clientes(id_tenant PK, proximo_numero)` + `UPDATE ... SET proximo_numero = proximo_numero + 1 RETURNING proximo_numero - 1` inside the write transaction, keyed by `id_tenant` (not `id_punto_venta` — clientes is catalog-scoped, shared across a tenant's empresas). | Per-tenant Postgres `SEQUENCE` objects | Same family as `numeraciones_comprobante` (doc 09), proven pattern, no dynamic per-tenant DDL, row-level lock gives atomicity for free, gaps only on rollback (already accepted precedent). |
| 3 | Numero read path | Raw ADO.NET command (`Database.GetDbConnection()` + the transaction's `DbTransaction`), not `Database.SqlQuery<T>()`/`FromSqlRaw<T>()`. | EF `SqlQuery<T>()` | Stage-1 slice-2 confirmed `SqlQuery<T>()` throws `IndexOutOfRangeException` against this project's model (`NavigationExpandingExpressionVisitor` bug, pre-existing, reproduces on `main`). `VerificarRolSinBypassAsync` already works around it the same way — reused here rather than rediscovered. |
| 4 | Consumidor Final protection | Domain guard in `ServicioDeClientes` (block edit/delete when `Numero == 1`) **plus** DB CHECK `ck_clientes_cf_protegido CHECK (numero <> 1 OR deleted_at IS NULL)`. | Trigger-based protection; app guard only | A CHECK constraint is cheap, declarative, and matches existing convention (`ofertas`' `num_nonnulls` checks) — no triggers exist anywhere in this codebase, would be new machinery. It only blocks the irreversible path (soft-delete), which is the highest-severity bypass risk (proposal Risk row); other CF field edits stay behind the app guard, consistent with "domain guard, not just UI hiding." |
| 5 | Provisioning template shape | Extend `PlantillaV1` in place (fill the `ItemsDiferidos` gap it already declared), not a `V2` bump. | `V2` version (literal proposal wording) | ADR-16's versioning rule is for a *different vertical template*, not staged completion of the same vertical. `ItemsDiferidos` already documented CF/General-list as V1's own roadmap gap; closing it finishes V1, a `V2` would falsely imply a parallel business vertical exists. |
| 6 | ADR-10 (`DeLaEmpresa`) | Stays deferred, not re-raised. | — | Both tables use the exact same `id_tenant`/`id_empresa` catalog shape stage 1 already ships without it; no new querying gap found. |

## Table Shapes (all `[catálogo]`, `HabilitarRlsDeTenant`)

- **`listas_precio`**: `CatalogoSimple` shape + `es_default bool`, `modo modo_lista` (enum: `fija`|`derivada`), `id_lista_base int? → listas_precio`, `porcentaje numeric(5,2)?`. Indexes: standard nombre compartido/propio pair (reused) + a **second** compartido/propio pair on `es_default` (`ux_listas_precio_default_compartido/empresa`, same partial-NULL technique, guards "one default per scope").
- **`numeraciones_clientes`**: `id_tenant int PK`, `proximo_numero int NOT NULL DEFAULT 1`, `fk_numeraciones_clientes_tenant`.
- **`clientes`**: `id_cliente`, `id_tenant`, `id_empresa?`, `numero int NOT NULL`, `nombre citext NOT NULL`, `apellido citext?`, `razon_social citext?`, `tipo_documento tipo_documento?` (enum dni|cuit|cuil|pasaporte|otro — nullable, CF has none), `numero_documento citext?` (no unique index, resolved decision #2), `id_condicion_fiscal int NOT NULL → condiciones_fiscales` (app defaults to seeded `CF` row when omitted — **superseded**: `specs/clientes/spec.md`'s "id_lista_precio and id_condicion_fiscal are required" scenario overrides this default-on-omit statement; both fields are REQUIRED, see apply-progress.md batch 4), `nacimiento date?`, `domicilio/telefono/celular/email citext?`, `observaciones text?`, `id_lista_precio int NOT NULL → listas_precio`, `limite_credito numeric(14,2) DEFAULT 0`, `credito_ilimitado bool DEFAULT false`, `saldo numeric(14,2) DEFAULT 0`, `activo`. Indexes: `ux_clientes_numero (id_tenant, numero) WHERE deleted_at IS NULL`; `ck_clientes_cf_protegido`; `ix_clientes_tenant`, `ix_clientes_empresa`; `fk_clientes_{tenant,empresa,condicion_fiscal,lista_precio}`.
- **`proveedores`**: `id_proveedor`, `id_tenant`, `id_empresa?`, `razon_social citext NOT NULL`, `nombre_fantasia citext?`, `cuit varchar(13)?`, `id_condicion_fiscal int NOT NULL`, `domicilio/telefono/email citext?`, `vendedor/celular_vendedor/supervisor/celular_supervisor citext?`, `margen numeric(5,2)?`, `observaciones text?`, `activo`. Indexes: `ux_proveedores_cuit (id_tenant, cuit) WHERE deleted_at IS NULL AND cuit IS NOT NULL` (no empresa in the key — tenant-wide, resolved decision #1); `ix_proveedores_tenant/empresa`; `fk_proveedores_{tenant,empresa,condicion_fiscal}`.

## Numero Assignment Flow

    ServicioDeClientes.CrearAsync
        └─→ AsignadorDeNumeroCliente.AsignarSiguienteAsync(idTenant)  [raw ADO.NET, same tx]
                └─→ UPDATE numeraciones_clientes SET proximo_numero = proximo_numero+1
                    WHERE id_tenant = @t RETURNING proximo_numero - 1
        └─→ Conjunto.Add(cliente with Numero = asignado) → SaveChangesAsync

`AsignadorDeNumeroCliente` also exposes `AsegurarContadorAsync` (idempotent `INSERT ...
ON CONFLICT (id_tenant) DO NOTHING`), reused by `ServicioDeAprovisionamiento` (CF
bootstrap: ensure counter → assign → insert `numero = 1` cliente) and by
`InicializadorDeBaseDeDatos`'s new `BackfillDeClientesYListaPreciosAsync` step for
existing tenants (idempotent: skip a tenant that already has its General list / CF row).

## Backstop Map (db-error-backstops)

| Constraint | SQLSTATE | `ManejadorDeErrores` mapping | Race test |
|---|---|---|---|
| `ux_proveedores_cuit` | 23505 | extend suffix classifier: `_cuit` → `cuit_duplicado` | 2 concurrent creates, same CUIT → 1×201 + 1×409 |
| `ux_clientes_numero` | 23505 | extend suffix classifier: `_numero` → `numero_duplicado` | 2 concurrent `CrearAsync` → both succeed, distinct sequential `numero` (proves the atomic path never hits this 23505 in normal operation) |
| `ux_listas_precio_default_*` | 23505 | covered generically (`_nombre`/new `_default` suffix) | seed-only this stage, no client write path — documented exemption per skill's decision gate |
| `ck_clientes_cf_protegido` | 23514 | new case: → 409 `consumidor_final_protegido` | direct raw-SQL `UPDATE ... SET deleted_at=now() WHERE numero=1` bypassing the service asserts 23514/409 |
| `fk_clientes_*`, `fk_proveedores_*` | 23503 | already generic (`fk_` prefix) | one smoke test per new FK, cross-tenant id |

## Migration Sequencing (DB CHANGE GATE)

**One migration**, `ClientesYProveedoresEtapa2`: creates all 4 tables, all indexes/FKs/CHECK
above, RLS on each via `HabilitarRlsDeTenant`, registers `tipo_documento`/`modo_lista`
native enums (`WaysDbContextFactory` + prod DI, same pattern as `comportamiento_medio_pago`).
**[DB CHANGE GATE — mandatory, blocking]**: full model summary (tables/columns/indexes/
constraints/RLS) **and** the backfill plan (which tenants get CF/General-list rows)
presented together, per resolved decision #3, before generation.

## Testing Strategy

| Layer | What | Approach |
|---|---|---|
| Unit (Domain) | CF protection rule, field validators | Pure functions, no DB (mirrors `PoliticaDeRoles`) |
| Integration | RLS raw-SQL proofs (4 new tables) | Cross-tenant SELECT/UPDATE/DELETE blocked, real Postgres |
| Integration | Backstop map races (table above) | SQLSTATE-asserted, real Postgres |
| Integration | Provisioning + backfill | New tenant → CF+General+counter exist; backfill run twice → idempotent no-op on 2nd run |

## Open Questions

None blocking.
