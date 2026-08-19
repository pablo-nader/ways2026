# Tasks: Stage 16 — Órdenes de compra

## Orchestrator Decisions Recorded This Phase

> `spec.md` and `design.md` ran in PARALLEL (state.yaml). Where they diverge, `state.yaml`'s
> OD7 (spec tensions) and OD8 (design tensions) are authority — both ratify **design** in every
> case cited below. Precedent: stage-14/15 "conflict found and resolved" numbering.

1. **6 slices, stacked-to-main, adopted verbatim from `design.md`'s ratified Slicing table**
   (`design.md:471-484`), which is itself the proposal's tentative plan re-scoped by OD8/T1's
   decision 10 (backstops move to slice 1). Merge order `1 → 2 → 3 → 4 → 5 → 6`. Slice 1 owns
   the only migration and blocks everything; 3 depends on 2 for the entity surface; 4 depends on
   3 for the projection; 5 depends on 3 for the derivation; 6 depends on 5.
2. **DB gate — `db_gate: UNA-MIGRACION-APROBADA`** (`state.yaml`): slice 1 carries **exactly
   one** new migration, `OrdenesDeCompraEtapa16`, matching proposal gate §A-§D verbatim (1 enum
   type/5 values, 2 tables/9 FKs/4 CHECKs/11 named indexes + 1 implicit AK index, 1 additive
   ALTER, RLS last on both new tables, **zero data statements**). Slices 2-6 each carry a
   gate-guard task requiring `dotnet ef migrations has-pending-model-changes` clean and zero new
   files under `Migraciones/`. **Binding count**: total new indexes = **12** (7 on
   `ordenes_compra` incl. the implicit AK + 4 on `items_orden_compra` + 1 on
   `comprobantes_compra`) — any other count reopens the gate per `state.yaml`'s
   `db_gate_approval`.
3. **Pre-authorized cut points**, inherited verbatim from `design.md:490-505`: `1a`/`1b` at the
   table/link boundary; `3a`/`3b` at the write-path boundary (link+confirm vs anulación); `5a`/`5b`
   at the read boundary (list vs detail); `6` may drop the `Reposicion.tsx` action (API still
   serves it). **Never degraded**: lock-then-re-read-then-update, zero-extra-statements, the
   `_numero` ordering trap, the manual-close short-circuit.
4. **CONFLICT FOUND AND RESOLVED #1 — the `enviar` concurrency criterion.**
   `ordenes-de-compra/spec.md`'s Requirement "Enviar Assigns The Own Number…" states two
   concurrent `enviar` calls "for OCs at the same punto de venta" must both succeed with distinct
   numbers, and separately (sequential, not concurrent) that re-sending an already-`enviada` OC is
   `409`. It never states the **concurrent same-OC** case. `design.md` flagged this ambiguity
   itself (Open Question T1, `design.md:533-537`) and resolved it: the no-409 guarantee applies
   only to **two distinct OCs** of one PV; two concurrent `enviar` of the **same** OC must produce
   one `200` + one `409`, burning the loser's number. **`state.yaml` OD8/T1 is authoritative**
   (ratified in favor of design). Tasks 2.14-2.15 implement both shapes as two separate binding
   tests.
5. **CONFLICT FOUND AND RESOLVED #2 — ligadura state-gating has no named domain codes in the
   spec.** `ordenes-de-compra/spec.md`'s "Ligadura Invariant" requirement states only the
   tenant/proveedor/punto-de-venta match; it never states which OC `estado`s accept a link.
   `spec.md`'s own OD7/T3 (state.yaml, spec phase) anticipated exactly this: *"los códigos de
   dominio sin nombre… los NOMBRA el design — si no lo hace, tasks los reconcilia."* `design.md`
   decision 8 (`design.md:63`, Open Question T2 `design.md:538-541`) DOES name them: linkable
   `enviada`/`recibida_parcial`/`cerrada`; refused `borrador` → `orden_compra_no_enviada` (409),
   `anulada` → `orden_compra_anulada` (409). **`state.yaml` OD8/T2 ratifies design's "cerrada
   stays linkable" call.** Adopted verbatim; tasks 3.9-3.11 add the state-gated link tests the
   spec's scenario list does not enumerate.
6. **CONFLICT FOUND AND RESOLVED #3 — an OC with zero items.** Neither `spec.md` names nor
   forbids sending an empty OC. `design.md` decision 7 (`design.md:62`, Open Question T6
   `design.md:555-557`) refuses it at `enviar` (`orden_compra_sin_items`, 400) because the
   derivation's `NOT EXISTS` is vacuously true and the first projection would read `cerrada` for
   an order nobody placed. **`state.yaml` OD8/T6 ratifies.** Task 2.13 implements and tests the
   guard (mutation target #17).
7. **CONFLICT FOUND AND RESOLVED #4 — `CompraDetalle` response shape.** `proposal.md`'s Affected
   Areas table for `ComprasEndpoints.cs` originally implied "no response shape changes" beyond the
   request gaining `idOrdenCompra`. `design.md` decision (Open Question T7, `design.md:558-561`)
   corrects this under `dto-contract-honesty` rule 2: a request-only field cannot satisfy the
   round-trip assertion, so `CompraDetalle` gains exactly one nullable field,
   `IdOrdenCompra`. **`state.yaml` OD8/T7 ratifies.** Task 3.4 implements; task 3.16 is the
   round-trip test.
8. **`mutation-proof-tests` compliance**: the **34** named mutation targets in `design.md:427-469`
   are each placed in exactly one slice, per design's own "Slice" column: 1 → 9 (targets 1-9),
   2 → 8 (10-17), 3 → 13 (18-30), 4 → 3 (31-33), and target 34 (a compound row) is split by its own
   sub-clauses across 4 (the authorization `.RequireAuthorization` route stack), 5 (pagination
   tiebreaker + per-filter + `Desvio` null branch), and 6 (the two web gating branches + the
   pre-load exclusion filter) — no sub-clause duplicated, none dropped. Every target requires
   apply-time evidence (mutation applied → named failing test → reverted → green) recorded in its
   slice's PR body.
9. **`db-error-backstops` applies across three slices, per design decision 10 (`design.md:65`,
   which OVERRODE the proposal's original slice-2 placement — `state.yaml` OD8 ratifies).** All
   **6** `ManejadorDeErrores.cs` branches (2 exact-name `23505` incl. the `_numero` ordering trap +
   4 exact-name `23514`) ship in **slice 1**, proven out-of-band by raw insert (no call site
   needed yet). Slice 2 then carries only the **concurrency proof** of the numbering assigner
   (decision 4 above). Slice 3 carries the client-reachable FK 9 pre-check + race
   (`ExigirOrdenLigableAsync`). FK 2/FK 3/FK 8 pre-checks land in slice 2's draft write path.
10. **`react-async-state` + `web-descriptor-tests` apply to slice 6 only** — the single
    web-touching slice.
11. **`dto-contract-honesty` applies at slice 3** (`SolicitudDeCompra`/`CompraDetalle` gain
    `IdOrdenCompra`, decision 7 above) **and slice 5** (`ContratosDeOrdenDeCompra.cs` — every new
    read/write DTO, the `Cobertura` list never a fabricated per-line split, decision 13).
12. **`work-unit-commits` applies to every slice.**
13. **Testing convention — fixed clock and asymmetric seeds.** Every date-bearing test pins
    `RelojFijo(2026-08-19T12:00:00Z)` (mediodía UTC); at least one listing/offset test additionally
    sends the real client offset `-03:00` (never `Z`) and asserts both the returned rows and the
    displayed período (`mutation-proof-tests` rule 10, the stage-14 verify W2 / PR #129 lesson —
    only this shape can see a raw-ADO UTC-normalization regression). Every fixture uses
    **deliberately desynchronized ids** (`id_tenant`, `id_proveedor`, `id_punto_venta`,
    `id_articulo`, `id_orden_compra` never coincidentally equal or sequentially aligned) so a
    mutant that swaps one identity column for a numerically-similar one cannot pass by accident.
14. **Archive-phase carryover, registered so it is not read as an omission from this phase's
    scope.** `state.yaml`'s spec-phase OD7/T1 is **binding for `sdd-archive`, not `sdd-apply`**:
    the Purpose prose of the LIVE `openspec/specs/reposicion-de-stock/spec.md` (not the delta in
    this change folder) must be updated together with the delta fusion, because the delta format
    cannot express a Purpose-level correction. No task below touches the live spec tree. Similarly,
    `docs/11-programa-post-paridad.md`'s Etapa 16 status block is explicitly "orchestrator, outside
    the phase" per `proposal.md:760` — no task here either.
15. **Process rule (stage-12/14/15 discipline): every deviation `sdd-apply` takes from this plan
    is registered IN `tasks.md`** — as a task-level note or a new numbered decision appended to
    this section — never left to verify-phase archaeology.

**Not a new conflict, no action required** (already resolved in earlier phases): T3 (spec OD7) —
the `comprobantes-compra` mirroring is the stage-15 pattern, not duplication; T4 (spec OD7) — the
word-budget overage is a house precedent, no action; T5 (spec OD7) — `cuenta-corriente-de-
proveedores` verified untouched, task 4.13 confirms by `git diff --stat`; T3/T4/T9/T10 (design
OD8) — `FOR SHARE` is validation-only outside a transaction (task 3.2 note), the `ServicioDeGastos`
citation correction is prose-only, `algoRecibido` sourced from the reception side (task 3.7,
mutation target #25), and the anulación guard stays lock-free (task 4.7, mutation target #33).

---

## Suggested Work Units

| Unit | Goal | Likely PR | Focused test command | Runtime harness | Rollback boundary |
|---|---|---|---|---|---|
| 1 | Migration (type, 2 tables, 9 FKs, 4 CHECKs, 12 indexes, ALTER, RLS last) + entities + `ProyectorDeEstadoDeOrden` + EF configs + `MapEnum` + 6 `ManejadorDeErrores` branches + doc 10 | PR 1 | `dotnet test --filter FullyQualifiedName~OrdenesCompraSchema\|FullyQualifiedName~ProyectorDeEstadoDeOrden` | Testcontainers Postgres 17, `ways_app` NOSUPERUSER NOBYPASSRLS | Revert branch: `DROP` FK→col→both tables→type, no dependent object, no data migration to undo |
| 2 | `ServicioDeOrdenesDeCompra` draft CRUD + `enviar` (own numbering) + backstop concurrency proof | PR 2 | `dotnet test --filter FullyQualifiedName~ServicioDeOrdenesDeCompra` | Real Postgres, forced rendezvous (two `enviar` tasks) | Revert branch: endpoints/service disappear, schema untouched |
| 3 | `idOrdenCompra` on compra draft + `ExigirOrdenLigableAsync` + `EscriturasDeOrdenDeCompra` + guarded calls in `ConfirmarAsync`/`AnularAsync` | PR 3 | `dotnet test --filter FullyQualifiedName~EscriturasDeOrdenDeCompra\|FullyQualifiedName~ServicioDeComprasLigadura` | Real Postgres, forced rendezvous (confirm×confirm, anular×confirmar) | Revert branch: call sites disappear, unlinked confirms already proven byte-identical |
| 4 | `POST /cerrar` + `POST /anular` + `anulada` refusal inside confirm + 409/authorization matrix | PR 4 | `dotnet test --filter FullyQualifiedName~OrdenesCompraCierreYAnulacion` | Real Postgres, `SuperficieDeAutorizacionTests` allowlist | Revert branch: endpoints disappear, no dependent write path |
| 5 | Paginated list + detail read model (cobertura, price deviation) | PR 5 | `dotnet test --filter FullyQualifiedName~OrdenesCompraLectura` | Real Postgres, `RelojFijo` tied-fecha fixture | Revert branch: read endpoints disappear, no write-side impact |
| 6 | Web: list/detail/draft screens + client + `Reposicion.tsx` action + `CompraEditor.tsx` pre-load + `Compras.tsx` link | PR 6 | `npm run test -- OrdenesDeCompra` (Vitest) | Vitest + RTL, no browser required | Revert branch: screens/routes disappear, API still serves the shape |

Total ≈ **2 540 lines**. `Decision needed before apply: No` — `auto-chain` + `stacked-to-main`
already resolved in `state.yaml`.

---

## Slice 1: Schema + Backstops (PR 1)

**Start**: `main`. **Finish**: `estado_orden_compra` + both tables + the `comprobantes_compra`
ALTER exist with standard RLS, 12 total new indexes, the 6 `ManejadorDeErrores` branches proven
out-of-band; doc 10 carries both tables. No write path calls anything yet (slice 2). **Rollback**:
`ALTER TABLE comprobantes_compra DROP CONSTRAINT fk_comprobantes_compra_orden_compra` → `DROP
COLUMN id_orden_compra` → `DROP TABLE items_orden_compra` → `DROP TABLE ordenes_compra` → `DROP
TYPE estado_orden_compra` — no dependent object in that order (proposal Rollback Plan). **Done** =
tests green + `judgment-day` clean round + PR merged.

**Budget note**: pre-authorized split `1a` (type + both tables + entities + configs + RLS/CHECK
tests) / `1b` (the ALTER + the 6 backstops + doc 10) if this slice overflows — decision 3 above.

- [x] 1.1 Migration `OrdenesDeCompraEtapa16`: `CREATE TYPE estado_orden_compra AS ENUM
  ('borrador','enviada','recibida_parcial','cerrada','anulada')`. *(proposal.md:514-517, gate §A)*
- [x] 1.2 Same migration: `CREATE TABLE ordenes_compra` — 16 columns exactly per §B (`numero
  bigint NULL`, `fecha_emision` **no DEFAULT**, `fecha_envio/fecha_esperada/fecha_cierre NULL`,
  `id_empleado_cierre NULL`); `pk_ordenes_compra`. *(proposal.md:540-558)*
- [x] 1.3 Same migration: 5 named FKs on `ordenes_compra` — `fk_..._tenant`, `fk_..._punto_venta`
  (composite), `fk_..._proveedor` (composite), `fk_..._empleado` (simple), `fk_..._empleado_cierre`
  (simple, nullable) — all RESTRICT. `ak_ordenes_compra_id_orden_compra_id_tenant UNIQUE
  (id_orden_compra, id_tenant)`. *(proposal.md:566-576)*
- [x] 1.4 Same migration: `ck_ordenes_compra_envio_completo` and `ck_ordenes_compra_cierre` exactly
  per §B's table. *(proposal.md:577-578)*
- [x] 1.5 Same migration: 6 named indexes on `ordenes_compra` — `ix_..._tenant`,
  `ix_..._punto_venta_fecha`, `ix_..._proveedor`, `ix_..._empleado` (simple), `ix_..._empleado_cierre`
  (simple), `ux_ordenes_compra_numero` **UNIQUE PARTIAL** `WHERE numero IS NOT NULL` — plus the
  implicit AK index (7 total). Zero EF-autogenerated FK-support index beyond these.
  *(proposal.md:584-597)*
- [x] 1.6 Same migration: `CREATE TABLE items_orden_compra` — 11 columns exactly per §C (no
  `cantidad_recibida`); `pk_items_orden_compra`. *(proposal.md:606-617)*
- [x] 1.7 Same migration: 3 named FKs on `items_orden_compra` — `fk_..._tenant`,
  `fk_..._orden_compra` (composite, against §B's AK), `fk_..._articulo` (composite); `ck_..._
  cantidad_positiva`, `ck_..._costo_no_negativo`. *(proposal.md:630-634)*
- [x] 1.8 Same migration: 3 named indexes on `items_orden_compra` — `ix_..._tenant`,
  `ix_..._orden_compra`, `ix_..._articulo`, `ux_items_orden_compra_orden` **UNIQUE**
  `(id_orden_compra, orden)` (4 total). *(proposal.md:641-644)*
- [x] 1.9 Same migration: `ALTER TABLE comprobantes_compra ADD COLUMN id_orden_compra integer
  NULL` + `fk_comprobantes_compra_orden_compra` composite, MATCH SIMPLE + explicit
  `ix_comprobantes_compra_orden_compra` (named by hand, never EF-autogenerated). Metadata-only, no
  rewrite. *(proposal.md:652-663, gate §D)*
- [x] 1.10 Migration ordering verified in the generated file: `CREATE TYPE` → `CREATE TABLE
  ordenes_compra` (+AK/FKs/CHECKs/indexes) → `CREATE TABLE items_orden_compra` → `ALTER TABLE
  comprobantes_compra` → `HabilitarRlsDeTenant` on **both** new tables, **LAST**.
  *(proposal.md:721-724)* — DEVIATION registered: `dotnet ef migrations add` emitted the ALTER's
  `AddColumn` BEFORE both `CreateTable`s and the FK/index AFTER all other indexes; hand-reordered
  the `Up()` body to the gate's exact sequence (type → ordenes_compra + its indexes →
  items_orden_compra + its indexes → ALTER comprobantes_compra column+FK+index → RLS on both new
  tables last). No semantic change, only statement order — verified by a clean apply against a
  fresh Testcontainers Postgres 17 in the integration run.
- [x] 1.11 Create `src/Ways.Domain/Compras/EstadoOrdenCompra.cs` — 5 values, member order = native
  type order. *(design.md:146)*
- [x] 1.12 Create `src/Ways.Domain/Compras/ProyectorDeEstadoDeOrden.cs` — the pure five-arm rule,
  `PoliticaDeRoles` pattern, no database. *(design.md:148-156, decision 4)*
- [x] 1.13 Create `src/Ways.Domain/Compras/OrdenCompra.cs` · `ItemOrdenCompra.cs` — `EntidadTenant`
  ⇒ `EntidadBase`, standard tenant filter + `EstamparTenant()`, like `ComprobanteCompra`.
  *(design.md:158-160, gate §B/§C)*
- [x] 1.14 Create `OrdenCompraConfiguration.cs` · `ItemOrdenCompraConfiguration.cs` — shaped on
  `ComprobanteCompraConfiguration.cs:17-136` / `ItemComprobanteCompraConfiguration.cs:16-143`; all
  support indexes declared by hand with doc-10 names. *(design.md:370)*
- [x] 1.15 Modify `ComprobanteCompraConfiguration.cs` — `IdOrdenCompra` + FK 9 +
  `ix_comprobantes_compra_orden_compra` (named, never autogenerated). *(design.md:371, mutation
  target #9)*
- [x] 1.16 Modify `WaysDbContext.cs` + `IWaysDbContext.cs` — two new `DbSet`s. *(design.md:372,
  374)*
- [x] 1.17 Modify `WaysDbContextFactory.cs` **and** `DependencyInjection.cs` —
  `MapEnum<EstadoOrdenCompra>` in **both** builders, never also `HasPostgresEnum`.
  *(design.md:373, mutation target #8)* — DEVIATION registered: `WaysApiFixture.cs` (test-only,
  three `MapEnum` blocks: `ConfigurarNpgsqlDePrueba`, `CrearContextoDeAplicacion`,
  `CrearContextoDeOwner`) also needed `MapEnum<EstadoOrdenCompra>` added — not named by design.md's
  File Changes table (production-only), but the integration test host fails to resolve the enum
  without it. No production behavior change.
- [x] 1.18 Modify `docs/10-modelo-de-datos.md` — both tables (§5-adjacent), `comprobantes_compra.
  id_orden_compra`, "Estado (Etapa 16)" annotation. Landed from inside this slice (stage-12
  task-1.17 discipline). *(proposal.md:65-66, design.md:387)*
- [x] 1.19 Modify `src/Ways.Api/Seguridad/ManejadorDeErrores.cs` — 6 exact-name branches: (a)
  `ux_ordenes_compra_numero` → `numero_de_orden_duplicado`, 409, **placed ABOVE**
  `ClasificarUnicidad`'s generic `_numero` branch (`:180-182`) — third occurrence of the ordering
  trap; (b) `ux_items_orden_compra_orden` → `orden_de_item_duplicado`, 409; (c)
  `ck_ordenes_compra_envio_completo` → `orden_compra_envio_incompleto`, 409; (d)
  `ck_ordenes_compra_cierre` → `orden_compra_cierre_incoherente`, 409; (e)
  `ck_items_orden_compra_cantidad_positiva` → `cantidad_pedida_invalida`, 409/400; (f)
  `ck_items_orden_compra_costo_no_negativo` → `costo_estimado_invalido`, 409/400.
  *(design.md:65-66, decisions 10-11; proposal.md:679, 688-689, gate §E)* — status codes for (e)/(f)
  resolved to 400, mirroring `ck_items_comprobante_compra_cantidad_positiva`/`..._costo_no_negativo`
  in `ClasificarCheckDeCompras` (both client-reachable, service validates first with the same
  domain code, this branch is only the schema backstop); (a)/(b) explicit 409 per proposal §E's own
  table.
- [x] 1.20 [P] Domain unit — `ProyectorDeEstadoDeOrden` full truth table: 2 estados × cierreManual
  × completa × algoRecibido; `anulada` terminal from every input; manual close never moved;
  `completa` beats `algoRecibido`. *(design.md:412)*
- [x] 1.21 [P] Integration — RLS on `ways_app` (NOSUPERUSER NOBYPASSRLS): cross-tenant `SELECT`
  returns **0** rows on both new tables; `INSERT` with a foreign `id_tenant` refused `42501`.
  *(design.md:418, mutation targets #1-#2)*
- [x] 1.22 [P] Integration — both CHECKs by raw insert, both directions, asserting SQLSTATE
  `23514`: `ck_ordenes_compra_envio_completo` (numero without fecha_envio; fecha_envio without
  numero); `ck_ordenes_compra_cierre` (fecha_cierre with estado ≠ cerrada; closer without
  fecha_cierre). *(design.md:419, mutation targets #3-#4)*
- [x] 1.23 [P] Integration — both item CHECKs by raw insert: `cantidad_pedida <= 0`,
  `costo_unitario_estimado < 0` — SQLSTATE `23514`. *(design.md:419, mutation target #5)*
- [x] 1.24 [P] Integration — `ux_items_orden_compra_orden` by raw insert (server-assigned `orden`,
  race-test exemption documented, same family as `ux_items_comprobante_compra_orden`).
  *(proposal.md:680)*
- [x] 1.25 [P] Integration — **the ordering trap, raw out-of-band insert**: a duplicate `numero`
  at one punto de venta resolves to SQLSTATE `23505` **translated to `numero_de_orden_duplicado`**,
  never the `ux_clientes_numero`/generic `_numero` family. *(design.md:65, decision 11; binding
  gate test (c))* — implemented as TWO complementary tests: a raw Postgres proof (SQLSTATE +
  exact `ConstraintName`, `OrdenesCompraSchemaTests.cs`) and a unit-style `ManejadorDeErrores`
  proof asserting the actual translated domain code through both the EF (`DbUpdateException`) and
  raw-ADO (bare `PostgresException`) paths (`ManejadorDeErroresOrdenesDeCompraTests.cs`, mirroring
  the `ManejadorDeErroresComprasTests` precedent) — the SQL-level test alone cannot see the
  ordering trap (the trap lives in C# switch-arm order, not in Postgres), so the second file is
  the one that actually proves decision 11 end-to-end without needing slice 2's HTTP endpoint.
- [x] 1.26 [P] Integration — `pg_indexes` shows **exactly 12** new indexes and no unnamed
  EF-generated FK support index; `has-pending-model-changes` clean. *(design.md:419, mutation
  target #9)*
- [x] 1.27 [P] `db-error-backstops` exemption tests — FK 1/FK 6 (`_tenant`), FK 4/FK 5
  (`_empleado*`), FK 7 (items→OC): generic `23503` mapping, one SQLSTATE test per exempt FK.
  *(proposal.md:685-687)*
- [ ] 1.28 [P] **Mutation target #1** — `HabilitarRlsDeTenant("ordenes_compra")` → delete → cross-
  tenant count + `42501` test (1.21) must fail.
- [ ] 1.29 [P] **Mutation target #2** — `HabilitarRlsDeTenant("items_orden_compra")` → delete →
  same, child table (1.21) must fail.
- [ ] 1.30 [P] **Mutation target #3** — `ck_ordenes_compra_envio_completo` → delete → raw-insert
  `23514` test (1.22) must fail, both directions.
- [ ] 1.31 [P] **Mutation target #4** — `ck_ordenes_compra_cierre` → delete → raw-insert `23514`
  test (1.22) must fail, both directions.
- [ ] 1.32 [P] **Mutation target #5** — either item CHECK → delete → its raw-insert `23514` test
  (1.23) must fail.
- [ ] 1.33 [P] **Mutation target #6** — `HasFilter("numero IS NOT NULL")` on `ux_ordenes_compra_
  numero` → delete → two drafts (numero NULL) in one PV ⇒ spurious `23505` test must fail.
- [ ] 1.34 [P] **Mutation target #7** — the exact-name `ux_ordenes_compra_numero` branch **above**
  `ClasificarUnicidad` → move it below → the ordering-trap test (1.25) must fail (translated code
  becomes `numero_duplicado`).
- [ ] 1.35 [P] **Mutation target #8** — `MapEnum<EstadoOrdenCompra>` in either builder → delete →
  that builder's path fails / `has-pending-model-changes` dirty (1.26).
- [ ] 1.36 [P] **Mutation target #9** — explicit `ix_comprobantes_compra_orden_compra` name → drop
  `HasDatabaseName` → `pg_indexes` audit (1.26) must fail (an EF `IX_…` appears).
- [ ] 1.37 Gate guard (**VINCULANTE**, `state.yaml` db_gate_approval): `git diff --stat main --
  src/Ways.Infrastructure/Persistencia/Migraciones/` shows **exactly one** new file, named for
  `OrdenesDeCompraEtapa16`; `dotnet ef migrations has-pending-model-changes` clean; final new
  index count = **12**; **zero** data statements anywhere in the migration. Any deviation reopens
  the gate.
- [ ] 1.38 Run `judgment-day` on the slice diff; fix confirmed issues; re-judge until clean.
- [ ] 1.39 Branch `feat/stage16-slice1-schema` off `main`; PR; merge stacked-to-main.

**Test plan**: Domain truth table (1.20); RLS (1.21); both CHECKs both directions (1.22-1.23);
both `23505` families incl. race exemption (1.24); the ordering trap (1.25); index-count audit
(1.26); FK exemptions (1.27); 9 mutation targets (1.28-1.36).

**Verify**: `dotnet test --filter FullyQualifiedName~OrdenesCompraSchema|FullyQualifiedName~ProyectorDeEstadoDeOrden`

---

## Slice 2: Borrador + Envío (PR 2)

**Start**: slice 1 merged. **Finish**: `ServicioDeOrdenesDeCompra` draft CRUD (replace-set) +
`enviar` exist; `numero` consumed only at `enviar`, per PV; two concurrent `enviar` proven both
ways (distinct OCs / same OC). **Rollback**: revert branch — the service and its two endpoints
disappear, schema unused for these paths, nothing to repair. **Done** = tests green +
`judgment-day` clean round + PR merged.

**Budget note**: no pre-authorized split named for this slice; if it overflows, split at the
draft-CRUD / `enviar` boundary and register the new cut here (decision 15).

- [ ] 2.1 Create `src/Ways.Application/Compras/ContratosDeOrdenDeCompra.cs` — the write records
  (`SolicitudDeOrdenDeCompra`, `LineaDeOrdenSolicitada`, `ItemDeOrden`); `orden` is server-assigned
  1..N, never accepted from the request. *(design.md:166-174, mutation target #14)*
- [ ] 2.2 Create `src/Ways.Application/Compras/ServicioDeOrdenesDeCompra.cs` — `CrearBorradorAsync`
  (`INSERT`, `estado='borrador'`, `numero NULL`). *(proposal.md:266, design.md:376)*
- [ ] 2.3 Same file: `ActualizarBorradorAsync` — full replace-set under `SELECT … FOR UPDATE …
  WHERE estado = 'borrador'`, `RemoveRange`/`AddRange` items, the `BloquearBorradorAsync` pattern.
  *(proposal.md:267, mutation targets #10, #15)*
- [ ] 2.4 Same file: `EnviarAsync` — outside the caller's transaction, wrapped in
  `db.Database.CreateExecutionStrategy()`, call `AsignadorDeNumeroComprobante.
  AsignarComprometidoAsync(db, idTenant, idPuntoVenta, "OC")`; then `EstrategiaSinReintento ⇒
  UPDATE ordenes_compra SET numero, fecha_envio, estado='enviada' WHERE id AND tenant AND
  estado='borrador' AND id_punto_venta = $pv RETURNING numero`; 0 rows ⇒ reclassify under read
  (409), number stays burnt. *(design.md:36-40, 244-250, mutation targets #11-#13, #16)*
- [ ] 2.5 Guard: refuse `enviar` on an OC with zero items — `orden_compra_sin_items`, 400 —
  mirroring `compra_sin_items`. *(design.md:62, decision 7; mutation target #17; conflict #3
  above)*
- [ ] 2.6 Every raw-ADO parameter through `ParametrosDeComando.Agregar`/`AgregarNulo` — no hand-
  built parameter without `ToUniversalTime()`. *(design.md, mutation target #16)*
- [ ] 2.7 Create `src/Ways.Api/Endpoints/OrdenesDeCompraEndpoints.cs` — `POST /`, `PUT /{id}`,
  `POST /{id}/enviar`, grouped under `OperacionDePos`, stacking `GestionDeCatalogo` on writes.
  *(design.md:301-307, decision 16)*
- [ ] 2.8 [P] Integration — `PUT` on a `borrador` OC replaces items exactly (add + remove in one
  request, no stale row). *(ordenes-de-compra/spec.md:52-55)*
- [ ] 2.9 [P] Integration — `PUT` on a non-`borrador` OC is rejected `409`.
  *(ordenes-de-compra/spec.md:57-60, mutation target #10)*
- [ ] 2.10 [P] Integration — `enviar` on a fresh PV assigns `numero = 1`, sets `fecha_envio`,
  `estado = enviada`. *(ordenes-de-compra/spec.md:73-76)*
- [ ] 2.11 [P] Integration — re-sending an already-`enviada` OC is rejected `409`, `numero` not
  reassigned. *(ordenes-de-compra/spec.md:83-86)*
- [ ] 2.12 [P] Integration — `enviar` on an OC with no items is rejected `orden_compra_sin_items`,
  400. *(conflict #3 above, mutation target #17)*
- [ ] 2.13 [P] Integration — the `-03:00` offset test on `fecha_envio`: `RelojFijo(2026-08-19T12:
  00:00Z)` at offset zero AND a real `-03:00` write both persist the exact fixed instant.
  *(decision 13 above, mutation target #16)*
- [ ] 2.14 [P] **Binding gate test (b), part 1** — two concurrent `enviar` on **two distinct** OCs
  at one punto de venta ⇒ two distinct `numero` values, **neither** response `409`.
  *(ordenes-de-compra/spec.md:78-81; design.md decision T1; conflict #1 above)*
- [ ] 2.15 [P] **Binding gate test (b), part 2** — two concurrent `enviar` on the **same** OC ⇒
  one `200` + one `409`, the loser's number burnt (never reassigned). *(design.md T1, conflict #1
  above)*
- [ ] 2.16 [P] Integration — concurrent `PUT`-moves-the-PV race: a `PUT` relinking the OC's punto
  de venta between the pre-read and the `enviar` lock leaves the number correctly scoped to the
  **old** series (0 rows under the mismatched `WHERE`, reclassified). *(design.md:61, mutation
  target #11)*
- [ ] 2.17 [P] **Mutation target #10** — `WHERE estado = 'borrador'` in the draft lock → delete →
  `PUT` on an `enviada` OC ⇒ expected 409 (2.9) must fail.
- [ ] 2.18 [P] **Mutation target #11** — `AND id_punto_venta = $pv` in the `enviar` UPDATE →
  delete → the concurrent-`PUT`-moves-the-PV test (2.16) must fail.
- [ ] 2.19 [P] **Mutation target #12** — `AsignarComprometidoAsync` → replace with `MAX(numero) +
  1` → two concurrent `enviar` on one PV (2.14) ⇒ same number / `23505` must surface.
- [ ] 2.20 [P] **Mutation target #13** — the assigner call moved **inside** the `enviar`
  transaction → nested-transaction failure / the burnt-number semantics test (2.15) must fail.
- [ ] 2.21 [P] **Mutation target #14** — server-assigned `orden` 1..N replaced with the request's
  own value → `ux_items_orden_compra_orden` ⇒ `orden_de_item_duplicado` test (1.24) must fail.
- [ ] 2.22 [P] **Mutation target #15** — `RemoveRange(itemsExistentes)` in the replace-set →
  delete → the per-line count/identity assertion (2.8) must fail.
- [ ] 2.23 [P] **Mutation target #16** — `ParametrosDeComando.Agregar` on `fecha_envio` → hand-
  built parameter without `ToUniversalTime()` → the `-03:00` offset test (2.13) must fail.
- [ ] 2.24 [P] **Mutation target #17** — the `orden_compra_sin_items` guard → delete → an empty OC
  projects straight to `cerrada` (2.12 regresses).
- [ ] 2.25 Gate guard: `dotnet ef migrations has-pending-model-changes` clean; zero new files under
  `Migraciones/`.
- [ ] 2.26 Run `judgment-day`; fix confirmed issues; re-judge until clean.
- [ ] 2.27 Branch `feat/stage16-slice2-borrador-y-envio` off `main` (parent: slice 1); PR; merge
  stacked-to-main.

**Test plan**: replace-set (2.8-2.9); enviar happy/blocked paths (2.10-2.12); the `-03:00` offset
(2.13); the two binding concurrency tests (2.14-2.15); the PV-relink race (2.16); 8 mutation
targets (2.17-2.24).

**Verify**: `dotnet test --filter FullyQualifiedName~ServicioDeOrdenesDeCompra`

---

## Slice 3: Ligadura + Proyección (PR 3)

**Start**: slice 2 merged. **Finish**: a compra draft may link to a matching OC; confirming/
annulling a linked comprobante projects the OC's `estado` inside the same transaction, at lock
position 2; an unlinked confirm/anular emits **zero** extra statements; the pinned lock order and
both races are proven. **Rollback**: revert branch — the guarded calls and `ExigirOrdenLigableAsync`
disappear, `ServicioDeCompras`'s engine returns to its pre-stage shape exactly. **Done** = tests
green + `judgment-day` clean round + PR merged.

**Budget note**: pre-authorized split `3a` (link + `FOR SHARE` guard + projection class + confirm
call + confirm×confirm race) / `3b` (anulación call + estado regression + anular×confirmar race)
if this slice overflows — decision 3 above.

- [ ] 3.1 Create `src/Ways.Application/Compras/EscriturasDeOrdenDeCompra.cs` — static class,
  `ProyectarEstadoAsync` (lock → short-circuit → derive → conditional `UPDATE … RETURNING`, 3
  statements) and `BloquearYExigirNoAnuladaAsync` (defense-in-depth guard). *(design.md:78-99,
  decisions 1-2)*
- [ ] 3.2 Same file, statement 1: `SELECT estado::text, (id_empleado_cierre IS NOT NULL) FOR
  UPDATE`; `anulada` OR manual close ⇒ return **without** statements 2/3. *(design.md:102-108,
  mutation targets #18, #26, #27)*
- [ ] 3.3 Same file, statement 2: the derivation CTE — `pedido`/`recibido` grouped by
  `id_articulo` on **both** sides, `c.estado = 'confirmada'`, `deleted_at IS NULL` on both joined
  tables, `algoRecibido` sourced from the **reception** side. *(design.md:110-129, mutation targets
  #22-#25)*
- [ ] 3.4 Same file, statement 3: `UPDATE ordenes_compra SET estado, fecha_cierre (CASE, regresión
  limpia NULL), updated_at WHERE … AND estado = $anterior RETURNING`, **skipped** when projected ==
  current. *(design.md:131-139, mutation targets #19, #28)*
- [ ] 3.5 Modify `ServicioDeCompras.cs`'s `ConfirmarHeaderAsync` — widen `RETURNING` to add
  `id_orden_compra`. *(design.md:44-46, mutation target #20)*
- [ ] 3.6 Modify `ServicioDeCompras.cs`'s `MarcarAnuladaAsync` — same widening.
  *(design.md:44-46)*
- [ ] 3.7 Modify `ServicioDeCompras.cs`'s `EjecutarConfirmarAsync` — after step 1 (header lock),
  before lotes: `if (encabezado.IdOrdenCompra is { } idOc) { BloquearYExigirNoAnuladaAsync; }` at
  lock position 2, before `proveedores`. *(design.md:214-221, mutation targets #21, #29)*
- [ ] 3.8 Modify `ServicioDeCompras.cs`'s `EjecutarAnulacionAsync` — same guarded call at position
  2, after the (unmoved) audit step. *(design.md:229-236)*
- [ ] 3.9 Modify `ServicioDeCompras.cs`'s draft path (both `CrearBorradorAsync` and
  `ActualizarBorradorAsync`) — accept `idOrdenCompra`, call `ExigirOrdenLigableAsync` (`SELECT …
  FOR SHARE`) validating tenant + proveedor + punto de venta + linkable estado
  (`enviada`/`recibida_parcial`/`cerrada`; refuse `borrador` → `orden_compra_no_enviada` 409,
  `anulada` → `orden_compra_anulada` 409). *(design.md:63, conflict #2 above, mutation target #30)*
- [ ] 3.10 Modify `src/Ways.Api/Endpoints/ComprasEndpoints.cs` — `SolicitudDeCompra` gains `int?
  IdOrdenCompra`; **no route/policy change**. *(design.md:206-208)*
- [ ] 3.11 Modify `ComprasEndpoints.cs`'s response contract — `CompraDetalle` gains `int?
  IdOrdenCompra`. *(design.md:206-208, conflict #4 above)*
- [ ] 3.12 [P] Integration — **binding gate test (a): zero-extra-statements**. A confirm with
  `id_orden_compra IS NULL` issues the exact pre-stage command count; existing
  `ComprasConfirmarTests`/`ComprasAnularTests` green and **unedited** in the diff.
  *(comprobantes-compra/spec.md:50-54, 75-79; design.md, Testing Strategy)*
- [ ] 3.13 [P] Integration — a borrador draft links to a matching (`enviada`) OC; persisted.
  *(comprobantes-compra/spec.md:14-17)*
- [ ] 3.14 [P] Integration — a mismatched proveedor/PV/tenant cannot link, refused before any
  write. *(ordenes-de-compra/spec.md:196-204, comprobantes-compra/spec.md:19-22)*
- [ ] 3.15 [P] Integration — linking to a `borrador` OC ⇒ `409 orden_compra_no_enviada`; linking to
  an `anulada` OC ⇒ `409 orden_compra_anulada`; linking to a `cerrada` OC succeeds. *(conflict #2
  above)*
- [ ] 3.16 [P] Integration — the link is frozen once confirmed; `CompraDetalle.IdOrdenCompra`
  round-trips exactly what was set at draft time. *(comprobantes-compra/spec.md:24-27, conflict #4
  above)*
- [ ] 3.17 [P] Integration — confirming a linked reception moves the OC to `recibida_parcial` in
  the same transaction. *(ordenes-de-compra/spec.md:109-112, comprobantes-compra/spec.md:39-43)*
- [ ] 3.18 [P] Integration — confirming the remainder closes the OC automatically,
  `id_empleado_cierre IS NULL`. *(ordenes-de-compra/spec.md:114-117)*
- [ ] 3.19 [P] Integration — confirming against an `anulada` OC is refused `409
  orden_compra_anulada`, no write. *(ordenes-de-compra/spec.md:185-188, comprobantes-
  compra/spec.md:45-48)*
- [ ] 3.20 [P] Integration — annulling the only reception of an automatically-closed OC returns it
  to `enviada`. *(ordenes-de-compra/spec.md:119-122, comprobantes-compra/spec.md:65-68)*
- [ ] 3.21 [P] Integration — **derivation fidelity** (rule 11): two OC lines of one artículo (3+4
  ⇒ 7 pedidas), a reception splitting it (2 then 5), an artículo received but never ordered, an
  over-delivery (8 against 7), a soft-deleted reception, a linked `borrador` reception, a reception
  of another OC of the same proveedor — every `Recibida`/`Pendiente` asserted per artículo, never a
  fresh 1-line/1-reception seed. *(design.md, Testing Strategy; ordenes-de-compra/spec.md:124-129;
  decision 13 above — desynchronized ids)*
- [ ] 3.22 [P] Integration — **the two races**: confirm × confirm of two receptions of one OC (both
  commit, no deadlock, resulting estado = the sum of both, never only one); anular OC × confirmar
  reception in both orders (one `200` + one `409`, never a reception lands on an `anulada` OC,
  never a deadlock). *(ordenes-de-compra/spec.md:131-135, design.md Concurrency guarantees)*
- [ ] 3.23 [P] Integration — a fault injected after the projection leaves the OC untouched
  (fault-point test, both confirm and anular paths). *(design.md, Testing Strategy)*
- [ ] 3.24 [P] Integration — the pinned lock order holds: `comprobantes_compra → ordenes_compra →
  lotes → stock/stock_lotes → proveedores → ledger`, verified by source-order or interceptor per
  the stage-15 precedent if a live rendezvous cannot discriminate it (`mutation-proof-tests` rule
  3 escape hatch, registered if invoked). *(design.md:268-282)*
- [ ] 3.25 [P] **Mutation target #18** — `SELECT … FOR UPDATE` (statement 1) → delete, keep
  derive+update → confirm×confirm rendezvous (3.22) ⇒ stale estado.
- [ ] 3.26 [P] **Mutation target #19** — the derivation folded into one `UPDATE … FROM (SELECT
  …)` → same rendezvous (3.22) ⇒ `EvalPlanQual` stale snapshot.
- [ ] 3.27 [P] **Mutation target #20** — `id_orden_compra` read from `preLectura` instead of the
  widened `RETURNING` → confirm under a concurrent `PUT` that relinks the draft must fail.
- [ ] 3.28 [P] **Mutation target #21** — OC lock moved after the `proveedores` lock → confirm×
  confirm rendezvous (3.22) ⇒ deadlock/timeout.
- [ ] 3.29 [P] **Mutation target #22** — `c.estado = 'confirmada'` widened to any estado → a
  linked `borrador` reception moves the OC (3.21 fixture) must fail.
- [ ] 3.30 [P] **Mutation target #23** — either `deleted_at IS NULL` deleted → the soft-deleted-
  reception fixture (3.21) must fail.
- [ ] 3.31 [P] **Mutation target #24** — `GROUP BY id_articulo` on the ordered side matched line-
  to-line → the duplicate-OC-lines fixture (3.21, 3+4⇒7) must fail.
- [ ] 3.32 [P] **Mutation target #25** — `algoRecibido` sourced from the ordered side's coalesced
  sum instead of the reception side → the pure-substitution fixture (OC stays `enviada`) must
  fail.
- [ ] 3.33 [P] **Mutation target #26** — `id_empleado_cierre IS NOT NULL` short-circuit deleted →
  annulling a reception of a manually-closed OC reopens it (3.20's sibling test) must fail.
- [ ] 3.34 [P] **Mutation target #27** — `estado = 'anulada'` terminal short-circuit deleted →
  the projection resurrects an annulled OC (3.19's sibling test) must fail.
- [ ] 3.35 [P] **Mutation target #28** — `fecha_cierre = NULL` on regression kept as old value →
  `ck_ordenes_compra_cierre` ⇒ `23514` (3.20 regresses).
- [ ] 3.36 [P] **Mutation target #29** — `if (encabezado.IdOrdenCompra is { } idOc)` called
  unconditionally → the zero-extra-statements command count (3.12) must fail.
- [ ] 3.37 [P] **Mutation target #30** — `id_proveedor`/`id_punto_venta` equality dropped in
  `ExigirOrdenLigableAsync` → the cross-proveedor/cross-PV link test (3.14) must fail.
- [ ] 3.38 [P] `db-error-backstops` — FK 9 (`fk_comprobantes_compra_orden_compra`) client-
  reachable test: linking to an OC being annulled concurrently (race) + generic `23503` mapping.
  *(design.md:325)*
- [ ] 3.39 Gate guard: `dotnet ef migrations has-pending-model-changes` clean; zero new files under
  `Migraciones/`; `git diff --stat` confirms no file under `src/Ways.Application/Ventas/` or
  `src/Ways.Application/Stock/` appears.
- [ ] 3.40 Run `judgment-day`; fix confirmed issues; re-judge until clean.
- [ ] 3.41 Branch `feat/stage16-slice3-ligadura-y-proyeccion` off `main` (parent: slice 2); PR;
  merge stacked-to-main.

**Test plan**: zero-extra-statements (3.12); link happy/blocked paths incl. state-gating
(3.13-3.16); the projection scenarios (3.17-3.20); derivation fidelity (3.21); both races (3.22);
fault points (3.23); lock order (3.24); 13 mutation targets (3.25-3.37); FK 9 race (3.38).

**Verify**: `dotnet test --filter FullyQualifiedName~EscriturasDeOrdenDeCompra|FullyQualifiedName~ServicioDeCompras`

---

## Slice 4: Cierre + Anulación (PR 4)

**Start**: slice 3 merged. **Finish**: `POST /cerrar` and `POST /anular` exist; the 409 matrix and
the authorization matrix are proven; the anulación guard reads `comprobantes_compra` lock-free.
**Rollback**: revert branch — both endpoints disappear, the projection (slice 3) is unaffected.
**Done** = tests green + `judgment-day` clean round + PR merged.

- [ ] 4.1 Same `ServicioDeOrdenesDeCompra.cs`: `CerrarAsync` — `UPDATE … SET estado='cerrada',
  fecha_cierre=$m, id_empleado_cierre=$actor WHERE … AND estado IN ('enviada','recibida_parcial')
  RETURNING`. *(design.md:262-263, ordenes-de-compra/spec.md:139-141)*
- [ ] 4.2 Same file: `AnularAsync` — statement 1 `SELECT estado FOR UPDATE` (first and only lock);
  statement 2 the derived-received-zero guard (any artículo `> 0` ⇒ `409
  orden_compra_con_recepciones`); statement 3 the linked-`borrador` `EXISTS` guard, **WITHOUT any
  row lock** (decision 9); statement 4 `UPDATE … estado='anulada' WHERE … estado IN
  ('borrador','enviada') RETURNING`. *(design.md:252-259, decision 9, mutation target #33)*
- [ ] 4.3 Modify `OrdenesDeCompraEndpoints.cs` — add `POST /{id}/cerrar`, `POST /{id}/anular`,
  same policy stack. *(design.md:306-307)*
- [ ] 4.4 [P] Integration — a supplier order closed manually stamps `fecha_cierre` +
  `id_empleado_cierre`. *(ordenes-de-compra/spec.md:145-148)*
- [ ] 4.5 [P] Integration — a manually-closed OC does not reopen when its reception is annulled.
  *(ordenes-de-compra/spec.md:150-153, comprobantes-compra/spec.md:70-73)*
- [ ] 4.6 [P] Integration — closing an already-`cerrada` OC is rejected `409`.
  *(ordenes-de-compra/spec.md:155-158)*
- [ ] 4.7 [P] Integration — an OC whose only reception was later annulled CAN itself be annulled
  (derived quantity zero). *(ordenes-de-compra/spec.md:169-173)*
- [ ] 4.8 [P] Integration — an OC with an effective (not-annulled) reception CANNOT be annulled ⇒
  `409 orden_compra_con_recepciones`. *(ordenes-de-compra/spec.md:175-178)*
- [ ] 4.9 [P] Integration — an OC with a still-confirmable linked `borrador` draft CANNOT be
  annulled ⇒ `409 orden_compra_con_recepciones`. *(ordenes-de-compra/spec.md:180-183)*
- [ ] 4.10 [P] Integration — **the no-lock `EXISTS` proof**: adding `FOR SHARE` to the linked-draft
  guard's read reproduces the anular×confirmar deadlock; without it, both orders resolve to one
  `200` + one `409`, never a deadlock. *(design.md:64, decision 9, mutation target #33)*
- [ ] 4.11 [P] Integration — **authorization matrix**: Vendedor 200 on both GETs, `403` on all
  five writes (`POST`, `PUT`, `enviar`, `cerrar`, `anular`); Supervisor same; Admin 200 on all;
  tenant B never sees tenant A's OCs. `SuperficieDeAutorizacionTests` allowlist gains the five
  new non-GET routes. *(ordenes-de-compra/spec.md:232-240, design.md:309-310)*
- [ ] 4.12 [P] Integration — non-regression: `ComprasConfirmarTests`/`ComprasAnularTests` green
  and unedited in the diff (repeated per binding verify criterion 3). *(design.md:513-514)*
- [ ] 4.13 [P] Confirm `cuenta-corriente-de-proveedores`/`saldo-de-proveedor` untouched by
  `git diff --stat` (spec OD7/T5). *(not-a-new-conflict note above)*
- [ ] 4.14 [P] **Mutation target #31** — `WHERE estado IN ('enviada','recibida_parcial')` in
  `cerrar` widened → closing a `borrador`/`anulada` OC succeeds (must fail the test).
- [ ] 4.15 [P] **Mutation target #32** — `id_empleado_cierre = $actor` on manual close written
  NULL → the "a manually closed OC is not reopened" test (4.5) must fail.
- [ ] 4.16 [P] **Mutation target #33** — either the derived-received-zero guard or the linked-
  `borrador` `EXISTS` guard deleted → `409 orden_compra_con_recepciones` test (4.8/4.9) must fail,
  one test per guard; adding `FOR SHARE` to the `EXISTS` read → the anular × confirmar rendezvous
  (4.10) deadlocks.
- [ ] 4.17 [P] **Mutation target #34a** — `.RequireAuthorization(Politicas.GestionDeCatalogo)`
  dropped from any of the five write routes → its own 403-matrix test (4.11) must fail.
- [ ] 4.18 Gate guard: `dotnet ef migrations has-pending-model-changes` clean; zero new files under
  `Migraciones/`; `Politicas.cs` unchanged (`git diff --stat`).
- [ ] 4.19 Run `judgment-day`; fix confirmed issues; re-judge until clean.
- [ ] 4.20 Branch `feat/stage16-slice4-cierre-y-anulacion` off `main` (parent: slice 3); PR; merge
  stacked-to-main.

**Test plan**: cierre happy/blocked (4.4, 4.6); non-reopening (4.5); anulación book-governed rule
(4.7-4.9); the no-lock proof (4.10); authorization matrix (4.11); non-regression (4.12-4.13); 4
mutation targets incl. 34a (4.14-4.17).

**Verify**: `dotnet test --filter FullyQualifiedName~OrdenesCompraCierreYAnulacion|FullyQualifiedName~SuperficieDeAutorizacion`

---

## Slice 5: Lectura (PR 5)

**Start**: slice 4 merged (design lists slice 5 as depending on 3 for the derivation; merged after
4 per the stacked-to-main chain strategy). **Finish**: paginated list + detail read model (per-
artículo cobertura, received-not-ordered, price deviation with honest nulls). **Rollback**: revert
branch — read endpoints disappear, no write-side impact. **Done** = tests green + `judgment-day`
clean round + PR merged.

**Budget note**: pre-authorized split `5a` (paginated list) / `5b` (detail + cobertura +
deviation) if this slice overflows — decision 3 above.

- [ ] 5.1 Same `ContratosDeOrdenDeCompra.cs`: `CoberturaDeArticulo`, `OrdenDeCompraDetalle`,
  `OrdenDeCompraListada`, `PaginaDeOrdenesDeCompra` — per-artículo cobertura list, never a
  fabricated per-line split. *(design.md:176-192, decision 13, `dto-contract-honesty`)*
- [ ] 5.2 Same `ServicioDeOrdenesDeCompra.cs`: `ListarAsync` — `ConstruirQuery` with
  `idProveedor`/`idPuntoVenta`/`estado`/`desde`/`hasta` filters, `ORDER BY fecha_emision DESC,
  id_orden_compra DESC`, `Skip/Take`, `pagina = Math.Max(pagina,1)`, `tamanio =
  Math.Clamp(tamanio,1,200)`. *(design.md:70, 201-204, mutation target #34b)*
- [ ] 5.3 Same file: `ObtenerDetalleAsync` — items + the per-artículo cobertura (statement-2's
  derivation, read-only) + price deviation via `CalculadorDeCompra.
  CalcularCostoEfectivoDesdeItem`, `null` never `0` when `costo_unitario_estimado IS NULL`.
  *(design.md:67-69, decision 14, ordenes-de-compra/spec.md:242-249)*
- [ ] 5.4 Same file: `PreCargarDesdeReposicionAsync` (or the equivalent mapping in `POST /`) —
  accept the reposición list's shape, `FilaDeReposicion.{IdArticulo, Sugerido} →
  {IdArticulo, CantidadPedida}`, filtered by proveedor, excluding `sugerido = null` rows.
  *(design.md:proposal decision 10, ordenes-de-compra/spec.md:264-267)*
- [ ] 5.5 Modify `OrdenesDeCompraEndpoints.cs` — `GET /` (paginated) and `GET /{id}` under
  `OperacionDePos` only (no write policy). *(design.md:301-302)*
- [ ] 5.6 [P] Integration — pagination with `fecha_emision` tied on every row (RelojFijo) ⇒ page 2
  repeats and skips nothing (the `ThenByDescending(o => o.Id)` tiebreaker). *(design.md:70,
  mutation target #34b)*
- [ ] 5.7 [P] Integration — each filter (`idProveedor`/`idPuntoVenta`/`estado`/`desde`/`hasta`)
  with asymmetric seeds — an ignored filter must not silently return extra rows. *(design.md:199,
  mutation target #34b)*
- [ ] 5.8 [P] Integration — sibling OC of the same tenant seeded on every listing/detail test with
  its own items (rule 12c) — a raw `UPDATE` desyncing `estado` to a sentinel must surface the
  sentinel (rule 12a). *(design.md, Testing Strategy; design decision 12)*
- [ ] 5.9 [P] Integration — **projection fidelity**: for every derivation fixture, the stored
  `estado` equals `ProyectorDeEstadoDeOrden.Proyectar(...)` recomputed from the read model's own
  cobertura numbers. *(design.md, Testing Strategy — the raw-ADO/LINQ drift proof)*
- [ ] 5.10 [P] Integration — a price increase between order and invoice is surfaced (`+12%`), not
  blocked. *(ordenes-de-compra/spec.md:251-255)*
- [ ] 5.11 [P] Integration — a never-quoted line reports *no comparable*, never `0`.
  *(ordenes-de-compra/spec.md:257-260, mutation target #34b)*
- [ ] 5.12 [P] Integration — pre-load excludes `sugerido = null` rows, never defaults to `0`; the
  `"Sin proveedor"` bucket cannot pre-load. *(ordenes-de-compra/spec.md:270-278)*
- [ ] 5.13 [P] Integration — `GET /api/reportes/stock/reposicion`'s response shape and figures
  unchanged before/after this stage. *(ordenes-de-compra/spec.md:280-283, reposicion-de-
  stock/spec.md's "byte-identical" scenario)*
- [ ] 5.14 [P] Integration — the offset boundary: a listing sent at the real client `-03:00` (never
  `Z`) asserts both the returned rows and the displayed período. *(decision 13 above)*
- [ ] 5.15 [P] **Mutation target #34b (part 1)** — `.ThenByDescending(o => o.Id)` deleted → the
  tied-fecha pagination test (5.6) must fail.
- [ ] 5.16 [P] **Mutation target #34b (part 2)** — any single `if (filtro is { } x)` conjunct
  deleted → its asymmetric-seed test (5.7) must fail.
- [ ] 5.17 [P] **Mutation target #34b (part 3)** — the `Desvio` null branch replaced with `0` →
  the no-comparable test (5.11) must fail.
- [ ] 5.18 Gate guard: `dotnet ef migrations has-pending-model-changes` clean; zero new files under
  `Migraciones/`.
- [ ] 5.19 Run `judgment-day`; fix confirmed issues; re-judge until clean.
- [ ] 5.20 Branch `feat/stage16-slice5-lectura` off `main` (parent: slice 4); PR; merge
  stacked-to-main.

**Test plan**: pagination + tiebreaker (5.6); filters (5.7); read-model rules 12a/12c (5.8);
projection fidelity (5.9); price deviation incl. honest nulls (5.10-5.11); pre-load (5.12);
stage-13 byte-identity (5.13); offset boundary (5.14); 3 mutation sub-targets (5.15-5.17).

**Verify**: `dotnet test --filter FullyQualifiedName~OrdenesCompraLectura`

---

## Slice 6: Web (PR 6)

**Start**: slice 5 merged. **Finish**: list/detail/draft screens ship with the reception entry
point, the `Reposicion.tsx` "generar OC" action (Admin-gated), and the `Compras.tsx` link.
**Rollback**: revert branch — screens/routes disappear, the API still serves the shape (isolated,
non-retracting per design's Pre-approved degradation). **Done** = tests green + `judgment-day`
clean round + PR merged.

**Budget note**: pre-approved degradation — if this slice overflows, ship list + detail + draft
and drop the `Reposicion.tsx` action; a documented reduction, never silent (decision 3 above).

- [ ] 6.1 Create `src/Ways.Web/src/api/ordenesDeCompra.ts` (+ `.test.ts`) — client + pure mappers;
  `tipos.ts` mirrors the read/write DTOs. *(design.md:353, 385)*
- [ ] 6.2 Create `src/Ways.Web/src/paginas/OrdenesDeCompra.tsx` (+ `.test.tsx`) — route
  `/ordenes-compra`, `RutaProtegida rolesPermitidos={[Vendedor,Supervisor,Admin]}` (the read gate);
  filters + pager. *(design.md:330-333)*
- [ ] 6.3 Create `src/Ways.Web/src/paginas/OrdenDeCompra.tsx` (+ `.test.tsx`) — draft editor +
  detail with cobertura table (`—` never `0` when `null`) + `enviar`/`cerrar`/`anular` actions +
  "Registrar recepción" → `navigate('/compras/nueva?idOrdenCompra=' + id)`. `key={idOrden ??
  'nueva'}` on the subtree; `generacionRef` per fetch; generation bumps before each write;
  post-write refetch has its own `try/catch`; first-line re-entrancy guard + full-window disable
  on `enviar`/`cerrar`/`anular`. *(design.md:334-343, `react-async-state` rules 2,3,6,8,9)*
- [ ] 6.4 Modify `src/Ways.Web/src/paginas/CompraEditor.tsx` — read `idOrdenCompra` from
  `useSearchParams`, pre-fill proveedor/PV/OC and one line per artículo with `Pendiente > 0`;
  `key={idNumerico ?? 'nuevo-' + (idOrdenCompra ?? 's')}`. *(design.md:344-346)*
- [ ] 6.5 Modify `src/Ways.Web/src/paginas/Reposicion.tsx` — per-group "Generar OC" button
  rendered only when `grupo.idProveedor !== null` **and** `useAuth().usuario.rolId === ROL.Admin`;
  posts `filas.filter(f => f.sugerido !== null)` mapped `{IdArticulo, Sugerido} →
  {IdArticulo, CantidadPedida}`; `"Sin proveedor"` renders without the action. *(design.md:347-351,
  decision 16, mutation target #34c)*
- [ ] 6.6 Modify `src/Ways.Web/src/paginas/Compras.tsx` — show the linked OC with a link to it.
  *(design.md:352)*
- [ ] 6.7 Modify `src/Ways.Web/src/App.tsx` — two new routes.
  *(design.md:File Changes)*
- [ ] 6.8 [P] `web-descriptor-tests` — colocated tests for every new pure helper (reposición→OC
  mapper, cobertura formatter, filter builder) and both screens' descriptors. *(design.md:359)*
- [ ] 6.9 [P] Vitest — the `"Sin proveedor"` bucket offers no action; a Supervisor session renders
  no action. *(mutation target #34c)*
- [ ] 6.10 [P] Vitest — a `sugerido === null` row is excluded from the pre-load.
  *(mutation target #34c)*
- [ ] 6.11 [P] Vitest — a double click on `enviar`/`cerrar`/`anular` issues exactly one POST
  (`react-async-state` rule 9).
- [ ] 6.12 [P] Vitest — a stale response is discarded, resolved **inside `act`** (`react-async-
  state` rule 7).
- [ ] 6.13 [P] Vitest — pager disabled at the edges.
- [ ] 6.14 [P] `react-async-state` rule 10 — grep every recovery path added on one screen and
  replicate it in its sibling in the same commit; record the grep evidence in the PR body.
- [ ] 6.15 [P] **Mutation target #34c (part 1)** — the `grupo.idProveedor !== null` branch deleted
  → the "Sin proveedor" no-action test (6.9) must fail.
- [ ] 6.16 [P] **Mutation target #34c (part 2)** — the `rolId === ROL.Admin` branch deleted → the
  Supervisor no-action test (6.9) must fail.
- [ ] 6.17 [P] **Mutation target #34c (part 3)** — the `sugerido !== null` filter deleted → the
  pre-load exclusion test (6.10) must fail.
- [ ] 6.18 Gate guard: `dotnet ef migrations has-pending-model-changes` clean (no schema drift from
  this slice); `npm run build` clean.
- [ ] 6.19 Run `judgment-day`; fix confirmed issues; re-judge until clean.
- [ ] 6.20 Branch `feat/stage16-slice6-web` off `main` (parent: slice 5); PR; merge
  stacked-to-main.

**Test plan**: descriptor tests (6.8); gating branches (6.9); pre-load exclusion (6.10); double-
click guard (6.11); stale-response discard (6.12); pager edges (6.13); 3 mutation sub-targets
(6.15-6.17).

**Verify**: `npm run test -- OrdenesDeCompra`

---

## Review Workload Forecast

| Field | Value |
|-------|-------|
| Estimated changed lines | ~2 540 total (6 slices: 500/410/470/320/360/480) |
| 400-line budget risk | High — slices 1, 2, 3 and 6 sit at or above the cap on the estimate alone; the programme's own record (stages 13-15 came in 1.5-3x their naive estimate from test depth) says they *will* exceed it |
| Chained PRs recommended | Yes |
| Suggested split | 6 PRs, stacked-to-main, per the Suggested Work Units table above |
| `size:exception` anticipated | No — the four pre-authorized cut points (decision 3 above) absorb it; a **7-8 PR outturn is the expected case**, not the exception |
| Delivery strategy | `auto-chain` (already resolved, `state.yaml`) |
| Chain strategy | `stacked-to-main` |
| Decision needed before apply | No — already resolved |

Decision needed before apply: No
Chained PRs recommended: Yes
Chain strategy: stacked-to-main
400-line budget risk: High

Per-slice budget risk: 1 **High (~500)** · 2 **High (~410)** · 3 **High (~470)** · 4 Medium
(~320) · 5 Medium (~360) · 6 **High (~480)**. Overflow is expected to come from test depth, not
scope creep: slice 1 carries 4 constraint families + the ordering trap + 9 mutation targets;
slice 3 carries derivation fidelity + two race families + fault points + 13 mutation targets;
slice 6 carries two full screens + descriptor tests + the gating matrix. **Never degraded**: the
projection's lock-then-re-read-then-update discipline, the zero-extra-statements proof, the
`_numero` ordering-trap assertion, and the manual-close short-circuit — those split, never trim.
