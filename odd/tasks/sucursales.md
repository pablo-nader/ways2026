# Sucursales (branch level above the punto de venta)

## Objective

Let one physical store run several cash registers that share one stock. A new `sucursales`
level sits between `empresas` and `puntos_venta` (tenant → empresa → sucursal → punto de venta);
stock moves up to the sucursal and each punto de venta stays a cash register.

## Problem / why

Today a punto de venta is both the store and the register. `ux_dispositivos_punto_venta_activo`
allows one active device per PV, so a store with two registers needs two PVs, and stock
(`stock`, `stock_lotes`, `movimientos_stock`, keyed by `id_punto_venta`) splits in two even
though the shelf is one. A customer already has this case.

## Decisions (owner, 2026-10-30)

- Option A: a real `sucursales` table, not a "stock-owner PV" pointer.
- `puntos_venta` stays the register: devices, turnos, numeración, numeración fiscal (ARCA PV),
  offline reservations and the comprobante series remain per PV.
- Stock, mínimos/reposición and the purchase receiving destination become per sucursal.
- Migration creates one sucursal per existing PV (1:1), so nothing changes on deploy day.
- `movimientos_stock` keeps `id_punto_venta` as the originating register for traceability.

## Scope

- Schema + backfill of `sucursales` and `puntos_venta.id_sucursal`.
- Stock re-keyed to `id_sucursal`; the four duplicated `UpsertStockAsync` helpers collapse into one.
- Reads (stock reports, ficha de artículo, MCP `articulo_info`, sales roll-up) by sucursal.
- Web: sucursales ABM, PV picks its sucursal, stock screens speak sucursal.
- Merge two sucursales (sum stock with an audited movement) — needed only if the customer already
  runs two PVs with split stock.
- Split `parametros` keys into sucursal-level and register-level.

## Constraints

- Every new table follows doc 10 naming and doc 09 scoping; RLS backfills set the platform GUC and
  are tested under `ways_app` (`rls-migration-backfills`).
- New constraints ship with their error-translation backstop (`db-error-backstops`).
- `InventarioDeDependientes` / `InspectorDeUso` must learn the new level, or baja guards miss stock
  (`structural-guard-tests`).
- Stock invariants (`stock.cantidad = Σ movimientos`, `Σ stock_lotes = stock`) and the
  deterministic lock order must hold after re-keying.
- POS web changes reach registers only with a Ways.Desktop bump + `pos-v*` tag (owner's OK).

## Delivery strategy

`ask-on-risk` with the standing chain preference `stacked-to-main`: one PR per task.
Forecast ~3,500 authored lines over six tasks (T2 is the largest, ~1,200).

## Tasks

- [x] T1 — `sucursales` table (tenant + empresa scoped), `puntos_venta.id_sucursal NOT NULL`,
  1:1 backfill, RLS, entity/config, PV alta creates its own sucursal, delete-guard bridge,
  docs 09/10. Route: delegated (2+ non-trivial files). Commit b3c906fe.
- [x] T2 — Stock tables re-keyed to `id_sucursal`; single upsert helper used by ventas, remitos,
  compras, stock, lotes; transfers between sucursales; lock order. Route: delegated (opus writer).
- [x] T3 — Reads by sucursal: existencias/vencimientos/reposición/rotación, ficha de artículo, MCP
  `articulo_info`, sales grouped by sucursal, plus the T2 judgment-day carry-overs. Route: delegated.
  Commit 2087d350.
- [ ] T4 — Web: sucursales ABM, PV form picks sucursal, stock screens and transfers by sucursal.
  Route: delegated.
- [ ] T5 — Merge two sucursales (move PVs, sum stock and lots with audited movements).
  Route: delegated. Conditional on the customer's current setup.
- [ ] T6 — `parametros`: sucursal-level keys (`lotes_habilitado`, `zona_horaria`, `dias_*`) vs
  register-level keys (`tolerancia_pago`, `importe_envio`). Route: delegated.

## Acceptance criteria

- After T1 every PV belongs to exactly one sucursal; a fresh PV gets its own; existing data intact.
- After T2 two PVs in the same sucursal sell, buy and adjust against one stock row per article.
- Stock reports never show the same stock twice for two registers of one sucursal.

## Progress

- 2026-10-30: plan approved (option A). Worktree `.claude/worktrees/sucursales-t1`,
  branch `pnader/sucursales-t1`.
- 2026-10-31: T1 done (commit b3c906fe). Model: `sucursales` (`id_sucursal` identity, `id_tenant`,
  `id_empresa`, `nombre citext(150)`, timestamps + `deleted_at`; FK `(id_empresa, id_tenant)` →
  empresas; AKs `(id_sucursal, id_tenant)` and `(id_sucursal, id_empresa, id_tenant)`; RLS
  `sucursales_tenant`). `puntos_venta.id_sucursal NOT NULL`, FK `(id_sucursal, id_empresa, id_tenant)`
  so a PV cannot point at another empresa's sucursal. Migration `Sucursales` with idempotent 1:1
  backfill under `ways_app`. New PVs get their sucursal from a `WaysDbContext.SaveChanges` hook
  (skips `IdEmpresa == 0`, only reachable in InMemory tests). PV baja soft-deletes a sucursal left
  without live PVs; empresa/tenant bajas cascade. `nombre` is citext(150) to mirror `puntos_venta`.
  Checks: build 0 errors; Domain.Tests 2037 ✔; Application.Tests 1464 ✔ (structural guards
  included); IntegrationTests filtered (PuntoVenta, Organizacion, Aprovisionamiento,
  AislamientoDeTenant, Sucursal, Dispositivos, Migracion, Backfill, Rls) 442 ✔ + 1 fixed and
  re-run green; full integration suite left to CI. Mutations: hook removed → 4/5 red; sibling
  guard forced → red. judgment-day: APPROVED round 1, no findings from either judge.
  RDD: assess pending (judgment-day is the project's PR gate).

- 2026-10-31: T1 merged (#547). Full local integration suite on T1: 5686 ✔, 0 failed (1 h 12 m).
- 2026-10-31: T2 done. Migration `StockPorSucursal`: `stock` PK `(id_articulo, id_sucursal)`,
  `stock_lotes` PK `(id_articulo, id_sucursal, id_lote)`, FKs to sucursales; `movimientos_stock`
  + `id_sucursal NOT NULL` + `id_sucursal_destino`, keeps `id_punto_venta`. Backfill under
  `ways_app`, aborts on collapse. `EscritorDeStock` (Application/Stock) replaces the five raw-SQL
  writers; lock order (sucursal, artículo, lote NULLS FIRST); reversals use the stored sucursal;
  same-sucursal transfer → 409 `transferencia_misma_sucursal`. Delete guard: Empresa bridges via
  sucursales; last PV of a sucursal with stock → `punto_venta_en_uso`. Approved out-of-surface
  edits: `CalculadorDeEdicionDeCompra.cs`, `ServicioDeCompras.Edicion.cs`.
  Checks: build 0 errors; Domain 2038 ✔; Application 1464 ✔; integration filter (stock, lotes,
  ventas, remitos, compras, transferencias, conteo, mínimos, reportes, ficha, sucursal,
  migraciones, RLS, SuperficieDeAutorizacion) 2344 ✔ + 5 MCP ficha tests fixed and re-run in a
  1493-test subset ✔; guard round 572 ✔. Mutations: reversal by current sucursal, wrong
  resolver, collision check off, calculator key without sucursal, bridge removed, sucursal usage
  check skipped/forced — each red. RED came from mutations (code written before tests).
  judgment-day: APPROVED round 1, no CRITICAL; info: migration holds ledger locks for its
  duration (deploy off-hours), Down() lossy once PVs share a sucursal, items carried to T3.
  Deferred to T5: `ResolverSucursalAsync` takes no lock on `puntos_venta`.

- 2026-10-31: T3 done (commit 2087d350). Stock report routes take `idSucursal` or the legacy
  `idPuntoVenta` (resolved to its sucursal; exactly one, else 400 `alcance_de_stock_requerido` /
  `alcance_de_stock_ambiguo`); responses carry `idSucursal` + `nombreSucursal` and drop `idPuntoVenta`
  (the web only read it as a type; TS types and fixtures updated, no screen redesign). Zone and
  alert/rotation days still resolve per caja: with `idSucursal`, from the lowest-id caja (moves to the
  sucursal in T6). Export files are `existencias_sucursal{id}_…`; the shared exporter header still
  prints "Punto de venta: Sucursal N" (its label lives outside T3's surface, fix with T4). Soft-deleted
  sucursal, decided per surface: stock reports treat it as nonexistent (404, also via the caja that
  still references it); ficha and `ventas_agrupadas` keep the row labelled `(sucursal id N)` so totals
  do not lose stock or amount. Ficha already read stock by sucursal (T2): only the copy and the
  `StockDeFicha.Sucursal` field changed. New `sucursal` dimension in `ventas_agrupadas`. Carry-overs:
  stale comments and the `stock_duplicado` message now say sucursal; `EscritorDeStock` comment now
  states it is the only writer of movements and quantities (the mínimos upsert only creates a row at
  0); same-sucursal transfer check moved before article/lot resolution (kept again in the
  transaction for a caja moved between the two reads); compra edición takes the aggregate `stock`
  row before the lots even when the group nets to zero.
  Checks: build 0 errors; Domain 2109 ✔; Application 1476 ✔ (guards included); integration filter
  (stock, lote, reporte, ficha, mcp, venta, sucursal, reposicion, existencia, compra, vencimiento,
  rotacion, manejador de errores, SuperficieDeAutorizacion) 2576 ✔ (49 min); web build ✔, vitest
  8857 ✔ (one unrelated CompraEditor flake on the first run, green on rerun).
  RED came from mutations (code was written before tests). Mutants killed: no sucursal filter in
  existencias, vencimientos, reposición and rotación; `IgnoreQueryFilters` on both sucursal lookups
  (soft-deleted); caja+sucursal both accepted; export name by caja; sales grouped by caja id; sales
  ignoring the chosen caja; transfer check after article resolution; the four `EnOrdenDeLocks` keys
  (sucursal, artículo, lote.HasValue, lote). Not mutation-covered: the ficha two-cajas test is weak
  (a by-caja read would be needed to fail it), and the compra edición aggregate-first lock has no
  observable effect besides lock order (raw ADO, no pause point), so it is fixed but not pinned.

## Next step

T4 — web: sucursales ABM, PV form picks its sucursal, stock screens and transfers by sucursal (switch
the stock screens to `idSucursal`, header label "Sucursal").
