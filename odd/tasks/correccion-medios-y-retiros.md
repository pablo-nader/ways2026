# Corrección de medios de pago y retiros

## Objective

Let cashiers fix the medio de pago of a ticket from the POS while the turno is open, and let
the back office fix a venta's medios and the retiros a cashier recorded wrong, as long as the
conciliación de tesorería has not frozen that date.

## Problem / why

Cashiers pick the wrong medio (efectivo vs. tarjeta, one card vs. another) and type retiros
wrong. Today nothing edits a venta's `cobros` and `movimientos_caja` is append-only, so the
arqueo and the libro de tesorería carry the mistake forever.

## Owner decisions (2026-11-01)

- A venta with an approved CAE cannot change medios (anular and redo). Without CAE the change
  is allowed and the venta's aporte moves from the old medio's `acumulados_de_facturacion` to
  the new one; no factura is emitted or anulled by the change.
- Cuenta corriente is excluded: neither the old nor the new medio may be `CuentaCorriente`.
- Admin may edit importe and motivo of a retiro and delete it. The automatic
  "Retiro de cierre de turno" row is not editable.
- Every new grid (POS tickets del turno, back-office ventas, back-office retiros) has a filter
  on every column and click-to-sort on every column.

## Scope

- POS: change medios of a ticket of the open turno (`VentasDelTurno`).
- Back office (Admin): change medios of any venta; edit/delete retiros; open or closed turno.
- Guards: CAE, cuenta corriente, conciliación (`GuardaDeConciliacion` shared lock first),
  turno abierto for the POS, medios validation (`ValidadorDePagos`), same total.
- Closed turno: recalculate the arqueo with the #322 marca (`fecha_recalculo`,
  `importe_esperado_original`), append `movimientos_tesoreria` Ajuste for retiros, and
  compensate `movimientos_cuenta` for electronic medios that already credited a cuenta.
- Audit every edit (`AccionAuditada`).

Out of scope: editing cuenta corriente cobros, editing the cierre retiro, Mercado Pago.

## Constraints

- Offline queue: the replay dedup compares pagos (`ExigirMismoContenido`). Editing a venta the
  device has not acknowledged would make its retry answer 409; the POS only edits ventas it
  lists from the server, and the dedup must accept a replay of an already-edited venta.
- Lock order follows `ServicioDePagos.EjecutarEscrituraAsync`: conciliación shared → turno →
  rows; single read under lock.
- POS web changes reach the cajas only with a Ways.Desktop bump + `pos-v*` tag.

## Delivery strategy

Forecast well above 400 authored lines → chained PRs, `stacked-to-main` (project protocol).
Each slice passes judgment-day before its PR.

## Tasks

- [x] T1 — Backend: change a venta's medios (service, POS + admin endpoints, guards, facturación
  acumulados, closed-turno arqueo + cuenta compensation, audit, offline dedup), with tests.
- [x] T2 — Backend: retiros list (filters + sort) and admin edit/delete (soft delete, arqueo
  recalc, tesorería Ajuste, audit), with tests and migration.
- [x] T3 — Backend: back-office ventas list with per-column filters and sort, with tests.
- [x] T4 — Web: reusable sortable header + POS `VentasDelTurno` sort and "cambiar medio" modal.
- [x] T5 — Web: back-office ventas grid (filters + sort) with medio edit.
- [x] T6 — Web: back-office retiros grid (filters + sort) with edit/delete.

## Acceptance criteria

- POS changes medios only with the turno open; admin with any turno; both refused with 409
  when the date is conciliada, the venta has CAE, or a cuenta corriente medio is involved.
- Total of cobros unchanged; arqueo and libro reflect the change; audit row written.
- Grids filter on every column and sort on every column.

## Progress

| Task | Route | Commit | Review | Checks |
|------|-------|--------|--------|--------|
| T1 | delegated (writer: 2+ non-trivial files) | a672d216 (refactor), f5e2fc1d (feat) | pending (judgment-day at slice) | build 0 errors; Domain.Tests 2258/2258; Application.Tests 1639/1639; integration CambioDeMedios + SuperficieDeAutorizacion + regression (pagos edición, checkout, anulación, offline/reservas, facturación, cuentas, conciliación, sin reintento) 430/430; 5 mutants killed |
| T2 | delegated (writer: 2+ non-trivial files) | e7e0ab43 (fix, audit labels), 712ed19e (feat) | pending (judgment-day at slice) | build 0 errors; Domain.Tests 2298/2298; Application.Tests 1641/1641; integration new (RetirosDeAdministracion + ListadoDeRetiros) 43/43; regression (Caja, Turno, Libro, Conciliacion, Pago, Auditoria, Superficie, Aislamiento, SinReintento, CambioDeMedios, FiscalSchema, Tesoreria, Historico, Reportes) 1617/1617; web tsc clean, vitest tipos + Auditoria 28/28; mutants killed: 11 filter clauses, sort switch, 3 deleted_at/guard clauses, conciliación guard, backfill SET LOCAL |
| T3 | delegated (writer: 2+ non-trivial files) | 369100ed (refactor), 87a96a32 (feat) | pending (judgment-day at slice) | build 0 errors; Domain.Tests 2358/2358; Application.Tests 1641/1641; integration new (ListadoDeVentasDeAdministracion) 64/64; regression (LectoresDeComprobantesVenta, SuperficieDeAutorizacion, Aislamiento, VentasListadoExport, CambioDeMedios, RetirosDeAdministracion, ListadoDeRetiros, SinReintento) 210/210; mutants killed: all Filtrar clauses, sort switch, SinEspejosFiscales, c.DeletedAt, co.DeletedAt (filter and summary), min(cierre, fecha) rule |
| T4 | delegated (writer: 2+ non-trivial files) | b93fc48b (modal + rules + client), dd02e315 (VentasDelTurno action + sort tests) | pending (judgment-day at slice) | tsc -b clean; oxlint no findings in touched files; vitest full 310 files / 9469 tests green (first full run had one unrelated flake in GrillaDeItemsDeCompra, green alone and on the rerun); new tests: cambioDeMedios 34, ModalDeCambioDeMedios 31, useEnLinea 2, VentasDelTurno +21 (6 columns x asc/desc, 9 action cases); mutants killed: double-click ref guard, stale-read generation, CC filter, no-changes gate, error focus, offline disable, sort fallback to original order |
| T5 | delegated (writer: 2+ non-trivial files) | c17ccb11 | pending (judgment-day at slice) | tsc clean; oxlint no new findings; vitest full 316 files / 9692 tests, one unrelated flake in GrillaDeItemsDeCompra (green alone); new tests: VentasDeAdministracion 60 (13 sort columns x 3 clicks, 15 filters, role gating, 6 blocked-reason titles, modal contract, stale response, clamp), query builder 34, utilities 14, menu +2; mutants killed: stale gate, page reset (filter, text filter, sort), Limpiar keeps tamanio, debounce, numero integer guard, idMedioPago param, turnoCerrado, Admin-only gate, medioEditable gate, refresh after save, inert grid with modal |
| T6 | delegated (writer: 2+ non-trivial files) | 9c59ed81 | pending (judgment-day at slice) | same full run; new tests: RetirosDeAdministracion 69 (9 sort columns, 11 filters, edit/delete flows, 5 code mappings x edit and delete, double click, stale), query builder 24, utilities 20; mutants killed: modal ref guard, conciliado gate, 404 refresh, motivo trim, stale error cleared on typing, refresh after delete, inert screen with modal; one mutant (screen-level ref in confirmarBaja) survived and the dead guard was deleted because ConfirmacionDeBaja already absorbs the double click |

## T1 notes

- Endpoints: `PUT /api/ventas/{id}/medios` (OperacionDePos, open turno) and `PUT /api/ventas/administracion/{id}/medios`
  (GestionDeCatalogo). Codes: `venta_turno_cerrado`, `venta_con_cae`, `venta_con_factura_en_tramite`, `venta_anulada`,
  `comprobante_sin_cambio_de_medios`, `cambio_de_medios_con_cuenta_corriente`, `total_de_cobros_distinto` (400),
  `cambio_de_medios_sin_diferencia`, `periodo_conciliado`, plus `ValidadorDePagos` codes.
- No schema change: old cobros soft-deleted, new inserted; offline replay matches the deleted cobros born with the
  comprobante (`created_at`). Facturación: stored decision aportes rewritten; ventas without a decision row recompute the old
  devengo with current percentages (documented limitation). Closed turno: shared arqueo recalculation + one `ajuste` per
  credited medio, or cierre rows for a medio the cierre never credited.
- Open: behavioral transient-retry test for the new non-retrying site (structural guard only).

## T2 notes

- Endpoints: `GET /api/caja/retiros` (LecturaDeReportes: Supervisor + Admin, same as the libro), `PUT /api/caja/retiros/{id}`
  body `{importe, motivo}` → 200 `FilaDeRetiro`, `DELETE /api/caja/retiros/{id}` → 204 (both GestionDeCatalogo, Admin).
- List params: `desde`, `hasta` (creado_el, inclusive), `idPuntoVenta`, `idTurnoCaja`, `idEmpleado`, `importeDesde`, `importeHasta`,
  `motivo` (accent/case-insensitive), `turnoCerrado`, `conciliado`, `esRetiroDeCierre`, `pagina` (1), `tamanio` (25, max 200),
  `orden` (fecha, puntoVenta, turno, empleado, importe, motivo, estadoTurno, conciliado, esRetiroDeCierre), `direccion`
  (ascendente/descendente). Default order fecha desc, id desc; with orden, id asc tiebreak. Envelope `{items, total, pagina, tamanio}`.
- Codes: 404 `no_encontrado` (missing, other tenant, deleted, refuerzo/apertura) before the body; 409 `retiro_de_cierre_no_editable`;
  400 `movimiento_de_caja_importe_invalido` / `movimiento_de_caja_sin_motivo`; 409 `periodo_conciliado`; 400 `orden_invalido`,
  `direccion_invalida`, `direccion_sin_orden`.
- Schema: `movimientos_caja.deleted_at` (EF `BajaLogica` filter on MovimientoCaja) and `es_retiro_de_cierre` (set by
  CerrarPorRetiro; backfill by fixed motivo + creado_el = fecha_cierre). Libro date = turno fecha_cierre, or now if open.
- Locks: conciliación shared → turno FOR UPDATE → retiro FOR UPDATE + single EF read → (closed) recalculation + tesorería advisory.
  Closed turno: `RecalculoDeArqueoDeTurnoCerrado` + `movimientos_tesoreria` Ajuste for the difference. Only Retiro is editable.
- Finding: `IgnoreQueryFilters(["BajaLogica"])` disables the filter for the WHOLE query, so queries that use it exclude deleted
  rows by hand.

## T3 notes

- Decision: a dedicated `GET /api/ventas/administracion`, not an extension of `GET /api/ventas`. The existing list is `OperacionDePos`
  (a Vendedor reads it), returns ids without names and feeds the XLSX export; widening it would change who reads what and its
  response shape. Both stay untouched (tests green). The grid lives beside T1's `PUT /api/ventas/administracion/{id}/medios`.
- Auth: group `/api/ventas/administracion` is `LecturaDeReportes` (Supervisor + Admin, as the libro and the retiros list); the PUT adds
  `GestionDeCatalogo` (AND), so Supervisor reads but cannot edit. The UI should show the edit action to Admin only.
- Query params (all optional): `desde`, `hasta` (fecha, inclusive), `numero` (exact sequential number; PV and tipo have their own
  filters), `idTipoComprobante`, `idPuntoVenta`, `idTurnoCaja`, `cliente` (text, accent/case-insensitive over the shown name),
  `idEmpleado`, `idMedioPago` (a live cobro with that medio), `totalDesde`, `totalHasta`, `estado` (`Emitido`|`Anulado`),
  `estadoFiscal` (`sin_factura`|`en_tramite`|`con_cae`), `turnoCerrado`, `conciliado`, `pagina` (1), `tamanio` (25, max 200),
  `orden` (fecha, numero, tipo, puntoVenta, turno, cliente, empleado, medios, total, estado, estadoFiscal, estadoTurno, conciliado),
  `direccion` (ascendente/descendente). Default order fecha desc, id desc; with `orden`, id asc tiebreak; empty names / no turno /
  no cobros go last in both directions. `estado` sorts alphabetically (Anulado < Emitido), `numero` by (punto de venta, numero),
  `medios` by the summary string. `turnoCerrado` true/false never matches a venta without turno.
- Envelope `{ items, total, pagina, tamanio }`. Item: `id, numero, numeroVisible, fecha, idTipoComprobante, codigoTipo, tipo,
  idPuntoVenta, puntoVenta, idTurnoCaja, turnoCerrado (null = sin turno), idCliente, cliente, idEmpleado, empleado, total, estado,
  estadoFiscal, medios (distinct names by name, "A, B"; null without cobros), conciliado, medioEditable, motivoMedioNoEditable`.
  Deleted PV / cliente / empleado come as null id + null name (filters do not match them).
- Errors: 400 `orden_invalido`, `direccion_invalida`, `direccion_sin_orden`, `estado_fiscal_invalido`; 401/403 by role.
- `medioEditable` is computed in C# per page row by `ReglaDeCambioDeMedios.MotivoDeBloqueo` (the same guards, same order as
  `ServicioDeCambioDeMedios`: venta_anulada, comprobante_sin_cambio_de_medios, venta_con_cae, venta_con_factura_en_tramite,
  periodo_conciliado, cambio_de_medios_con_cuenta_corriente). A test PUTs each blocked venta and asserts the real 409 code equals
  `motivoMedioNoEditable`. It is not a filter/sort column (derived from the columns that are). Rejected invoices count as `sin_factura`.
- `conciliado` uses T1's date rule (`ReglaDeCambioDeMedios.FechaQueCuentaParaElLibro` = min(cierre del turno, fecha de la venta)) for every
  venta, so it also freezes ventas of open turnos or without turno whose own date is at or before the mark.
- Cobros for the edit modal: use the existing `GET /api/ventas/{id}` (`pagos`: idMedioPago, importe, referencia, vuelto; the web resolves
  names from the medios catalogue it needs for the selector anyway). The grid carries only the `medios` summary, aggregated in the same
  SQL query (no per-row reads). Sorting by `medios` joins a per-venta aggregate over all cobros (only that sort pays it).
- Structural: two reads registered in `LectoresDeComprobantesVentaEstructuralesTests` (filter by medio, medios aggregate), exact count 1.
- Open: the grid has no export; `numero` is an exact match, not a contains over the formatted number.

## T4 notes

- The sortable header and the sort already existed (`componentes/grilla`: `EncabezadoOrdenable`, `useOrdenamiento`, `ordenarFilas`,
  `siguienteOrden`), and `VentasDelTurno` already sorted all six data columns client-side. T4 built no new header; it added per-column
  asc/desc tests for the screen. Header API for T5/T6 (controlled, does not own the sort): `<EncabezadoOrdenable columna orden
  onAlternar className>` with `orden: {columna, direccion: 'ascendente' | 'descendente'} | null` (same literals as the server `direccion`
  param) and `onAlternar(columna)`; the cycle is asc → desc → none (`siguienteOrden`). For server-side sort keep `orden` in `useState`, call
  `siguienteOrden` in `onAlternar` and send `orden=<columna>&direccion=<direccion>` (omit both when `null`); do not use `useOrdenamiento`
  there (it sorts rows locally).
- Modal: `componentes/ModalDeCambioDeMedios` props `{ venta: {id, numeroVisible}, medios: MedioPagoListado[] (full catalogue; CC is dropped
  by the selector), modo?: 'pos' | 'administracion', onGuardado(comprobante), onCerrar() }`. Mount it with `key={venta.id}`. It reads
  `GET /ventas/{id}`, prefills the cobros, calls `clienteDeVentas.cambiarMedios(id, {pagos}, modo)` (`PUT /ventas/{id}/medios` or
  `/ventas/administracion/{id}/medios`) and never refreshes the list: the screen toasts and reloads in `onGuardado`. Pure rules live in
  `api/cambioDeMedios.ts` (`evaluarCambioDeMedios`, `motivoDeNoCambiarMedios(venta, medios)`, `mensajeDeErrorDeCambioDeMedios`, nine fixed
  Spanish texts for the 409/400 codes). For the grid in T5 use `medioEditable`/`motivoMedioNoEditable` from the server instead of
  `motivoDeNoCambiarMedios`.
- Local validation mirrors `ValidadorDePagos` through the checkout helpers (`validarPagosLocal`, `filasAPagosConVuelto`) plus the
  total check (importe − vuelto must equal the original cobrado; difference shown live, save disabled) and "no changes" (same reparto as
  the original). The tolerance is unknown to the web, so it is inferred as `total − cobrado original`.
- Offline: the POS lists ventas from the server, so the action is online-only. `pos/useEnLinea` (navigator.onLine, optimistic) disables
  it with an explanation; a real network failure still shows the error on save. It is not queued in the outbox.
- Not done here: the vuelto field is editable only for medios that admit it (non-admitting medios ignore a manual vuelto, so the
  `medio_no_admite_vuelto` rule is unreachable from this UI).
- POS web changes reach the cajas only with a Ways.Desktop bump + `pos-v*` tag (the release was NOT done here).

## T5 / T6 notes

- Routes: `/caja/ventas` ("Listado de ventas") and `/caja/retiros` ("Retiros"), Supervisor + Admin, in the Reportes menu group next to Tesorería.
  They hang under `/caja` because `RutasDeLaSpa` already lists `caja` (no backend change). Edit/delete actions render for Admin only
  (`puedeGestionarCatalogos`); the Supervisor reads.
- Shared pieces (new): `componentes/grilla/useGrillaDeServidor` (filters with 300 ms draft for text/number-range keys, server-side orden,
  page, generation gate, stable `recargar()` that resolves when the refresh landed and never rejects, out-of-range page clamp),
  `componentes/grilla/PaginadorDeServidor`, `paginas/useOpcionesDeEmpleado`, pure query builders in `api/ventas.ts` and `api/caja.ts`.
- Empleado filter: Supervisor cannot list users (`/api/usuarios` is Admin and Root), so Supervisor gets the employees already seen in the
  loaded pages (deduplicated); Admin gets the full user list. Known limit: a Supervisor cannot filter by an employee not yet on screen.
- Ventas: the cobros modal is the T4 `ModalDeCambioDeMedios` with `modo="administracion"`; the action uses the server's `medioEditable` and maps
  `motivoMedioNoEditable` through `MENSAJES_DE_CAMBIO_DE_MEDIOS` (unknown text shown as is). The tipo filter lists only `clase === 'Venta'`.
  Row click opening `ModalDetalleDeVenta` was not done (optional).
- Retiros: own edit modal (`ModalDeEdicionDeRetiro`, client rules = `importeValidoParaTipo` + `motivoValido`, mentions the arqueo recalculation
  on a closed turno, disabled while unchanged) and `ConfirmacionDeBaja` for delete. 404 and 409 on edit or delete re-request the list because
  the row on screen went stale. The retiro-de-cierre reason wins over the conciliado one.
- Not done: no export, no row detail; POS release is not involved (back office only).

## Judgment-day T5/T6 (round 1 fixes)

- JD-T56-1 (fixed, 8195314a): `desde`/`hasta` of the ventas and retiros administration queries now travel as local-offset day bounds
  (`00:00:00` / `23:59:59.999`) through the shared `api/fechas.ts#fechaIsoConOffset` (extracted from `reportes.ts`, behavior unchanged).
  Tests assert the bounds with the same helper plus a timezone-independent regex. Mutation: reverting the builders to the bare date failed
  10 tests.
- JD-T56-2 (fixed, 1c00c92b): the focus-restore effect waits for `!bloqueado && !cargando` before consuming the pending flag, and the
  fallback is Limpiar when enabled, otherwise the table (`tabIndex=-1`). Retiros delete now also restores focus. Tests: focus after save
  (ventas, retiros) and after delete with no filters. Mutation: dropping the `cargando` guard failed the two save tests.

## Next step

Judgment-day over the T1-T6 slices, then the stacked PRs (stacked-to-main).
