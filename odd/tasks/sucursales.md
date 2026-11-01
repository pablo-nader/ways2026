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
- [x] T4 — Sucursales API + ABM, PV picks/moves sucursal, stock screens and transfers by sucursal.
  Route: delegated.
- [x] T5 — Merge two sucursales (move PVs, sum stock and lots with audited movements), plus the T4 judges'
  follow-ups (transfer re-check under lock, PV baja single read, transfer copy). Route: delegated. Commits 16c3bc89, 37c8da13, b687094d.
- [x] T6 — `parametros`: sucursal-level keys (`lotes_habilitado`, `zona_horaria`, `dias_*`) vs
  register-level keys (`tolerancia_pago`, `importe_envio`). Route: delegated. Commits a19eb387, 25ef462a.

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

- 2026-10-31: T4 done. API: `GET /api/sucursales[?idEmpresa]` (LecturaDePuntosVenta, la usan los filtros de
  stock), `POST|PUT|DELETE` (GestionDeOrganizacion); PV alta con `idSucursal` opcional (404 / 409
  `sucursal_de_otra_empresa`); mover caja = `POST /api/puntos-venta/{id}/sucursal {idSucursal}`; si era la
  última caja y la sucursal tiene uso: 409 `sucursal_en_uso` (mismo guard que la baja de la última caja, así que
  mover la única caja de una sucursal con historia es la fusión de T5); sin uso, la sucursal cae (`sucursal.baja`
  por cascada). Baja directa: 409 `sucursal_con_puntos_venta` / `sucursal_en_uso`. Audit: `sucursal.alta|edicion|baja`,
  `pv.sucursal`. Sin migración ni constraints nuevas (el nombre no es único, igual que el de PV).
  Locks: advisory del tenant + `FOR UPDATE` de la fila del PV; `EscritorDeStock.ResolverSucursalAsync` ahora lee
  con `FOR SHARE`, y la baja del PV toma también el lock de fila (cierra el hueco que T2 dejó). Transferencias:
  `idSucursalOrigen/Destino` alternativo a los PV (exactamente una forma, 400
  `transferencia_origen_destino_invalido`); la caja de trazabilidad es la de menor id. Web: pantalla Sucursales
  (`/organizacion/sucursales`), PV con selector en el alta y modal "Sucursal" para mover, Existencias / Vencimientos
  / Reposición / Conteo / Transferencias por sucursal (las escrituras que aún piden PV usan la caja de referencia).
  T3: cabecera de exports "Sucursal: <nombre>"; `TryGetValue` en ventas por sucursal.
  Checks: build 0 errores; Domain 2110 ✔; Application 1476 ✔; integración filtrada 1313 ✔ + 1 guard
  (`SuperficieDeAutorizacion`, allowlist) corregido y re-corrido ✔; web build ✔, vitest 8894 (un flake de
  GrillaDeItemsDeCompra bajo carga, verde al re-correr). RED por mutación (código antes que tests). Mutantes
  muertos: guard del movimiento, guard de uso y de cajas vivas de la baja, misma empresa (alta y movimiento),
  cascada de baja de la sucursal vacía, `TryGetValue`. Diferido: los ajustes web de Tablero siguen por PV.

- 2026-10-31: T5 done. `POST /api/sucursales/{id}/fusionar {idSucursalDestino}` (GestionDeOrganizacion): 404 primero para
  origen o destino inexistente, dado de baja o de otro tenant; 400 `sucursal_destino_requerida` / `fusion_misma_sucursal`
  (mismo criterio que `transferencia_origen_igual_destino`); 409 `sucursal_de_otra_empresa`. Una transacción sin reintento:
  cajas vivas del origen → destino; `EscritorDeStock.FusionarSucursalesAsync` (primitiva nueva del único escritor) asienta un
  par `transferencia` por saldo no nulo (agregado fuera de lotes y cada lote, negativos incluidos), egreso en el origen e
  ingreso en el destino con `id_sucursal_destino`; el origen queda en cero y con `deleted_at`, rastro único
  `sucursal.fusion` (destino, ids de cajas, artículos, lotes, movimientos). Motivo: `transferencia` (sin motivo nuevo ni
  migración; las observaciones dicen "Fusión de la sucursal X en Y"). Umbrales: el destino conserva su par
  `minimo`/`reposicion`; donde no tiene ninguno de los dos toma el par del origen. Caja firmante: la viva de menor id de cada
  lado (o la del otro lado); sin cajas en ninguna, solo un origen sin saldo (409 `sucursal_sin_puntos_venta`). Locks: advisory
  del tenant → `FOR UPDATE` de las cajas de las dos sucursales en orden de id → stock de las dos sucursales en orden de
  sucursal, agregados antes que lotes. Seguimientos de T4: la transferencia relee la sucursal de cada caja con `FOR SHARE` en
  orden de id (el mismo de la fusión) y rechaza 409 `sucursal_cambio_durante_transferencia` si difiere de la pedida (o, por
  caja, de la leída antes; reemplaza el chequeo en transacción de misma sucursal, que ya no podía dispararse); la baja de caja
  lee solo el tenant antes de los locks y la caja una sola vez después; la confirmación de Transferencias dice "entre
  sucursales". Web: botón "Fusionar" por fila en Sucursales, modal con destino de la misma empresa (sin el origen), texto de
  lo que pasa y casilla de confirmación; escritura con guarda síncrona y pantalla inerte hasta el refresco.
  Checks: build 0 errores; Domain 2111 ✔; Application 1476 ✔ (guards); integración filtrada 984 ✔ (24 min, una sola corrida); web build ✔,
  vitest 8899 ✔ (sin flakes). RED antes del código: la baja de caja (aserción de la cascada) y los 400/409 de la fusión (sin
  ruta; el de 404 pasaba en falso); la suma de la fusión y la carrera de la transferencia fallaban por la siembra, así que su
  RED es por mutación. Mutantes muertos: sin lotes, sin negativos, sin par de movimientos
  (solo saldos), sin baja del origen, sin chequeo de misma empresa, sin chequeo de cambio de sucursal en la transferencia,
  pre-lectura trackeada en la baja de caja. Prueba de concurrencia: seis ventas en las dos cajas durante la fusión, sin
  deadlock y con la suma conservada (no determinista; el argumento de orden está en el comentario del servicio).
  Diferido: la baja de caja que se lleva su sucursal vacía no deja `sucursal.baja` (la marca sin rastro, desde T1).
  Judgment-day ronda 1 (CRITICAL, los dos jueces): las reversas (anulación de venta, remito y compra; edición de compra
  confirmada) asentaban en la sucursal guardada, que tras una fusión es el origen dado de baja, y no se serializaban con la
  fusión. Corrección (f02d4102): modelo `sucursales.id_sucursal_fusion integer NULL` + FK compuesta `fk_sucursales_fusion`
  `(id_sucursal_fusion, id_empresa, id_tenant)` → sucursales, migración `FusionDeSucursales`, sin backfill, sin mapeo 23503
  nuevo (validación bajo lock + backstop genérico; SQLSTATE en `SucursalesEsquemaTests`). La fusión la fija al destino y
  toma `FOR UPDATE` sobre las dos sucursales después de las cajas y antes del stock (lectura única después de los locks);
  `EscritorDeStock.ResolverSucursalesDeReversaAsync` toma `FOR SHARE` de las cajas y después de cada sucursal de la cadena y
  las cuatro reversas asientan en la resuelta (lotes bajo el mismo `id_lote`). Comentarios de orden de locks corregidos.
  Pruebas: `ReversasTrasFusionDeSucursalesTests` (venta, cadena S1→S2→S3, remito, compra con lote, edición, carrera
  determinista con lock del owner); mutantes muertos: sin resolución en cada reversa, cadena de un solo tramo, sin
  `FOR UPDATE` de sucursales (deadlock → 503). Integración filtrada 2363 ✔.

- 2026-10-31: T6 done (a19eb387 backend, 25ef462a web). Un primer escritor quedó cortado a mitad de camino (WIP
  c5fc46f7, reemplazado). Modelo: `parametros.id_sucursal integer NULL` + FK compuesta `fk_parametros_sucursal`
  `(id_sucursal, id_empresa, id_tenant)` → sucursales (Restrict), CHECK `ck_parametros_un_solo_alcance` (a lo sumo uno de
  `id_punto_venta`/`id_sucursal`), índice `ix_parametros_sucursal (id_sucursal, id_empresa, id_tenant)` (respalda la FK;
  se quitó el índice duplicado con nombre de convención del WIP), único parcial `ux_parametros_sucursal` (vivas) y
  `ux_parametros_empresa` que ahora pide `id_sucursal IS NULL`; 23505 → 409 `parametro_duplicado`. Migración
  `ParametrosPorSucursal` con backfill `SqlDelBackfill` (const, `SET LOCAL app.acceso`) y reversa. `ParametroConocido`
  tiene `Alcance`; `ResolucionDeParametros` resuelve sucursal → empresa → default para las cinco claves de sucursal (las
  filas por caja no cuentan) y deja igual las de caja; `ConsultaDeParametros` para los lectores directos; el checkout trae
  las filas de la sucursal en la misma consulta (sin viaje extra). Escritura: 400 `parametro_de_sucursal`,
  `parametro_de_caja`, `parametro_alcance_ambiguo`, `sucursal_no_pertenece_a_la_empresa`; flip de lotes reconcilia la
  sucursal escrita o las de la empresa por valor efectivo. Reportes de stock, lotes y `AlcanceDeListadoHttp` resuelven por
  sucursal (sin caja de referencia). La fusión conserva las filas del destino y toma las del origen donde falta la clave.
  Golden de dependientes con `parametros` → sucursales. Web: selector de sucursal o de punto de venta según la clave.
  Correcciones sobre el WIP: índice FK duplicado, doc-comment de `AlcanceDeListadoHttp` partido, comentarios de
  precedencia "PV → empresa" en reportes que ya no eran ciertos, test de cajas que comparten lotes. Checks: build 0
  errores; Domain 2118 ✔; Application 1476 ✔; integración filtrada 3343 ✔ (56 min); web build ✔, vitest 8906 ✔.
  Mutantes muertos: orden de niveles sucursal/empresa, filas por caja contando para una clave de sucursal, fila de
  sucursal tomada como de empresa, backfill con la caja de mayor id (3 tests), regla de fusión invertida, cada guarda de
  escritura (de sucursal, de caja, los dos ids, sucursal de otra empresa) y reconciliación de lotes sin la sucursal; el
  `SET LOCAL` lo prueba el test sobre `ways_app` corriendo la constante con y sin esa línea.

## T6 design (2026-10-31)

Map: `parametros` is empresa-scoped with an optional `id_punto_venta` override; 12 keys live in
`ParametroConocido`; the only writer is `ServicioDeParametros.EstablecerAsync`; sucursal reports and
`ServicioDeLotes.ReconciliarAsync` pick the lowest-id caja as reference; the packaged POS reads only
`tolerancia_pago`/`importe_envio` (instantánea + online GET), so no POS release is needed.

- Domain: `ParametroConocido` gains an `Alcance` (Caja | Sucursal). Sucursal: `zona_horaria`,
  `lotes_habilitado`, `dias_alerta_vencimiento`, `dias_rotacion`, `dias_cobertura_objetivo`. Every
  other key stays Caja (PV → empresa → default, unchanged).
- Model: `parametros.id_sucursal integer NULL`, composite FK `(id_sucursal, id_empresa, id_tenant)` →
  sucursales (Restrict), CHECK at most one of `id_punto_venta`/`id_sucursal`, partial unique
  `ux_parametros_sucursal` (live rows), `ux_parametros_empresa` also requires `id_sucursal IS NULL`.
  23505 backstop for the new index.
- Resolution of a sucursal key: sucursal row → empresa row → default; PV rows never count. Callers
  that pass a PV resolve through that PV's sucursal.
- Backfill (migration, under `ways_app` GUC, idempotent): for each live sucursal and sucursal key,
  copy the value of the lowest-id live caja that has a row for that key; then soft-delete every PV
  row of a sucursal key. With the 1:1 T1 backfill this keeps every effective value.
- Writes: `ParametroAlta` gains `IdSucursal`; sucursal key with `IdPuntoVenta` → 400
  `parametro_de_sucursal`; caja key with `IdSucursal` → 400 `parametro_de_caja`; both ids → 400.
  The `lotes_habilitado` false→true reconcile runs on the affected sucursal(s) by effective value.
- Reports: `ResolverAlcanceAsync` and lotes reconcile resolve by sucursal; lowest-id caja removed.
- Merge (T5): destination keeps its sucursal rows and takes the origin's where it has none; origin's
  rows soft-deleted. PV move: nothing to do (the caja now reads its new sucursal's values).
- Web: `Parametros.tsx` shows a sucursal selector for sucursal keys and the PV selector for caja keys.
- Docs: `docs/10-modelo-de-datos/09-parametros-operativos.md` updated.

## Next step

Feature completa (T1–T6). Pendiente: judgment-day y PRs apilados de las tareas que todavía no se mergearon, en orden,
reapuntando cada hijo a main cuando su base entre.
