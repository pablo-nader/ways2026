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

- [ ] 4.1 Extend `ServicioDeLotes.cs`: `ReconciliarAsync(idArticulo?,
  idPuntoVenta?)` — one transaction **per pair**, ascending: (1)
  `ResolverSinIdentificarAsync` (lotes before stock, decision 3);
  (2) `BloquearYCrearSiFaltaStockAsync` (aggregate lock first); (3)
  `SELECT COALESCE(SUM(cantidad),0) FROM stock_lotes ... ORDER BY id_lote
  FOR UPDATE`; (4) `residuo = agregado - sumaLotes`; (5) `residuo == 0` ⇒
  commit, write nothing; (6) else, two `movimientos_stock` rows
  (`motivo = reclasificacion`, net zero) + `UpsertStockLoteAsync` on the
  sin-identificar lot. `stock` is **never** touched.
- [ ] 4.2 Modify `src/Ways.Application/Articulos/ServicioDeArticulos.cs`:
  detect `controla_lote false → true` on save; trigger
  `ReconciliarAsync(idArticulo, null)` scoped to every PV of every
  lot-enabled empresa of the tenant.
- [ ] 4.3 Modify `src/Ways.Application/Parametros/ServicioDeParametros.cs`:
  detect `lotes_habilitado false → true` on save; trigger
  `ReconciliarAsync(null, ...)` scoped to already-`controla_lote`-flagged
  articulos × that empresa's PVs.
- [ ] 4.4 Modify `StockEndpoints.cs`: `POST /api/stock/lotes/reconciliacion`
  (`GestionDeCatalogo`), `SolicitudDeReconciliacion`,
  `ResultadoDeReconciliacion`.
- [ ] 4.5 [P] Net-zero proof: reconciliation writes a pair summing to
  zero, `stock.cantidad` unaffected, sin-identificar `stock_lotes.cantidad`
  becomes the residue. *(spec: "Activation reconciles existing stock into
  the sin-identificar lot")*
- [ ] 4.6 [P] **Mutation target**: delete the `residuo == 0 ⇒ write
  nothing` guard → the idempotence test (asserting the `movimientos_stock`
  **row count** is unchanged on a second run) MUST fail; revert → green.
  *(spec: "A second reconciliation run is a no-op"; mutation-proof-tests)*
  Record evidence.
- [ ] 4.7 [P] Self-heal test: sell into an unreconciled pair (drives the
  sin-identificar lot negative), then reconcile, assert `SUM(stock_lotes) =
  stock.cantidad` afterward.
- [ ] 4.8 [P] `motivo`-discrimination test: reconciliation rows always
  `motivo = reclasificacion`, never `ajuste`. *(spec: "Reclasificación
  never uses motivo ajuste")*
- [ ] 4.9 [P] Zero-residue-never-violates-CHECK test. *(spec: "A
  zero-cantidad reclasificación row never violates the non-zero CHECK")*
- [ ] 4.10 [P] Activation-trigger tests: `controla_lote` flip via
  `ServicioDeArticulos` triggers reconciliation across every PV of
  lot-enabled empresas; `lotes_habilitado` flip via `ServicioDeParametros`
  triggers reconciliation across already-flagged articulos.
- [ ] 4.11 Gate guard: `dotnet ef migrations has-pending-model-changes` →
  no pending changes.
- [ ] 4.12 Run `judgment-day`; fix; re-judge until clean.
- [ ] 4.13 Branch `feat/stage12-slice4-reconciliacion` off `main` (parent:
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
- [ ] 5.14 Run `judgment-day`; fix; re-judge until clean.
- [ ] 5.15 Branch `feat/stage12-slice5-recepcion` off `main` (parent:
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

- [ ] 6.1 Modify `ServicioDeCompras.cs`: `EjecutarAnulacionAsync` reorders
  `.OrderBy(m => m.IdArticulo).ThenBy(m => m.IdLote)`, copies
  `original.IdLote`; **two** mandatory checks — aggregate `nueva < 0` AND
  per-lot `nuevaDelLote < 0` — both raise `409
  compra_anulacion_stock_negativo`.
- [ ] 6.2 [P] **Mutation target**: `if (clave.IdLote is not null && delta <
  0 && nueva < 0) throw` — delete the `if` → the per-lot insufficiency test
  (seeded with a **sufficient aggregate** across two lots) MUST fail;
  revert → green. *(spec: "Anulación refused by a lot-level underflow
  despite a sufficient aggregate"; mutation-proof-tests)* Record evidence.
- [ ] 6.3 [P] Exact reversal test. *(spec: "Anulación reverses a
  lot-bearing item into its exact lot")*
- [ ] 6.4 [P] Aggregate refusal regression, unaffected by the lot
  dimension. *(spec: "Anulación refused when the goods were already sold")*
- [ ] 6.5 [P] `costo_nominal`-not-reverted regression (unaffected by this
  slice).
- [ ] 6.6 Gate guard: `dotnet ef migrations has-pending-model-changes` →
  no pending changes.
- [ ] 6.7 Run `judgment-day`; fix; re-judge until clean.
- [ ] 6.8 Branch `feat/stage12-slice6-compra-anulacion` off `main` (parent:
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

- [ ] 7.1 Modify `src/Ways.Application/Ventas/ServicioDeVentas.cs`:
  `EmitirAsync` decide phase — immediately after `MaterializarItems`
  (`articuloPorId` already loaded), compute `lineasConLote`; if non-empty,
  call `servicioDeLotes.LeerSaldosAsync(puntoVenta.Id, articulos,
  lotesPedidos)` — the one new round trip.
- [ ] 7.2 Modify `ServicioDeVentas.cs`: per-line resolution —
  `ReglaDeLotes.ElegirFefo` over saldos; a supplied `idLote` is validated
  against `saldos` (exists, belongs to the articulo, not soft-deleted) or
  rejected `400 lote_invalido`; an omitted `idLote` defaults via
  `ElegirFefo`, a `null` result (no lot with positive balance) resolves the
  sin-identificar lot via `ResolverSinIdentificarAsync` (raw
  `ExecuteScalarAsync`, invisible to the round-trip counter).
- [ ] 7.3 Modify `LineaDeVenta`/`PlanDeVenta`/`ItemEmitido` contracts:
  `LineaDeVenta.IdLote` (optional input), `ItemEmitido.IdLote`/
  `CodigoLote`/`LoteVencido` (output); the plan carries the resolved lot,
  immutable once decided.
- [ ] 7.4 [P] Query-count test: module on + ≥1 lot-controlled articulo in
  cart → `17`. *(spec lotes-y-vencimientos: "Module on with a
  lot-controlled articulo nets zero round-trip change")*
- [ ] 7.5 [P] Omitted-`idLote`-picks-FEFO test. *(spec: "An omitted idLote
  resolves to the nearest-expiry dated lot")*
- [ ] 7.6 [P] End-to-end sin-identificar-first proof (Domain-level mutation
  target already in slice 2; this is the write-path confirmation). *(spec:
  "The sin-identificar lot is offered before every dated lot")*
- [ ] 7.7 [P] Supplied-`idLote`-honoured test. *(spec: "A supplied idLote
  is honoured even when it is not the FEFO pick")*
- [ ] 7.8 [P] Invalid-`idLote` test → `400 lote_invalido`. *(spec: "An
  invalid supplied idLote is rejected")*
- [ ] 7.9 [P] No-auto-split test. *(spec: "A lot running short still
  completes the line, never auto-splitting")*
- [ ] 7.10 [P] Regression: module on, no lot-controlled articulo in cart →
  still `16`, no FEFO query. *(spec: "Module on with no lot-controlled
  articulo in the cart issues no FEFO query")*
- [ ] 7.11 [P] Legacy-client compatibility test. *(spec comprobantes-venta:
  "A client that knows nothing about lots still transacts correctly")*
- [ ] 7.12 [P] Mixed-cart test (lot-controlled + non-lot line in one
  cart). *(spec: "A cart with a lot-controlled and a non-lot articulo mixes
  both paths")*
- [ ] 7.13 Gate guard: `dotnet ef migrations has-pending-model-changes` →
  no pending changes.
- [ ] 7.14 Run `judgment-day`; fix; re-judge until clean.
- [ ] 7.15 Branch `feat/stage12-slice7-venta-plan-fefo` off `main` (parent:
  slice 3); PR; merge stacked-to-main.

**Test plan**: query count ×2 (7.4, 7.10), FEFO resolution ×4 (7.5-7.9),
compatibility (7.11), mixed cart (7.12).

**Verify**: `dotnet test --filter FullyQualifiedName~PlanDeVentaFefo`

---

## Slice 8: Venta Escritura (PR 8)

**Start**: slice 7 merged. **Finish**: per-lot writes land inside the
pinned lock order; the item snapshot freezes `id_lote`; anulación reverses
the exact lot. **Rollback**: revert the branch.

- [ ] 8.1 Modify `ServicioDeVentas.cs`: `EjecutarTransaccionAsync` step 5 —
  loop `OrderBy(IdArticulo).ThenBy(IdLote)`; `InsertarMovimientoStockAsync`
  gains `idLote`; `UpsertStockAsync` (aggregate, `id_lote NULL`, always
  first); if `item.IdLote` present, `UpsertStockLoteAsync` (new private
  method, same `INSERT ... ON CONFLICT DO UPDATE ... RETURNING` shape as
  `UpsertStockAsync`).
- [ ] 8.2 Modify the `AddRange` items block: `ItemComprobanteVenta.IdLote =
  i.IdLote` — frozen snapshot, never re-derived.
- [ ] 8.3 Modify `ServicioDeVentas.cs`: `EjecutarAnulacionAsync` reorders
  `.OrderBy(m => m.IdArticulo).ThenBy(m => m.IdLote)`, the inverse movement
  copies `original.IdLote`, the `stock_lotes` upsert mirrors it — no
  lookup, no re-derivation.
- [ ] 8.4 [P] Invariant test: `stock.cantidad` and `stock_lotes.cantidad`
  both correct after a venta + anulación sequence.
- [ ] 8.5 [P] **Mutation target**: `ItemComprobanteVenta.IdLote = i.IdLote`
  replaced with `null` → the exact-anulación test (asserting the reversal's
  `id_lote` **and** the resulting per-lot balance) MUST fail; revert →
  green. *(spec comprobantes-venta: "A lot-effective line freezes its
  resolved lot onto the snapshot", "Anulación of a lot-bearing sale
  reverses the exact lot"; mutation-proof-tests)* Record evidence.
- [ ] 8.6 [P] Lock-order test: the `stock` row upserts before any
  `stock_lotes` row for the same `(articulo, PV)`. *(spec stock: "A
  checkout locks stock before stock_lotes for the same pair")*
- [ ] 8.7 [P] **Mutation target (half A, deadlock)**: `.ThenBy(c =>
  c.IdLote.HasValue).ThenBy(c => c.IdLote ?? 0)` in the checkout ordering —
  delete it and confirm the ordering test now fails on a hand-built key
  set. The **joint** checkout-vs-reverse-transfer deadlock proof itself is
  deferred to slice 10 (task 10.12), once `ServicioDeStock`'s transfer
  write also exists — recorded as an explicit cross-slice dependency, not a
  gap.
- [ ] 8.8 [P] Non-lot-articulo regression: no lot on the item or the
  movement. *(spec comprobantes-venta: "A non-lot articulo's item never
  carries a lot"; spec stock: "A non-lot articulo's movement never carries
  a lot")*
- [ ] 8.9 Gate guard: `dotnet ef migrations has-pending-model-changes` →
  no pending changes.
- [ ] 8.10 Run `judgment-day`; fix; re-judge until clean.
- [ ] 8.11 Branch `feat/stage12-slice8-venta-escritura` off `main` (parent:
  slice 7); PR; merge stacked-to-main.

**Test plan**: invariant (8.4), snapshot mutation (8.5), lock order (8.6),
ordering mutation half (8.7), non-lot regression (8.8).

**Verify**: `dotnet test --filter FullyQualifiedName~VentaEscrituraLote`

---

## Slice 9: NCX (PR 9)

**Start**: slice 8 merged. **Finish**: an NCX line for a lot-effective
articulo requires an explicit `idLote`; the response carries a
`loteVencido` warning, never a block. **Rollback**: revert the branch.

- [ ] 9.1 Modify `ServicioDeVentas.cs`: NCX validation — lot-effective
  articulo without `idLote` → `400 lote_requerido` before the transaction;
  FEFO defaulting is refused for NCX lines (returns are not "oldest
  first").
- [ ] 9.2 Modify the POS-facing contract: suggestion source — from
  `id_comprobante_asociado`'s item snapshot when present, else the
  articulo's existing lots; the sin-identificar lot stays a valid explicit
  choice.
- [ ] 9.3 Modify `ServicioDeVentas.cs`: `ItemEmitido.LoteVencido =
  ReglaDeLotes.EstaVencido(...)` computed for TX and NCX lines alike; an
  expired-lot sale/return is accepted with the flag set, never blocked.
- [ ] 9.4 [P] `lote_requerido`-on-NCX test. *(spec comprobantes-venta: "An
  NCX line for a lot-effective articulo requires idLote")*
- [ ] 9.5 [P] Suggested-`idLote`-from-snapshot test. *(spec: "idLote is
  suggested from the associated comprobante's snapshot")*
- [ ] 9.6 [P] Standalone-devolución-sin-identificar-accepted test. *(spec:
  "idLote is required even without an associated comprobante")*
- [ ] 9.7 [P] Return-into-expired-lot-permitted test. *(spec: "Returning
  into an expired lot is permitted")*
- [ ] 9.8 [P] Expired-lot-sale-warns-never-blocks test. *(spec: "A sale of
  an explicitly expired lot succeeds with a warning")*
- [ ] 9.9 [P] FEFO-prefers-non-expired-lot test. *(spec: "FEFO prefers a
  non-expired lot when one has stock")*
- [ ] 9.10 Gate guard: `dotnet ef migrations has-pending-model-changes` →
  no pending changes.
- [ ] 9.11 Run `judgment-day`; fix; re-judge until clean.
- [ ] 9.12 Branch `feat/stage12-slice9-ncx` off `main` (parent: slice 8);
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

- [ ] 10.1 Modify `src/Ways.Application/Stock/ServicioDeStock.cs`:
  `ClaveDeStock` widens (`IdLote`, `IdLoteDelMovimiento`);
  `ConstruirClavesOrdenadas` — per lot-effective line, 4 keys (aggregate +
  lot at origen, aggregate + lot at destino); order
  `.OrderBy(IdArticulo).ThenBy(IdPuntoVenta).ThenBy(IdLote.HasValue).ThenBy(IdLote ?? 0)`.
- [ ] 10.2 Modify `ServicioDeStock.cs`: pre-transaction phase — read
  `stock_lotes` of the origin PV for requested articulos, FEFO-default
  omitted lots via `ReglaDeLotes.ElegirFefo`, apply decision 11's
  `(IdArticulo, IdLote)` duplicate refusal **after** defaulting;
  `transferencia_lote_vencido` check alongside `ResolverArticuloAsync`.
- [ ] 10.3 Modify `ServicioDeStock.cs`: transaction loop — at an aggregate
  element, insert the ledger row (carrying `IdLoteDelMovimiento`) + upsert
  `stock`; at a lot element, upsert `stock_lotes` only. Both `RETURNING`
  values checked for negativity → `409
  stock_insuficiente_para_transferencia` (aggregate check unchanged, lot
  check new).
- [ ] 10.4 [P] **Mutation target**: `.ThenBy(c => c.IdLote.HasValue).ThenBy(c
  => c.IdLote ?? 0)` deleted in `ConstruirClavesOrdenadas` → the
  transfer-vs-reverse-transfer deadlock test MUST fail; revert → green.
  Record evidence.
- [ ] 10.5 [P] A→B vs. B→A concurrency test, write-site 3: both transfers
  complete, no `40P01`.
- [ ] 10.6 [P] Per-lot insufficiency with a sufficient aggregate. *(spec
  transferencias-de-stock: "A lot-level underflow is refused even with a
  sufficient aggregate")*
- [ ] 10.7 [P] Lot-travels test. *(spec: "A lot-effective articulo transfer
  moves the same lot at both ends")*
- [ ] 10.8 [P] Omitted-`idLote`-resolves-via-FEFO test. *(spec: "An omitted
  idLote resolves via FEFO at transfer time")*
- [ ] 10.9 [P] `transferencia_lote_vencido` tests. *(spec: "Transferring an
  explicitly expired lot is refused", "A non-expired lot transfers
  normally")*
- [ ] 10.10 [P] Duplicate-line detection ×3. *(spec: "Two lines of the same
  articulo with different explicit lots are accepted", "Two lines
  resolving to the same explicit lot are rejected", "Two lines both
  omitting idLote that resolve to the same FEFO lot are rejected")*
- [ ] 10.11 [P] Single-ascending-order test over both origin and
  destination lot rows. *(spec: "A single ascending order covers both
  origin and destination lot rows")*
- [ ] 10.12 [P] **Joint deadlock proof** (completes the pairing opened at
  slice 8.7): a checkout selling lot 7 of articulo 40 at PV 1, concurrent
  with a transferencia moving the same lot 7 of articulo 40 from PV 1 to
  PV 2 — both complete, no deadlock. *(spec stock: "A concurrent checkout
  and reverse transfer of the same articulo and lots do not deadlock")*
- [ ] 10.13 Gate guard: `dotnet ef migrations has-pending-model-changes` →
  no pending changes.
- [ ] 10.14 Run `judgment-day`; fix; re-judge until clean.
- [ ] 10.15 Branch `feat/stage12-slice10-transferencias` off `main`
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

- [ ] 11.1 Modify `ServicioDeStock.cs`: `EjecutarAjusteAsync` — a
  lot-effective articulo requires `idLote` (`400 lote_requerido`), a
  non-lot articulo refuses it (`400 lote_no_aplica`); aggregate upsert then
  lot upsert, in that order; no negativity refusal (ajuste is the
  correction operation).
- [ ] 11.2 Create `EjecutarDecomisoAsync` in `ServicioDeStock.cs`
  (structurally `EjecutarAjusteAsync` with three deltas): `motivo =
  Decomiso`; client-supplied `cantidad` is positive, negated server-side;
  the `RETURNING` of the lot upsert (or aggregate for a non-lot articulo)
  checked `< 0` → `409 stock_insuficiente_para_decomiso`; `observaciones`
  mandatory (`ExigirObservaciones` reused verbatim).
- [ ] 11.3 Modify `StockEndpoints.cs`: `POST /api/stock/decomiso`,
  `Politicas.GestionDeCatalogo` stacked over `OperacionDePos`.
- [ ] 11.4 [P] **Mutation target**:
  `.RequireAuthorization(Politicas.GestionDeCatalogo)` deleted on
  `/stock/decomiso` → the Vendedor-403 test MUST fail (the group's
  `OperacionDePos` alone admits Vendedor); revert → green. *(spec:
  "Vendedor is blocked from decomiso"; mutation-proof-tests)* Record
  evidence.
- [ ] 11.5 [P] `stock_insuficiente_para_decomiso` test. *(spec: "A decomiso
  that would go negative is refused")*
- [ ] 11.6 [P] Sign-discipline test: positive client `cantidad` negated
  server-side. *(spec: "A positive client cantidad is negated by the
  server")*
- [ ] 11.7 [P] Decomiso-of-lot-effective-requires-`idLote` test. *(spec:
  "A decomiso of a lot-effective articulo requires idLote")*
- [ ] 11.8 [P] Non-expired-lot decomiso allowed. *(spec: "Decomiso applies
  to a non-expired lot too")*
- [ ] 11.9 [P] Observaciones-required test. *(spec: "Decomiso without
  observaciones is rejected")*
- [ ] 11.10 [P] Ajuste lot-aware tests. *(spec stock: "Ajuste of a
  lot-effective articulo requires idLote and updates both caches", "Ajuste
  of a lot-effective articulo without idLote is rejected")*
- [ ] 11.11 Gate guard: `dotnet ef migrations has-pending-model-changes` →
  no pending changes.
- [ ] 11.12 Run `judgment-day`; fix; re-judge until clean.
- [ ] 11.13 Branch `feat/stage12-slice11-ajuste-decomiso` off `main`
  (parent: slice 10); PR; merge stacked-to-main.

**Test plan**: 403 mutation (11.4), insufficiency (11.5), sign discipline
(11.6), lot-required (11.7), non-expired-ok (11.8), observaciones (11.9),
ajuste ×2 (11.10).

**Verify**: `dotnet test --filter FullyQualifiedName~Decomiso|FullyQualifiedName~AjusteLote`

---

## Slice 12: Conteo (PR 12)

**Start**: slice 11 merged. **Finish**: conteo of a lot-effective articulo
counts per lot, acquiring every lock before deriving any delta; the
cross-cutting stock/stock_lotes invariants are now provable end to end
across all eight motivos. **Rollback**: revert the branch.

- [ ] 12.1 Modify `src/Ways.Application/Stock/Contratos.cs`:
  `SolicitudDeConteo.Contada` widens to `decimal?`; add `Lotes:
  IReadOnlyList<ConteoDeLote>?`; `ConteoDeLote(IdLote, Contada)`;
  `ResultadoConteo.Lotes: IReadOnlyList<LoteContado>`.
- [ ] 12.2 Modify `ServicioDeStock.cs`: `ContarAsync` — exactly-one-of
  validation (`400 conteo_contada_y_lotes` if both or neither present);
  `conteo_lote_repetido` refusal on a duplicated `idLote` before any lock.
- [ ] 12.3 Modify `ServicioDeStock.cs`: per-lot conteo, decision 12's
  split — **acquisition phase**: `BloquearYCrearSiFaltaStockAsync`
  (aggregate first), then each lot's `BloquearYCrearSiFaltaStockLoteAsync`
  ascending `id_lote`, no delta written yet; **application phase**: derive
  every delta, write `movimientos_stock` (`motivo = inventario`) + upsert
  `stock_lotes` per lot with a non-zero delta, aggregate accumulates the
  sum.
- [ ] 12.4 Note: proposal decision 11's pre-approved degradation (`409
  conteo_lote_no_soportado`) exists for a delivery slice that ships an
  aggregate-only conteo without per-lot support. This slice ships per-lot
  conteo in full, so the refusal path is documented but not the primary
  behavior — keep the `409` branch reachable only if a future regression
  removes per-lot support.
- [ ] 12.5 [P] Lock-acquisition-order test: every lock (aggregate no-op
  upsert, then each lot's no-op upsert ascending) is acquired before any
  delta write. *(design decision 12, proves the acquisition/application
  split)*
- [ ] 12.6 [P] Zero-difference-lot-writes-nothing test. *(spec
  conteo-de-inventario: "A lot with no difference writes no row even when
  a sibling lot differs")*
- [ ] 12.7 [P] `conteo_contada_y_lotes` tests. *(spec: "Supplying both
  cantidad_contada and lotes is rejected", "Supplying neither
  cantidad_contada nor lotes is rejected")*
- [ ] 12.8 [P] Per-lot-derives-aggregate-delta test. *(spec: "A
  lot-effective conteo derives the aggregate delta from per-lot deltas")*
- [ ] 12.9 [P] Never-fabricate-into-sin-identificar test. *(spec: "A
  lot-effective conteo never writes into the sin-identificar lot to absorb
  a difference")*
- [ ] 12.10 [P] `conteo_lote_repetido` test.
- [ ] 12.11 [P] Aggregate-grain regression: a matching count still writes
  nothing. *(spec: "A matching count writes nothing")*
- [ ] 12.12 [P] **Cross-cutting invariant suite** (now provable with all
  eight motivos live), one long-form test per invariant asserting **every**
  row, not just totals: (1) `stock.cantidad = SUM(movimientos)` after a
  sequence covering all eight motivos including a `reclasificacion` pair
  *(spec stock: Cantidad Is Always The Sum Of Its Movimientos)*; (2)
  `stock_lotes.cantidad = SUM(movimientos with that lot)` after
  compra→venta→transferencia→NCX→anulación→conteo→decomiso *(spec
  lotes-y-vencimientos: Stock Lotes Balance And Its Two Invariants)*; (3)
  `SUM(stock_lotes) = stock.cantidad` for a reconciled lot-effective pair.
- [ ] 12.13 Gate guard: `dotnet ef migrations has-pending-model-changes` →
  no pending changes.
- [ ] 12.14 Run `judgment-day`; fix; re-judge until clean.
- [ ] 12.15 Branch `feat/stage12-slice12-conteo` off `main` (parent:
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

- [ ] 13.1 Modify `src/Ways.Application/Reportes/ServicioDeReportesDeStock.cs`:
  `ObtenerVencimientosAsync` — lot rows with positive `stock_lotes.cantidad`,
  classified via `ReglaDeLotes.Clasificar` (four states incl. `SinFecha`),
  ordered `fecha_vencimiento ASC NULLS LAST`, `dias` defaults to the
  resolved `dias_alerta_vencimiento`; `ResolverZonaAsync` resolves "hoy" in
  the PV's own `zona_horaria`, never UTC.
- [ ] 13.2 Modify the same file: `ObtenerResumenDeVencimientosAsync` —
  Tablero tile counts (`vencido`/`por_vencer`/`sin_fecha`).
- [ ] 13.3 Modify `src/Ways.Application/Reportes/ExportacionDeReportes.cs`:
  `De(Vencimientos, ctx)` mapper — **listing shape** (design decision 17):
  `COUNT(*) → refuse → single read with .Take(tope + 1)`, never an
  aggregate-row-count guard.
- [ ] 13.4 Modify `src/Ways.Api/Endpoints/ReportesEndpoints.cs`: `GET
  /reportes/stock/vencimientos` (`LecturaDeReportes`), `GET
  .../vencimientos/export` (co-located, inherited policy), `GET
  .../vencimientos/resumen`.
- [ ] 13.5 [P] **Mutation target**: `TimeZoneInfo.ConvertTime(reloj.Ahora,
  zona)` replaced with `reloj.Ahora.UtcDateTime` → the non-UTC
  classification test MUST fail (the lot flips `PorVencer → Vencido`);
  revert → green. *(spec: "'Hoy' is resolved in the punto de venta's own
  zona horaria, not UTC"; mutation-proof-tests)* Record evidence.
- [ ] 13.6 [P] Classification-boundary tests: `vencido`/`por_vencer`/
  `vigente`/`sin_fecha`-counts-in-totals. *(spec: "A lot past its expiry
  classifies as vencido", "A lot within the alert horizon classifies as
  por_vencer", "A lot beyond the horizon classifies as vigente", "The
  sin-identificar lot appears in the report as sin_fecha and counts toward
  the totals")*
- [ ] 13.7 [P] Zero-balance-lot-excluded test. *(spec: "A zero-balance lot
  never appears in the report")*
- [ ] 13.8 [P] Export equality, cell by cell (`mutation-proof-tests` rule
  6): different values per row and column so a swap is detectable. *(spec:
  "The export sibling's figures equal the JSON endpoint's")*
- [ ] 13.9 [P] Cap + `+1` race backstop (listing shape, stage-11
  precedent): `COUNT(*) → refuse` with the actual row count named.
- [ ] 13.10 [P] 403 test. *(spec: "A Vendedor is rejected from the
  vencimientos report and its export")*
- [ ] 13.11 Gate guard: `dotnet ef migrations has-pending-model-changes` →
  no pending changes.
- [ ] 13.12 Run `judgment-day`; fix; re-judge until clean.
- [ ] 13.13 Branch `feat/stage12-slice13-vencimientos` off `main` (parent:
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

- [ ] 15.1 Create `src/Ways.Web/src/paginas/Vencimientos.tsx`: report
  screen — filters, four-state classification badges (incl. `sin_fecha`),
  download button.
- [ ] 15.2 Modify `src/Ways.Web/src/App.tsx` + `componentes/Layout.tsx`:
  `/reportes/stock/vencimientos` route (`LecturaDeReportes`) + nav entry.
- [ ] 15.3 Modify `src/Ways.Web/src/paginas/Articulos.tsx`: `controlaLote`
  toggle on the articulo editor.
- [ ] 15.4 Modify `src/Ways.Web/src/paginas/Parametros.tsx`:
  `lotesHabilitado` + `diasAlertaVencimiento` toggles.
- [ ] 15.5 Modify `src/Ways.Web/src/paginas/Transferencias.tsx`: lot column
  + picker per line, incomplete-line counter extended.
- [ ] 15.6 Modify `src/Ways.Web/src/paginas/ConteoDeInventario.tsx`:
  per-lot counted-total input UI, exactly-one-of enforcement mirrored
  client-side.
- [ ] 15.7 Modify `src/Ways.Web/src/paginas/Tablero.tsx`: vencimientos tile
  (counts + link), completing slice 13's backend groundwork.
- [ ] 15.8 [P] `web-descriptor-tests` for `Vencimientos.tsx`,
  `Articulos.tsx` (`controlaLote`), `Parametros.tsx` (2 toggles),
  `Transferencias.tsx`, `ConteoDeInventario.tsx`, the Tablero tile.
- [ ] 15.9 [P] Incomplete-line-counter test replicated across both
  `Transferencias` and `ConteoDeInventario` grids (mirrors slice 14.7's
  `CompraEditor` pattern).
- [ ] 15.10 [P] `controlaLote` coercion test (`'' → null`, `aAlta`/
  `aValores` boolean coercion).
- [ ] 15.11 Gate guard: `dotnet ef migrations has-pending-model-changes` →
  no pending changes (web-only slice).
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
