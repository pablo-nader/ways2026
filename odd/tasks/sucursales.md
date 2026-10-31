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
- [ ] T2 — Stock tables re-keyed to `id_sucursal`; single upsert helper used by ventas, remitos,
  compras, stock, lotes; transfers between sucursales; lock order. Route: delegated.
- [ ] T3 — Reads by sucursal: existencias/vencimientos/reposición, ficha de artículo, MCP
  `articulo_info`, sales grouped by sucursal. Route: delegated.
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

## Next step

T2 — re-key stock to `id_sucursal`, stacked on T1.
