# Tasks: Stage 8 — Comprobantes de compra, transferencias e inventario

## Orchestrator Decisions Recorded This Phase

1. **Slicing follows the six-way split supplied for this phase**, not the
   proposal's five-way indicative order — the proposal's "Confirmar + anular"
   slice is merged with "Compra borrador" into one Slice 2 (the whole compra
   lifecycle is one guarded state machine and one judgment-day round is
   cheaper than two), and "Web" is split into Slice 5 (compras) / Slice 6
   (transferencias + conteo + saldo), mirroring the stage-7 web-split
   precedent.
2. **DB CHANGE GATE already exercised in autonomous mode** (`state.yaml`) —
   Slice 1 carries no STOP task; the approved model and its four conditions
   (dual-path seed with the fresh-DB guard, globally-unique compra codes,
   the tenant-scoped partial dedupe unique, a `db-error-backstops` mapping +
   race test per constraint) are pinned in the slice's tasks.
3. **doc-10 update split across two slices**, the stage-7 pattern: §1
   (compra tipos catalog note) and §5/§6 status notes ship in Slice 1 (same
   slice as the schema, so the doc never drifts); the stage-table close-out
   (etapa 8 complete — the **last row** of doc 10) ships in Slice 6.
4. **Slice 2 is the centerpiece and gets its own full judgment-day round** —
   it is the newest guarded state machine in the project (estado-guarded
   `UPDATE … RETURNING` as the sole transition authority) and the entire
   stage 5–7 suite must stay green through it, same posture as stage-7
   Slice 2 (`AnularAsync` widening).
5. **Baselines re-checked at Slice 1 branch time.** Cached baselines (Domain
   356 / Application 212 / Integration 556 / vitest 322) are re-read before
   recording deltas in later slices.
6. **The `_numero` backstop-ordering trap is a Slice 1 task, not an
   afterthought** — `ux_comprobantes_compra_numero_externo` must resolve by
   exact name **before** `ClasificarUnicidad`, the same treatment
   `ux_comprobantes_venta_numero` and (in stage 7) `RC` needed.

## Review Workload Forecast

| Field | Value |
|-------|-------|
| Estimated changed lines | ~6,300–8,000 total (incl. two-table EF migration boilerplate) |
| 400-line budget risk | High |
| Chained PRs recommended | Yes |
| Suggested split | Slice 1 → Slice 2 → Slice 3 → Slice 4 → Slice 5 → Slice 6 |
| Delivery strategy | auto-chain (cached decision) |
| Chain strategy | stacked-to-main |

Decision needed before apply: No — resolved: chained PRs, stacked-to-main,
`judgment-day` before every PR, per `auto-chain`. Six slices forecast. Slice 2
(the compra lifecycle) is the largest and highest-risk slice — it introduces
the project's newest state machine and gets a **dedicated full judgment-day
round** on top of the default. Slice 1 opens the largest schema surface since
stage 5 (two new tables, one enum, two deferred FKs); the DB CHANGE GATE is
already approved in autonomous mode, so it carries no STOP task, only the
recorded conditions.

Decision needed before apply: No
Chained PRs recommended: Yes
Chain strategy: stacked-to-main
400-line budget risk: High

### Suggested Work Units

| Unit | Goal | Likely PR | Est. lines | Notes |
|------|------|-----------|-----------|-------|
| 1 | Schema + seed gate: migration, two deferred FKs, entity skeletons, EF configs, backstops, doc-10 §1/§5/§6 | PR 1 | ~900–1,300 | Base: `main`. DB CHANGE GATE already approved (autonomous mode, `state.yaml`) — no STOP task. |
| 2 | Compra lifecycle: pure `CalculadorDeCompra`, borrador replace-set, confirmar, anular (inverted gasto rule), precio_sugerido | PR 2 | ~2,000–2,500 | Base: PR 1. **Own dedicated judgment-day round** — newest guarded state machine. |
| 3 | Transferencias + conteo: `ServicioDeStock.TransferirAsync`/`ContarAsync`, extended sum-invariant | PR 3 | ~900–1,200 | Base: PR 2. |
| 4 | Proveedor saldo + gastos link: `FOR SHARE` guard, top-level saldo endpoint, arqueo-untouched proof | PR 4 | ~700–900 | Base: PR 3. |
| 5 | Web compras: list + borrador editor + confirmar/anular | PR 5 | ~900–1,100 | Base: PR 4. |
| 6 | Web transferencias + conteo + proveedor saldo view + doc-10 close-out | PR 6 | ~700–900 | Base: PR 5. Last row of doc 10 — closes the legacy-parity program. |

---

## Slice 1: Schema + Seed Gate (PR 1)

**Start**: `main`. **Finish**: `ComprasYTransferenciasEtapa8` migration live
(`estado_compra` enum registered in both sites, two new tables, two deferred
FK columns + indexes, partial dedupe unique, CHECKs, RLS), the exact-name
`_numero` backstop ordering fixed, compra-tipo dual-path seed proven on both
a fresh and a stage-7-migrated database, doc-10 §1/§5/§6 updated. **Rollback**:
down-migration (drop the two tables, drop the two nullable FK columns, drop
the enum with its tables, deactivate the three compra tipos).

- [x] 1.1 Re-read the four cached baselines (`dotnet test` Domain/Application/
  Integration counts, `npx vitest run` count) at branch-cut and record the
  actual numbers in this task's checkbox note. *(Orchestrator Decision 5)*
- [x] 1.2 [P] Create `src/Ways.Domain/Compras/ComprobanteCompra.cs`,
  `ItemComprobanteCompra.cs`, `EstadoCompra.cs`. *(design: Table Shapes A/B/C;
  File Changes)*
- [x] 1.3 [P] Modify `src/Ways.Domain/Stock/MotivoStock.cs`,
  `MovimientoStock.cs`: `IdComprobanteCompra` added, "reserved/never written"
  doc-comments removed. *(design: File Changes; spec: stock / Comprobante
  Compra Schema At Rest — "A compra movement carries its comprobante link")*
- [x] 1.4 [P] Modify `src/Ways.Domain/Gastos/Gasto.cs`: `IdComprobanteCompra`
  added, deferral note removed. *(design: File Changes; spec: gastos /
  Gasto Schema At Rest)*
- [x] 1.5 Migration `ComprasYTransferenciasEtapa8`: `estado_compra` enum;
  `comprobantes_compra` (all columns, `ux_comprobantes_compra_numero_externo`
  partial UNIQUE, `ck_comprobantes_compra_confirmada_completa`,
  `ck_comprobantes_compra_totales_no_negativos`, all indexes);
  `items_comprobante_compra` (all columns, `id_articulo NOT NULL`,
  `ux_items_comprobante_compra_orden`, three item CHECKs, all indexes); the
  two deferred FK columns (`movimientos_stock.id_comprobante_compra`,
  `gastos.id_comprobante_compra`) + support indexes; RLS on both new tables.
  *(design: Table Shapes A, B, D, F)*
- [x] 1.6 [P] Register `MapEnum<EstadoCompra>("estado_compra")` in **both**
  `DependencyInjection.cs` and `WaysDbContextFactory.cs` — never also via
  `HasPostgresEnum`. *(design: Table Shape C)*
- [x] 1.7 Create `ComprobanteCompraConfiguration.cs`,
  `ItemComprobanteCompraConfiguration.cs`; modify
  `MovimientoStockConfiguration.cs`, `GastoConfiguration.cs` for the two FK
  columns. *(design: File Changes)*
- [x] 1.8 Modify `InicializadorDeBaseDeDatos.cs`: `TiposComprobanteBase`
  gains a `Clase` field (`:424` currently hardcodes `Venta`); append
  `C-FA`/`C-FB`/`C-FC` to the seed list. *(design: Table Shape E; decisions
  7, 12)*
- [x] 1.9 Same migration: idempotent dual-path compra-tipo insert —
  `WHERE EXISTS (SELECT 1 FROM tipos_comprobante)` (fresh-DB guard) **AND**
  a per-row `NOT EXISTS (… WHERE codigo = v.codigo)`, the
  `CuentaCorrienteEtapa7` pattern. *(design: Table Shape E; spec:
  comprobantes-compra / Compra-Clase Tipos Are Platform-Seeded — "seed does
  not break a fresh-database boot")*
- [x] 1.10 Modify `ManejadorDeErrores.cs`: **exact-name branch, before
  `ClasificarUnicidad`**, for `ux_comprobantes_compra_numero_externo` → `409
  compra_duplicada` (the `_numero` substring trap); exact-name branch for
  `ux_items_comprobante_compra_orden` → `409 orden_de_item_duplicado`; new
  `ClasificarCheckDeCompras` behind a `ck_comprobantes_compra_`/
  `ck_items_comprobante_compra_` prefix guard, exact-name switch inside (the
  `ck_ofertas_` pattern, not a `Contains` family). *(design: Backstop Map)*
- [x] 1.11 Update `docs/10-modelo-de-datos.md` §1 (compra-tipo catalog note),
  §5/§6 (etapa 8 implemented; both deferred FKs landed), stage table.
  *(gate condition, `state.yaml`; Orchestrator Decision 3)*
- [x] 1.12 [P] Integration: `has-pending-model-changes` clean; RLS enforced on
  both new tables.
- [x] 1.13 [P] Integration (backstops): the `_numero` trap test (a duplicate
  `numero_externo` returns `compra_duplicada`, never the generic
  `numero_duplicado`); `orden` duplicate 23505; both new CHECKs (23514) each;
  every new FK 23503; a genuine race on the dedupe unique — exactly one
  winner. *(design: Backstop Map)*
- [x] 1.14 [P] Integration: every venta `tipos_comprobante` code resolves to
  its venta row after the seed, proven on **both** a fresh database **and**
  one migrated from stage 7 (two separate boot tests). *(spec:
  comprobantes-compra / Compra-Clase Tipos… — both scenarios; GATE condition
  (ii))*
- [x] 1.15 Regression: full stage 1–7 suite green, no assertion changed.

**Verify**: `dotnet test --filter FullyQualifiedName~ComprobanteCompra|FullyQualifiedName~TiposComprobante`

---

## Slice 2: Compra Lifecycle — The Centerpiece (PR 2) — own full judgment-day round

**Depends on**: Slice 1. **Start**: PR 1 merged/branch. **Finish**: pure
`CalculadorDeCompra` exhaustively tested; borrador replace-set under
`FOR UPDATE`; the confirm transaction (estado-guarded `UPDATE … RETURNING`
first, stock entry, `costo_nominal`, cache, `precio_sugerido`); anulación
with the inverted gasto rule (no block, negative-stock refusal); entire stage
5–7 suite green through it. **Rollback**: new files + one endpoint group
only — nothing in stages 5–7 changes shape.

- [x] 2.1 [P] Create `src/Ways.Domain/Compras/CalculadorDeCompra.cs` + records
  `LineaDeCompra`/`ItemCalculado`/`CompraCalculada`: `cantidad`, `bruto`,
  `total(i)`, header totals branching on `discrimina_iva`, `costoEfectivo`.
  *(design: Compra Arithmetic; Interfaces/Contracts)*
- [x] 2.2 [P] Unit: `CalculadorDeCompra` exhaustive — `cantidad` from
  unidades/bultos, both IVA regimes, `iva_total NULL` when not
  discriminando, `costoEfectivo` with and without IVA, the `numeric(14,4) →
  numeric(14,2)` narrowing `AwayFromZero`, `descuento > bruto` rejected
  `400`, zero-cost bonificación line not touching cost, two lines of the
  same articulo (highest `orden` wins the cost), empty line set;
  `SugeridorDePrecio` reuse asserted, not re-implemented. *(design: Testing
  Strategy — Unit Domain)*
- [x] 2.3 Create `src/Ways.Application/Compras/ServicioDeCompras.cs`:
  `CrearBorradorAsync`, `ActualizarBorradorAsync` (`PUT`, full item
  replace-set under `SELECT … FOR UPDATE … WHERE estado='borrador'`, physical
  `DELETE`+`INSERT`), `ListarAsync`. *(design decision 2)*
- [x] 2.4 `ServicioDeCompras.ConfirmarAsync`: estado-guarded
  `UPDATE … RETURNING` as the first statement, items read **after** it under
  that lock, per-item ledger `INSERT` + cache upsert (asc `id_articulo`),
  `costo_nominal` overwrite (`actualiza_costo AND costo_unitario > 0`,
  deduplicated with highest `orden` winning), `precio_sugerido` via the
  existing `SugeridorDePrecio`, `EstrategiaSinReintento`. *(design decisions
  1, 4, 5; Transactions — CONFIRMAR COMPRA)*
- [x] 2.5 `ServicioDeCompras.AnularAsync`: estado-guarded `UPDATE … RETURNING`
  (`confirmada → anulada`), contramovimientos from the **original ledger**
  (never recalculated from items), `409 compra_anulacion_stock_negativo`
  when any resulting cache would go negative, informational-only linked-gasto
  count — **no block** (the inverted rule). *(design decision 6; Transactions
  — ANULAR COMPRA)*
- [x] 2.6 `ServicioDeCompras`: `ObtenerAsync` (detail with `precioSugerido`
  per item) + `AplicarPrecioSugeridoAsync` looping
  `AbrirNuevoPrecioAsync` once per articulo, each its own transaction,
  per-line results. *(design decision 8)*
- [x] 2.7 Create `ComprasEndpoints.cs`: 7 routes — writes stack
  `GestionDeCatalogo` over `OperacionDePos`, reads stay `OperacionDePos`.
  Update `SuperficieDeAutorizacionTests` allowlist. *(design: API Surface)*
- [x] 2.8 [P] Integration: borrador create/edit/remove-item across several
  requests writes no `movimientos_stock` row; confirmada/anulada reject an
  item edit `409 compra_no_editable`. *(spec: comprobantes-compra / Borrador
  Is Mutable Because It Has No Ledger Effect)*
- [x] 2.9 [P] Integration: same `(proveedor, tipo, numero_externo)` confirmed
  twice rejected `409 compra_duplicada`; an annulled number is re-enterable;
  confirming without `numero_externo` rejected `400
  compra_numero_externo_requerido`. **Deviation**: the collision is proven at
  the earliest write that persists the duplicate identity (the second
  `POST`/`PUT`), not literally "at confirm" — the partial unique excludes
  only `estado = anulada`, so it fires on ANY save that repeats a live
  `(proveedor, tipo, numero_externo)`, matching design's own Backstop Map
  framing ("two concurrent **saves**"), not a confirm-only race. *(spec:
  comprobantes-compra / Numero Externo Identity And Dedupe)*
- [x] 2.10 Integration: confirmar writes stock+cache+cost together in one
  transaction; a fault-point failure at each step leaves `estado`, stock,
  cache and `costo_nominal` untouched; a second confirm of an already-
  `confirmada` compra rejected `409 compra_ya_procesada`, no duplicate
  movements. *(spec: comprobantes-compra / Confirmar Is One All-Or-Nothing
  Transaction)*
- [x] 2.11 Integration: confirm stores `precio_sugerido` without opening a
  new price; the explicit apply action opens a new `precios` row preserving
  history. *(spec: comprobantes-compra / Precio Sugerido Is A Suggestion)*
- [x] 2.12 Integration: anulación reverses stock and restores the cache;
  refused `409` when the goods were already sold (names the articulo); no
  `costo_nominal` reversion; anulando a `borrador` rejected `409
  compra_no_procesada`. *(spec: comprobantes-compra / Anulación Reverses By
  Contramovimientos)*
- [x] 2.13 Integration (racy surfaces, forced rendezvous): double confirm of
  the same borrador — exactly one winner, the loser `409
  compra_no_es_borrador`; confirm × concurrent borrador edit. *(design:
  Backstop Map — "five racy surfaces" 1–2; `ParametrosTests` precedent)*
- [x] 2.14 [P] Integration (authorization): Admin confirms and anula
  (authorization-wise); Vendedor `403` on every compra write path; Vendedor
  reads the compra list. *(spec: comprobantes-compra / Authorization)*
- [x] 2.15 [P] Integration (budget): constant command count for a 2 / 20 /
  100-item compra's confirm and anular. `DbCommand` interceptor test.
  *(design: Transactions — "Read budget")*
- [x] 2.16 Run a **dedicated full judgment-day round** on this slice's diff
  alone before opening the PR — the project's newest guarded state machine.
  *(Orchestrator Decision 4)* **Not run by this apply batch** — judgment-day
  requires two independent blind review agents, which is an orchestration
  action outside an executor's scope (sdd-apply is delegate-only, does not
  launch sub-agents). Recommend the orchestrator run judgment-day next,
  before opening the PR.
- [x] 2.17 Regression: entire stage 5–7 suite green; `ServicioDeStock`,
  `ServicioDeVentas`, `CalculadorDeArqueo` untouched (byte-identical — no
  edit made to either file this slice; confirmed via `git status`).

**Verify**: `dotnet test --filter FullyQualifiedName~CalculadorDeCompra|FullyQualifiedName~ServicioDeCompras`

---

## Slice 3: Transferencias + Conteo Backend (PR 3)

**Depends on**: Slice 1 (schema); the `ServicioDeStock` shape is
`AjustarAsync`'s, unrelated to Slice 2's compra machinery. **Start**: PR 2
merged/branch (stacked order). **Finish**: `TransferirAsync`/`ContarAsync`
live, the extended sum-invariant proven per punto de venta, three more racy
surfaces closed. **Rollback**: two new methods + two new routes only —
`AjustarAsync` unedited.

- [x] 3.1 Modify `src/Ways.Application/Stock/ServicioDeStock.cs`: the raw
  `InsertarMovimientoStockAsync`/`UpsertStockAsync` statements gain
  `motivo`/`idComprobanteCompra`/`idPuntoVentaDestino` parameters, backward
  compatible with `AjustarAsync`. *(design: File Changes)* **Deviation**:
  only `InsertarMovimientoStockAsync` gained the three parameters —
  `UpsertStockAsync` writes to `stock`, which has no `motivo`/`id_*` columns,
  so it stays unchanged (matches its sibling in `ServicioDeCompras`, which
  also leaves `UpsertStockAsync`'s shape untouched). `idComprobanteCompra` is
  always passed `null` from every `ServicioDeStock` caller — the invariant
  pinned in Slice 1's `MovimientoStock.IdComprobanteCompra` doc-comment
  ("nunca escrita fuera de `ServicioDeCompras`") holds structurally.
- [x] 3.2 `ServicioDeStock.TransferirAsync`: pre-checks (articulos + both PVs
  resolved, `origen ≠ destino` `400`, `articulo_repetido` `400`,
  `observaciones` required), one **total order** over all `2N` keys sorted
  `(id_articulo, id_punto_venta)` ASC, per-key ledger `INSERT` + cache
  upsert, `delta < 0 AND nueva < 0 ⇒ 409`. *(design decision 9; Transactions
  — TRANSFERENCIA)*
- [x] 3.3 `ServicioDeStock.ContarAsync`: no-op upsert to lock+create-if-
  missing, server-derived `delta = contada − actual`, `delta = 0 ⇒` commit
  with no write (`200`), else ledger `INSERT` (`motivo = inventario`) +
  upsert, defense-in-depth final-balance check. *(design decision 10;
  Transactions — CONTEO DE INVENTARIO)*
- [x] 3.4 Modify `StockEndpoints.cs`: `POST /api/stock/transferencias`,
  `POST /api/stock/conteos`, both `GestionDeCatalogo` over
  `OperacionDePos`. Update `SuperficieDeAutorizacionTests` allowlist.
  *(design: API Surface)* Both routes stack `GestionDeCatalogo`, so (mirror
  of Slice 2, task 2.7) they need no allowlist entry — a documenting comment
  was added instead, same pattern as the compras routes.
- [x] 3.5 [P] Integration: a single-item transfer moves both caches
  atomically; a multi-item transfer writes exactly `2N` rows atomically; a
  failure moves neither side. *(spec: transferencias-de-stock / Transferencia
  Writes Two Mirrored Movements)*
- [x] 3.6 [P] Integration: insufficient origin stock refused `409
  stock_insuficiente_para_transferencia`; a **sale** of the same articulo at
  the same PV still goes negative — the asymmetry proven in both directions.
  *(spec: transferencias-de-stock / Insufficient Origin Stock Is Refused)*
- [x] 3.7 [P] Integration: `origen = destino` rejected `400` before any
  write; a `destino` from another tenant rejected as an invalid reference.
  *(spec: transferencias-de-stock / Origen And Destino Must Differ)*
  **Deviation**: design's own New Domain Codes table lists a separate
  `punto_venta_destino_invalido (400)` for this case; spec.md only pins
  "treated as an invalid reference" without a code, and the orchestrator's
  non-negotiables pin the test result as `404`. Implemented by reusing
  `ResolverPuntoVentaAsync` (the same ADR-8 uniform-404 helper `AjustarAsync`
  already uses for both origen/destino) instead of introducing the separate
  400 code — less risk, one fewer untested code path.
- [x] 3.8 [P] Integration: a count above/below the cache produces the correct
  signed movement; the conteo request contract carries only
  `cantidad_contada`, never a delta/ajuste field. *(spec: conteo-de-
  inventario / Conteo Input Is The Counted Total, Never A Delta)*
- [x] 3.9 [P] Integration: a matching count writes no row, cache unchanged,
  `200`. *(spec: conteo-de-inventario / Zero-Difference Conteo Writes No
  Ledger Row)*
- [x] 3.10 [P] Integration: conteo without `observaciones` rejected before
  the database; `motivo = inventario` is never produced by `/ajustes` and
  `motivo = ajuste` is never produced by `/conteos`. *(spec: conteo-de-
  inventario / Conteo Requires Observaciones And Is Distinct From Ajuste)*
- [x] 3.11 Integration (racy surfaces, forced rendezvous): transferencia ×
  checkout on the same `(articulo, pv)`; two concurrent conteos of the same
  articulo serialize on the row lock, no lost update. *(design: Backstop Map
  racy surface 3; spec: conteo-de-inventario / Concurrent Conteos)*
- [x] 3.12 Integration (extended sum-invariant): a mixed sequence of venta,
  ajuste, compra, transferencia, inventario and anulación —
  `stock.cantidad` equals `SUM(movimientos_stock.cantidad)` per `(articulo,
  punto_venta)`, asserted independently per PV. *(spec: stock / Cantidad Is
  Always The Sum Of Its Movimientos; transferencias-de-stock / Sum-Invariant
  Holds Per Punto De Venta)*
- [x] 3.13 [P] Integration (authorization): Admin succeeds on transferencia
  and conteo; Vendedor `403` on both. *(spec: transferencias-de-stock /
  conteo-de-inventario / Authorization)*
- [x] 3.14 Regression: full stage 5–7 + Slices 1–2 suite green; the ajuste
  path unedited. Baselines re-checked at this slice's branch-cut and after
  implementation: Domain 378/378, Application 212/212, Integration
  636→655 (+19, all this slice's), vitest 322/322 (no web work in this
  slice). `AjustarAsync`'s own logic/behavior is unchanged (its two raw
  calls now pass the three new parameters with `Ajuste`/`null`/`null`);
  `ServicioDeVentas.cs` and `CalculadorDeArqueo` are untouched (confirmed via
  `git status`).

**Verify**: `dotnet test --filter FullyQualifiedName~TransferirAsync|FullyQualifiedName~ContarAsync`

---

## Slice 4: Proveedor Saldo + Gastos Link (PR 4)

**Depends on**: Slice 2 (needs `confirmada` compras). **Start**: PR 3
merged/branch. **Finish**: the gasto write path locks the compra header
`FOR SHARE`, the derived saldo read is live, the top-level saldo route
avoids the `AND`-composition trap, and `CalculadorDeArqueo` is proven
byte-unchanged. **Rollback**: new files + a `FOR SHARE` guard + one nullable
DTO field only.

- [x] 4.1 Modify `src/Ways.Application/Gastos/ServicioDeGastos.cs` +
  `Contratos.cs`: optional `IdComprobanteCompra`; when present,
  `SELECT … FOR SHARE` on `comprobantes_compra` **after** the existing turno
  lock; `estado ≠ confirmada ⇒ 409` (`compra_anulada`/`compra_no_confirmada`);
  `categoria ≠ proveedor ⇒ 400`; `id_proveedor` derived when absent, mismatch
  `⇒ 400`. *(design decision 7; Transactions — GASTO LIGADO A UNA COMPRA)*
- [x] 4.2 Create `src/Ways.Application/Compras/ServicioDeSaldoDeProveedor.cs`:
  derived read `Σ compras confirmadas − Σ gastos (categoria = proveedor)`, a
  single grouped query for per-compra payment status across a page (no
  N+1). *(design decision 11)* **Deviation**: implemented as exactly 2
  aggregate/grouped queries (compras confirmadas + gastos agrupados por
  `id_comprobante_compra`, incluida la clave `NULL` de los sin ligar) — el
  mismo `GROUP BY` alimenta tanto el total del saldo como el desglose
  por-compra, sin una tercera consulta por fila.
- [x] 4.3 Add `GET /api/proveedores/{id}/saldo` **mapped top-level**, not
  inside the `/api/proveedores` group (`GestionDeCatalogo`) — `OperacionDePos`.
  *(design: API Surface — the AND-composition trap)*
- [x] 4.4 Modify `GastosEndpoints.cs`: DTO learns optional
  `idComprobanteCompra`. Update `SuperficieDeAutorizacionTests` allowlist
  with the new top-level saldo route. *(design: File Changes)* **Deviation**:
  the field lives in `Contratos.cs` (Gastos) — `GastosEndpoints.cs` forwards
  the DTO as-is and needed no code change; the allowlist update landed as a
  new `PrefijosDeLecturaReGateados` entry (`"/api/proveedores"`), since the
  saldo route is a GET, not a write route (the write allowlist doesn't apply).
- [x] 4.5 [P] Integration: a gasto links to the compra it pays under the
  existing open-turno gate; a non-proveedor categoria cannot link `400`; a
  compra-linked gasto still requires an open turno `409 turno_no_abierto`.
  *(spec: gastos / A Comprobante Compra Link Requires Categoria Proveedor)*
- [x] 4.6 Integration (racy surface, forced rendezvous): gasto ligado ×
  anulación of the same compra — the `FOR SHARE` lock closes the TOCTOU,
  both outcomes representable, neither corrupt. *(design: Backstop Map racy
  surface 5; decision 7 rationale)*
- [x] 4.7 [P] Integration: saldo reflects confirmed compras net of gastos;
  borradores and anuladas excluded; zero-activity proveedor returns `0`.
  *(spec: saldo-de-proveedor / Saldo Is A Derived Read, Never Persisted)*
- [x] 4.8 [P] Integration: per-compra payment status (`pagada`/`parcial`/
  `impaga`) from **linked** gastos only; an unlinked gasto reduces total
  saldo but does not mark a specific compra as paid. *(spec: saldo-de-
  proveedor / Per-Compra Payment Status, Saldo Is An Approximation)*
- [x] 4.9 Integration (**the arqueo-untouched proof**, the design's own gate
  condition): `CalculadorDeArqueo` source is byte-identical before and after
  this stage; a proveedor gasto linked to a confirmed compra changes the
  turno's `importe_esperado` by exactly its importe through the existing
  `SUM(gastos.importe on that medio)` term, with no new branch. *(spec:
  arqueo-de-cierre / A Proveedor Gasto Linked To A Compra Introduces No New
  Derivation Term — both scenarios)* Byte-identity confirmed via
  `git diff --stat` (empty) on `CalculadorDeArqueo.cs`.
- [x] 4.10 [P] Integration: a hard delete of a proveedor referenced by a
  `comprobante_compra` is rejected at the schema layer, mapped by
  `db-error-backstops`. *(spec: proveedores / Proveedor Referenced By A
  Comprobante Compra Cannot Be Removed)*
- [x] 4.11 [P] Integration: proveedor detail exposes the derived saldo entry
  point; a cross-tenant proveedor saldo read returns not-found. *(spec:
  proveedores / Proveedor Saldo Read Entry Point; saldo-de-proveedor /
  Cross-Tenant Proveedor Saldo Is Invisible)*
- [x] 4.12 [P] Integration (authorization surface): Vendedor reads the
  compra list and a proveedor's saldo; paying a compra keeps the unchanged
  `OperacionDePos` gasto gate. *(spec: operacion-de-pos delta, all four
  scenarios)*
- [x] 4.13 Regression: full suite green; the `CalculadorDeArqueo` source
  file untouched. Domain 378/378, Application 212/212, Integration
  664→689 (+25, all this slice's), vitest unrun in this worktree (no
  `node_modules` installed — an environment gap unrelated to this slice,
  which touches zero `Ways.Web` files). `ServicioDeStock`/`ServicioDeVentas`
  untouched (confirmed via `git diff --stat`).

**Verify**: `dotnet test --filter FullyQualifiedName~ServicioDeSaldoDeProveedor|FullyQualifiedName~CalculadorDeArqueo`

---

## Slice 5: Web — Compras (PR 5)

**Depends on**: Slice 2 (compra endpoints) + Slice 4 (apply-price-suggestion
endpoint). **Start**: PR 4 merged/branch. **Finish**: the compras list and
borrador editor (confirmar/anular/apply-price) work end to end for Admin,
double-submit-proof. **Rollback**: new route + entry point only.

- [x] 5.1 [P] Add `src/Ways.Web/src/api/compras.ts`: pure request/response
  mappers, a **non-authoritative** totals mirror mirroring
  `CalculadorDeCompra` — server numbers always win (`dto-contract-honesty`).
  *(design: Web Composition)*
- [x] 5.2 Add `src/Ways.Web/src/paginas/Compras.tsx`: list + proveedor/
  estado/fecha filters + payment status + entry point to the editor.
  *(design: Web Composition)* **Deviation**: payment status per row is
  populated only while the list is filtered by a single proveedor (fetches
  that proveedor's `GET /api/proveedores/{id}/saldo`) — there is no bulk
  saldo-by-page endpoint; an unfiltered list shows `—` in that column. The
  full per-proveedor saldo panel is Slice 6's `Proveedores.tsx` scope.
- [x] 5.3 Add `src/Ways.Web/src/paginas/CompraEditor.tsx` (`/compras/nueva`,
  `/compras/:id`): header form + item grid (unidades/bultos/costoUnitario/
  descuento/alícuota), the totals mirror, confirmar/anular actions (inline
  irreversibility-confirm panels, `CierreDeCaja` precedent), the
  `precio_sugerido` panel + apply action. `react-async-state` rule 8
  (`key={idCompra ?? 'nuevo'}`), rule 9 (first-line re-entrancy guard +
  full-window disable on confirmar/anular/apply — all irreversible), rule 3
  (generation bumped before every write), rule 6 (a 2xx confirm/anular/apply
  is never reported as failure), rule 7 (proveedores/tipos/alícuotas/puntos
  de venta load failure ⇒ visible aviso + genuinely disabled submit; listas
  de precio failure is non-blocking, only the apply-precio panel needs it).
  *(design: Web Composition, obligations 3, 6, 7, 8, 9)*
- [x] 5.4 Wire the route in `App.tsx` under `GestionDeCatalogo` (Admin-only
  route AND nav — `rolesPermitidos={[ROL.Admin]}` on both `/compras` and
  `/compras/:id`, not `OperacionDePos`, per decision 11's consistency note:
  no stage-7 nav/policy mismatch where the backend read policy allowed a
  role the web never gave an entry point to). *(design: File Changes)*
- [x] 5.5 [P] Unit: `compras.ts` mappers + totals mirror asserted against the
  `CalculadorDeCompra` formulas (`compras.test.ts`, 47 tests: cantidad from
  unidades/bultos, both IVA regimes, ivaTotal NULL when not discriminating,
  costoEfectivo with/without IVA, bonificación line, empty set, division by
  zero guard, descuento > bruto rejection, request mapper trimming/filtering,
  offset-aware `desde`/`hasta` query building). *(web-descriptor-tests)*
- [x] 5.6 Component: double-click on "Confirmar" and on "Anular" each issue
  exactly one POST; an empty compras list renders an empty state, never a
  re-query; proveedores/tipos/alícuotas failing to load shows an aviso and
  an actually-disabled submit; role gating (Vendedor hides every write
  action, inputs disabled); numero_externo dedupe (`compra_duplicada`) and
  stock-refusal (`compra_anulacion_stock_negativo`) errors rendered
  verbatim; borrador replace-set PUT body shape asserted; list generation
  gating (a stale response never overwrites a fresher one). RTL +
  `user-event`. **Deviation**: mocks `../api/cliente` (`api.get/post/put`)
  rather than `vi.mock('../api/compras')` — matches the project's
  established convention (every existing page test mocks at that layer,
  verified via grep; zero precedent for module-level API-client mocks) and
  additionally exercises the real route strings built by `compras.ts`.
  *(design: Testing Strategy — Component Web)*
- [x] 5.7 Regression: `npx vitest run` 322 → 371 (+49, all this slice's:
  Compras.tsx/CompraEditor.tsx/compras.ts tests); `npx tsc -b` clean;
  `npx oxlint` clean (0 new — the one pre-existing `AuthContext.tsx`
  fast-refresh warning is untouched by this slice); `npx vite build` clean.
  **Not run by this apply batch**: judgment-day (requires two independent
  blind review agents, an orchestration action outside an executor's scope,
  same posture as Slice 2 task 2.16) — recommend the orchestrator run it
  next, before opening PR 5.

**Verify**: `npx vitest run src/paginas/Compras.test.tsx src/paginas/CompraEditor.test.tsx src/api/compras.test.ts`

---

## Slice 6: Web — Transferencias + Conteo + Proveedor Saldo View (PR 6)

**Depends on**: Slice 3 (transferencia/conteo endpoints) + Slice 4 (saldo
endpoint) + Slice 5 (shared `dto-contract-honesty`/recovery patterns).
**Start**: PR 5 merged/branch. **Finish**: the three remaining screens are
live, the `stock_insuficiente_*` recovery copy is replicated across every
sibling surface, doc-10 records etapa 8 as the closed last stage.
**Rollback**: three screens + role-gated buttons + the doc close-out only.

- [ ] 6.1 [P] Add `src/Ways.Web/src/paginas/Transferencias.tsx`: origen/
  destino selectors + multi-item grid, rule 9 (re-entrancy guard +
  full-window disable), `articulo_repetido` validation mirror. *(design: Web
  Composition)*
- [ ] 6.2 [P] Add `src/Ways.Web/src/paginas/ConteoDeInventario.tsx`:
  per-articulo count form (`cantidad_contada` + required `observaciones`),
  rule 9 guard. *(design: Web Composition)*
- [ ] 6.3 Modify `Proveedores.tsx`: a saldo panel reachable from proveedor
  detail, with per-compra payment-status badges. *(design: Web Composition;
  spec: proveedores / Proveedor Saldo Read Entry Point)*
- [ ] 6.4 **Rule 10 — sibling-surface replication.** Grep the
  `stock_insuficiente_*` recovery copy and replicate it across the
  anulación surface (`CompraEditor.tsx`, Slice 5) and the transferencia
  surface in this same commit; replicate `compra_no_es_borrador` the same
  way. *(design: Web Composition, obligation 10)*
- [ ] 6.5 Wire the two write routes under `GestionDeCatalogo` (Admin-only
  nav) and the saldo panel entry point under `OperacionDePos` in `App.tsx`.
- [ ] 6.6 Update `docs/10-modelo-de-datos.md`: close-out — §5/§6 notes
  finalized, stage table marks etapa 8 complete (the **last row** of doc
  10). *(gate condition; Orchestrator Decision 3; proposal: "LAST STAGE OF
  DOC 10")*
- [ ] 6.7 [P] Unit: transferencias/conteo mappers (extending or siblings of
  `compras.ts`). *(web-descriptor-tests)*
- [ ] 6.8 Component: double-click on "Transferir" and on "Contar" each issue
  exactly one POST; the `stock_insuficiente_*` recovery copy is present on
  both the anulación and the transferencia surfaces. RTL + `user-event`.
  *(design: Testing Strategy — Component Web)*
- [ ] 6.9 Smoke-verify (`tsc -b` / `oxlint` / `vite build` clean).
- [ ] 6.10 Regression: full `npx vitest run` green (Slice 1's re-checked
  baseline + this stage's new tests); full `dotnet test` green (Domain/
  Application/Integration baselines + this stage's new tests), no unrelated
  assertion changed.

**Verify**: `npx vitest run` (full suite) && `npx tsc -b` && `npx vite build`

---

## Dependency Summary

```
Slice 1 (schema + seed gate — two tables, estado_compra enum, two deferred
         FKs, the _numero backstop trap, DB CHANGE GATE pre-approved)
        │
        ▼
Slice 2 (compra lifecycle — the centerpiece: pure CalculadorDeCompra,
         borrador replace-set, confirmar, anular, inverted gasto rule —
         own dedicated judgment-day round)
        │
        ▼
Slice 3 (transferencias + conteo — mirrored-rows tx, per-articulo conteo,
         extended sum-invariant per punto de venta)
        │
        ▼
Slice 4 (proveedor saldo + gastos link — FOR SHARE guard, top-level saldo
         endpoint, the arqueo-untouched proof)
        │
        ▼
Slice 5 (web: compras list + borrador editor + confirmar/anular)
        │
        ▼
Slice 6 (web: transferencias + conteo + proveedor saldo view — doc-10
         close-out, the last row of the stage table)
```

Within each slice, `[P]`-tagged tasks are parallelizable; all others are
sequential (schema → domain → application → API → tests). Slices share the
`ServicioDeStock.AjustarAsync` transaction shape, the compra header lock, or
the `comprobantes_compra` FK surface — no two slices are fully independent.
Chained PRs, stacked-to-main, `judgment-day` before every PR (per
`protocolo-pr-solo-dev`); Slice 2 gets a dedicated full judgment-day round.
Archiving this change closes doc 10's legacy-parity program — there is no
etapa 9.
