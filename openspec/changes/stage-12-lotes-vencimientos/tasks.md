# Tasks: Stage 12 — Lotes y vencimientos (FEFO)

## Orchestrator Decisions Recorded This Phase

1. **15 slices, 15 PRs, stacked-to-main** — design.md's refinement of the
   proposal's 11-slice plan (the 8|9 sale cut: FEFO planning is pure/testable
   without a transaction, write path needs the concurrency fixture — two
   shapes in one PR is how a 400-line budget becomes 700). Merge order
   follows the dependency graph at the bottom of this file, not strict
   numeric order — four fronts run in parallel after slice 3.
2. **DB CHANGE GATE is APPROVED-WITH-AMENDMENTS** (`state.yaml`). Slice 1 is
   the **only** slice that may touch schema/migration/EF model — every other
   slice carries a gate-guard task (`dotnet ef migrations
   has-pending-model-changes` → no pending changes). If any slice 2-15 finds
   itself needing a schema change, STOP and reopen the gate.
3. **Slice 1 is a declared `size:exception`** (design decision 21): the gate
   contract names **one** migration; a migration cannot be split across
   merged PRs without one PR mutating an artifact the other already merged.
4. **Format reference**: this file follows the archived
   `2026-08-12-stage-11-exportacion-reportes/tasks.md` structure — per-slice
   Start/Finish/Rollback, hierarchical task numbering, a Verify line, and a
   closing Review Workload Forecast.
5. **`judgment-day` runs once per slice**, on that slice's diff, before its
   PR — per `protocolo-pr-solo-dev`. Fifteen independent rounds.
6. **Mutation-proof-tests placement** (per design.md's mutation-target
   table, not duplicated across slices): `ControlEfectivo`'s `&&` → slice 2;
   the per-clave `Where` filter → slice 2; `OrdenarFefo`'s
   `OrderByDescending(EsSinIdentificar)` → slice 2 (Domain) + slice 7
   (end-to-end); `ConstruirClavesOrdenadas`'s `NULLS FIRST` `ThenBy` pair →
   slice 10 (transfer side) with the joint checkout-vs-transfer deadlock
   proof landing in slice 10 once both write sites exist; the per-lot
   insufficiency `if` → slice 6; the reconciliation zero-residue guard →
   slice 4; the vencimientos timezone conversion → slice 13; the venta
   snapshot assignment → slice 8; the `/stock/decomiso` policy line →
   slice 11.
7. **Cross-slice concurrency dependency, called out explicitly**: the
   "checkout vs. reverse transfer" deadlock test named in both slice 8's and
   slice 10's design test plans needs `ServicioDeVentas`'s per-lot write
   (slice 8) **and** `ServicioDeStock`'s transfer write (slice 10) to exist
   simultaneously. Slice 8 ships the lock-order proof and the mutation
   target on its own side; the **joint** test is placed in slice 10 (task
   10.12), which is downstream of slice 8 in every valid merge order.
8. **`db-error-backstops` placement**: the `ux_lotes_articulo_codigo` race
   (SQLSTATE `23505` asserted) is proven twice — once in slice 1 as a raw
   backstop-translation test, once end-to-end in slice 3's admin-alta route
   and again in slice 5's purchase-confirm get-or-create race. The
   `ux_lotes_sin_identificar` exemption (a schema backstop with no live
   route racing it) gets its documented raw-SQL proof in slice 1 per
   design decision 5.

---

## Suggested Work Units

| Unit | Goal | Branch | Depends on | ~Lines |
|---|---|---|---|---|
| 1 | Schema: `lotes`, `stock_lotes`, 6 columns, 2 enum values, RLS, EF configs, tenant filter, `ManejadorDeErrores` | `feat/stage12-slice1-esquema` | none | **~430 ⚠ size:exception** |
| 2 | Activación: 2 parametros, `ReglaDeLotes`, batched parametro read | `feat/stage12-slice2-activacion` | 1 | ~260 |
| 3 | `ServicioDeLotes` (get-or-create, sin-identificar, saldos) + lot routes | `feat/stage12-slice3-servicio-de-lotes` | 1, 2 | ~300 |
| 4 | Reconciliación (`reclasificacion` pair, activation hooks, admin re-run) | `feat/stage12-slice4-reconciliacion` | 3 | ~330 |
| 5 | Recepción (compra draft/confirm, per-lot movement) | `feat/stage12-slice5-recepcion` | 3 | ~330 |
| 6 | Compra anulación (per-lot refusal) | `feat/stage12-slice6-compra-anulacion` | 5 | ~180 |
| 7 | Venta FEFO planning (decide phase) | `feat/stage12-slice7-venta-plan-fefo` | 3 | ~280 |
| 8 | Venta escritura (per-lot writes, snapshot, exact anulación) | `feat/stage12-slice8-venta-escritura` | 7 | ~330 |
| 9 | NCX lot rules + expired-lot warning | `feat/stage12-slice9-ncx` | 8 | ~200 |
| 10 | Transferencias (lot travels, lock order, refusals) | `feat/stage12-slice10-transferencias` | 3 | ~360 |
| 11 | Ajuste lot-aware + `POST /stock/decomiso` | `feat/stage12-slice11-ajuste-decomiso` | 10 | ~280 |
| 12 | Conteo per lot | `feat/stage12-slice12-conteo` | 11 | ~250 |
| 13 | Vencimientos report + export + resumen | `feat/stage12-slice13-vencimientos` | 1 | ~320 |
| 14 | Web operación (POS picker, reception lot input) | `feat/stage12-slice14-web-operacion` | 5, 8 | ~400 |
| 15 | Web back-office (Vencimientos screen, toggles, lot columns) | `feat/stage12-slice15-web-backoffice` | 12, 13 | ~400 |

**Parallelism.** Everything blocks on `1 → 2 → 3`. After 3 merges, four
fronts are independent because they live in **different files**:
`[4]` (`ServicioDeLotes`), `[5 → 6]` (`ServicioDeCompras`),
`[7 → 8 → 9]` (`ServicioDeVentas`), `[10 → 11 → 12]` (`ServicioDeStock`).
`[13]` needs only slice 1 and can run from the start of that wave. `14`
needs `5 + 8`; `15` needs `12 + 13`. Conflict surface between fronts:
`Contratos.cs` per capability (disjoint files) and `StockEndpoints.cs`
(one route line per slice). Only 14 and 15 touch `App.tsx`/`Layout.tsx`.

---

## Slice 1: Esquema (PR 1) — `size:exception`

**Start**: `main`. **Finish**: `lotes`/`stock_lotes` exist with RLS, six
additive columns on `movimientos_stock`/`articulos`/
`items_comprobante_venta`/`items_comprobante_compra`, two `motivo_stock`
enum values, EF configs named per the gate contract, `ManejadorDeErrores`
maps the new `23505` target. **No writer touches these columns yet.**
**Rollback**: revert the branch — every element is additive/nullable, zero
rows rewritten.

- [x] 1.1 Create `src/Ways.Domain/Stock/Lote.cs`: `EntidadTenant`,
  `IdArticulo`, `Codigo`, `FechaVencimiento`, `EsSinIdentificar`. *(proposal
  gate §A)*
- [x] 1.2 Create `src/Ways.Domain/Stock/StockLote.cs`: PK-only,
  `IdArticulo`/`IdPuntoVenta`/`IdLote`/`IdTenant`/`Cantidad`, no audit
  columns — the `Stock` precedent. *(proposal gate §B)*
- [x] 1.3 Modify `src/Ways.Domain/Stock/MotivoStock.cs`: add `Decomiso`,
  `Reclasificacion`.
- [x] 1.4 Modify `src/Ways.Domain/Stock/MovimientoStock.cs`: add
  `int? IdLote`.
- [x] 1.5 Modify `src/Ways.Domain/Articulos/Articulo.cs`: add
  `bool ControlaLote` (default `false`).
- [x] 1.6 Modify `src/Ways.Domain/Ventas/ItemComprobanteVenta.cs`: add
  `int? IdLote` (snapshot field, no re-derivation).
- [x] 1.7 Modify `src/Ways.Domain/Compras/ItemComprobanteCompra.cs`: add
  `string? CodigoLote`, `DateOnly? FechaVencimiento`, `int? IdLote`.
- [x] 1.8 Create `.../Configuraciones/LoteConfiguration.cs`: `pk_lotes`;
  alternate key `ux_lotes_id_articulo_tenant`; `fk_lotes_tenant`,
  `fk_lotes_articulo`; `ck_lotes_vencimiento_segun_tipo`,
  `ck_lotes_codigo_no_vacio`; unique partial indexes
  `ux_lotes_articulo_codigo` (`WHERE deleted_at IS NULL`) and
  `ux_lotes_sin_identificar` (`WHERE es_sin_identificar AND deleted_at IS NULL`);
  `ix_lotes_tenant`, `ix_lotes_articulo`, `ix_lotes_vencimiento`. All names
  explicit — EF's PascalCase default is always overridden.
- [x] 1.9 Create `.../Configuraciones/StockLoteConfiguration.cs`:
  `pk_stock_lotes (id_articulo, id_punto_venta, id_lote)`;
  `fk_stock_lotes_tenant`, `fk_stock_lotes_lote` (composite against
  `ux_lotes_id_articulo_tenant`), `fk_stock_lotes_punto_venta`;
  `ix_stock_lotes_tenant`, `ix_stock_lotes_punto_venta`,
  `ix_stock_lotes_lote`. **No CHECK on `cantidad`** (deliberate — negative
  balance is legal at the counter, `stock` parity).
- [x] 1.10 Modify `ArticuloConfiguration.cs`: `controla_lote boolean NOT
  NULL DEFAULT false`; `ix_articulos_controla_lote (id_tenant) WHERE
  controla_lote AND deleted_at IS NULL`.
- [x] 1.11 Modify `MovimientoStockConfiguration.cs`: `id_lote integer NULL`;
  `fk_movimientos_stock_lote (id_lote, id_articulo, id_tenant)` against
  `lotes`'s alternate key; `ix_movimientos_stock_lote`.
- [x] 1.12 Modify `ItemComprobanteVentaConfiguration.cs`: `id_lote integer
  NULL`; `fk_items_comprobante_venta_lote` (3-column composite, gate
  amendment 2, `MATCH SIMPLE`); `ix_items_comprobante_venta_lote`.
- [x] 1.13 Modify `ItemComprobanteCompraConfiguration.cs`: `codigo_lote text
  NULL`, `fecha_vencimiento date NULL`, `id_lote integer NULL`;
  `fk_items_comprobante_compra_lote` (gate amendment 2);
  `ix_items_comprobante_compra_lote`;
  `ck_items_comprobante_compra_lote_input`.
- [x] 1.14 Modify `WaysDbContext.cs`: `DbSet<Lote>`, `DbSet<StockLote>`,
  `AplicarFiltroDeTenantEnStockLote` mirroring
  `AplicarFiltroDeTenantEnStock` (decision 20 — `stock_lotes` has no audit
  columns, so it needs the hand-rolled filter, not `EntidadTenant`'s global
  one), register both configurations.
- [x] 1.15 Create `.../Migraciones/…_LotesYVencimientosEtapa12.cs`:
  statement order per design — `AlterDatabase` (enum diff) first,
  `CreateTable lotes`, `CreateTable stock_lotes`, `AddColumn ×6` with FKs +
  indexes, `HabilitarRlsDeTenant("lotes")` +
  `HabilitarRlsDeTenant("stock_lotes")`. **No `Sql()` statement in this
  migration may name `'decomiso'`/`'reclasificacion'`** (PG forbids using an
  `ADD VALUE` enum member in the same transaction that added it).
- [x] 1.16 Modify `src/Ways.Api/Seguridad/ManejadorDeErrores.cs`: `23505` on
  `ux_lotes_articulo_codigo` → `409 lote_duplicado`; document the
  `ux_lotes_sin_identificar` backstop exemption (no live route races it —
  proven by raw SQL only, `pk_stock` precedent). `23503` needs no change
  (the existing `fk_` prefix arm covers the four new FKs).
- [x] 1.17 Update `docs/10-modelo-de-datos.md` §6: add `lotes` and
  `stock_lotes` table entries and the two new `motivo_stock` values, tagged
  `Estado (Etapa 12)` per the doc's existing annotation convention.
- [x] 1.18 [P] Migration-apply test: fresh testcontainer DB applies
  cleanly; both tables, all six columns, both enum values exist post-`Up()`.
- [x] 1.19 [P] RLS tests ×2 over the **`ways_app`** connection (NOSUPERUSER
  NOBYPASSRLS, `mutation-proof-tests` rule 5): `lotes` and `stock_lotes`
  cross-tenant `SELECT`/`INSERT`/`UPDATE` at statement level, asserting row
  counts for silent 0-row cases and `42501` where an error is raised.
- [x] 1.20 [P] **`db-error-backstops`**: two concurrent inserts racing
  `ux_lotes_articulo_codigo` → SQLSTATE `23505` asserted, backstop
  translates to the existing row, exactly one `lotes` row survives. *(spec
  lotes-y-vencimientos: The Same Articulo And Codigo Cannot Be Created
  Twice)*
- [x] 1.21 [P] **`db-error-backstops`**, documented exemption: raw-SQL proof
  that `ux_lotes_sin_identificar` fires `23505` on a second
  `es_sin_identificar = true` row for the same articulo — no application
  route races this index directly (get-or-create serializes on
  `ux_lotes_articulo_codigo` first, per design decision 5), so the proof is
  a direct constraint test, not an end-to-end race.
- [x] 1.22 [P] `23503` regression ×4 FKs (`fk_movimientos_stock_lote`,
  `fk_items_comprobante_venta_lote`, `fk_items_comprobante_compra_lote`,
  `fk_stock_lotes_lote`): a lot referenced with a mismatched articulo is
  rejected. *(spec stock: A Movement Referencing A Foreign Articulo's Lot
  Is Unrepresentable)*
- [x] 1.23 [P] CHECK constraint tests: `ck_lotes_vencimiento_segun_tipo`,
  `ck_lotes_codigo_no_vacio`, `ck_items_comprobante_compra_lote_input`.
  *(spec lotes-y-vencimientos: A Blank Codigo Is Unrepresentable, A Dated
  Lot Without An Expiry Is Unrepresentable; spec comprobantes-compra: A Lot
  Code Without An Expiry Is Unrepresentable)*
- [x] 1.24 Gate guard: `dotnet ef migrations has-pending-model-changes` →
  no pending changes after this migration (confirms the EF model matches
  exactly the gate-approved schema, nothing more).
- [x] 1.25 Run `judgment-day` on the slice diff; fix confirmed issues;
  re-judge until clean. *(APPLY-RUN NOTE: round 1 double REJECT — judge A:
  BLOCKER, duplicate `HasIndex(IdTenant)` metadata slot silently dropped
  `ix_articulos_tenant` from the model; judge B: BLOCKER, `Down()` threw
  `NotSupportedException` on enum-label removal, proven live on postgres:17,
  plus MAJOR, the two EF-filter tests were vacuous under the RLS confound —
  proven by mutation. Fix batch bf45913/d0be19c/b7aa957 with per-fix mutation
  evidence; round 2 double APPROVE, including a fresh snapshot-drift mutation
  probe on the partial index filter.)*
- [x] 1.26 Branch `feat/stage12-slice1-esquema` off `main`; PR **flagged
  `size:exception`** per design decision 21; merge stacked-to-main.

**Test plan**: migration apply, RLS ×2, `23505` backstop ×2, `23503`
regression ×4, CHECK ×3.

**Verify**: `dotnet test --filter FullyQualifiedName~LotesMigracion|FullyQualifiedName~LotesRls`

---

## Slice 2: Activación (PR 2)

**Start**: slice 1 merged. **Finish**: `ReglaDeLotes` exists as a pure,
DB-free Domain rule; `ServicioDeVentas`'s parametro read is batched
2→1 (three keys, one query); the query-count guard reads `16`.
**Rollback**: revert the branch — no schema, no writer wired to lots yet.

- [x] 2.1 Modify `src/Ways.Domain/Catalogos/ParametroConocido.cs`: add
  `LotesHabilitado` (`bool`, default `false`), `DiasAlertaVencimiento`
  (`int`, default `30`), both registered in `PorClave`. *(no migration —
  stage-10 pattern)*
- [x] 2.2 Create `src/Ways.Domain/Stock/ReglaDeLotes.cs`: `SaldoDeLote`
  record, `EstadoDeVencimiento` enum (`Vencido`/`PorVencer`/`Vigente`/
  `SinFecha`), `CodigoSinIdentificar` constant, `ControlEfectivo`,
  `OrdenarFefo`, `ElegirFefo`, `DerivarCodigo`, `Clasificar`, `EstaVencido`
  — pure, no `IWaysDbContext`. *(design decision 1)*
- [x] 2.3 Modify `src/Ways.Application/Ventas/ServicioDeVentas.cs`:
  `ResolverParametrosDeVentaAsync` — single `WHERE clave IN (...)` query for
  `tolerancia_pago`/`vuelto_maximo`/`lotes_habilitado`; per-clave
  `.Where(p => p.Clave == c.Clave)` filter **before** delegating to
  `ResolucionDeParametros.Resolver` (design decision 2 — the named mutation
  target: `Resolver` filters by PV but not by clave, so a naive multi-key
  candidate set corrupts cross-parametro).
- [x] 2.4 [P] Domain unit suite (`PoliticaDeRoles` pattern, no DB, 5
  facts): `ControlEfectivo` truth table *(spec lotes-y-vencimientos:
  Effective Lot Control Is `controla_lote` AND `lotes_habilitado`)*;
  `OrdenarFefo` with a sin-identificar lot + two dated lots + a tie on
  expiry, asserting the **id sequence** *(spec: FEFO Is The Server-Computed
  Default, "The sin-identificar lot is offered before every dated lot")*;
  `ElegirFefo` returns `null` when every balance is `≤ 0`; `DerivarCodigo`
  ISO-formats the expiry *(spec: "A lot is created with a server-derived
  codigo")*; `Clasificar` at all four boundaries (`hoy-1`, `hoy`,
  `hoy+dias`, `hoy+dias+1`, `null`).
- [x] 2.5 [P] **Mutation target**: `candidatos.Where(p => p.Clave ==
  c.Clave)` — delete it; two-key test where `tolerancia_pago` has a
  PV-scoped row and `vuelto_maximo` only an empresa row, both values
  asserted correctly (must fail once deleted). *(spec parametros-operativos:
  The Batched Query Still Resolves Punto De Venta Overrides Correctly;
  mutation-proof-tests)* Record mutation evidence in the PR body.
  *(APPLY-RUN NOTE: mutation applied — `.Where(p => p.Clave == c.Clave)`
  replaced by unfiltered `candidatos` in `ResolverParametrosDeVentaAsync` —
  `ElCheckoutResuelveVueltoMaximoDeEmpresaAunConUnaFilaDePuntoDeVentaDeOtraClave`
  went RED (`500 error_interno`: the lone PV-scoped `tolerancia_pago` row
  leaks into `lotes_habilitado`'s resolution too, `JsonException` on `bool`
  deserialization of `"15"`); reverted, same test back to GREEN alongside
  the full `VentasCheckoutTests` suite (27/27).)*
- [x] 2.6 [P] Query-count test: the batched parametro read issues exactly
  **1** query for the three keys. *(spec parametros-operativos: A Single
  Batched Query Resolves All Three Keys)*
- [x] 2.7 [P] `ContadorDeComandos` regression: the existing constant moves
  deliberately `17 → 16`. *(spec lotes-y-vencimientos: Module Off Issues
  One Fewer Parametro Round-Trip Than The Baseline)*
- [x] 2.8 [P] `parametros-operativos` scenarios: `lotes_habilitado` resolves
  `false` with no configured row; an empresa-level row turns the module on
  for every PV of that empresa; `dias_alerta_vencimiento` defaults to `30`;
  a PV-level override wins.
- [x] 2.9 Gate guard: `dotnet ef migrations has-pending-model-changes` → no
  pending changes.
- [x] 2.10 Run `judgment-day`; fix; re-judge until clean. *(APPLY-RUN NOTE:
  round 1 — judge B APPROVE with 6/6 mutations killed and strict-equality
  guards confirmed; judge A REJECT with 1 MAJOR: the spec's timezone scenario
  said `fecha == hoy` classifies `vencido`, contradicting design.md AND the
  scenario's own naive-UTC intent. Orchestrator decision 13: expiry date is
  inclusive, `vencido` = `fecha < hoy` strict — SPEC amended (cc1d10f), code
  untouched. Round 2 — judge A APPROVE, blob-hash-verified no code drift.)*
- [x] 2.11 Branch `feat/stage12-slice2-activacion` off `main` (parent:
  slice 1); PR; merge stacked-to-main.

**Test plan**: Domain suite (2.4), parametro-filter mutation (2.5), query
count (2.6), `ContadorDeComandos` regression (2.7), parametros-operativos
suite (2.8).

**Verify**: `dotnet test --filter FullyQualifiedName~ReglaDeLotes|FullyQualifiedName~ResolverParametrosDeVenta`

---

## Slice 3: Servicio De Lotes (PR 3)

**Start**: slices 1+2 merged. **Finish**: `ServicioDeLotes` resolves lots
get-or-create and reads bounded saldos; `GET/POST /api/stock/lotes` live.
**Rollback**: revert the branch — no other write site depends on this yet.

- [x] 3.1 Create `src/Ways.Application/Stock/ServicioDeLotes.cs`:
  `ResolverOCrearAsync` — `INSERT ... ON CONFLICT (ux_lotes_articulo_codigo)
  DO UPDATE SET updated_at = lotes.updated_at ... RETURNING id_lote,
  fecha_vencimiento` (no-op `DO UPDATE`, never `DO NOTHING` — design
  decision 4); null `codigo` derives via `ReglaDeLotes.DerivarCodigo`;
  immutability check (`fecha_vencimiento` mismatch → `409
  lote_vencimiento_incompatible`) evaluated **under the returned lock**, no
  retry-read loop. `ResolverSinIdentificarAsync`. `LeerSaldosAsync` — one
  query, `lotes ⟕ stock_lotes` bounded to `cantidad <> 0 OR
  es_sin_identificar OR id_lote IN (@lotesPedidos)`. `ListarAsync`,
  `CrearAsync` (admin alta). *(APPLY-RUN NOTE: `ResolverOCrearAsync`/
  `ResolverSinIdentificarAsync` came out `static` — CA1822 (error in
  `.editorconfig`) forces it, same shape as
  `ServicioDeStock.InsertarMovimientoStockAsync`/`UpsertStockAsync`; callers
  from slices 5/7/8/10 invoke them as `ServicioDeLotes.ResolverOCrearAsync(...)`.
  `IWaysDbContext` gained `DbSet<Lote> Lotes`/`DbSet<StockLote> StockLotes`
  — Slice 1 mapped them in `WaysDbContext` but never exposed them past
  Infrastructure; this is the first Application consumer, no migration.)*
- [x] 3.2 Extend `src/Ways.Application/Stock/Contratos.cs`:
  `SolicitudDeLote`, `LoteListado` (with `Sugerido`).
- [x] 3.3 Modify `src/Ways.Api/Endpoints/StockEndpoints.cs`: `GET
  /api/stock/lotes?idPuntoVenta&idArticulo` (`OperacionDePos`, carries
  `sugerido` per `ElegirFefo`); `POST /api/stock/lotes`
  (`GestionDeCatalogo`, admin alta, `409 lote_duplicado`).
- [x] 3.4 Modify `ServicioDeLotes.CrearAsync`: reject a client-supplied
  `codigo` equal to `ReglaDeLotes.CodigoSinIdentificar` with `400
  codigo_de_lote_reservado`.
- [x] 3.5 [P] **`db-error-backstops`**: two concurrent `POST
  /api/stock/lotes` with the same código ⇒ exactly one `201` + one `409
  lote_duplicado`, SQLSTATE `23505` asserted before the mapping runs.
  *(APPLY-RUN NOTE: two tests — `DosCrearAsyncConcurrentesDelMismoCodigoChocanConSqlstate23505AntesDelMapeo`
  calls `ServicioDeLotes.CrearAsync` directly from two independent
  `WaysDbContext`s and asserts the raw `DbUpdateException`/`PostgresException`
  (SqlState `23505`, ConstraintName `ux_lotes_articulo_codigo`) BEFORE
  `ManejadorDeErrores` (HTTP-only) ever runs; `DosPostConcurrentesAApiStockLotesConElMismoCodigoDanExactamenteUnCreadoYUnConflicto`
  races the real endpoint, asserting exactly one `201` + one `409
  lote_duplicado`. `mutation-proof-tests` evidence on the second: temporarily
  deleted the exact-name `ux_lotes_articulo_codigo` arm in
  `ManejadorDeErrores` — the "_codigo" generic `ClasificarUnicidad` fallback
  caught it first and returned `codigo_duplicado`, the exact ordering-trap
  documented in the Slice-1 doc-comment; test went RED
  (`Expected: "lote_duplicado" / Actual: "codigo_duplicado"`); reverted, back
  to GREEN across the full `ServicioDeLotesTests`/`Lotes*`/`ManejadorDeErrores*`
  suite (74/74).)*
- [x] 3.6 [P] Immutability tests: matching-expiry reuse *(spec: "A second
  reception with a matching expiry reuses the lot")*; conflicting-expiry
  refusal → `409 lote_vencimiento_incompatible` *(spec: "A second reception
  with a conflicting expiry is refused")*.
- [x] 3.7 [P] `codigo_de_lote_reservado` test: client supplies
  `codigo = "SIN-IDENTIFICAR"` → `400`.
- [x] 3.8 [P] Sin-identificar idempotence: created once, reused across two
  puntos de venta. *(spec: "The sin-identificar lot is created once and
  reused")* *(APPLY-RUN NOTE: `ResolverSinIdentificarAsync` takes no
  `idPuntoVenta` — the sin-identificar lot is tenant-wide per articulo, not
  PV-scoped (`lotes` has no PV column) — so the test simulates two
  independent write sites of two different PVs calling it sequentially on
  the same raw connection and asserts the same `idLote` both times.)*
- [x] 3.9 [P] `LeerSaldosAsync` bounded-query test: returns lots with
  nonzero balance + explicitly requested lots + the sin-identificar lot;
  excludes a zero-balance, non-requested dated lot. *(APPLY-RUN NOTE: split
  in two — `GetLotesDevuelveSaldoNoCeroYSinIdentificarExcluyendoUnFechadoDeSaldoCeroNoPedido`
  end to end via `GET /api/stock/lotes` (also proves the endpoint wiring and
  the `estado`/`sugerido` projection, dto-contract-honesty); `LeerSaldosAsyncIncluyeUnLoteDeSaldoCeroCuandoFueExplicitamentePedido`
  calls `ServicioDeLotes.LeerSaldosAsync` directly for the "explicitly
  requested" branch, which the picker GET route never exercises (no
  `idsLotePedidos` parameter — only slices 5/7/8/10's writers pass that
  list). `mutation-proof-tests` evidence, three mutations on the bounded
  `WHERE` clause in `LeerSaldosAsync`, each run→RED→revert→GREEN: (1)
  deleted `|| lote.EsSinIdentificar` → sin-identificar-always-included test
  failed (`Expected: 2 / Actual: 1`); (2) deleted `|| idsLotePedidos.Contains(lote.Id)`
  → explicitly-requested test failed (empty collection); (3) widened
  `stockLote != null && stockLote.Cantidad != 0m` to `stockLote != null` →
  the zero-balance dated lot leaked in (`Expected: 2 / Actual: 3`). All
  three reverted, full suite back to green (74/74).)*
- [x] 3.10 [P] Role tests: `POST /api/stock/lotes` rejects a non-Admin
  role.
- [x] 3.11 Gate guard: `dotnet ef migrations has-pending-model-changes` →
  no pending changes. *(Verified clean before AND after the slice's
  changes — no `Migraciones/`/`Configuraciones/` file touched, per the
  DB CHANGE GATE.)*
- [x] 3.12 Run `judgment-day`; fix; re-judge until clean. *(FIX 1 — juez A,
  honestidad documental: doc-comment en `ListarAsync`/`CrearAsync`
  declarando "hoy" UTC-naive interino por diseño en este slice 3, mismo
  criterio que `diasAlertaPorDefecto`. FIX 2 — juez B, mutación
  sobreviviente en `Sugerido` (`ServicioDeLotes.cs:162`): nuevo test con
  ≥2 lotes fechados de vencimientos distintos + sin-identificar,
  `sugerido` assertado en las CUATRO filas; mutación aplicada→RED→
  revertida→GREEN. FIX 3 — juez B, cobertura de `estado` vía HTTP con
  vencimientos fijos lejanos (`2020-01-15` vencido, `2099-12-31` vigente),
  independiente de la hora de corrida. FIX 4 — juez B, blind spot: test
  del código server-derivado (`POST /api/stock/lotes` sin `codigo`).
  DEUDA MENOR ANOTADA (no en este batch): los caminos de fallo
  `referencia_invalida`/404 de los endpoints de lotes quedan sin test
  dedicado — patrón ya cubierto por suites hermanas del repo
  (`LotesBackstopTests` y equivalentes de otros stocks); no bloquea el
  merge de este slice.)*
- [x] 3.13 Branch `feat/stage12-slice3-servicio-de-lotes` off `main`
  (parent: slices 1+2); PR; merge stacked-to-main. *(APPLY-RUN NOTE: round 1
  double REJECT — judge A: MAJOR, missing honesty doc-comment on the
  UTC-naive `hoy` behind `LoteListado.Estado`; judge B: MAJOR, the
  `Sugerido = last-of-order` mutation survived because the only
  multi-candidate test made first and last coincide, plus `Estado` and
  derived-código had zero HTTP coverage. Fix batch f21f408/6dee921; round 2
  double APPROVE with all three re-mutations RED→revert→GREEN.)*

**Test plan**: race backstop (3.5), immutability ×2 (3.6), reserved-código
(3.7), sin-identificar idempotence (3.8), bounded-query (3.9), role (3.10).

**Verify**: `dotnet test --filter FullyQualifiedName~ServicioDeLotes`

---

## Slice 4: Reconciliación (PR 4)

**Start**: slice 3 merged. **Finish**: activation reconciles pre-existing
stock into the sin-identificar lot via a net-zero `reclasificacion` pair,
idempotently; `POST /api/stock/lotes/reconciliacion` re-runs it on demand.
**Rollback**: revert the branch — the trigger hooks are the only coupling
point, both additive.

- [x] 4.1 Extend `ServicioDeLotes.cs`: `ReconciliarAsync(idArticulo?,
  idPuntoVenta?)` — one transaction **per pair**, ascending: (1)
  `ResolverSinIdentificarAsync` (lotes before stock, decision 3);
  (2) `BloquearYCrearSiFaltaStockAsync` (aggregate lock first); (3)
  `SELECT COALESCE(SUM(cantidad),0) FROM stock_lotes ... ORDER BY id_lote
  FOR UPDATE`; (4) `residuo = agregado - sumaLotes`; (5) `residuo == 0` ⇒
  commit, write nothing; (6) else, two `movimientos_stock` rows
  (`motivo = reclasificacion`, net zero) + `UpsertStockLoteAsync` on the
  sin-identificar lot. `stock` is **never** touched. *(APPLY-RUN NOTE:
  `BloquearYCrearSiFaltaStockAsync` visibility widened `private → internal`
  in `ServicioDeStock.cs` (reused, not duplicated, per the design text
  naming it). Step 3's literal SQL is invalid Postgres — `FOR UPDATE`
  cannot combine with an aggregate in the same `SELECT` ("FOR UPDATE is not
  allowed with aggregate functions") — implemented as a subquery that locks
  rows ascending by `id_lote` first, then sums in the outer query; same
  lock order, valid SQL. Scope resolution (`idArticulo`/`idPuntoVenta`
  both `null`) is resolved in SQL: every `stock` row whose articulo has
  `controla_lote = true` AND whose PV's empresa resolves
  `lotes_habilitado` effective `true`; a broader-than-strictly-necessary
  scope is safe because every already-reconciled pair is a no-op (design
  decisión 13).)*
- [x] 4.2 Modify `src/Ways.Application/Articulos/ServicioDeArticulos.cs`:
  detect `controla_lote false → true` on save; trigger
  `ReconciliarAsync(idArticulo, null)` scoped to every PV of every
  lot-enabled empresa of the tenant. *(APPLY-RUN NOTE: `ControlaLote` added
  to `AltaArticulo`/`EdicionArticulo`/`ArticuloListado` — Contratos.cs had
  no field for it yet, task 4.2's trigger needs a client-settable value to
  detect the flip on. `CrearAsync` sets it without triggering
  reconciliation (a brand-new articulo has no preexisting stock, any run
  would be a no-op by construction). `ServicioDeLotes` injected as a new
  constructor dependency.)*
- [x] 4.3 Modify `src/Ways.Application/Parametros/ServicioDeParametros.cs`:
  detect `lotes_habilitado false → true` on save; trigger
  `ReconciliarAsync(null, ...)` scoped to already-`controla_lote`-flagged
  articulos × that empresa's PVs. *(APPLY-RUN NOTE: flip detected by
  comparing the touched row's raw value before/after `EstablecerAsync`
  (design: Reconciliation — "Scope resolution"), not a hierarchy-resolved
  effective value — matches the two other clave-agnostic call sites
  already in this file.)*
- [x] 4.4 Modify `StockEndpoints.cs`: `POST /api/stock/lotes/reconciliacion`
  (`GestionDeCatalogo`), `SolicitudDeReconciliacion`,
  `ResultadoDeReconciliacion`.
- [x] 4.5 [P] Net-zero proof: reconciliation writes a pair summing to
  zero, `stock.cantidad` unaffected, sin-identificar `stock_lotes.cantidad`
  becomes the residue. *(spec: "Activation reconciles existing stock into
  the sin-identificar lot")*
- [x] 4.6 [P] **Mutation target**: delete the `residuo == 0 ⇒ write
  nothing` guard → the idempotence test (asserting the `movimientos_stock`
  **row count** is unchanged on a second run) MUST fail; revert → green.
  *(spec: "A second reconciliation run is a no-op"; mutation-proof-tests)*
  Record evidence. *(APPLY-RUN NOTE: mutation applied — `if (residuo ==
  0m)` in `ServicioDeLotes.ReconciliarParAsync` replaced by `if (false)`;
  build, filter `UnaSegundaReconciliacionSobreElMismoParEsUnNoOpQueNoDuplicaMovimientos`:
  RED — not via the row-count assertion but earlier, a `500 error_interno`
  (`ck_movimientos_stock_cantidad_no_cero`): the second run tried to write
  a `cantidad = 0` reclasificación row, exactly the row the guard exists to
  prevent — the strongest possible mutation evidence, a real Postgres CHECK
  catching it. Reverted, build, same filter: GREEN; full
  `ReconciliacionTests`: GREEN, 7/7.)*
  *(judgment-day ronda 2 note — juez A CRITICAL, falsa alarma: la mutación
  en vivo que juez A observó (una fila `Reclasificacion` → `Ajuste`) era el
  probe 2(c) de juez B corriendo en paralelo, único mutador autorizado del
  worktree en ese momento — no una fuga real. Clase de falsa alarma ya
  documentada en la memoria del proyecto (revisiones judgment-day corriendo
  en paralelo sobre el mismo worktree pueden observarse mutuamente). En
  ronda 2 la verificación de juez A corre DESPUÉS de juez B para eliminar
  la ventana de colisión.)*
  *(judgment-day ronda 2 note — juez A MINOR, resuelto empíricamente: la
  evidencia de este ítem 4.6 depende del CHECK `ck_movimientos_stock_cantidad_no_cero`
  para caer RED (según lo documentado arriba). El probe de juez B (fix 2,
  evidencia de mutación #1/#2 sobre el filtro `ControlaLote` y el guard de
  `lotes_habilitado` en `ReconciliarAsync`) mostró que, sin ese CHECK, el
  assert de conteo de la línea ~212 (`Assert.Equal(2, ...MovimientosStock
  ...)`) igualmente caza la mutación por sí solo — contó 4 filas en vez de
  2. La aserción de conteo es independiente del CHECK; nota registrada, sin
  cambio de código.)*
- [x] 4.7 [P] Self-heal test: sell into an unreconciled pair (drives the
  sin-identificar lot negative), then reconcile, assert `SUM(stock_lotes) =
  stock.cantidad` afterward. *(APPLY-RUN NOTE: slices 7/8's lot-aware venta
  write path doesn't exist yet on this branch — the test simulates it
  directly, writing a `movimientos_stock` row + decrementing `stock`/
  `stock_lotes(sin-identificar)` by hand, same shape the real write path
  will produce.)*
- [x] 4.8 [P] `motivo`-discrimination test: reconciliation rows always
  `motivo = reclasificacion`, never `ajuste`. *(spec: "Reclasificación
  never uses motivo ajuste")*
- [x] 4.9 [P] Zero-residue-never-violates-CHECK test. *(spec: "A
  zero-cantidad reclasificación row never violates the non-zero CHECK")*
- [x] 4.10 [P] Activation-trigger tests: `controla_lote` flip via
  `ServicioDeArticulos` triggers reconciliation across every PV of
  lot-enabled empresas; `lotes_habilitado` flip via `ServicioDeParametros`
  triggers reconciliation across already-flagged articulos.
- [x] 4.11 Gate guard: `dotnet ef migrations has-pending-model-changes` →
  no pending changes. *(Verified after the slice's changes — no
  `Migraciones/`/`Configuraciones/` file touched, per the DB CHANGE GATE;
  run via `--project src/Ways.Infrastructure --startup-project
  src/Ways.Infrastructure`, `WaysDbContextFactory` design-time factory.)*
- [x] 4.12 Run `judgment-day`; fix; re-judge until clean. *(APPLY-RUN NOTE:
  round 1 double REJECT — judge A: MAJOR undocumented partial-failure
  contract on both flip triggers, plus a CRITICAL that resolved as a FALSE
  ALARM (the live mutation it observed was judge B's own authorized probe
  running concurrently; round 2 was serialized B→A to remove that race);
  judge B: MAJOR, both ReconciliarAsync scope filters survived deletion —
  the trigger tests never seeded negative cases. Fix batch
  ac75137/9b0836b/f40d1f3: contract comments at both call sites + 3 new
  tests (negative-scope, multi-pair exact counts, idPuntoVenta-only branch)
  with per-filter mutation evidence. Round 2 double APPROVE; judge A
  blob-hash-verified ServicioDeLotes.cs unchanged since 32bd1de.)*
- [x] 4.13 Branch `feat/stage12-slice4-reconciliacion` off `main` (parent:
  slice 3); PR; merge stacked-to-main.

**Test plan**: net-zero proof (4.5), idempotence mutation (4.6), self-heal
(4.7), motivo discrimination (4.8), zero-residue CHECK (4.9), trigger ×2
(4.10).

**Verify**: `dotnet test --filter FullyQualifiedName~Reconciliacion`

---

## Slice 5: Recepción (PR 5)

**Start**: slice 3 merged (parallel to 4, 7, 10, 13). **Finish**: a
compra's draft carries lot input untouched; confirm resolves (get-or-create)
lots under the header lock, before the stock loop; per-lot movement and
balance write in the same transaction. **Rollback**: revert the branch.

- [x] 5.1 Modify `src/Ways.Application/Compras/ServicioDeCompras.cs`:
  `CrearAsync`/`ActualizarBorradorAsync` pass `codigo_lote`/
  `fecha_vencimiento` straight through `MaterializarItems` — **no
  resolution at draft time** (replace-set semantics would litter `lotes`
  with never-confirmed rows). *(APPLY-RUN NOTE: `MaterializarItems` gained
  a third parallel list parameter (`IReadOnlyList<LineaDeCompraSolicitada>
  solicitudItems`, same order/index as `lineas`/`calculada`) instead of
  threading `CodigoLote`/`FechaVencimiento` through the Domain
  `LineaDeCompra`/`CalculadorDeCompra` — the calculator is pure arithmetic
  and the task list never names it for this slice; `Contratos.cs` gained
  the two fields on `LineaDeCompraSolicitada` (request) and
  `CodigoLote`/`FechaVencimiento`/`IdLote` on `ItemDeCompra` (response),
  both with a documented one-fate trace per `dto-contract-honesty`.)*
- [x] 5.2 Modify `ServicioDeCompras.cs`: validate `fecha_vencimiento` is
  not in the past on every borrador save/edit → `409
  lote_vencido_en_recepcion` (fires at save time, not only confirm — spec
  ADDED requirement). *(APPLY-RUN NOTE: `ValidarVencimientosDeRecepcion` is
  a pure, DB-free static check called from `CrearBorradorAsync` and
  `ActualizarBorradorAsync` before any read — deliberately unconditional on
  `controla_lote`/`lotes_habilitado` since the CHECK constraint
  `ck_items_comprobante_compra_lote_input` doesn't gate on effectiveness
  either and the spec scenario doesn't condition the rejection on it.)*
- [x] 5.3 Modify `ServicioDeCompras.cs`: `EjecutarConfirmarAsync` gains a
  resolution block between the item read (under the header lock) and the
  stock loop — `lotes` before `stock`, decision 3 — `ORDER BY id_articulo,
  codigo_lote` ascending, `servicioDeLotes.ResolverOCrearAsync` per
  lot-effective item, `item.IdLote` frozen. The stock loop reorders
  `OrderBy(IdArticulo).ThenBy(IdLote)` and writes both caches per line.
  *(APPLY-RUN NOTE: `ServicioDeLotes.ResolverOCrearAsync` is invoked
  STATICALLY — `ServicioDeLotes.ResolverOCrearAsync(...)`, no injected
  instance — same criterion as the task 3.1 apply-run note; `ServicioDeLotes`
  itself was NOT modified, consumed as-is from `main`. The resolution loop
  also re-checks `EstaVencido` at confirm time (defense-in-depth alongside
  5.2's save-time check, per the design pseudocode) and throws `400
  lote_requerido` when a lot-effective item carries no `fecha_vencimiento`
  at all (not explicitly named by 5.5-5.12 but present in the design
  pseudocode; covered by an extra test, see 5.6-5.8 note). `item.IdLote`
  mutations are flushed via one extra `db.SaveChangesAsync(ct)` right after
  the resolution loop, before the stock loop reads them in memory for
  ordering — required because `items` is EF-tracked and nothing else in
  `EjecutarConfirmarAsync` previously called `SaveChangesAsync`.
  `InsertarMovimientoStockAsync` gained a required `int? idLote` parameter
  (shared by confirm AND anulación); the anulación call site
  (`EjecutarAnulacionAsync`, Slice 6's territory) passes `idLote: null`
  explicitly with a doc-comment — lot-aware anulación is task 6.1, out of
  this slice's scope. Added `UpsertStockLoteAsync` (same
  `INSERT ... ON CONFLICT DO UPDATE ... RETURNING` shape as `UpsertStockAsync`)
  and `AgregarParametroNulo` (first nullable raw-SQL param this class ever
  sent).)*
- [x] 5.4 Modify `ServicioDeCompras.cs`: `EsLoteEfectivo` reads
  `controla_lote` per articulo (reused from the existing `costo_nominal`
  context read) plus one parametro resolution — both outside the checkout
  round-trip budget. *(APPLY-RUN NOTE: implemented as an inline filter
  (`items.Where(i => ReglaDeLotes.ControlEfectivo(...))`) inside the 5.3
  resolution block rather than a standalone `EsLoteEfectivo` method — no
  existing `costo_nominal` articulo read survives to confirm time in this
  codebase (`ResolverContextoAsync`'s `articuloPorId` is a draft-time-only
  local, discarded before confirm), so this is a FRESH
  `db.Articulos.Where(a => idsArticulo.Contains(a.Id))` read at confirm,
  plus `ResolverLotesHabilitadoAsync` (new helper, same
  `ResolucionDeParametros.Resolver` pattern as
  `ServicioDeLotes.ResolverDiasAlertaAsync`) for the empresa's
  `lotes_habilitado`. Both correctly sit outside `ContadorDeComandos`'s
  budget — `ComprasAnulacionYConcurrenciaTests`'s existing
  `ConfirmarEmiteUnaCantidadConstanteDeConsultasIndependienteDeLaCantidadDeItems`
  test (constant-count, not a fixed ceiling) still passes unmodified.)*
- [x] 5.5 [P] Invariant test: `stock_lotes.cantidad = SUM(movimientos with
  that lot)` holds after a compra. *(spec lotes-y-vencimientos: Stock Lotes
  Balance And Its Two Invariants — compra leg)* *(APPLY-RUN NOTE:
  `StockLotesCantidadEsLaSumaDeSusMovimientosTrasDosCompras` — two
  confirmed compras of the SAME lot (30 + 20 units) so the SUM check is
  non-trivial, not a single-movement coincidence.)*
- [x] 5.6 [P] Get-or-create-and-freeze test. *(spec comprobantes-compra:
  "Confirmar get-or-creates a lot and freezes it onto the item")*
  *(APPLY-RUN NOTE: `ConfirmarGetOrCreaUnLoteYLoCongelaSobreElItem`. Also
  added `ConfirmarRechazaUnItemLoteEfectivoSinFechaDeVencimientoConLoteRequerido`
  — not named by the slice's test plan but exercises the `lote_requerido`
  guard task 5.3 implements per the design pseudocode; asserts `400` and
  full rollback (borrador state, zero movimientos).)*
- [x] 5.7 [P] Reuse-existing-lot test. *(spec: "Confirmar reuses an
  existing lot with a matching expiry")* *(APPLY-RUN NOTE:
  `ConfirmarReusaUnLoteExistenteConVencimientoCoincidente` — the "existing
  lot" is a REAL prior reception (a first confirmed compra), not a raw-SQL
  seed, so the reuse path is exercised exactly as production traffic would
  hit it.)*
- [x] 5.8 [P] Conflicting-expiry refusal → `409
  lote_vencimiento_incompatible`. *(spec: "Confirmar rejects a conflicting
  expiry for an existing codigo")* *(APPLY-RUN NOTE:
  `ConfirmarRechazaUnVencimientoEnConflictoParaElMismoCodigo` — asserts full
  rollback too: the second compra stays `borrador`, `IdLote` stays null,
  zero movimientos, and the `lotes` table still shows exactly one row for
  that código (the conflicting insert never landed).)*
- [x] 5.9 [P] **`db-error-backstops`**: two concurrent confirms racing the
  same `(articulo, codigo_lote)` → SQLSTATE `23505` asserted, backstop
  resolves both confirms to the same lot. *(spec: "A concurrent
  get-or-create race resolves to one lot via the 23505 backstop")*
  *(APPLY-RUN NOTE — SPEC INACCURACY FOUND AND DOCUMENTED, code untouched:
  ran the literal race — `DosConfirmacionesConcurrentesSobreElMismoCodigoDeLoteResuelvenAlMismoLote`,
  two independent confirms, two independent connections, same
  `(articulo, codigo_lote)` — against real Postgres. Observed: BOTH
  confirms return `200 OK`, NO exception of any kind surfaces, both resolve
  to the SAME `id_lote`, `stock_lotes.cantidad` sums correctly (20). This
  contradicts the spec scenario's literal claim ("Postgres raises 23505 for
  the loser, the backstop translates it") but is EXACTLY what design
  decision 4 documents and what Slice 3's own doc-comment on
  `DosCrearAsyncConcurrentesDelMismoCodigoChocanConSqlstate23505AntesDelMapeo`
  already establishes: `ResolverOCrearAsync`'s `ON CONFLICT (id_tenant,
  id_articulo, codigo) WHERE deleted_at IS NULL DO UPDATE ... RETURNING`
  target matches `ux_lotes_articulo_codigo` EXACTLY (`LoteConfiguration.cs`),
  so Postgres resolves the race internally via the INSERT-ON-CONFLICT
  wait-then-retry protocol — no 23505 is ever raised to the client for
  THIS conflict target, by design ("There is therefore no retry-read loop
  in this design, and saying so is more honest than writing one that can
  never fire"). The higher-level business requirement ("both confirms
  succeed against the same lot") IS satisfied — just not via a
  `ManejadorDeErrores` backstop, because none is needed. Recommend the
  orchestrator amend the spec scenario's mechanism wording in a future
  pass (same pattern as slice 2's task 2.10 spec amendment) — flagging
  here rather than editing `specs/comprobantes-compra/spec.md` unilaterally
  since spec amendment authority was reserved to orchestrator decisions in
  this stage's prior slices.)*
- [x] 5.10 [P] Concurrency test, write-site 2: purchase confirm vs.
  checkout of the same articulo+lots, both complete, no `40P01`.
  *(APPLY-RUN NOTE — SCOPE NARROWED, documented: this worktree has only
  slices 1-3 merged, so `ServicioDeVentas` (checkout) is NOT yet lot-aware
  (Slice 7/8, not present) — it cannot race for "the same lot" because it
  never touches `lotes`/`stock_lotes` at all yet. Implemented
  `ConfirmarYCheckoutDelMismoArticuloEnParaleloNuncaDan40P01`: races a
  lot-effective compra confirm (new `lotes`-lock-then-`stock`-lock order)
  against a checkout of the SAME articulo+PV via TODAY's aggregate-only
  checkout path — proves the NEW lock step this slice adds to confirm
  didn't introduce a deadlock against the other major stock writer. No
  `lotes`-vs-`lotes` cross-write-site contention is possible yet by
  construction (checkout doesn't hold that lock), so a rendezvous-forced
  interleave wasn't needed — a plain `Task.WhenAll` against real pool
  timing was sufficient and honest. The genuine "same lot" joint proof is
  deferred to whichever of Slice 7/8 lands, mirroring the note-7 cross-slice
  dependency pattern already used for the checkout-vs-transfer deadlock
  test in this same tasks.md.)*
- [x] 5.11 [P] Draft-capture test: lot input persists without resolving.
  *(spec: "A borrador line captures lot input without resolving it")*
  *(APPLY-RUN NOTE: `UnaLineaDeBorradorCapturaElInputDeLoteSinResolverlo` —
  asserts both the HTTP response AND the raw persisted row, plus zero
  `lotes`/`movimientos_stock` rows for that código.)*
- [x] 5.12 [P] Expired-reception tests. *(spec: "A reception line with a
  past expiry is refused", "A future expiry is accepted")* *(APPLY-RUN
  NOTE: three tests, not two — past-on-create
  (`UnaLineaConVencimientoPasadoEsRechazadaAlGuardarElBorrador`),
  future-on-create
  (`UnaLineaConVencimientoFuturoEsAceptadaAlGuardarElBorrador`), and
  past-on-EDIT
  (`UnaLineaConVencimientoPasadoEsRechazadaAlEditarElBorrador`, spec: "fires
  when the line is saved OR EDITED") — all with FIXED far dates (2020-01-15
  past / 2099-12-31 future, per permanent rule 3), no pinned clock, no
  boundary-day assertion.)*
- [x] 5.13 Gate guard: `dotnet ef migrations has-pending-model-changes` →
  no pending changes. *(APPLY-RUN NOTE: ran via
  `--project src/Ways.Infrastructure --startup-project src/Ways.Infrastructure`
  — `Ways.Api` lacks the `Microsoft.EntityFrameworkCore.Design` package
  reference the tool needs; `Ways.Infrastructure` has it. Output: "No
  changes have been made to the model since the last migration." Confirmed
  clean both by diff (`Migraciones/`/`Configuraciones/` untouched by this
  slice — verified via `git status`) and by the tool itself.)*
- [x] 5.14 Run `judgment-day`; fix; re-judge until clean. *(APPLY-RUN NOTE:
  round 1 double REJECT — judge A: cross-spec contradiction (the sibling
  lotes-y-vencimientos spec still promised a 23505 on the get-or-create
  race after decision 14) and the anulación-of-a-lot-tracked-compra hole
  (aggregate reversed, stock_lotes silently left inflated, 200 OK);
  judge B: CRITICAL — codigo_lote without fecha via the API returned a raw
  500 (CHECK unmapped in ManejadorDeErrores, no app guard) — plus the
  confirm-time expiry re-check had zero test coverage. Fix batch
  7a507dd/28af7da: two-layer 400 lote_input_incompleto (app guard +
  ManejadorDeErrores backstop, proven layer by layer), interim 409
  compra_anulacion_lotes_pendiente guard (slice 6 replaces it), re-check
  test via owner-connection date mutation, sibling spec amended. Round 2
  double APPROVE, serialized B→A.)*
- [x] 5.15 Branch `feat/stage12-slice5-recepcion` off `main` (parent:
  slice 3); PR; merge stacked-to-main.

**Test plan**: invariant 2 (5.5), get-or-create ×3 (5.6-5.8), race backstop
(5.9), confirm-vs-checkout concurrency (5.10), draft capture (5.11),
expired-reception ×2 (5.12).

**Verify**: `dotnet test --filter FullyQualifiedName~RecepcionDeLotes`

---

## Slice 6: Compra Anulación (PR 6)

**Start**: slice 5 merged. **Finish**: anulación reverses the exact lot
snapshot; refusal checks both the aggregate **and** the lot. **Rollback**:
revert the branch.

> Note (judgment-day, slice 5, FIX 4): 6.1 must REPLACE the interim 409
> compra_anulacion_lotes_pendiente guard added at slice-5 judgment-day with
> the exact per-lot reversal.

- [x] 6.1 Modify `ServicioDeCompras.cs`: `EjecutarAnulacionAsync` reorders
  `.OrderBy(m => m.IdArticulo).ThenBy(m => m.IdLote)`, copies
  `original.IdLote`; **two** mandatory checks — aggregate `nueva < 0` AND
  per-lot `nuevaDelLote < 0` — both raise `409
  compra_anulacion_stock_negativo`. *(APPLY-RUN NOTE: replaces the interim
  `compra_anulacion_lotes_pendiente` guard from slice 5 FIX 4 in full — the
  guard block and its now-superseded doc-comments are removed. The per-lot
  `UpsertStockLoteAsync` upsert + check run right after the aggregate
  `UpsertStockAsync` + check, inside the same per-movement loop iteration,
  so a rejection at either level rolls back the whole `EjecutarAnulacionAsync`
  transaction via the outer `using`.)*
- [x] 6.2 [P] **Mutation target**: the per-lot `if (nuevaDelLote < 0m)
  throw` guard — delete the `if` → the per-lot insufficiency test (seeded
  with a **sufficient aggregate** across two lots) MUST fail; revert →
  green. *(spec: "Anulación refused by a lot-level underflow despite a
  sufficient aggregate"; mutation-proof-tests)* Record evidence.
  *(APPLY-RUN NOTE: mutation applied — the per-lot guard's condition
  replaced with `if (false)` in `EjecutarAnulacionAsync`; build, filter
  `AnularUnaCompraEsRechazadaPorUnLoteInsuficienteAunConAgregadoSuficiente`:
  RED — anulación returned `200 OK`/`Anulada` instead of `409
  compra_anulacion_stock_negativo` (the aggregate-only check alone did not
  catch the lot-7 underflow, aggregate stayed at 60 ≥ 0). Reverted, build,
  same filter: GREEN; full `ComprasRecepcionDeLotesTests` +
  `ComprasAnulacionYConcurrenciaTests`: GREEN, 31/31.)*
- [x] 6.3 [P] Exact reversal test. *(spec: "Anulación reverses a
  lot-bearing item into its exact lot")* *(APPLY-RUN NOTE:
  `AnularUnaCompraConLoteResueltoRevierteElLoteExacto` — replaces the slice-5
  interim guard test `AnularUnaCompraConLoteResueltoEsRechazadaSinRevertirNada`
  per the slice-5 judgment-day FIX-4 note; asserts the inverse movimiento's
  `id_lote`, the aggregate `stock.cantidad`, and `stock_lotes.cantidad` for
  the lot all land at zero after anulación.)*
- [x] 6.4 [P] Aggregate refusal regression, unaffected by the lot
  dimension. *(spec: "Anulación refused when the goods were already sold")*
  *(APPLY-RUN NOTE: no new test — the existing
  `ComprasAnulacionYConcurrenciaTests.AnulacionEsRechazadaCuandoLaMercaderiaYaFueVendida`
  (lot-less articulo, pre-dates this slice) exercises this scenario
  end-to-end; re-ran the full suite after 6.1's reorder/copy changes to
  confirm it is byte-identically unaffected — GREEN, 15/15.)*
- [x] 6.5 [P] `costo_nominal`-not-reverted regression (unaffected by this
  slice). *(APPLY-RUN NOTE: no new test — the existing
  `ComprasAnulacionYConcurrenciaTests.CostoNominalNoSeRevierteAlAnular`
  covers this; confirmed GREEN in the same full-suite run as 6.4.)*
- [x] 6.6 Gate guard: `dotnet ef migrations has-pending-model-changes` →
  no pending changes. *(Confirmed via `--project src/Ways.Infrastructure
  --startup-project src/Ways.Infrastructure`: "No changes have been made to
  the model since the last migration." No `Migraciones/`/`Configuraciones/`
  file touched by this slice.)*
- [x] 6.7 Run `judgment-day`; fix; re-judge until clean. *(APPLY-RUN NOTE:
  FIRST CLEAN ROUND-1 of the stage — double APPROVE. Judge B re-ran the 6.2
  mutation plus 4 new probes (inexact reversal, order flip, aggregate-check
  removal, doubled reversal delta) — all caught except the order flip, which
  is the known cross-slice anti-deadlock dependency deferred to slice 8's
  joint proof (slice-5 precedent). Judge A verified ledger-sourced reversal
  (never re-derived), double check via RETURNING with no TOCTOU, and full
  removal of the interim guard. Recorded debt (both judges, SUGGESTION): no
  dedicated mixed-compra (lot + non-lot items) end-to-end anulación test —
  structurally the union of two covered paths, per-line independent loop.)*
- [x] 6.8 Branch `feat/stage12-slice6-compra-anulacion` off `main` (parent:
  slice 5); PR; merge stacked-to-main.

**Test plan**: per-lot insufficiency mutation (6.2), exact reversal (6.3),
aggregate regression (6.4), cost regression (6.5).

**Verify**: `dotnet test --filter FullyQualifiedName~CompraAnulacionLote`

---

## Slice 7: Venta Plan FEFO (PR 7)

**Start**: slice 3 merged (parallel to 4, 5, 10, 13). **Finish**: the
decide phase resolves the FEFO lot per line before the transaction opens;
round-trip budget verified `16 → 17` only when the cart holds a
lot-controlled articulo. **No writes yet** — the transaction shape is
unchanged. **Rollback**: revert the branch.

- [x] 7.1 Modify `src/Ways.Application/Ventas/ServicioDeVentas.cs`:
  `EmitirAsync` decide phase — immediately after `MaterializarItems`
  (`articuloPorId` already loaded), compute `lineasConLote`; if non-empty,
  call `servicioDeLotes.LeerSaldosAsync(puntoVenta.Id, articulos,
  lotesPedidos)` — the one new round trip. *(APPLY-RUN NOTE:
  `lineasConLote` is computed right after `ResolverParametrosDeVentaAsync`
  (immediately follows `MaterializarItems` in the existing sequence, and is
  the only source of `lotesHabilitado` — previously discarded with `_`, now
  consumed); `ServicioDeLotes` is injected as a constructor dependency
  (`servicioDeLotes`, DI-registered since slice 3) rather than invoked
  statically, since `LeerSaldosAsync` is an instance method (unlike
  `ResolverOCrearAsync`/`ResolverSinIdentificarAsync`, which stay static and
  are called directly off the type per the slice 5 apply-run precedent).*)*
- [x] 7.2 Modify `ServicioDeVentas.cs`: per-line resolution —
  `ReglaDeLotes.ElegirFefo` over saldos; a supplied `idLote` is validated
  against `saldos` (exists, belongs to the articulo, not soft-deleted) or
  rejected `400 lote_invalido`; an omitted `idLote` defaults via
  `ElegirFefo`, a `null` result (no lot with positive balance) resolves the
  sin-identificar lot via `ResolverSinIdentificarAsync` (raw
  `ExecuteScalarAsync`, invisible to the round-trip counter). *(APPLY-RUN
  NOTE: "not soft-deleted" needs no extra code — `db.Lotes`'s global
  `deleted_at IS NULL` query filter already excludes a soft-deleted row
  from `LeerSaldosAsync`'s result set, so `FindIndex` naturally returns -1
  for it, same 400 as a nonexistent/foreign-articulo id.)*
- [x] 7.3 Modify `LineaDeVenta`/`PlanDeVenta`/`ItemEmitido` contracts:
  `LineaDeVenta.IdLote` (optional input), `ItemEmitido.IdLote`/
  `CodigoLote`/`LoteVencido` (output); the plan carries the resolved lot,
  immutable once decided. *(APPLY-RUN NOTE: the private `LineaDelPlan`
  record struct gained the same three trailing optional fields — that is
  literally "the plan", per design. Since the transaction does NOT persist
  `id_lote` in this slice (slice 8), `Proyectar` gained an optional
  `planItems` parameter: the fresh-checkout call site
  (`EjecutarTransaccionAsync`) passes `plan.Items` (zipped to
  `itemsEntidad` by `Orden`, 1-based, same order) so `ItemEmitido` carries
  the decide-phase lot even though the DB row does not yet; the two
  read-back call sites (`BuscarPorNumeroComprometidoAsync`/`ObtenerAsync`)
  omit it and fall back to the persisted (currently always-NULL) entity
  field — an accepted, documented slice-8 dependency, not a bug.)*
- [x] 7.4 [P] Query-count test: module on + ≥1 lot-controlled articulo in
  cart → `17`. *(spec lotes-y-vencimientos: "Module on with a
  lot-controlled articulo nets zero round-trip change")* *(APPLY-RUN NOTE:
  `PlanDeVentaFefoTests.ElCheckoutEmiteDiecisieteConsultasConUnArticuloConLoteEnElCarrito`
  — 17 confirmed, plus a table-filtered counter asserting exactly 1
  `lotes`/`stock_lotes` query.)*
- [x] 7.5 [P] Omitted-`idLote`-picks-FEFO test. *(spec: "An omitted idLote
  resolves to the nearest-expiry dated lot")* *(APPLY-RUN NOTE:
  `UnIdLoteOmitidoResuelveAlLoteDeVencimientoMasCercano`.)*
- [x] 7.6 [P] End-to-end sin-identificar-first proof (Domain-level mutation
  target already in slice 2; this is the write-path confirmation). *(spec:
  "The sin-identificar lot is offered before every dated lot")* *(APPLY-RUN
  NOTE: `ElLoteSinIdentificarSeOfreceAntesQueCualquierLoteConFecha` — no
  new mutation evidence recorded here, per the task's own framing (already
  proven at Domain level in slice 2's `OrdenarFefo`/`ElegirFefo` suite);
  this is confirmation-only.)*
- [x] 7.7 [P] Supplied-`idLote`-honoured test. *(spec: "A supplied idLote
  is honoured even when it is not the FEFO pick")* *(APPLY-RUN NOTE:
  `UnIdLoteProvistoSeHonraAunqueNoSeaElPickFefo`.)*
- [x] 7.8 [P] Invalid-`idLote` test → `400 lote_invalido`. *(spec: "An
  invalid supplied idLote is rejected")* *(APPLY-RUN NOTE: two tests —
  `UnIdLoteInvalidoEsRechazadoConLoteInvalido` (nonexistent id) and
  `UnIdLoteDeOtroArticuloEsRechazadoConLoteInvalido` (real id, wrong
  articulo). Mutation evidence (permanent rule 6): the `if (posicion < 0)
  throw ...` guard in `ServicioDeVentas.EmitirAsync` was commented out,
  rebuilt, and both tests re-run — both went RED
  (`500`/`ArgumentOutOfRangeException` instead of `400 lote_invalido`);
  guard restored, rebuilt, both tests GREEN again. No other layer was
  weakened to produce the failure.)*
- [x] 7.9 [P] No-auto-split test. *(spec: "A lot running short still
  completes the line, never auto-splitting")* *(APPLY-RUN NOTE:
  `UnLoteCortoCompletaLaLineaSinAutoSplit` — FEFO lot has balance 3, line
  requests 5; asserts the response's single item still resolves to that one
  lot for the full quantity 5 (no second `ItemEmitido` row). The negative
  `stock_lotes.cantidad = -2` outcome itself is NOT asserted here — that
  write does not exist yet in this slice (slice 8's `UpsertStockLoteAsync`
  invariant test, task 8.4).)*
- [x] 7.10 [P] Regression: module on, no lot-controlled articulo in cart →
  still `16`, no FEFO query. *(spec: "Module on with no lot-controlled
  articulo in the cart issues no FEFO query")* *(APPLY-RUN NOTE:
  `ElCheckoutEmiteDieciseisConsultasSinArticuloConLoteEnElCarrito` — 16
  confirmed, plus the table-filtered counter asserting 0 `lotes`/
  `stock_lotes` queries.)*
- [x] 7.11 [P] Legacy-client compatibility test. *(spec comprobantes-venta:
  "A client that knows nothing about lots still transacts correctly")*
  *(APPLY-RUN NOTE: `UnClienteLegadoSinIdLoteTransaccionaCorrectamente` —
  posts a hand-built raw JSON body whose `lineas[0]` object has NO `idLote`
  property at all (not even `null`), to simulate a client that has never
  heard of the field; the sale succeeds and the FEFO default applies
  silently.)*
- [x] 7.12 [P] Mixed-cart test (lot-controlled + non-lot line in one
  cart). *(spec: "A cart with a lot-controlled and a non-lot articulo mixes
  both paths")* *(APPLY-RUN NOTE:
  `UnCarritoMixtoDeArticuloConLoteYSinLoteResuelveAmbosCaminos`.)*
- [x] 7.13 Gate guard: `dotnet ef migrations has-pending-model-changes` →
  no pending changes. *(APPLY-RUN NOTE: ran via
  `--project src/Ways.Infrastructure --startup-project src/Ways.Infrastructure`
  (same reason as the slice 5 apply-run note — `Ways.Api` lacks the EF
  Design package reference). Output: "No changes have been made to the
  model since the last migration." Expected — this slice adds no schema,
  only C#/records.)*
- [x] 7.14 Run `judgment-day`; fix; re-judge until clean. *(JD-FIX NOTE,
  primera ronda: 5 hallazgos confirmados — CRITICAL 1 (`ElegirFefo` elegía el
  vencido con orden fecha ASC puro, sin preferir no-vencidos — resuelto por
  decisión 15, registrada en `state.yaml`), CRITICAL 2 (el fallback
  sin-identificar sin ningún lote con saldo carecía de cobertura de
  integración), HIGH 3 (`LoteVencido == true` nunca se aserteaba en ningún
  test, solo el caso `false`), WARNING 4 (un `idLote` sobre una línea sin
  lote efectivo se ignoraba en silencio — ahora `400 lote_invalido`),
  WARNING 5 (sin test que contrastara el `IdLote` del response fresco contra
  la relectura, que hoy cae a `null` hasta slice 8). Regla de proceso
  reforzada tras esta ronda: el "desvío 3" que el apply de esta slice
  reportó al orquestador (FEFO no saltaba lotes vencidos, deferido a este
  judgment-day) nunca quedó anotado en el repo — solo en el mensaje al
  orquestador; el juez B lo encontró por grep. A partir de acá, todo desvío
  se registra ACÁ, en `tasks.md`, además de comunicarse al orquestador — la
  resolución de ESTE desvío es exactamente la decisión 15 implementada en
  esta misma ronda de fixes.)*
- [x] 7.15 Branch `feat/stage12-slice7-venta-plan-fefo` off `main` (parent:
  slice 3); PR; merge stacked-to-main.

**Test plan**: query count ×2 (7.4, 7.10), FEFO resolution ×4 (7.5-7.9),
compatibility (7.11), mixed cart (7.12). *(JD-FIX round 1 additions:
decisión-15 repro ×1 (vencido+vigente ambos con saldo, elige el vigente),
fallback sin-identificar sin ningún lote con saldo ×1, `LoteVencido == true`
asertado ×1, `idLote` sobre línea sin lote efectivo → `400` ×1,
response-fresco-vs-relectura ×1 — más 2 tests nuevos en
`ServicioDeLotesTests` (decisión 15 vía el picker) y 4 hechos nuevos de
partición en `ReglaDeLotesTests` (Domain).)*

**Verify**: `dotnet test --filter FullyQualifiedName~PlanDeVentaFefo`

---

## Slice 8: Venta Escritura (PR 8)

**Start**: slice 7 merged. **Finish**: per-lot writes land inside the
pinned lock order; the item snapshot freezes `id_lote`; anulación reverses
the exact lot. **Rollback**: revert the branch.

**JD-FIX carryover (slice 7, FIX 5)**: once this slice persists `id_lote` on
`items_comprobante_venta`, `PlanDeVentaFefoTests
.ElCheckoutFrescoDevuelveIdLotePeroLaRelecturaTodaviaLoDevuelveNullHastaSlice8`
stops being true — UPDATE it to assert the SAME `IdLote` on both the fresh
checkout response and the `ObtenerAsync` re-read (drop the `Assert.Null` on
the re-read, replace with `Assert.Equal(idLote, itemReleido.IdLote)`); rename
away the "HastaSlice8" suffix once updated. *(APPLY-RUN NOTE: done — renamed
to `ElCheckoutFrescoYLaRelecturaDevuelvenElMismoIdLote`, asserts
`Assert.Equal(idLote, itemReleido.IdLote)`. `ServicioDeVentas.Proyectar`'s
doc-comment updated too — it previously documented the slice-7 limitation as
permanent; now records that both paths return the same value since slice 8.
`Contratos.cs`'s `ItemEmitido` doc-comment updated for the same reason.)*

- [x] 8.1 Modify `ServicioDeVentas.cs`: `EjecutarTransaccionAsync` step 5 —
  loop `OrderBy(IdArticulo).ThenBy(IdLote)`; `InsertarMovimientoStockAsync`
  gains `idLote`; `UpsertStockAsync` (aggregate, `id_lote NULL`, always
  first); if `item.IdLote` present, `UpsertStockLoteAsync` (new private
  method, same `INSERT ... ON CONFLICT DO UPDATE ... RETURNING` shape as
  `UpsertStockAsync`). *(APPLY-RUN NOTE: the loop's secondary/tertiary key
  is `.ThenBy(i => i.IdLote.HasValue).ThenBy(i => i.IdLote ?? 0)` — decisión
  9's explicit NULLS-FIRST pattern, not the plain `.ThenBy(IdLote)` design's
  own pseudocode shows — chosen because task 8.7 names this EXACT substring
  as its mutation target for "the checkout ordering". `InsertarMovimientoStockAsync`
  gained `int? idLote` + a new `AgregarParametroNulo` helper (first nullable
  raw param this class ever sent, same pattern as `ServicioDeCompras`/
  `ServicioDeStock`'s siblings of the same name).)*
- [x] 8.2 Modify the `AddRange` items block: `ItemComprobanteVenta.IdLote =
  i.IdLote` — frozen snapshot, never re-derived.
- [x] 8.3 Modify `ServicioDeVentas.cs`: `EjecutarAnulacionAsync` reorders
  `.OrderBy(m => m.IdArticulo).ThenBy(m => m.IdLote)`, the inverse movement
  copies `original.IdLote`, the `stock_lotes` upsert mirrors it — no
  lookup, no re-derivation. *(APPLY-RUN NOTE: plain `.ThenBy(m => m.IdLote)`
  here, not the HasValue pattern — this `OrderBy`/`ThenBy` runs over an
  `IQueryable<MovimientoStock>` (SQL-translated), matching the EXACT
  precedent already reviewed clean at slice 6's
  `ServicioDeCompras.EjecutarAnulacionAsync` for the identical shape.)*
- [x] 8.4 [P] Invariant test: `stock.cantidad` and `stock_lotes.cantidad`
  both correct after a venta + anulación sequence.
  (`StockYStockLotesQuedanCorrectosTrasVentaYAnulacion`)
- [x] 8.5 [P] **Mutation target**: `ItemComprobanteVenta.IdLote = i.IdLote`
  replaced with `null` → the exact-anulación test (asserting the reversal's
  `id_lote` **and** the resulting per-lot balance) MUST fail; revert →
  green. *(spec comprobantes-venta: "A lot-effective line freezes its
  resolved lot onto the snapshot", "Anulación of a lot-bearing sale
  reverses the exact lot"; mutation-proof-tests)* Record evidence.
  *(APPLY-RUN NOTE: `UnaVentaLoteEfectivaCongelaElSnapshotYSuAnulacionRevierteElLoteExacto`
  asserts THREE things in one run: (1) the persisted snapshot
  `items_comprobante_venta.id_lote` — this is the ONE the mutation actually
  breaks, since `EjecutarAnulacionAsync` reads its own reversal lot from
  `movimientos_stock` (the ledger), never from the item snapshot, so (2) the
  reversal's `id_lote` and (3) the restored per-lot balance stay green even
  under the mutation — documented explicitly so the next reader doesn't
  mistake "anulación still passes" for "the mutation is dead". Mutation
  applied — `IdLote = i.IdLote` → `IdLote = null` in the `AddRange`
  projection; build, filter on the test: RED
  (`Assert.Equal(idLote, itemPersistido.IdLote)` failed, `Expected: {idLote}
  / Actual: null`). Reverted, same filter: GREEN, full
  `VentaEscrituraLoteTests`/`PlanDeVentaFefoTests` (20/20).)*
- [x] 8.6 [P] Lock-order test: the `stock` row upserts before any
  `stock_lotes` row for the same `(articulo, PV)`. *(spec stock: "A
  checkout locks stock before stock_lotes for the same pair")*
  (`UnCheckoutBloqueaStockAntesQueStockLotesParaElMismoPar`.) *(APPLY-RUN
  NOTE — non-obvious discovery: both `InsertarMovimientoStockAsync`/
  `UpsertStockAsync`/`UpsertStockLoteAsync` run on a raw `DbCommand` created
  directly via `conexion.CreateCommand()` off the connection EF already
  opened — this NEVER goes through EF Core's `DbCommandInterceptor`
  pipeline (confirmed empirically: a `ScalarExecuting` override never
  fires), and Npgsql's own `NpgsqlLoggingConfiguration.InitializeLogging`
  is a process-wide, call-once API that must run before ANY connection
  opens — by the time a test can call it, `WaysApiFixture` has already
  opened connections and Npgsql has already cached a null logger (confirmed
  empirically: zero log lines ever arrived). Neither interception technique
  observes these statements. The test instead races a REAL Postgres lock:
  a second raw connection (RLS-authenticated with the same
  `set_config('app.tenant_id', ...)` GUC `InterceptorDeContextoDeTenant`
  sets — a plain unauthenticated raw connection sees ZERO rows of
  `stock_lotes` and its `FOR UPDATE` silently locks nothing) holds
  `stock_lotes`'s row open under `FOR UPDATE`; the checkout is fired
  concurrently and necessarily blocks trying to touch that same row; a
  third connection polls `pg_locks` for the checkout's own backend pid
  until it sees `stock`'s table-level `RowExclusiveLock` already `granted`
  at the same moment the checkout has an ungranted lock pending (Postgres
  represents "blocked on another transaction's row" as an ungranted
  `transactionid` `ShareLock`, never as an ungranted `tuple` lock on the
  contested relation — confirmed by dumping the full `pg_locks` state
  mid-block). Releasing the held lock lets the checkout complete; final
  `stock.cantidad` asserted too.)*
- [x] 8.7 [P] **Mutation target (half A, deadlock)**: `.ThenBy(c =>
  c.IdLote.HasValue).ThenBy(c => c.IdLote ?? 0)` in the checkout ordering —
  delete it and confirm the ordering test now fails on a hand-built key
  set. The **joint** checkout-vs-reverse-transfer deadlock proof itself is
  deferred to slice 10 (task 10.12), once `ServicioDeStock`'s transfer
  write also exists — recorded as an explicit cross-slice dependency, not a
  gap. *(PAIRING CLOSED at slice 10, task 10.12 —
  `UnCheckoutYUnaTransferenciaConcurrentesDelMismoArticuloYLoteNoDeadlockean`
  in `TransferenciaLoteTests.cs` runs the checkout-vs-reverse-transfer
  scenario end to end and passes. Note also the negative finding recorded
  at slice 10's task 10.4: the joint proof, like the transfer-vs-transfer
  deadlock test, does NOT independently discriminate the `id_lote` `ThenBy`
  mutation — both checkout and transfer write the aggregate `stock` row
  before any `stock_lotes` row for the same pair, so two transactions
  sharing an articulo/PV always convoy-serialize on that shared row before
  either reaches lot granularity. 8.7's OWN single-transaction ordering
  test still kills the mutation correctly — that discriminating power
  never depended on a second, concurrent transaction.)* *(APPLY-RUN NOTE:
  "hand-built key set" implemented as a real,
  achievable production scenario rather than an artificial in-memory
  fixture — `LosMovimientosDeDosLotesDelMismoArticuloSeEscribenEnOrdenAscendentePorIdLote`
  submits TWO lines of the SAME lot-effective articulo, each with a
  DIFFERENT explicit `idLote`, in DESCENDING id order (higher id first);
  `.NET`'s stable sort means that without the `ThenBy` pair, the write order
  would silently follow submission order instead of ascending `id_lote`.
  Mutation applied — the two `ThenBy` calls deleted from the step-5 loop,
  leaving only `.OrderBy(i => i.IdArticulo)`; build, filter on the test:
  RED (`Assert.Equal(idLoteMenor, movimientos[0].IdLote)` failed, `Expected:
  1 / Actual: 2` — the higher-id lot landed first, exactly the submission
  order). Reverted, same filter: GREEN, full suite (20/20).)*
- [x] 8.8 [P] Non-lot-articulo regression: no lot on the item or the
  movement. *(spec comprobantes-venta: "A non-lot articulo's item never
  carries a lot"; spec stock: "A non-lot articulo's movement never carries
  a lot")* (`UnArticuloSinControlDeLoteNuncaLlevaIdLoteEnItemNiMovimiento`.)
- [x] 8.9 Gate guard: `dotnet ef migrations has-pending-model-changes` →
  no pending changes. *(Verified after the slice's changes — no
  `Migraciones/`/`Configuraciones/` file touched, per the DB CHANGE GATE;
  run via `--project src/Ways.Infrastructure --startup-project
  src/Ways.Infrastructure`. Output: "No changes have been made to the model
  since the last migration.")*
- [x] 8.10 Run `judgment-day`; fix; re-judge until clean. *(APPLY-RUN NOTE:
  two REJECT rounds, both with real findings on the hottest writer.
  Round 1 — judge B: the "hoist first IdLote onto every reversal movement"
  mutation survived 176 tests (every anulación test sold ONE line; balances
  stayed correct, the ledger's per-movement lot attribution corrupted
  silently). Fix 7b4b98a: multi-line and mixed anulación tests killing the
  exact mutation from both angles. Round 2 — judge B APPROVE; judge A
  (first pass): MAJOR — nothing proved the aggregate ACCUMULATES across two
  lines of the same articulo (a group-by-articulo "optimization" using only
  the last delta survived), plus stale design pseudocode and the
  provider-dependent NULLS ordering of the anulación read left implicit.
  Fix a600c52: 8.7 test extended (seeded aggregate, discriminant deltas,
  exact final stock 12m; mutation Expected-12/Actual-15 recorded), design
  write-site-1 pseudocode reconciled, provider note added. Round 3 — judge
  A APPROVE with arithmetic verification of the mutation evidence.)*
- [x] 8.11 Branch `feat/stage12-slice8-venta-escritura` off `main` (parent:
  slice 7); PR; merge stacked-to-main.

**Test plan**: invariant (8.4), snapshot mutation (8.5), lock order (8.6),
ordering mutation half (8.7), non-lot regression (8.8).

**Verify**: `dotnet test --filter FullyQualifiedName~VentaEscrituraLote`

---

## Slice 9: NCX (PR 9)

**Start**: slice 8 merged. **Finish**: an NCX line for a lot-effective
articulo requires an explicit `idLote`; the response carries a
`loteVencido` warning, never a block. **Rollback**: revert the branch.

- [x] 9.1 Modify `ServicioDeVentas.cs`: NCX validation — lot-effective
  articulo without `idLote` → `400 lote_requerido` before the transaction;
  FEFO defaulting is refused for NCX lines (returns are not "oldest
  first"). *(APPLY-RUN NOTE: single guard clause inserted at the top of
  the per-line lot resolution loop —
  `if (idLotePedido is null && tipo.Signo < 0) throw ... "lote_requerido" ...`
  — placed BEFORE the existing `idLotePedido is { }` / `ElegirFefo` /
  lazy-sin-identificar branches, so an omitted `idLote` on an NCX line
  never reaches FEFO. Entirely inside the decide phase (already before
  `EjecutarTransaccionAsync` opens), so "before the transaction" is
  structural, not an extra check. `tipo.Signo` was already resolved
  earlier in `EmitirAsync` (TX +1 / NCX −1, `ReglaDeComprobantes` — "nunca
  cero"), no new read.)*
- [x] 9.2 Modify the POS-facing contract: suggestion source — from
  `id_comprobante_asociado`'s item snapshot when present, else the
  articulo's existing lots; the sin-identificar lot stays a valid explicit
  choice. *(APPLY-RUN NOTE: `GET /api/stock/lotes` gained an optional
  `int? idComprobanteAsociado` query param (no new DTO field — query
  string only); `ServicioDeLotes.ListarAsync` resolves `idLoteSugerido`
  from `items_comprobante_venta.id_lote` of that comprobante for the
  requested articulo when the param is present, falling back to
  `ReglaDeLotes.ElegirFefo` (decisión 15) when absent OR when the snapshot
  lookup returns no row/a null lot (defensive — an articulo that wasn't
  lot-effective in the original sale). One `LoteListado.Sugerido`
  projection, single source of truth (`idLoteSugerido == s.IdLote`)
  regardless of which branch resolved it. Only call site
  (`StockEndpoints.cs`) updated; no other caller in `src/` or `tests/`.)*
  *(JD-FIX NOTE, slice 9 judgment-day ronda 1, juez A: dos hallazgos.
  MAJOR — `idLoteSugerido` del snapshot se resolvía DESPUÉS de
  `LeerSaldosAsync` con `idsLotePedidos` vacío: un lote agotado en el PV (el
  caso típico de devolución, saldo 0 tras la venta original) ni se listaba
  ni se sugería. Fix: se resuelve el `idLote` del snapshot ANTES de
  `LeerSaldosAsync` y se pasa como `idsLotePedidos` — mismo espejo que el
  write path de `ServicioDeVentas` (design decisión 6). Test:
  `NcxLoteTests.ElLoteSugeridoDelSnapshotApareceListadoAunqueSuSaldoEnElPvSeaCero`
  (mutación: revertir a "resolver después con lista vacía" → RED, el lote
  agotado no aparece en la colección; revertido → GREEN). MINOR — la query
  del snapshot no tenía `OrderBy`: con dos líneas del mismo artículo en el
  comprobante asociado (lotes distintos), el pick era no-determinista. Fix:
  `OrderBy(i => i.Id)` — gana el id de item más chico, la primera línea del
  comprobante. Sin test dedicado (a criterio del costo: el seed de
  dos-líneas-mismo-artículo no aporta más que el comentario in-code);
  determinismo-por-construcción documentado acá y en el doc-comment de
  `ServicioDeLotes.ListarAsync`.)*
- [x] 9.3 Modify `ServicioDeVentas.cs`: `ItemEmitido.LoteVencido =
  ReglaDeLotes.EstaVencido(...)` computed for TX and NCX lines alike; an
  expired-lot sale/return is accepted with the flag set, never blocked.
  *(APPLY-RUN NOTE: already true on this branch since Slice 7/8 —
  `EmitirAsync`'s lot-resolution loop is the SAME code path for TX and
  NCX (only `tipo.Signo` distinguishes them, consumed by 9.1's new
  guard), and `LoteVencido = ReglaDeLotes.EstaVencido(loteResuelto.FechaVencimiento, hoy)`
  already ran unconditionally for every lot-effective line. No code
  change beyond 9.1's guard; verified end-to-end from the NCX side by
  task 9.7's test, cited below — TX side already covered by Slice 7.)*
- [x] 9.4 [P] `lote_requerido`-on-NCX test. *(spec comprobantes-venta: "An
  NCX line for a lot-effective articulo requires idLote")*
  `NcxLoteTests.UnaLineaNcxDeArticuloLoteEfectivoSinIdLoteEsRechazadaConLoteRequerido`.
  **Mutation target** (skill mutation-proof-tests, named explicitly for
  this slice by the orchestrator, not in design.md's canonical table):
  the exact clause `if (idLotePedido is null && tipo.Signo < 0)` — proves
  BOTH halves of 9.1 (`lote_requerido` fires AND FEFO never silently
  defaults for NCX) in one assertion, since deleting the guard makes the
  line fall through to `ElegirFefo`, which resolves and returns `201
  Created` instead of `400`. *(APPLY-RUN NOTE: mutation applied — the
  condition replaced by `if (false)`; build; filter
  `FullyQualifiedName~UnaLineaNcxDeArticuloLoteEfectivoSinIdLoteEsRechazadaConLoteRequerido`
  → RED (`Assert.Equal() Failure: Expected: BadRequest / Actual:
  Created` — the line resolved via FEFO to the single lot with stock);
  reverted; same filter → GREEN; full `NcxLoteTests` +
  `PlanDeVentaFefoTests` + `VentaEscrituraLoteTests` + `VentasCheckoutTests`
  + `ServicioDeLotesTests` regression → GREEN, 67/67.)*
- [x] 9.5 [P] Suggested-`idLote`-from-snapshot test. *(spec: "idLote is
  suggested from the associated comprobante's snapshot")*
  `NcxLoteTests.UnaLineaNcxConIdComprobanteAsociadoSugiereElLoteDelSnapshotNoFefo`
  — seeds two lots (`L-CERCANO`, the FEFO default pick; `L-LEJANO`,
  explicitly chosen for the original TX), asserts the picker's `Sugerido`
  matches the snapshot lot (`L-LEJANO`), not FEFO's pick — a test that
  would pass under either source could not distinguish the two, so the
  fixture deliberately makes them disagree. Companion contrast test
  `ElMismoPickerSinIdComprobanteAsociadoSigueSugiriendoFefo` (not on the
  task list, added for honesty) proves the same endpoint still defaults
  to FEFO when `idComprobanteAsociado` is omitted — the snapshot source
  is conditional on the param, never the new default.
- [x] 9.6 [P] Standalone-devolución-sin-identificar-accepted test. *(spec:
  "idLote is required even without an associated comprobante")*
  `NcxLoteTests.UnaDevolucionStandaloneAceptaElLoteSinIdentificarComoValvulaDeEscape`
  — sin-identificar lot seeded directly (same convention as Slice 7's
  `ElLoteSinIdentificarSeOfreceAntesQueCualquierLoteConFecha`), submitted
  as an explicit `idLote`; asserts `201`, `IdLote`/`CodigoLote` on the
  response AND the persisted `stock_lotes` balance (proves the write
  path, not just the response shape).
- [x] 9.7 [P] Return-into-expired-lot-permitted test. *(spec: "Returning
  into an expired lot is permitted")*
  `NcxLoteTests.RetornarAUnLoteVencidoEsPermitidoYQuedaMarcadoConWarning` —
  also the end-to-end NCX-side proof of task 9.3 (`LoteVencido = true`,
  request still succeeds `201`, `stock_lotes` balance increases by the
  returned quantity — no negativity guard applies to a return, mirrors
  `UpsertStockLoteAsync`'s doc-comment).
- [x] 9.8 [P] Expired-lot-sale-warns-never-blocks test. *(spec: "A sale of
  an explicitly expired lot succeeds with a warning")* Already covered —
  cited, not duplicated:
  `PlanDeVentaFefoTests.UnIdLoteProvistoDeUnLoteVencidoDevuelveLoteVencidoEnTrue`
  (Slice 7) explicitly supplies an expired lot with positive balance on a
  TX line and asserts `201 Created` + `LoteVencido == true`. TX and NCX
  share the exact same resolution/assignment code (task 9.3), so no
  NCX-specific duplicate is needed; task 9.7's test is the NCX-side
  analogue for the return direction.
- [x] 9.9 [P] FEFO-prefers-non-expired-lot test. *(spec: "FEFO prefers a
  non-expired lot when one has stock")* Already covered — cited, not
  duplicated:
  `PlanDeVentaFefoTests.UnIdLoteOmitidoConVencidoYVigenteAmbosConSaldoEligeElVigente`
  (Slice 7, decisión 15) omits `idLote` with both an expired and a
  non-expired lot in stock and asserts the non-expired lot wins with
  `LoteVencido == false`. FEFO defaulting is exclusively a TX-path
  concern after 9.1 (NCX never reaches `ElegirFefo` when `idLote` is
  omitted — it throws `lote_requerido` first), so this scenario has no
  NCX equivalent to add.
- [x] 9.10 Gate guard: `dotnet ef migrations has-pending-model-changes` →
  no pending changes. *(Verified via
  `--project src/Ways.Infrastructure --startup-project src/Ways.Infrastructure`
  — "No changes have been made to the model since the last migration."
  No `Migraciones/`/`Configuraciones/` file touched by this slice, per
  the DB CHANGE GATE.)*
- [x] 9.11 Run `judgment-day`; fix; re-judge until clean. *(APPLY-RUN NOTE:
  judge B round 1 APPROVE with a real coverage gap (no test annulled a
  lot-bearing NCX — closed by 54a4a6f with a surgical sign-isolating
  mutation, Expected-5/Actual-8); judge A round 1 REJECT with a MAJOR
  product bug: the snapshot-suggested lot vanished from the picker when its
  balance hit zero — the MAINLINE devolución case — because idLoteSugerido
  resolved AFTER LeerSaldosAsync with empty idsLotePedidos. Fix c09db6f:
  pre-saldos resolution threaded through idsLotePedidos (exact mirror of
  the write path, design decision 6) + deterministic OrderBy tiebreaker on
  the snapshot query. Round 2: both judges APPROVE; judge A verified the
  fix hunk-by-hunk and blob-checked scope. One cosmetic nit recorded
  (doc-comment says Assert.Contains, actual is Assert.Single).)*
- [x] 9.12 Branch `feat/stage12-slice9-ncx` off `main` (parent: slice 8);
  PR; merge stacked-to-main.

**Test plan**: `lote_requerido` (9.4), suggestion (9.5), sin-identificar
escape hatch (9.6), expired-return (9.7), warning-not-block ×2 (9.8-9.9).

**Verify**: `dotnet test --filter FullyQualifiedName~NcxLote`

---

## Slice 10: Transferencias (PR 10)

**Start**: slice 3 merged (parallel to 4, 5, 7, 13). **Finish**: the lot
travels with the merchandise; the lock order gains a `≥2N`-key form; per-lot
insufficiency and expired-lot transfer are refused. **Rollback**: revert
the branch.

- [x] 10.1 Modify `src/Ways.Application/Stock/ServicioDeStock.cs`:
  `ClaveDeStock` widens (`IdLote`, `IdLoteDelMovimiento`);
  `ConstruirClavesOrdenadas` — per lot-effective line, 4 keys (aggregate +
  lot at origen, aggregate + lot at destino); order
  `.OrderBy(IdArticulo).ThenBy(IdPuntoVenta).ThenBy(IdLote.HasValue).ThenBy(IdLote ?? 0)`.
  *(APPLY-RUN NOTE — continuación tras corte de proceso: el wip rescatado
  (`e37313e`) tenía TODO el resto de la task correcto pero el `ThenBy` de
  `id_lote` estaba directamente AUSENTE del código shipeado — quedó solo
  `.OrderBy(IdArticulo).ThenBy(IdPuntoVenta)`, a pesar de que el doc-comment
  de la clase y el comentario de la task 10.4 en el test lo daban por
  puesto y por probado. Restaurado en esta continuación.)*
- [x] 10.2 Modify `ServicioDeStock.cs`: pre-transaction phase — read
  `stock_lotes` of the origin PV for requested articulos, FEFO-default
  omitted lots via `ReglaDeLotes.ElegirFefo`, apply decision 11's
  `(IdArticulo, IdLote)` duplicate refusal **after** defaulting;
  `transferencia_lote_vencido` check alongside `ResolverArticuloAsync`.
  *(Ya estaba correcto en el wip rescatado — sin cambios en esta
  continuación.)*
- [x] 10.3 Modify `ServicioDeStock.cs`: transaction loop — at an aggregate
  element, insert the ledger row (carrying `IdLoteDelMovimiento`) + upsert
  `stock`; at a lot element, upsert `stock_lotes` only. Both `RETURNING`
  values checked for negativity → `409
  stock_insuficiente_para_transferencia` (aggregate check unchanged, lot
  check new). *(Ya estaba correcto en el wip rescatado — sin cambios en
  esta continuación.)*
- [x] 10.4 [P] **Mutation target**: `.ThenBy(c => c.IdLote.HasValue).ThenBy(c
  => c.IdLote ?? 0)` deleted in `ConstruirClavesOrdenadas` → the
  transfer-vs-reverse-transfer deadlock test MUST fail; revert → green.
  Record evidence. *(DESVÍO documentado — el wip dejaba una "EVIDENCIA DE
  MUTACIÓN" en el doc-comment del test afirmando un ciclo RED→GREEN que la
  corrida real de esta continuación NO reprodujo: con el `ThenBy` borrado,
  el archivo completo (11/11, incluidos este test, 10.11 y el joint proof
  10.12) siguió en GREEN, corrida ×2. Causa raíz analizada y confirmada
  empíricamente: dentro de un mismo `(id_articulo, id_punto_venta)`, el
  elemento AGREGADO de cada línea precede a su elemento LOTE por
  construcción del array por línea, sea cual sea el `ThenBy` — así que el
  primer elemento nuevo tocado por dos transferencias recíprocas del MISMO
  artículo es siempre la MISMA fila física de `stock`, que actúa de convoy:
  quien la toca primero la retiene hasta el commit y la otra transacción
  simplemente espera, sin llegar nunca a competir en el orden opuesto que
  el test intenta forzar sobre `stock_lotes`. El mismo mecanismo neutraliza
  el joint proof 10.12 (checkout y transferencia comparten la fila
  agregada del mismo artículo/PV). El `ThenBy` de `id_lote` se mantiene —
  sigue siendo exigido por el design/spec (orden total `≥2N`, consistente
  con los otros dos sitios de escritura) y es la forma correcta — pero
  NINGÚN test de este archivo lo prueba por mutación viva; el comentario
  falso del wip fue corregido en el test para reflejar este hallazgo
  negativo documentado en vez de una evidencia fabricada. Ver el doc-comment
  de `TransferenciasReciprocasDelMismoArticuloConLotesEnOrdenOpuestoNoDeadlockean`
  en `TransferenciaLoteTests.cs`.)*
- [x] 10.5 [P] A→B vs. B→A concurrency test, write-site 3: both transfers
  complete, no `40P01`. *(Ya estaba correcto en el wip rescatado — mismo
  test que 10.4, sin cambios en esta continuación.)*
- [x] 10.6 [P] Per-lot insufficiency with a sufficient aggregate. *(spec
  transferencias-de-stock: "A lot-level underflow is refused even with a
  sufficient aggregate")*
- [x] 10.7 [P] Lot-travels test. *(spec: "A lot-effective articulo transfer
  moves the same lot at both ends")*
- [x] 10.8 [P] Omitted-`idLote`-resolves-via-FEFO test. *(spec: "An omitted
  idLote resolves via FEFO at transfer time")*
- [x] 10.9 [P] `transferencia_lote_vencido` tests. *(spec: "Transferring an
  explicitly expired lot is refused", "A non-expired lot transfers
  normally")*
- [x] 10.10 [P] Duplicate-line detection ×3. *(spec: "Two lines of the same
  articulo with different explicit lots are accepted", "Two lines
  resolving to the same explicit lot are rejected", "Two lines both
  omitting idLote that resolve to the same FEFO lot are rejected")*
- [x] 10.11 [P] Single-ascending-order test over both origin and
  destination lot rows. *(spec: "A single ascending order covers both
  origin and destination lot rows")*
- [x] 10.12 [P] **Joint deadlock proof** (completes the pairing opened at
  slice 8.7): a checkout selling lot 7 of articulo 40 at PV 1, concurrent
  with a transferencia moving the same lot 7 of articulo 40 from PV 1 to
  PV 2 — both complete, no deadlock. *(spec stock: "A concurrent checkout
  and reverse transfer of the same articulo and lots do not deadlock")*
  Cierra la nota cross-slice de la task 8.7. *(APPLY-RUN NOTE: verificado
  ×1 GREEN con el código correcto; verificado empíricamente que este test
  TAMPOCO discrimina la mutación del 10.4 por el mismo mecanismo de
  convoy sobre la fila agregada compartida — ver la nota de 10.4. El
  proof queda como cobertura funcional real (checkout y transferencia
  concurrentes del mismo artículo/lote no deadlockean), no como prueba de
  mutación del `ThenBy` de `id_lote`.)*
- [x] 10.13 Gate guard: `dotnet ef migrations has-pending-model-changes` →
  no pending changes. *(Ejecutado con `--project src/Ways.Infrastructure
  --startup-project src/Ways.Infrastructure` — Ways.Api no referencia
  `Microsoft.EntityFrameworkCore.Design`, mismo criterio que slices previos.
  Output: "No changes have been made to the model since the last
  migration.")*
> Note (judgment-day, slice 10, FIX round — juez B, 2 gaps de cobertura
> confirmados, ambos ya smoke-testeados en verde por el juez, dejados como
> tests permanentes en `TransferenciaLoteTests.cs`):
> 1. **Transferencia mixta** (`UnaTransferenciaMixtaConLineaLoteEfectivaYLineaSinLoteCompletaAmbas`):
>    una línea de artículo lote-efectivo + una línea de artículo sin lote en
>    la misma solicitud — ambas completan, sin que el filtro
>    `indicesConLoteEfectivo` de `ResolverLineasDeTransferenciaAsync`
>    contamine el tratamiento de la otra. Evidencia de mutación: el ternario
>    de `ConstruirClavesOrdenadas` mutado para emitir también una clave de
>    lote en la rama sin-lote → el test FALLA (500 por FK inválida sobre
>    `stock_lotes`); revertido, GREEN.
> 2. **`lote_invalido` sobre línea sin lote efectivo**
>    (`UnaLineaSinLoteEfectivoConIdLoteProvistoEsRechazadaComoLoteInvalido`):
>    un `idLote` provisto en una línea de artículo sin control de lote se
>    rechaza (400), nada se escribe. Evidencia de mutación: anulado el guard
>    de `ServicioDeStock.cs` (~269-281) → el test FALLA (200 en vez de 400);
>    revertido, GREEN.
>
> Filtro `~TransferenciaLote`: 13/13 (11 previos + 2 nuevos). Regresión
> `~TransferenciasYConteo`: 28/28.
> Note (judgment-day, slice 10, FIX round — juez A, 1 MAJOR + 1 MINOR
> confirmados):
> 1. **MAJOR — `LineaTransferida` sin `IdLote` + agregación que colapsaba
>    multi-lote** (design.md:180, `dto-contract-honesty`): el shipped había
>    dropeado `IdLote` del record sin documentar, y `EjecutarTransferenciaAsync`
>    agregaba por `IdArticulo` solo — dos líneas del mismo artículo con lotes
>    distintos (caso ACEPTADO por spec, ver `DosLineasDelMismoArticuloConLotesExplicitosDistintosSonAceptadas`)
>    colapsaban en una sola fila del response, y el caller nunca sabía qué
>    lote viajó en el FEFO-default. Fix: `LineaTransferida(int IdArticulo,
>    int? IdLote, decimal CantidadOrigen, decimal CantidadDestino)` restaurado
>    igual que el design; `resultadosPorArticuloYLote` ensanchado a clave
>    `(IdArticulo, IdLote)` — una fila por (artículo, lote), lote FEFO incluido.
>    `CantidadOrigen`/`CantidadDestino` siguen siendo el checkpoint del
>    `stock.cantidad` agregado devuelto por el upsert de ESA línea puntual, no
>    el saldo final del artículo ni el de `stock_lotes` (documentado en el
>    doc-comment del record). Test nuevo:
>    `LaRespuestaDeUnaTransferenciaConDosLotesDelMismoArticuloTraeUnaFilaPorLoteConIdLote`
>    — 2 líneas de un mismo artículo con lotes explícitos distintos + 1 línea
>    FEFO-default de otro artículo, asserta las 3 filas del body con
>    IdArticulo/IdLote/cantidades exactos. Evidencia de mutación: clave
>    revertida a `IdArticulo` solo → el test FALLA (2 filas en vez de 3);
>    revertido, GREEN. Consumidores verificados: `Ways.Web` (`tipos.ts`,
>    `Transferencias.tsx`) todavía NO consume lote en transferencias (ni
>    `LineaDeTransferencia` ni `LineaTransferida` tienen `idLote` del lado
>    TS) — el campo nuevo es transparente para el cliente actual. Riesgo
>    latente anotado, no corregido acá (fuera de alcance del slice 10):
>    `Transferencias.tsx:288` usa `key={l.idArticulo}` en la tabla de
>    resultado — el día que un slice futuro (14/15) sume selección de lote a
>    la UI, dos filas del mismo artículo con lotes distintos van a colisionar
>    esa key de React.
> 2. **MINOR — faltaba el test del FEFO-default que resuelve a un lote
>    solo-vencido**: el código ya hacía el re-check incondicional de
>    `EstaVencido` (aunque el lote viniera de FEFO), pero no había cobertura
>    para el caso "único lote con saldo, y ese lote está vencido". Test
>    nuevo: `UnaLineaSinIdLoteQueResuelvePorFefoAUnUnicoLoteVencidoEsRechazada`.
>    Evidencia de mutación: anulado el `if (ReglaDeLotes.EstaVencido(...))` →
>    el test FALLA (200, transfiere el vencido); revertido, GREEN.
>
> Filtro `~TransferenciaLote`: 15/15 (13 previos + 2 nuevos). Regresión
> `~TransferenciasYConteo`: 28/28. `has-pending-model-changes`: sin cambios
> pendientes (fix puramente de capa Application, sin tocar el modelo).
- [x] 10.14 Run `judgment-day`; fix; re-judge until clean. *(APPLY-RUN NOTE:
  this slice's apply died mid-run with the host process; the rescued wip had
  two defects the continuation caught — comments claiming a lock tie-break
  the code lacked (restored) and FALSE mutation evidence for 10.4 (the
  shared aggregate row is a natural lock convoy; judge B later derived the
  STRUCTURAL proof that a lot-row deadlock cycle is impossible by
  construction, validated cross-articulo ×3 under mutation — the tie-break
  stands as cross-write-site convention, honestly documented). Judge B:
  APPROVE ×2 with 5 standard mutations killed + 2 coverage gaps closed
  (mixed transfer, lote_invalido). Judge A: REJECT round 1 with a MAJOR
  contract drift — LineaTransferida had silently dropped the design-mandated
  IdLote and the per-articulo aggregation collapsed multi-lot lines in the
  response; fixed in 5703a29 (per-(articulo,lote) rows, field-by-field
  3-row test, FEFO-to-expired 409 test); APPROVE round 2 with hand-derived
  arithmetic verification. Latent Web risk (key={l.idArticulo} in
  Transferencias.tsx) recorded for slice 14/15.)*
- [x] 10.15 Branch `feat/stage12-slice10-transferencias` off `main`
  (parent: slice 3); PR; merge stacked-to-main.

**Test plan**: deadlock mutation (10.4), A→B/B→A concurrency (10.5),
per-lot insufficiency (10.6), lot-travels (10.7), FEFO default (10.8),
expired-transfer ×2 (10.9), duplicate-line ×3 (10.10), single order
(10.11), joint checkout-vs-transfer deadlock (10.12).

**Verify**: `dotnet test --filter FullyQualifiedName~TransferenciaLote`

---

## Slice 11: Ajuste + Decomiso (PR 11)

**Start**: slice 10 merged. **Finish**: ajuste is lot-aware; `POST
/api/stock/decomiso` is a first-class, Admin-only, never-negative write
path. **Rollback**: revert the branch.

- [x] 11.1 Modify `ServicioDeStock.cs`: `EjecutarAjusteAsync` — a
  lot-effective articulo requires `idLote` (`400 lote_requerido`), a
  non-lot articulo refuses it (`400 lote_no_aplica`); aggregate upsert then
  lot upsert, in that order; no negativity refusal (ajuste is the
  correction operation).
- [x] 11.2 Create `EjecutarDecomisoAsync` in `ServicioDeStock.cs`
  (structurally `EjecutarAjusteAsync` with three deltas): `motivo =
  Decomiso`; client-supplied `cantidad` is positive, negated server-side;
  the `RETURNING` of the lot upsert (or aggregate for a non-lot articulo)
  checked `< 0` → `409 stock_insuficiente_para_decomiso`; `observaciones`
  mandatory (`ExigirObservaciones` reused verbatim).
- [x] 11.3 Modify `StockEndpoints.cs`: `POST /api/stock/decomiso`,
  `Politicas.GestionDeCatalogo` stacked over `OperacionDePos`.
- [x] 11.4 [P] **Mutation target**:
  `.RequireAuthorization(Politicas.GestionDeCatalogo)` deleted on
  `/stock/decomiso` → the Vendedor-403 test MUST fail (the group's
  `OperacionDePos` alone admits Vendedor); revert → green. *(spec:
  "Vendedor is blocked from decomiso"; mutation-proof-tests)* Record
  evidence.
- [x] 11.5 [P] `stock_insuficiente_para_decomiso` test. *(spec: "A decomiso
  that would go negative is refused")*
- [x] 11.6 [P] Sign-discipline test: positive client `cantidad` negated
  server-side. *(spec: "A positive client cantidad is negated by the
  server")*
- [x] 11.7 [P] Decomiso-of-lot-effective-requires-`idLote` test. *(spec:
  "A decomiso of a lot-effective articulo requires idLote")*
- [x] 11.8 [P] Non-expired-lot decomiso allowed. *(spec: "Decomiso applies
  to a non-expired lot too")*
- [x] 11.9 [P] Observaciones-required test. *(spec: "Decomiso without
  observaciones is rejected")*
- [x] 11.10 [P] Ajuste lot-aware tests. *(spec stock: "Ajuste of a
  lot-effective articulo requires idLote and updates both caches", "Ajuste
  of a lot-effective articulo without idLote is rejected")*
- [x] 11.11 Gate guard: `dotnet ef migrations has-pending-model-changes` →
  no pending changes.
- [x] 11.12 Run `judgment-day`; fix; re-judge until clean. *(APPLY-RUN NOTE:
  judge B round 1 APPROVE-with-findings — 5 mutations killed cleanly but 3
  coverage gaps confirmed by SURVIVING mutations: the aggregate branch of
  decomiso (no-lot articulo) was dead code to the tests, invalid-cantidad
  validation was never exercised via /decomiso, and nothing proved an ajuste
  CAN leave a negative balance (its central difference from decomiso). Fix
  d36554a: 7 new tests (11→18), each verified by re-running the surviving
  mutation to RED. Judge B round 2 APPROVE with surgical-precision kills
  (1/18, 2/18, 2/18 — no collateral). Judge A APPROVE with 3 MINORs:
  ExigirObservaciones message says "ajuste" on the decomiso path
  (pre-existing since stage 5, follow-up ticket); the stock spec's multi-lot
  scenario was mislabeled "ajuste" — amended to "conteo" in this branch
  (single-IdLote request shape confirmed); cantidad-code reuse cosmetic and
  documented.)*
- [x] 11.13 Branch `feat/stage12-slice11-ajuste-decomiso` off `main`
  (parent: slice 10); PR; merge stacked-to-main.

**Test plan**: 403 mutation (11.4), insufficiency (11.5), sign discipline
(11.6), lot-required (11.7), non-expired-ok (11.8), observaciones (11.9),
ajuste ×2 (11.10).

**Verify**: `dotnet test --filter FullyQualifiedName~Decomiso|FullyQualifiedName~AjusteLote`

> **APPLY-RUN NOTE (tasks 11.1-11.11, sdd-apply)**: nuevo archivo
> `tests/Ways.IntegrationTests/AjusteDecomisoLoteTests.cs` (11 tests),
> `ServicioDeStock.AjustarAsync`/`EjecutarAjusteAsync` ganan la dimensión de
> lote vía `ResolverIdLoteEfectivoAsync` (helper compartido con
> `DecomisarAsync`, reusa `ServicioDeLotes.LeerSaldosAsync` para validar un
> `idLote` explícito — existe/pertenece al artículo/no borrado — antes de la
> transacción, mismo criterio que `TransferirAsync`); `DecomisarAsync` +
> `EjecutarDecomisoAsync` nuevos, motivo `Decomiso` de primera clase; nuevo
> endpoint `POST /api/stock/decomiso`. `Contratos.cs`:
> `SolicitudDeAjusteDeStock` gana `IdLote` opcional (posicional al final, no
> rompe los call-sites existentes); nuevo `SolicitudDeDecomiso`.
>
> Cobertura más allá de los 8 tests enumerados en el test plan: 3 tests
> extra para los guards `lote_no_aplica` (ajuste) y `lote_invalido`
> (decomiso) — código ya escrito por `ResolverIdLoteEfectivoAsync` (mismo
> guard que `TransferirAsync` usa para `lote_invalido`) que hubiera quedado
> sin cobertura si me ceñía estrictamente a la lista de 8; y un decomiso de
> lote VENCIDO como contraparte del "no vencido" de la task 11.8, probando
> que la fecha nunca entra en la decisión en ninguno de los dos sentidos.
> `ExigirCantidadDeDecomisoValida` (cantidad > 0, hasta 3 decimales) reusa
> el código `cantidad_de_ajuste_invalida`/`cantidad_invalida` de
> `ExigirCantidadValida` — no hay código nuevo para esto en la lista de la
> etapa (design.md "New domain error codes").
>
> Evidencia de mutación (11.4): borrado
> `.RequireAuthorization(Politicas.GestionDeCatalogo)` de `/decomiso` en
> `StockEndpoints.cs` — build, filtro
> `FullyQualifiedName~UnVendedorEsBloqueadoDelDecomiso`: el test **FALLÓ**
> (`200 OK` en vez de `403 Forbidden` — el Vendedor pasa con solo
> `OperacionDePos` del grupo). Revertido el mutante, build, corrida de
> nuevo: **GREEN**.
>
> Filtro `~AjusteDecomisoLoteTests`: 11/11. Regresión `~AjusteDeStockTests`:
> 10/10. Regresión `~TransferenciaLoteTests`: 15/15. Regresión
> `~TransferenciasYConteoDeInventarioTests`: 28/28. Corrida combinada de los
> tres archivos de regresión + el nuevo: 53/53 (10 + 15 + 28, sin contar el
> archivo nuevo en esa corrida puntual).
> `has-pending-model-changes`: sin cambios pendientes, antes y después de
> los edits (gate cerrado — los valores de enum `Decomiso` ya existen desde
> el slice 1).

> **JUDGMENT-DAY FIX NOTE (11.12, ronda 2, juez B)**: 3 gaps confirmados, los
> 3 sobre código ya escrito en el slice pero sin cobertura — 7 tests nuevos
> en `AjusteDecomisoLoteTests.cs` (11 → 18):
>
> 1. Decomiso de artículo SIN lote efectivo (rama `else`/`else if` de
>    `EjecutarDecomisoAsync`, código muerto para tests hasta esta ronda):
>    `UnDecomisoDeUnArticuloSinLoteEfectivoEsAceptado` (200, agregado baja,
>    movimiento con `id_lote = null`) y
>    `UnDecomisoDeUnArticuloSinLoteEfectivoQueDejariaElAgregadoNegativoEsRechazado`
>    (409 `stock_insuficiente_para_decomiso` sobre el agregado). Evidencia:
>    unconditional `throw` en la rama `else` — ambos **FALLARON** (200/409
>    reales vs. 409 forzado); revertido, **GREEN**.
> 2. Cantidad inválida en decomiso:
>    `UnDecomisoConCantidadCeroONegativaEsRechazado` (Theory, cantidad 0 y
>    -5 → 400 `cantidad_de_ajuste_invalida`) y
>    `UnDecomisoConMasDeTresDecimalesEsRechazado` (400 `cantidad_invalida`).
>    Evidencia: anulado el `if (cantidad <= 0)` de
>    `ExigirCantidadDeDecomisoValida` (`if (false)`) — ambos casos del
>    Theory **FALLARON** (200/500 en vez de 400); revertido, **GREEN**.
> 3. El ajuste PUEDE dejar negativo (diferencia central con decomiso):
>    `UnAjusteQueDejaSaldoNegativoEsAceptado` (200, saldo de lote Y agregado
>    negativo persistido exacto, -2). Evidencia: agregado temporalmente un
>    `if (nuevaDelLote < 0m) throw ...` a `EjecutarAjusteAsync` — el test
>    **FALLÓ** (409 en vez de 200); revertido, **GREEN**.
>
> Hallazgo menor (barato, helper ya existía):
> `UnDecomisoDeUnArticuloSinLoteConIdLoteProvistoEsRechazado` — guard
> simétrico de `lote_no_aplica` en decomiso, mismo criterio que el
> equivalente de ajuste.
>
> Filtro `~AjusteDecomisoLoteTests`: 18/18. Regresión `~AjusteDeStockTests`:
> 10/10. `has-pending-model-changes`: sin cambios pendientes (solo se tocó
> el archivo de tests). Árbol limpio tras el commit.

---

## Slice 12: Conteo (PR 12)

**Start**: slice 11 merged. **Finish**: conteo of a lot-effective articulo
counts per lot, acquiring every lock before deriving any delta; the
cross-cutting stock/stock_lotes invariants are now provable end to end
across all eight motivos. **Rollback**: revert the branch.

- [x] 12.1 Modify `src/Ways.Application/Stock/Contratos.cs`:
  `SolicitudDeConteo.Contada` widens to `decimal?`; add `Lotes:
  IReadOnlyList<ConteoDeLote>?`; `ConteoDeLote(IdLote, Contada)`;
  `ResultadoConteo.Lotes: IReadOnlyList<LoteContado>`. *(APPLY-RUN NOTE:
  both `SolicitudDeConteo.Lotes` and `ResultadoConteo.Lotes` carry a
  `= null` default to stay source-compatible with every positional caller
  already in the repo — matches decision 18's "source-compatible for every
  existing caller".)*
- [x] 12.2 Modify `ServicioDeStock.cs`: `ContarAsync` — exactly-one-of
  validation (`400 conteo_contada_y_lotes` if both or neither present);
  `conteo_lote_repetido` refusal on a duplicated `idLote` before any lock.
  *(APPLY-RUN NOTE: an empty `Lotes` array (`[]`) is treated the same as
  absent — `ExigirExactamenteUnaFormaDeConteo` checks `Count: > 0`, per
  dto-contract-honesty "a field with no actionable value is absent".)*
- [x] 12.3 Modify `ServicioDeStock.cs`: per-lot conteo, decision 12's
  split — **acquisition phase**: `BloquearYCrearSiFaltaStockAsync`
  (aggregate first), then each lot's `BloquearYCrearSiFaltaStockLoteAsync`
  ascending `id_lote`, no delta written yet; **application phase**: derive
  every delta, write `movimientos_stock` (`motivo = inventario`) + upsert
  `stock_lotes` per lot with a non-zero delta, aggregate accumulates the
  sum. *(APPLY-RUN NOTE: `BloquearYCrearSiFaltaStockLoteAsync` is new —
  same no-op `SET cantidad = stock_lotes.cantidad ... RETURNING` shape as
  `BloquearYCrearSiFaltaStockAsync`, one more key. `EjecutarConteoPorLoteAsync`
  mirrors `EjecutarConteoAsync`'s transaction/connection setup.)*
- [x] 12.4 Note: proposal decision 11's pre-approved degradation (`409
  conteo_lote_no_soportado`) exists for a delivery slice that ships an
  aggregate-only conteo without per-lot support. This slice ships per-lot
  conteo in full, so the refusal path is documented but not the primary
  behavior — keep the `409` branch reachable only if a future regression
  removes per-lot support. *(APPLY-RUN NOTE: no `409 conteo_lote_no_soportado`
  code was written — an unreachable branch with zero test coverage would be
  dead code, not a documented fallback. Doc-comment on `ContarAsync` records
  the decision instead.)*
- [x] 12.5 [P] Lock-acquisition-order test: every lock (aggregate no-op
  upsert, then each lot's no-op upsert ascending) is acquired before any
  delta write. *(design decision 12, proves the acquisition/application
  split)*
- [x] 12.6 [P] Zero-difference-lot-writes-nothing test. *(spec
  conteo-de-inventario: "A lot with no difference writes no row even when
  a sibling lot differs")* *(APPLY-RUN NOTE:
  `ConteoPorLoteTests.UnLoteSinDiferenciaNoEscribeFilaAunqueUnLoteHermanoDifiera`.)*
- [x] 12.7 [P] `conteo_contada_y_lotes` tests. *(spec: "Supplying both
  cantidad_contada and lotes is rejected", "Supplying neither
  cantidad_contada nor lotes is rejected")* *(APPLY-RUN NOTE: three tests,
  not two —
  `UnConteoConCantidadContadaYLotesEsRechazado`,
  `UnConteoSinCantidadContadaNiLotesEsRechazado`, plus
  `UnConteoConListaDeLotesVaciaEsRechazadoComoSiEstuvieraAusente` (an empty
  `Lotes: []` array counts as "absent", dto-contract-honesty).)*
- [x] 12.8 [P] Per-lot-derives-aggregate-delta test. *(spec: "A
  lot-effective conteo derives the aggregate delta from per-lot deltas")*
  *(APPLY-RUN NOTE:
  `ConteoPorLoteTests.UnConteoLoteEfectivoDerivaElDeltaAgregadoDeLosDeltasPorLote`
  — literal spec scenario numbers, L1 12→15/+3, L2 28→20/-8, agregado -5.)*
- [x] 12.9 [P] Never-fabricate-into-sin-identificar test. *(spec: "A
  lot-effective conteo never writes into the sin-identificar lot to absorb
  a difference")* *(APPLY-RUN NOTE:
  `ConteoPorLoteTests.UnConteoLoteEfectivoNuncaEscribeEnElLoteSinIdentificarParaAbsorberUnaDiferencia`
  — seeds the sin-identificar lot at saldo 0 and asserts it stays exactly
  0 after a differing per-lot count on a sibling lot.)*
- [x] 12.10 [P] `conteo_lote_repetido` test. *(APPLY-RUN NOTE:
  `ConteoPorLoteTests.UnConteoConUnIdLoteRepetidoEsRechazado`.)*
- [x] 12.11 [P] Aggregate-grain regression: a matching count still writes
  nothing. *(spec: "A matching count writes nothing")* *(APPLY-RUN NOTE:
  `ConteoPorLoteTests.UnConteoAgregadoDeContadaIgualALaActualSigueSinEscribirNada`
  — proves the pre-slice-12 aggregate (`Contada`) path is byte-identical
  after the `decimal?`/exactly-one-of widening.)*
- [x] 12.12 [P] **Cross-cutting invariant suite** (now provable with all
  eight motivos live), one long-form test per invariant asserting **every**
  row, not just totals: (1) `stock.cantidad = SUM(movimientos)` after a
  sequence covering all eight motivos including a `reclasificacion` pair
  *(spec stock: Cantidad Is Always The Sum Of Its Movimientos)*; (2)
  `stock_lotes.cantidad = SUM(movimientos with that lot)` after
  compra→venta→transferencia→NCX→anulación→conteo→decomiso *(spec
  lotes-y-vencimientos: Stock Lotes Balance And Its Two Invariants)*; (3)
  `SUM(stock_lotes) = stock.cantidad` for a reconciled lot-effective pair.
  *(APPLY-RUN NOTE: `InvarianteStockYStockLotesTests.cs`, three tests,
  invariant asserted after EACH step of the sequence (not only at the
  end), same discipline as `SaldoLedgerInvarianteTests` from stage 7: (1)
  `LaCantidadDeStockEsLaSumaDeSusMovimientosTrasUnaSecuenciaConLosOchoMotivos`
  — non-lot articulo through ajuste→compra→venta→transferencia→NCX→
  anulación→conteo→decomiso, THEN flips `controla_lote` and runs
  reconciliation to add the `reclasificacion` pair, asserting the
  aggregate invariant unmoved (decision 14: `stock` never touched by
  reconciliation) and that all eight `MotivoStock` values are present on
  the ledger; (2)
  `StockLotesCantidadEsLaSumaDeSusMovimientosConEseLoteTrasLaCadenaCompraVentaTransferenciaNcxAnulacionConteoDecomiso`
  — a single lot traced through the exact 7-step chain named by this task,
  asserted after each step at the origin PV (plus a bonus assertion at the
  destination PV after the transfer); (3)
  `LaSumaDeStockLotesIgualaElAgregadoParaUnParLoteEfectivoReconciliado` —
  pre-existing stock reconciled into the sin-identificar lot, then a
  dated-lot compra confirmed on top (proves the invariant holds under a
  MIX of lots, not only the trivial single-lot case), then a second
  reconciliation run (idempotence) re-checked. All 3 green against real
  Postgres.)*
- [x] 12.13 Gate guard: `dotnet ef migrations has-pending-model-changes` →
  no pending changes. *(Verified via `--project src/Ways.Infrastructure
  --startup-project src/Ways.Infrastructure` — "No changes have been made
  to the model since the last migration." No `Migraciones/`/`Configuraciones/`
  file touched by this slice, per the DB CHANGE GATE — `Contratos.cs`/
  `ServicioDeStock.cs` are Application-layer, no EF model surface.)*
> **JUDGMENT-DAY FIX NOTE (12.14, ronda 1, juez B)**: 2 gaps confirmados —
> uno BLOCKER de correctitud, uno secundario:
>
> 1. (BLOCKER) `ContarAsync`/`EjecutarConteoAsync` no chequeaba
>    `ReglaDeLotes.ControlEfectivo` antes de aceptar una `Contada` agregada:
>    un POST con solo `Contada` para un artículo lote-efectivo devolvía 200,
>    movía `stock.cantidad` y dejaba `stock_lotes` intacto — invariante 3
>    roto en silencio (probado empíricamente: 40→50 agregado, lotes quedan
>    en 40). El `409 conteo_lote_no_soportado` de la task 12.4 es la
>    degradación pre-aprobada para "el per-lot conteo no está implementado"
>    — no cubre este caso, donde SÍ está implementado. Fix:
>    `ExigirFormaDeConteoCoincideConControlDeLote`, guard en `ContarAsync`
>    ANTES de cualquier lock — `400 conteo_requiere_lotes` (código nuevo,
>    honesto, distinto del `409` de la degradación) para `Contada` contra
>    un artículo lote-efectivo; simetría inversa `400 conteo_no_aplica_lotes`
>    para `Lotes` contra un artículo SIN lote efectivo (¿ya existía? no —
>    mismo tratamiento que `lote_no_aplica` de `ResolverIdLoteEfectivoAsync`).
>    Spec `conteo-de-inventario` ENMENDADO (dos escenarios nuevos bajo
>    "Conteo Of A Lot-Effective Articulo...", marcados "Amended at slice-12
>    judgment-day"). Tests nuevos en `ConteoPorLoteTests.cs`:
>    `UnConteoAgregadoParaUnArticuloLoteEfectivoEsRechazado` (delta NO cero
>    a propósito — expone la escritura real si el guard fallara; la 12.11
>    original probaba delta CERO, que nunca llega a escribir nada
>    independientemente de la forma del conteo, escondiendo el gap) y
>    `UnConteoPorLoteParaUnArticuloSinLoteEfectivoEsRechazado`. La 12.11
>    original (`UnConteoAgregadoDeContadaIgualALaActualSigueSinEscribirNada`)
>    usaba (incorrectamente) `SembrarArticuloLoteEfectivoAsync` para un
>    escenario que su propio doc-comment describía como "sin control de
>    lote efectivo" — corregida a un nuevo helper
>    `SembrarArticuloSinLoteAsync` (`ControlaLote = false`), ahora
>    consistente con su propio contrato. Evidencia de mutación: guard
>    anulado (`if (false && ...)`) → `UnConteoAgregadoParaUnArticuloLoteEfectivoEsRechazado`
>    **FALLÓ** (200 real vs. 400 esperado); revertido, **GREEN**.
> 2. (secundario) `ExigirLotesDeConteoValidos` no validaba existencia/
>    pertenencia del `idLote` del desglose por lote — un `idLote` inexistente
>    solo se descubría dentro de la FK cruda del upsert no-op de
>    `BloquearYCrearSiFaltaStockLoteAsync`, un 500 crudo. Fix:
>    `ExigirLotesDeConteoExistenAsync`, SELECT-first contra
>    `ServicioDeLotes.LeerSaldosAsync` (mismo criterio que
>    `ResolverIdLoteEfectivoAsync`), ANTES de la transacción → `400
>    lote_invalido`. Test nuevo: `UnConteoPorLoteConUnIdLoteInexistenteEsRechazado`.
>    Evidencia de mutación: validación anulada (`if (false && ...)`) → el
>    test **FALLÓ** empíricamente con `500 InternalServerError` /
>    `SqlState 23503`, `fk_stock_lotes_lote` — exactamente el crudo que el
>    fix previene; revertido, **GREEN**.
>
> Nota adicional sin código (hallazgo 2(e) del juez B): el orden ascendente
> de adquisición del conteo por lote está under-tested para el orden
> cross-request concurrente entre dos conteos simultáneos del mismo par —
> mismo convoy-masking ya documentado en el slice 10 (una única transacción
> observada a la vez basta para probar el orden intra-request; el orden
> cross-request queda como convención probada por diseño, no por prueba
> directa, mismo criterio aceptado en slices previos).
>
> El gap sistémico de `ManejadorDeErrores` con excepciones ADO crudas (no
> `DbUpdateException`) — el mismo mecanismo detrás del hallazgo 2 de arriba
> — es repo-wide, no específico de este slice: se registra como follow-up
> del orquestador (chip ya spawneado), no se resuelve acá.
>
> Filtro `~ConteoPorLoteTests`: 12/12 (9 + 3 nuevos). Filtro
> `~InvarianteStockYStockLotesTests`: 3/3. Regresión combinada
> `~AjusteDeStockTests|~AjusteDecomisoLoteTests|~TransferenciaLoteTests|~TransferenciasYConteoDeInventarioTests`:
> 71/71. `has-pending-model-changes`: sin cambios pendientes. Árbol limpio
> tras el commit.

- [x] 12.14 Run `judgment-day`; fix; re-judge until clean. *(APPLY-RUN
  NOTE: judge B round 1 REJECT with a genuine BLOCKER — aggregate `Contada`
  against a lot-effective articulo returned 200 and silently broke
  invariant 3 (stock 40→50, stock_lotes stayed 40), the exact divergence
  decision 11 exists to prevent; the original 12.11 test HID it by seeding
  lot-effective with a zero delta. Plus the raw-500 on an unknown conteo
  idLote. Fix 7b88759: ExigirFormaDeConteoCoincideConControlDeLote (400
  conteo_requiere_lotes / 400 conteo_no_aplica_lotes, spec amended) +
  SELECT-first lote validation (400 lote_invalido) + the 12.11 test
  corrected. Judge B round 2 APPROVE (empirical re-probe: 400 and invariant
  intact). Judge A APPROVE with 2 MINORs: spec wording "before reaching the
  database" tightened to "before any lock" in this branch; recorded debt —
  the per-lot path lacks the aggregate path's defense-in-depth
  final!=contada loud check (consistency suggestion, very low risk).)*
- [x] 12.15 Branch `feat/stage12-slice12-conteo` off `main` (parent:
  slice 11); PR; merge stacked-to-main.

**Test plan**: acquisition order (12.5), zero-diff per lot (12.6),
exactly-one-of ×2 (12.7), aggregate-from-per-lot (12.8), no-fabrication
(12.9), duplicate lot (12.10), aggregate regression (12.11), cross-cutting
invariants ×3 (12.12).

**Verify**: `dotnet test --filter FullyQualifiedName~ConteoPorLote|FullyQualifiedName~InvarianteStock`

---

## Slice 13: Vencimientos (PR 13)

**Start**: slice 1 merged (independent front, parallel to 4, 5, 7, 10).
**Finish**: `GET /api/reportes/stock/vencimientos` classifies into four
states resolved in the PV's own zona horaria, with an `/export` sibling and
a `/resumen` tile feed. **Rollback**: revert the branch.

- [x] 13.1 Modify `src/Ways.Application/Reportes/ServicioDeReportesDeStock.cs`:
  `ObtenerVencimientosAsync` — lot rows with positive `stock_lotes.cantidad`,
  classified via `ReglaDeLotes.Clasificar` (four states incl. `SinFecha`),
  ordered `fecha_vencimiento ASC NULLS LAST`, `dias` defaults to the
  resolved `dias_alerta_vencimiento`; `ResolverZonaAsync` *(shipped as
  `ResolverContextoAsync` — broader name, it also resolves `idEmpresa` for
  the dias chain; rename noted at judgment-day, judge A MINOR-1)* resolves "hoy" in
  the PV's own `zona_horaria`, never UTC.
- [x] 13.2 Modify the same file: `ObtenerResumenDeVencimientosAsync` —
  Tablero tile counts (`vencido`/`por_vencer`/`sin_fecha`).
- [x] 13.3 Modify `src/Ways.Application/Reportes/ExportacionDeReportes.cs`:
  `De(Vencimientos, ctx)` mapper — **listing shape** (design decision 17):
  `COUNT(*) → refuse → single read with .Take(tope + 1)`, never an
  aggregate-row-count guard.
  *(Deviation, non-functional: the COUNT/refuse/Take pipeline lives in
  `ServicioDeReportesDeStock.ObtenerVencimientosParaExportacionAsync`, not
  inside the mapper itself — `De(Vencimientos, ctx)` stays pure/no-DB,
  matching this file's own documented "No Re-Query" invariant and the exact
  precedent of `ServicioDeHistoricoDeCajas.ListarCierresParaExportacionAsync`
  / `ServicioDeCuentaCorriente.ObtenerEstadoDeCuentaParaExportacionAsync`.
  The end-to-end shape — Count → refuse → Take(tope+1) → refuse — is
  unchanged; only which layer executes the SQL differs from a literal
  reading of the task line.)*
- [x] 13.4 Modify `src/Ways.Api/Endpoints/ReportesEndpoints.cs`: `GET
  /reportes/stock/vencimientos` (`LecturaDeReportes`), `GET
  .../vencimientos/export` (co-located, inherited policy), `GET
  .../vencimientos/resumen`.
- [x] 13.5 [P] **Mutation target**: `TimeZoneInfo.ConvertTime(reloj.Ahora,
  zona)` replaced with `reloj.Ahora.UtcDateTime` → the non-UTC
  classification test MUST fail (the lot flips `PorVencer → Vencido`);
  revert → green. *(spec: "'Hoy' is resolved in the punto de venta's own
  zona horaria, not UTC"; mutation-proof-tests)* Record evidence.
  **Evidence**: `VencimientosReporteTests.LaClasificacionSeResuelveEnLaZonaHorariaDelPuntoDeVentaNoEnUtc`
  — reloj pinned to `2026-08-13T01:30:00Z` (same instant as the spec
  scenario), PV zona `America/Argentina/Buenos_Aires` (default), lot
  `fecha_vencimiento = 2026-08-12`. Mutation applied to
  `ServicioDeReportesDeStock.ResolverContextoAsync` (`TimeZoneInfo.ConvertTime(reloj.Ahora,
  zona).DateTime` → `reloj.Ahora.UtcDateTime`): test FAILED — `Hoy` expected
  `12/08/2026`, actual `13/08/2026` (the lot would flip `PorVencer` →
  `Vencido`). Reverted → all 8 filtered tests green. The lock-step (dias,
  boundary-inclusive) domain-level `Clasificar` boundary math itself was
  already covered by `ReglaDeLotesTests` from an earlier slice; this
  mutation isolates the zone-conversion clause specifically, at the
  integration/HTTP level.
- [x] 13.6 [P] Classification-boundary tests: `vencido`/`por_vencer`/
  `vigente`/`sin_fecha`-counts-in-totals. *(spec: "A lot past its expiry
  classifies as vencido", "A lot within the alert horizon classifies as
  por_vencer", "A lot beyond the horizon classifies as vigente", "The
  sin-identificar lot appears in the report as sin_fecha and counts toward
  the totals")* **Test**: `VencimientosReporteTests.ClasificaLosCuatroEstadosYElSinFechaCuentaEnLosTotales`
  — 4 lots (`vencido`/`por_vencer`/`vigente`/`sin_fecha`, dias_alerta default
  30, boundary dates matching `ReglaDeLotesTests`), asserts each row's
  `Estado` plus the `/resumen` tile counts (1/1/1) consistent with the
  report.
- [x] 13.7 [P] Zero-balance-lot-excluded test. *(spec: "A zero-balance lot
  never appears in the report")* **Test**: `VencimientosReporteTests.UnLoteConSaldoCeroNuncaApareceEnElReporte`.
- [x] 13.8 [P] Export equality, cell by cell (`mutation-proof-tests` rule
  6): different values per row and column so a swap is detectable. *(spec:
  "The export sibling's figures equal the JSON endpoint's")* **Test**:
  `VencimientosExportTests.ElExportEsIgualAlEndpointJsonFilaPorFilaEnTodasLasColumnas`
  — 2 lots with distinct values on every column (one dated, one
  sin-identificar to cover the blank-date cell), all 7 columns compared per
  row.
- [x] 13.9 [P] Cap + `+1` race backstop (listing shape, stage-11
  precedent): `COUNT(*) → refuse` with the actual row count named. **Test**:
  `VencimientosExportTests.UnaExportacionQueSuperaElTopeSeRechazaConLaCantidadReal`
  — tope forced to 3 via `WithWebHostBuilder`, 4 lots seeded, asserts `400
  exportacion_demasiado_grande` naming `4`.
- [x] 13.10 [P] 403 test. *(spec: "A Vendedor is rejected from the
  vencimientos report and its export")* **Tests**:
  `VencimientosReporteTests.UnVendedorEsRechazadoDelReporteDeVencimientos`,
  `VencimientosExportTests.UnVendedorEsRechazadoDelExportDeVencimientos`.
- [x] 13.11 Gate guard: `dotnet ef migrations has-pending-model-changes` →
  no pending changes. Confirmed clean: "No changes have been made to the
  model since the last migration." (this slice touches only Application/Api
  layers — no `Ways.Domain`/EF configuration edits).
- [x] 13.12 Run `judgment-day`; fix; re-judge until clean. *(APPLY-RUN NOTE:
  round 1 — judge B APPROVE-with-MAJOR: the `dias ?? resolved` override
  branch had zero coverage on both report and export; all 8 mutation probes
  killed (zone flip, boundary, sin_fecha exclusion, zero-balance, cell swap,
  row drop, cap truncation, tile miscount). Fix 59474d7: 2 override tests
  with mutation evidence. Round 2 judge B APPROVE; judge A APPROVE with 4
  MINORs — rename note added above; accepted debt: the export-dias test
  uses UtcNow with a 5-day safety margin instead of RelojFijo (verified
  safe across the 00-03 UTC window by both judges), and the report's
  ORDER BY has no id tiebreaker (display-only, spec-silent).)*
- [x] 13.13 Branch `feat/stage12-slice13-vencimientos` off `main` (parent:
  slice 1); PR; merge stacked-to-main.

**Test plan**: non-UTC mutation (13.5), classification ×4 (13.6),
zero-balance exclusion (13.7), export equality (13.8), cap backstop (13.9),
403 (13.10).

**Verify**: `dotnet test --filter FullyQualifiedName~Vencimientos`

---

## Slice 14: Web Operación (PR 14)

**Start**: slices 5+8 merged. **Finish**: the POS lot picker pre-selects
FEFO and completes the happy path with zero keystrokes; the compra editor
captures lot input per line. **Rollback**: revert the branch — no backend
change.

- [ ] 14.1 Modify `src/Ways.Web/src/paginas/Pos.tsx`: lot picker component
  fed by `GET /api/stock/lotes` (pre-selected via `sugerido`); a line for a
  lot-effective articulo omits `idLote` by default; `loteVencido` renders
  as a prominent warning, never a block.
- [ ] 14.2 Modify `src/Ways.Web/src/paginas/CompraEditor.tsx`: lot input
  fields (`codigoLote`, `fechaVencimiento`) per draft line for a
  lot-effective articulo; the incomplete-line counter includes them.
- [ ] 14.3 Modify `src/Ways.Web/src/api/tipos.ts` / `catalogos.ts`:
  mirrored contracts — `LineaDeVenta.idLote`, `ItemEmitido.idLote`/
  `codigoLote`/`loteVencido`, `LineaDeCompraSolicitada.codigoLote`/
  `fechaVencimiento`, `LoteListado`, `controlaLote` descriptor field.
- [ ] 14.4 [P] Picker `sugerido`-preselection test.
- [ ] 14.5 [P] **Stale-response test** (`mutation-proof-tests` rule 7): a
  stale picker fetch resolves after the operator changed line — resolved
  inside `act`, asserted synchronously after the flush.
- [ ] 14.6 [P] Double-click test: exactly one `fetch` on the picker despite
  a double-click (`react-async-state` busy/re-entrancy guard).
- [ ] 14.7 [P] Incomplete-line counter test on `CompraEditor` (lot fields
  count toward incompleteness).
- [ ] 14.8 [P] `web-descriptor-tests` for the `Pos.tsx` picker and
  `CompraEditor`'s lot input, colocated `*.test.tsx`.
- [ ] 14.9 Gate guard: `dotnet ef migrations has-pending-model-changes` →
  no pending changes (web-only slice).
- [ ] 14.10 Run `judgment-day`; fix; re-judge until clean.
- [ ] 14.11 Branch `feat/stage12-slice14-web-operacion` off `main` (parent:
  slices 5+8); PR; merge stacked-to-main.

**Test plan**: preselection (14.4), stale-response (14.5), double-click
(14.6), incomplete-line (14.7), descriptor tests (14.8).

**Verify**: `npm run test -- Pos CompraEditor`

---

## Slice 15: Web Back-Office (PR 15)

**Start**: slices 12+13 merged. **Finish**: the Vencimientos screen, the
`controlaLote` flag on the articulo editor, the two parametro toggles, and
the lot column on transfers/conteo all ship. **Rollback**: revert the
branch — no backend change.

- [x] 15.1 Create `src/Ways.Web/src/paginas/Vencimientos.tsx`: report
  screen — filters, four-state classification badges (incl. `sin_fecha`),
  download button.
- [x] 15.2 Modify `src/Ways.Web/src/App.tsx` + `componentes/Layout.tsx`:
  `/reportes/stock/vencimientos` route (`LecturaDeReportes`) + nav entry.
- [x] 15.3 Modify `src/Ways.Web/src/paginas/Articulos.tsx`: `controlaLote`
  toggle on the articulo editor.
- [x] 15.4 Modify `src/Ways.Web/src/paginas/Parametros.tsx`:
  `lotesHabilitado` + `diasAlertaVencimiento` toggles. *(APPLY-RUN NOTE:
  `PARAMETROS_CONOCIDOS` gained a fourth `tipo` — `'booleano'` — the first
  boolean-typed entry of the registry; `Parametros.tsx` gained a checkbox
  branch alongside the existing texto/entero branches, JSON-serializing the
  raw `true`/`false` literal, never a quoted string.)*
- [x] 15.5 Modify `src/Ways.Web/src/paginas/Transferencias.tsx`: lot column
  + picker per line, incomplete-line counter extended. *(APPLY-RUN NOTE:
  closed the slice-10 debt — `key={l.idArticulo}` on the result table
  replaced by a composite `${idArticulo}-${idLote ?? 'sin-lote'}` key; the
  per-line picker pre-selects `sugerido` (design decisión 19) and can be
  cleared back to "Auto (FEFO)"; `LineaDeTransferenciaFormulario` gained a
  UI-only `controlaLote` field (never sent to the backend) to decide
  per-row whether the picker renders. `mutation-proof-tests` evidence on
  the composite key: a plain content-only assertion (two rows, correct
  cantidades) passed EVEN with the mutation reverted to `key={l.idArticulo}`
  — a first controlled render never shows stale content, the exact
  confound rule 3 warns about. Re-routed below the confound: spy on
  `console.error` and assert the ABSENCE of React's "Encountered two
  children with the same key" warning, which fires ONLY on the collision.
  Mutation applied → RED (warning captured, assertion failed) → reverted →
  GREEN, full `Transferencias.test.tsx` suite 12/12.)*
- [x] 15.6 Modify `src/Ways.Web/src/paginas/ConteoDeInventario.tsx`:
  per-lot counted-total input UI, exactly-one-of enforcement mirrored
  client-side. *(APPLY-RUN NOTE: the aggregate "Cantidad contada" field and
  the per-lot grid are structurally mutually exclusive in the render tree
  — never both mounted — which is the client-side mirror of the backend's
  `400 conteo_contada_y_lotes`; an incomplete-lot counter mirrors
  `Transferencias.tsx`'s pattern, per react-async-state rule 10.)*
- [x] 15.7 Modify `src/Ways.Web/src/paginas/Tablero.tsx`: vencimientos tile
  (counts + link), completing slice 13's backend groundwork. *(APPLY-RUN
  NOTE: the tile requires a concrete punto de venta — `/vencimientos/resumen`
  doesn't accept "Todos" — and shows a neutral aviso instead of a query
  with a manufactured PV when none is chosen.)*
- [x] 15.8 [P] `web-descriptor-tests` for `Vencimientos.tsx`,
  `Articulos.tsx` (`controlaLote`), `Parametros.tsx` (2 toggles),
  `Transferencias.tsx`, `ConteoDeInventario.tsx`, the Tablero tile.
- [x] 15.9 [P] Incomplete-line-counter test replicated across both
  `Transferencias` and `ConteoDeInventario` grids (mirrors slice 14.7's
  `CompraEditor` pattern).
- [x] 15.10 [P] `controlaLote` coercion test (`aAlta`/`aEdicion` boolean
  coercion). *(APPLY-RUN NOTE — task wording amended: `controlaLote` is a
  plain boolean toggle (`e.target.checked`), not a nullable field — the
  `'' → null` half of the task's literal wording doesn't apply to this
  field's shape (no empty-string state is representable by a checkbox).
  Delivered instead: three tests proving the checkbox never leaks a string/
  `"on"`/`1` — `true`/`false` travel as JSON booleans end-to-end through
  `aEdicion`, both toggled and left untouched.)*
- [x] 15.11 Gate guard: `dotnet ef migrations has-pending-model-changes` →
  no pending changes (web-only slice). *(Verified via `--project
  src/Ways.Infrastructure --startup-project src/Ways.Infrastructure`, same
  precedent as slices 4/5: "No changes have been made to the model since
  the last migration.")*
- [ ] 15.12 Run `judgment-day`; fix; re-judge until clean.
- [ ] 15.13 Branch `feat/stage12-slice15-web-backoffice` off `main`
  (parent: slices 12+13); PR; merge stacked-to-main.

**Test plan**: descriptor tests ×6 (15.8), incomplete-line ×2 (15.9),
`controlaLote` coercion (15.10).

**Verify**: `npm run test -- Vencimientos Articulos Parametros Transferencias ConteoDeInventario Tablero`

---

## Global Cross-Slice Tasks

- **`dto-contract-honesty` compliance**: every new DTO field named in
  design.md's Interfaces/Contracts section has a documented destination
  (`IdLote → snapshot`, `CodigoLote → ticket projection`, `LoteVencido →
  ReglaDeLotes.EstaVencido`, etc.) — enforced per-slice above at the point
  each field is introduced, not deferred to a sweep.
- **`db-error-backstops` compliance**: every `23505` target
  (`ux_lotes_articulo_codigo`) has its mapping written **before** the
  endpoints that can race it, with SQLSTATE asserted — slices 1, 3, 5.
- **`mutation-proof-tests` compliance**: the nine named mutation targets in
  design.md's table are each placed in exactly one slice above (§
  Orchestrator Decisions #6); every one requires recorded apply-time
  evidence (mutation applied → test failed → reverted → green) in the PR
  body.
- **`react-async-state`/`web-descriptor-tests` compliance**: slices 14-15
  are the only web-touching slices; every new/modified screen ships a
  colocated descriptor test in the same slice, never deferred.
- **Containment discipline carries the same repo-wide gap noted in
  stage-11** (no CI lint rule for source-scan-style containment tests) —
  not reopened here, `lotes-y-vencimientos` introduces no new
  cross-boundary containment risk.
- **`ways_owner` testcontainer-superuser weakness** (state.yaml, carried
  from prior stages): slice 1's RLS tests run over the **`ways_app`**
  connection specifically to route around it — recorded as adequately
  covered for `lotes`/`stock_lotes`, the repo-wide weakness stays open.

---

## Dependency Summary

```
Slice 1 (esquema, size:exception)
  └─ Slice 2 (activación)
       └─ Slice 3 (servicio de lotes)
            ├─ Slice 4  (reconciliación)                          ─┐
            ├─ Slice 5  (recepción) → Slice 6 (compra anulación)   │
            ├─ Slice 7  (venta plan FEFO) → Slice 8 (venta         │ parallel
            │     escritura) → Slice 9 (NCX)                       │ fronts,
            └─ Slice 10 (transferencias) → Slice 11 (ajuste/       │ merge in
                  decomiso) → Slice 12 (conteo)                   ─┘ any order
  Slice 1 ──────────────────────────────────────────→ Slice 13 (vencimientos)
  Slice 5 + Slice 8 → Slice 14 (web operación)
  Slice 12 + Slice 13 → Slice 15 (web back-office)
```

Merge order is strictly `1 → 2 → 3 → (4, 5→6, 7→8→9, 10→11→12, 13 in any
order that respects the arrows)`, stacked-to-main. The one cross-front
coupling is task 10.12's joint deadlock proof, which needs slice 8's
per-lot write to exist — slice 10 is downstream of slice 8 in every valid
merge order, so this is satisfied by construction, not by luck.

---

## Review Workload Forecast

| Field | Value |
|-------|-------|
| Estimated changed lines | ~4 650 total (15 slices: 430/260/300/330/330/180/280/330/200/360/280/250/320/400/400) |
| 400-line budget risk | High overall — slice 1 is a declared `size:exception` (~430); slices 10, 14 and 15 sit closest to the cap and are the ones to watch during apply, per design.md's own risk call |
| Chained PRs recommended | Yes |
| Suggested split | 15 PRs, stacked-to-main, per the Suggested Work Units table above |
| Delivery strategy | auto-chain (already resolved, `state.yaml`) |
| Chain strategy | stacked-to-main |

Per-slice budget risk: 1 **High (⚠ size:exception, ~430)** · 2 Low (~260) ·
3 Low (~300) · 4 Low (~330) · 5 Low (~330) · 6 Low (~180) · 7 Low (~280) ·
8 Low (~330) · 9 Low (~200) · 10 **Medium (~360)** · 11 Low (~280) ·
12 Low (~250) · 13 Low (~320) · 14 **Medium (~400)** ·
15 **Medium (~400)**. As in every prior stage, overflow is expected to come
from **test depth** (concurrency fixtures, mutation-evidence tests,
invariant suites), not scope — if slice 10, 14 or 15 grows past 400 during
apply, split the descriptor/mutation-evidence tests into their own commit
within the same PR before splitting the slice itself.

Decision needed before apply: No
Chained PRs recommended: Yes
Chain strategy: stacked-to-main
400-line budget risk: High
