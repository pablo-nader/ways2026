# Pagos y aplicaciones (compras ↔ pagos)

## Objective

Model supplier payments the way the owner thinks about them: four document groups — Compras,
Ventas, Pagos, Cobros. A compra is cancelled (fully or partially) by pagos; a venta by cobros.
What the code calls `Gasto` today **is a Pago**. Pagos carry several payment methods, can be
loaded on their own (the cashier records "how much and for what") and applied later to one or
more compras; a compra can be born paid, partially paid or 100% pending.

## Problem / why

- A payment is a `Gasto` row with ONE `IdMedioPago` and ONE optional `IdComprobanteCompra`.
- Overpaying a compra is rejected (`pago_excede_saldo_pendiente`, `ReglaDePagoDeCompra`).
- A pago not tied to a compra lowers the supplier balance but can never be applied to invoices
  later except whole, to one compra (`vincular-compra`).
- No rounding write-off: paying 1500 for 1503.50 leaves 3.50 owed.
- Confirmed compras cannot be edited (only anulada).

## Decisions (owner, 2026-10-28)

- Rename `Gasto` → `Pago` everywhere (code, DB, UI, docs).
- Pagos have N payment-method lines; origin can be the shift cash box (POS) or treasury.
- Applications pago ↔ compra are N:M; the unapplied part of a pago is a credit in favor.
- A compra, when loaded, can be paid on the spot, get existing unapplied pagos applied, or stay
  100% pending (stock, IVA and supplier CC as today).
- A pago without supplier (cashier) takes the compra's supplier when applied and only then
  enters that supplier's CC.
- Rounding: "close at zero" write-off up to a per-empresa tolerance, default $500
  (new known parameter, not the sales `tolerancia_pago`).
- Pagos and compras are editable without restrictions for now (restrictions come later).
- Mirror model for ventas/cobros comes last; `PagoComprobante`/`pagos_comprobante` (sale method
  lines) is renamed to cobros there.

## Constraints

- The POS bundle is packaged in Ways.Desktop: changing the HTTP wire contract breaks installed
  registers until a `pos-v*` release. Stage 1 keeps routes and JSON property names unchanged;
  stage 2 introduces the new wire contract and keeps `/api/gastos` as a compatibility adapter.
- Ledgers are never rewritten: edits produce compensating CC/treasury entries (existing
  `CalculadorDeAjustesDeGasto` pattern).
- Schema changes follow doc 10 naming, ship backstops (`db-error-backstops`), RLS-safe
  backfills (`rls-migration-backfills`) and the structural guard tests.

## Delivery strategy

`auto-chain` with `stacked-to-main` (owner asked for fully autonomous delivery, 2026-10-28):
one PR per stage slice, judgment-day before each PR, auto-merge + Auto-fix.

## Tasks

- [ ] E1a — Rename Gasto → Pago in Domain/Application/Infrastructure + DB migration (tables,
  columns, enum types/values, constraints, indexes, RLS policies, audit action codes, parameter
  keys). Wire contract (routes, JSON names) unchanged. Route: delegated (200+ files).
- [ ] E1b — Rename in Ways.Web (UI copy "Gasto" → "Pago", internal TS names), wire unchanged.
  Docs (doc 10 parts, doc 01 vocabulary). Route: delegated.
- [ ] E2 — Pago with N payment-method lines (arqueo, cierre, treasury per line) + new wire
  contract `/api/pagos` with `/api/gastos` compatibility adapter. Route: delegated.
- [ ] E3 — Applications pago ↔ compra (N:M), paid amount derived from applications, apply
  existing pagos when loading a compra, overpayment stays in favor, one pago over several
  compras, supplierless pago adopts the compra's supplier. Route: delegated.
- [x] E4 (written; CI pending) — Rounding write-off with `tolerancia_redondeo_pago_proveedor` (default 500); see the E4 progress entry.
- [ ] E5 — Pago editing with lines and applications, compensating entries.
- [ ] E6 — Confirmed compra editing (items, total, stock and CC by difference; excess applied
  amount goes back to credit). Slices S1–S4 and W1 below; S1 and S2 are written.
- [ ] E7 — Cobros: rename sale method lines to cobros, apply cobros to CC sales.
- [ ] POS release (`pos-v*`) once the POS bundle uses the new wire contract.

### E2 slices

Stacked on the Gasto → Pago rename. Each slice is one PR through judgment-day.

- [ ] E2-S1 — `items_pago` (one payment-method line per pago), migration `ItemsDePago` with backfill and the
  deferred header-sum constraint trigger, dual write in every `ServicioDePagos` writer, error-translation
  backstops, doc 10. No behavior change: each pago has exactly one item equal to its header.
- [x] E2-S2 — Per-medio readers derive from the items: the arqueo/cierre pagos-per-medio totals
  (`LectorDeMovimientosDelTurno`), the personal desglose (one line per item, `idItem` added, `idGasto` kept), and
  `CalculadorDeAjustesDePago` (`EstadoContableDePago.ImportePorMedio`, `AfectaElArqueo` compares per-medio maps).
  The audit payload of edit/baja gains `lineas: [{id_medio_pago, importe}]` (header `id_medio_pago` kept).
  Treasury, supplier-CC adjustments and exports keep the header (category/total only). The turno listing
  (`/api/gastos`, `LectorDeLineasDelTurno` via `ProyeccionesDePago`) showed the header medio only; S3b added
  `lineas` (additive) to the turno/`/api/gastos` listings and to the administration history.
- [ ] E2-S3a — Alta de pagos con N líneas por medio (POS and administración), new service contract. Treasury
  movements stay one per pago on the header total. Done together with the edit/replace-set and pagar compra:
  `ReglaDeLineasDePago` (1..20 lines, codes `pago_sin_lineas`, `pago_demasiadas_lineas`,
  `pago_linea_importe_invalido`, `pago_linea_referencia_muy_larga`), `Comando*` records with `Lineas`, and the
  current request records adapt to one line. Edit replaces the line set (live items reused by position, surplus
  soft-deleted, no physical delete); the old PUT routes answer `409 gasto_con_varios_medios` on a multi-item pago.
  Plantel keeps its own wire codes and shares only the amount arithmetic. The POS alta still does not validate
  the medio server-side (unchanged). Integration tests (`PagosConLineasTests`) need CI.
- [ ] E2-S3b — Plantel y baja de `pagos.id_medio_pago`. (1) El pago de personal con N líneas SIGUE siendo un pago por
  línea, cada uno con un único ítem (referencia en `pagos.detalle` y en el ítem) y un movimiento de tesorería por pago:
  el POS instalado ignora `lineas` y mostraría la fusión como una sola fila; se difiere a E2-P. El desglose trae una
  línea por ítem (`idGasto` por pago, `idItem` por ítem). (2) Migración `QuitarMedioDeLaCabeceraDelPago`: elimina la columna, `fk_pagos_medio_pago`
  e `ix_pagos_medio_pago`; `Down` los recrea y rellena con el ítem vivo de menor id (backfill con `SET LOCAL`, probado bajo
  `ways_app`). Todo lector del medio de la cabecera deriva el «medio de compatibilidad» del ítem vivo de menor id
  (`ProyeccionesDePago`, historial de administración, respuestas de alta, payload de auditoría `id_medio_pago`), y
  `PagoListado` y `PagoDeAdministracionListado` ganan `lineas` (aditivo). Compensaciones por medio: ya resueltas en
  S2/S3a (el recálculo del arqueo y los `EstadoContableDePago` leen los ítems). El test del backfill de `ItemsDePago` bajo
  `ways_app` vive ahora en `QuitarMedioDeLaCabeceraDelPagoMigracionTests` (base aislada migrada hasta `ItemsDePago`, donde
  la columna todavía existe). Integration tests (CI): `PagoDePersonalTests`, `PagosConLineasTests`,
  `PagosEdicionEndpointsTests`, `QuitarMedioDeLaCabeceraDelPagoMigracionTests`.
- [ ] E2-S4 (written; CI pending) — Wire contract `/api/pagos` with lines (POS alta/list/edit, administración CRUD, `de-compra/{idCompra}`, `vincular-compra`), same policies as the `/api/gastos` twins; `/api/gastos` and `POST /api/compras/{id}/pagos` untouched. New request records `Solicitud*ConLineas`, responses `PagoConLineas` / `ResultadoDePagoDeCompraConLineas` (key `pago`). The POS alta through `/api/pagos` validates each medio server-side (`ComandoDePago.ValidarMedios`); the legacy route still does not. Listings reuse `PagoListado`/`PagoDeAdministracionListado` (they keep the compatibility `idMedioPago`; trimming it is stage-C cleanup). Integration tests (`PagosApiConLineasTests`, allowlist entries in `SuperficieDeAutorizacionTests`) need CI.
- [x] E2-W1 — Web foundation: types and clients for the `/api/pagos` contract, pure helpers (`api/lineasDePago.ts`, with tests) and the shared `EditorDeLineasDePago` component. The POS payment form (`PagosDelTurno`, still on `/api/gastos` with one medio) does NOT use it yet: pending, tracked under E2-W2.
- [x] E2-W2 — Web: lines in the POS payment form. `PagosDelTurno` alta uses `EditorDeLineasDePago` (max 20 lines) and posts `POST /api/pagos` (`SolicitudDePagoConLineas`); the cash-drawer availability warning, the shortfall and the drawer pulse consider only the Efectivo lines paid from the shift cash (`importeEnEfectivoDeCaja`, `haySalidaDeEfectivoDeCaja`). The shift list and `DetalleDeTurnoListas` show the reparto (`textoDeMediosDelPago`) when a pago has more than one medio. `ModalDeEdicionDePago` is now lines-only and the POS edits through `PUT /api/pagos/{id}`; the legacy one-medio mode and the `/gastos` client calls are gone from the web (the backend keeps the legacy routes for old bundles). The administration part shipped with W3 (branch `pnader/pagos-e2-w1-w3-web`). Web release: POS bundle needs Ways.Desktop bump + tag `pos-v0.16.0` to reach the registers.
- [ ] E2-W4 — Lift the back-office CajaTurno single-line restriction (`Pagos`/`ModalDeEdicionDePago` `origenFondos` prop, `maximoDeLineas=1` and the read-only note) once every register runs `pos-v0.16.0`. Not lifted in W2 on purpose: installed registers keep the old bundle until each caja updates.
- [x] E2-W3 — Web: administración (`Pagos`/`ModalDeAltaDePago`/edición with lines, `/api/pagos/administracion`) and pagar compra (`ModalDePagoDeCompra` via `/api/pagos/de-compra/{idCompra}`, result key `pago`; `CompraEditor` and `CuentaCorrienteDeProveedor` updated; `vincular-compra` and `desdePago` read on `/api/pagos`). Personal payments untouched (E2-P).
- [x] E2-C — Cleanup: drop `pagos.id_medio_pago` once every reader uses the items. Done by S3b (column, FK and index
  dropped).
- [ ] E2-P — Pago de sueldo en un solo pago, después del release del POS (and after E2-W2): the personal payment with N
  lines becomes ONE pago with N items and one treasury movement for the total (`referencia` only in the items; the
  desglose already reads `item.referencia ?? pago.detalle`). Deferred because the installed POS bundle reads only
  `idMedioPago`/`importe` from the turno detail `gastos` and `/api/gastos`, so the merge would show 6000 efectivo + 500
  transferencia as one 6500 row under efectivo.

### E3 slices

Stacked on E2 (branch `pnader/pagos-e3-s1a-aplicaciones`). Each slice is one PR through judgment-day.

- [ ] E3-S1a (written; CI pending) — `aplicaciones_pago` (N:M pago ↔ compra, `importe`, `origen` enum
  `pago_de_compra|aplicacion|migracion`), migration `AplicacionesDePago`: RLS, partial unique per live pair, deferred
  constraint triggers (an application never exceeds its pago or its compra; only on `confirmada` compras; pago must be
  `proveedor`, same supplier and empresa), backfill from `pagos.id_comprobante_compra` capped at the compra total (the
  excess and pagos of anuladas compras stay unapplied = credit). Schema + backfill only: no writer, readers unchanged (writers arrive in S1b).
- [ ] E3-S1b (written; CI pending) — Dual write: every writer that sets `pagos.id_comprobante_compra` also writes the application, capped at the
  compra saldo; anulación of the compra and baja of the pago release their applications; editing a linked pago resyncs
  its application against the compra saldo (unrestricted edits).
- [ ] E3-S2 (written; CI pending) — Readers switch to the applications (pagado / disponible per compra, supplier credit, CC).
- [ ] E3-S3 (written; CI pending) — Endpoints: aplicar, desaplicar, and adoption of a supplierless pago (takes the compra's
  supplier); `vincular-compra` applies `min(disponible, saldo)` and adds `pago_sin_disponible`; two read endpoints of
  aplicable pagos.
- [x] E3-S4 (written; CI pending) — Pagar compra (`de-compra`) with `aplicaciones[]` and overpayment staying as credit.
- [x] E4 — Rounding write-off of type CC plus the `tolerancia_redondeo_pago_proveedor` parameter (backend + web, branch `pnader/pagos-e4-redondeo`).
- [ ] E3-W1 — Web: apply existing pagos when loading/paying a compra, credit per supplier.
- [ ] E3-W2 — Web: apply/unapply screen and adoption of cashier pagos.

### E6 slices

Owner (2026-10-28): "se deberían permitir editar los pagos y las compras, por ahora sin restricción". Branch `pnader/pagos-e6-edicion-compras`
(stacked on E4). Each slice is one PR through judgment-day.

- [x] E6-S1 (written; CI pending) — `CalculadorDeEdicionDeCompra` (Domain, pure): stock difference per `(artículo, PV, lote)` netting the
  ledger and skipping zeros, CC difference per proveedor, "requires handling the pagos" flag, lot-reuse matcher; `compra.edicion`
  audit action (catalogue 27 → 28) and its payload.
- [x] E6-S2 (written; CI pending) — `PUT /api/compras/{id}` dispatcher (`ActualizarAsync`: 404 first; borrador untouched; confirmada →
  `ActualizarConfirmadaAsync`; anulada → `409 compra_no_editable`), stock by difference, lots, OC re-projection, CC by difference, audit;
  anulación reverses the stock NET per key; CC statement shows the pending balance only on the newest `compra` row. Temporary
  `409 compra_edicion_con_aplicaciones` (replaced by S3).
- [ ] E6-S3 (written; CI pending; branch `pnader/pagos-e6-s3-aplicaciones`) — Applications: edit that lowers the total below what is applied
  trims the applications newest first (shared `RecorteDeAplicaciones`, also used by the pago edit); a proveedor/PV change releases all
  applications and unlinks `pagos.id_comprobante_compra`; compra-side redondeo reversed; migration `AplicacionesAlCambiarLaCompra` widens
  `tg_comprobantes_compra_admiten_aplicaciones` to `id_proveedor`/`id_punto_venta`. The temporary 409 is gone.
- [x] E6-S4 (written; CI pending; branch `pnader/pagos-e6-s4-costos`) — Costs: the edit re-runs the confirmation's `costo_nominal` step only for articles whose effective cost changed vs the previous version, never over a newer confirmed compra; new/changed supplier codes are associated; E6-S3 review follow-ups (PV inheritance only within the pago's empresa, CC ajuste PV fallback, `IgnoreQueryFilters` on `cambiaEmpresa`).
- [ ] E6-W1 — Web: edit button on a confirmed compra, difference preview, new error codes.

## Progress

- 2026-10-28: planning closed with the owner; worktree `pnader/pagos-e1-renombre`.
- 2026-10-28 E1a: commits bd466fe4 (rename + migration `20261028180308_RenombrarGastosAPagos`,
  rename-only SQL + audit code backfill) and 44c4477b (judgment-day round 1 fixes: legacy SPA
  route `gastos`, old-schema backfill test SQL, five wire error codes, export file name, baja
  label). Evidence: `dotnet build Ways.slnx` 0 errors (42 baseline warnings); Domain.Tests 1888
  passed; Application.Tests 1075 passed; System.Text.Json shape dump of every public type
  identical for everything an endpoint serializes. Integration suite NOT run locally (Docker
  Desktop down, starting it was denied): CI `backend-integration` is the proof, pending.
  Judgment-day: round 1 confirmed 1 CRITICAL (+1 parent-verified), 3 shared WARNINGs fixed;
  scoped re-judgment clean → APPROVED.
- E1b (web + docs) is written and verified in worktree `pagos-compras-e1b` (build, lint, vitest
  7876 passed) but must be committed from this worktree; it must also add `pagos` to
  `RutasDeLaSpa.RutasHeredadasDelBackOffice` (App.tsx gains `/pagos` plus the `/gastos` redirect).

- 2026-10-28 E2-S2 (branch `pnader/pagos-e2-s2-lectores`, commit f0bbe4c3): readers derive per-medio from
  `items_pago`; `RecalcularArqueoAsync` needed no change (it re-derives the whole arqueo through the shared reader,
  so it follows the items). Evidence: `dotnet build Ways.slnx` 0 errors; Domain.Tests 1892 passed; Application.Tests
  1076 passed; web `tsc -b` clean, vitest on the two touched files 190 passed; mutation check on `MismoReparto`: removing
  the value comparison is killed by the per-medio domain tests; removing the `a.Count == b.Count` clause survived
  until the judgment-day fix added `UnMapaQueEsSubconjuntoEstrictoDelOtro_AfectaElArqueoEnAmbosSentidos`, which then
  failed under that mutation (1 failed of 1893) and passed after restoring the line. Integration tests added (three arqueo/cierre tests with two-item pagos, audit `lineas`,
  `idItem` in the personal desglose) NOT run locally (Docker down): CI `backend-integration` is the proof, pending.

- 2026-10-28 E2-S3b (branch `pnader/pagos-e2-s3b-plantel`): `dotnet build Ways.slnx` 0 errors; Domain.Tests 1910 passed;
  Application.Tests 1076 passed (golden `inventario-de-dependientes` sin `MedioPago | pagos`); web `tsc -b` clean;
  `dotnet ef migrations has-pending-model-changes` clean. Integration tests NOT run locally (Docker down): CI
  `backend-integration` is the proof, pending.
- 2026-10-28 E2-S3b judgment-day fix: personal payment back to one pago per line (E2-P deferred); administration
  history gains `lineas` (test in `PagosEdicionEndpointsTests`); ItemsDePago backfill `ways_app` test restored.

- 2026-10-28 E2-S4 (branch `pnader/pagos-e2-s4-api`): `dotnet build Ways.slnx` 0 errors; Domain.Tests 1910 passed; Application.Tests 1076 passed. Integration tests NOT run locally (Docker down): CI `backend-integration` is the proof, pending.

- 2026-10-28 E2-W1 + E2-W3 (branch `pnader/pagos-e2-w1-w3-web`): web `tsc -b` clean; `npm run lint` 0 errors; full `vitest run` 260 files / 8111 tests passed. Route: delegated writer (2+ non-trivial files).

- 2026-10-28 E3-S1a (branch `pnader/pagos-e3-s1a-aplicaciones`): `dotnet build Ways.slnx` 0 errors; Domain.Tests 1910 passed;
  Application.Tests 1076 passed (golden `inventario-de-dependientes` gains `Tenant | aplicaciones_pago` and
  `Usuario | aplicaciones_pago`); `dotnet ef migrations has-pending-model-changes` clean. Integration tests NOT run locally
  (Docker down): `AplicacionesPagoEsquemaTests` and `AplicacionesDePagoMigracionTests` (backfill under `ways_app`) need CI.
  Route: delegated writer.

- 2026-10-28 E3-S1b (same branch, ships in ONE PR with S1a): `ServicioDePagos` (POS alta, admin alta, `PagarCompraAsync`,
  `VincularCompraAsync`) writes the application capped at the compra saldo from live applications, under the compra lock now
  `FOR UPDATE` everywhere (`BloquearCompraAsync`, also behind `ExigirCompraLigableAsync`); anulación
  (`ServicioDeCompras.LiberarAplicacionesDeLaCompraAsync`) and pago baja release them; editing a linked pago is
  unrestricted and resyncs its application to `min(new importe, compra.total - other live applications)` (0 releases it,
  none + room creates one) under the lock order turno -> compra -> pago, with a revalidation of the link under the pago
  lock (`409 pago_cambio_concurrente`). (Superseded by the S2 judgment-day fix below: cross-empresa links are now applied
  too.) No audit codes
  added (sub-effect of already audited operations); no new execution strategies, so the structural maps are unchanged.
  `dotnet build Ways.slnx` 0 errors; Domain.Tests 1910 passed; Application.Tests 1076 passed. Integration tests
  (`AplicacionesDePagoEscritoresTests`) NOT run locally (Docker down): CI `backend-integration` is the proof, pending.
  Route: delegated writer.

- 2026-10-28 E3-S2 (branch `pnader/pagos-e3-s2-lectores`, stacked on E3-S1): `LectorDePagadoPorCompra` = Σ live applications
  (dropped the `pagos.importe` and imputed-`ajuste` terms: the only ajuste imputed to a compra is the anulación
  counter-movement, `ServicioDeCompras` ~:1026, and anulación releases the applications; manual ajustes and pago ajustes
  write `id_comprobante_compra` NULL). New `LectorDeDisponiblePorPago` (aplicado/disponible, batch, with the E4 seam in
  `Disponible`). Additive JSON: `aplicado`/`disponible`/`aplicaciones[]` on `PagoDeAdministracionListado` (filled in
  `ProyeccionesDePago.ConLineasAsync`, one extra query per page; `disponible` null for non-proveedor pagos),
  `disponibleDelPago` on CC `pago` rows, `saldoAFavor`/`disponibleEnPagos` on the CC header; POS `PagoListado` and
  `/saldo` unchanged. Behavior changes documented in docs/10 §5 (over-imputed legacy shows pagado = total; anulada compra
  shows pagado 0). Seeder in
  `SaldoDeProveedorReSourceadoTests` now writes the application for its pre-cutover pago. Web: types, "Disponible" column in
  `Pagos`, "Disponible en pagos sin aplicar" line in the CC header (ResumenSaldoDeProveedor untouched: it reads `/saldo`).
  `dotnet build Ways.slnx` 0 errors; Domain.Tests 1910 passed; Application.Tests 1076 passed; web `tsc -b` clean, lint 0
  errors, vitest on the two touched files 73 passed. Integration tests (`AplicacionesDePagoLectoresTests`, 10 tests) NOT run
  locally (Docker down): CI is the proof. Mutation proof PENDING CI: `LaBajaDeUnPagoLiberaSuAplicacionYLoPagadoNoCuentaLaLiberada`
  must fail when `LectorDePagadoPorCompra` reads `db.AplicacionesPago.IgnoreQueryFilters()` (counts the released row, 400
  instead of 100) and pass after reverting. Route: delegated writer.

- 2026-10-28 E3-S2 judgment-day fix (same branch): (1) cross-empresa links keep counting: new migration
  `AplicacionesEntreEmpresas` recreates `fn_aplicaciones_pago_exigir_compra` without the same-empresa condition (Down
  restores it and soft-deletes the cross-empresa applications) and backfills the applications S1 skipped only because of the
  empresa (per compra, order `(fecha, id_pago)`, capped by the room left after live applications, origen `migracion`,
  idempotent, `SET LOCAL` block tested under `ways_app` in an isolated DB); `AplicarPagoALaCompraAsync` and
  `ReajustarAplicacionDelVinculoAsync` no longer require the same empresa; NEW links (POS alta, admin alta, vincular) are
  rejected with `400 compra_de_otra_empresa` through the shared `ExigirCompraDeLaEmpresaAsync` (legacy links stay valid).
  (2) Reapplication: `ReaplicarVinculosDeLaCompraAsync` runs after a pago baja or an edit under the compra `FOR UPDATE` lock
  and applies the other live pagos linked to the compra, in order `(fecha, id_pago)`, up to the freed room and each pago's
  own disponible; baja now takes the compra lock too (turno -> compra -> pago, pre-read/re-read with `409
  pago_cambio_concurrente`); anulada compras never reapply. (3) False comments fixed (`AplicarPagoALaCompraAsync`,
  docs/10 §5, `ServicioDeSaldoDeProveedor`). (4) Tests: `LaBajaDeUnPagoLibera...` renamed to match its assertion;
  `LoPagadoDeLaCompraEsLaSumaDeLasAplicacionesDeSusPagos` now uses 800 + 600 (raw sum 1400 vs pagado 1210);
  `AplicacionesDePagoMigracionTests` pins its own migration instead of HEAD (the new backfill would add a row);
  new CI tests in `AplicacionesDePagoEscritoresTests` (reapply on baja / edit down / order and cap / anulada, new alta
  rejected, legacy cross-empresa link counts and edit adjusts) and `AplicacionesEntreEmpresasMigracionTests`.

- 2026-10-28 E3-S3 (branch `pnader/pagos-e3-s3-aplicar`, stacked on E3-S2): `ServicioDePagos` became `partial`; new
  `ServicioDePagos.Aplicaciones.cs` holds `AplicarAsync` / `DesaplicarAsync` (both on `FabricaDeEstrategiaSinReintento`,
  registered in the structural map) and the two readers of aplicable pagos. Lock order: compras `FOR UPDATE` ascending id ->
  pago `FOR UPDATE` -> proveedor row (adoption only); treasury untouched. Adoption writes the supplier-CC `pago` movement for
  the full importe, sets `id_comprobante_compra` to the first compra if null. Desaplicar clears the pago link when it was
  that compra's and the pago has its own punto de venta, then reapplies the freed room to the other linked pagos.
  `vincular-compra` now caps at `min(disponible, saldo)`, grows an existing pair and answers `409 pago_sin_disponible`;
  the edit resync also discounts what the pago has applied to other compras. Audit codes `pago.aplicacion` /
  `pago.desaplicacion` (catalogue 23 -> 25). Doc 10 §5 updated. Routes: `POST /api/pagos/{id}/aplicaciones`,
  `DELETE /api/pagos/{id}/aplicaciones/{idAplicacion}`, `GET /api/compras/{id}/pagos-aplicables`,
  `GET /api/proveedores/{id}/pagos-disponibles`, all `GestionDeCatalogo` (no allowlist entry needed: they stack it).
  `dotnet build Ways.slnx` 0 errors; Domain.Tests 1910 passed; Application.Tests 1080 passed. Integration tests
  (`AplicacionesDePagoEndpointsTests`, 21 tests) NOT run locally (Docker down): CI is the proof, pending. Route: delegated writer.

- 2026-10-28 E3-S3 judgment-day fix, round 1 (same branch): automatic reapplication REMOVED
  (`ReaplicarVinculosDeLaCompraAsync` and its three call sites): it undid `desaplicar` of a PV-less linked pago and read other
  pagos' disponible without their lock. Released room just returns to the compra saldo; the admin applies the other pagos
  explicitly with `POST /api/pagos/{id}/aplicaciones`. `desaplicar` no longer clears `pagos.id_comprobante_compra`.
  `AplicarAsync` requires each compra to be of the pago's empresa (`400 compra_de_otra_empresa`); adoption always sets the link
  to the first requested compra. Edit/baja lock turno -> ALL the pago's compras (applications + link, ascending) -> pago
  (pre-read/re-read, `409 pago_cambio_concurrente`); edit with any live application cannot change proveedor/categoria; lowering
  the importe trims applications newest first; raising grows the link application by at most the increase
  (`AjustarAplicacionesAlNuevoImporteAsync` replaces `ReajustarAplicacionDelVinculoAsync`). `pagos-aplicables` filters by the
  compra's empresa; both readers push `disponible > 0` into SQL and cap at 200 rows. Doc 10 §5 updated. Tests replaced in
  `AplicacionesDePagoEscritoresTests` / `AplicacionesDePagoEndpointsTests` (CI pending, Docker down).

- 2026-10-28 E3-S4 (branch `pnader/pagos-e3-s4-de-compra`, stacked on E3-S3): `ServicioDePagos.PagarCompraConAplicacionesAsync`
  (in `ServicioDePagos.Aplicaciones.cs`, registered in the structural map) serves `POST /api/pagos/de-compra/{idCompra}` only; the
  legacy `PagarCompraAsync` and its routes keep `pago_excede_saldo_pendiente`. Lock order: compra `FOR UPDATE` -> existing pagos
  `FOR UPDATE` ascending id -> new pago insert -> proveedor row -> treasury advisory. Existing credit is applied first; the new
  pago is created for the full total of its lines and applied for `min(total, remaining saldo)`; the excess stays as `disponible`
  and the supplier CC carries the full pago. Response `ResultadoDePagoDeCompraConLineas` gains `aplicaciones`
  (`AplicacionALaCompraListada`) and `disponibleDelPago`; `pago` is nullable (200 instead of 201 when no lines). A compra without
  saldo stays `409 compra_sin_saldo_pendiente`. New codes: `aplicacion_demasiados_pagos`, `aplicacion_pago_repetido`,
  `aplicacion_importe_invalido`. Web: `ModalDePagoDeCompra` drops the client-side total check, shows "Quedan $X a cuenta", lists
  `GET /api/compras/{id}/pagos-aplicables` ("Usar pagos a cuenta"), lines are ignored when pagos are ticked and no line has a medio;
  `textoDePagoRegistrado` feeds the notice in `CompraEditor` / `CuentaCorrienteDeProveedor`. Doc 10 §5 updated.
  `dotnet build Ways.slnx` 0 errors; Domain.Tests 1910 passed; Application.Tests 1082 passed; web `tsc -b` clean, lint 0 errors,
  vitest on the four touched files green. Integration tests (`PagosApiConLineasTests`: overpay, only aplicaciones,
  aplicaciones + lineas, 409/400/404 cases, legacy route still 409) NOT run locally (Docker down): CI is the proof, pending.
  Judgment-day fix round (E3-S4): real lock order is compra -> existing pagos (asc) -> proveedor row inside each adopted pago's
  step (before the new pago insert) -> new pago insert -> proveedor (already held if adopted) -> treasury advisory. `fecha` is
  always validated and dates the new applications; `concepto` without lineas is `400 concepto_sin_pago_nuevo`; 404 for compra and
  each idPago comes before body validation; exact fill + lineas is `409 compra_sin_saldo_pendiente`. The modal never drops typed
  money (line with importe and no medio is an error) and its prefilled line follows the remaining amount until the user edits it.

- 2026-10-28 E4 (branch `pnader/pagos-e4-redondeo`, stacked on E3-S4): new supplier-CC movement `redondeo` (enum value in its own
  migration `RedondeoEnCuentaCorrienteDeProveedor`, CHECK `ck_movimientos_cuenta_corriente_proveedor_redondeo_un_destino` in
  `RedondeoUnDestino`; no model change besides the enum and the CHECK). Compra side: importe NEGATIVE with `id_comprobante_compra`
  (pagado = Σ aplicaciones − Σ redondeo importes); pago side: importe POSITIVE with `id_pago` (disponible = importe − aplicado −
  Σ redondeo importes); both leave the supplier saldo net zero. All writes go through `RedondeoDeProveedor` (Application) which
  reuses `EscriturasDeCuentaCorrienteProveedor` (movement needs punto de venta + empleado). Parameter
  `tolerancia_redondeo_pago_proveedor` (decimal, default 500, PV -> empresa -> default; `400 parametro_tolerancia_redondeo_invalida`).
  Triggers: `cerrarEnCero` on `POST /api/pagos/de-compra/{id}` (response gains `redondeo` = signed difference), `POST
  /api/compras/{id}/ajuste-de-redondeo`, `POST /api/pagos/{id}/ajuste-de-redondeo` (all `GestionDeCatalogo`, no-retry strategy
  registered in the structural map, audit `compra.redondeo` / `pago.redondeo`, catalogue 25 -> 27); `pagos-aplicables` gains
  `toleranciaRedondeo`. 409 `redondeo_excede_tolerancia`, `pago_sin_proveedor`, `pago_sin_disponible`, `compra_sin_saldo_pendiente`.
  Compensations (a revert is a `redondeo` movement of opposite sign): compra anulation reverts the compra redondeo (before the
  counter-movement); desaplicar, pago baja and an edit that lowers an application revert the redondeo of each compra that gets less
  applied; pago baja or an importe change reverts the pago-side redondeo (concept-only edits do not). `AplicarPagoALaCompraAsync` and
  `AjustarAplicacionesAlNuevoImporteAsync` count the redondeo when computing room. Readers: `LectorDePagadoPorCompra`,
  `LectorDeDisponiblePorPago` (all eight callers pass the redondeo, including the SQL filter of aplicables and the supplier credit).
  Web: `ModalDePagoDeCompra` "Cerrar en cero" (enabled only when 0 < |saldo - aplicado - lineas| <= tolerancia), `CompraEditor`
  "Ajustar redondeo", CC row label "Redondeo", parameter catalogue entry. Docs: doc 10 parts 05, 08, 09.
  `dotnet build Ways.slnx` 0 errors; Domain.Tests 1926 passed; Application.Tests 1091 passed; `dotnet ef migrations
  has-pending-model-changes` clean; web `tsc -b` clean, lint 0 errors, full vitest 260 files / 8161 tests passed. Integration tests
  (`RedondeoDePagosTests`, `RedondeoEsquemaTests`) NOT run locally (Docker down): CI `backend-integration` is the proof, pending.
  Route: delegated writer.

- 2026-10-28 E6-S1 + E6-S2 (branch `pnader/pagos-e6-edicion-compras`, stacked on E4): `dotnet build Ways.slnx` 0 errors; Domain.Tests 1957
  passed; Application.Tests 1099 passed (new `ServicioDeComprasEdicionLockOrderTests`, the new site in the no-retry structural map).
  Mutation evidence (Domain): netting replaced by last-wins is killed by 2 tests; dropping the zero-diff `Where` is killed by 3; lock-order
  mutant (a tracked header read before the membership lock) is killed by 2 of the new source-text tests; every mutant reverted.
  Lock order of `EjecutarEdicionConfirmadaAsync`: membership (shared) → compra header `FOR UPDATE WHERE estado='confirmada'` (0 rows → 404 or
  `409 compra_cambio_concurrente`) → single EF read → OCs ascending `FOR UPDATE` (`EscriturasDeOrdenDeCompra.BloquearAsync`) → lots ascending
  `(artículo, código)` → stock (aggregate per `(artículo, PV)` then lots) → EF replace-set → OC re-projection → audit → proveedores ascending
  (last), CC `compra` rows by difference. Codes: `compra_no_editable` (anulada), `compra_cambio_concurrente`, `compra_edicion_stock_negativo`,
  `compra_edicion_con_aplicaciones` (TEMPORARY, removed in S3), `compra_edicion_sin_asiento_de_cuenta` (pre-ledger compras cannot change
  total/proveedor), plus the confirm codes `compra_numero_externo_requerido`/`compra_incompleta_para_confirmar`/`compra_sin_items`. Docs:
  doc 10 parts 05, 06, 08. Integration tests (`ComprasEdicionConfirmadaTests`, 23 tests incl. a deterministic edit×anular rendezvous;
  `ComprasLifecycleTests` confirmed-PUT test rewritten) NOT run locally (Docker down): CI `backend-integration` is the proof, pending.
  Not done on purpose: costs and supplier codes on edit (S4); web (W1). Route: delegated writer.

- 2026-10-28 E6-S3 (branch `pnader/pagos-e6-s3-aplicaciones`, stacked on E6-S2): `dotnet build Ways.slnx` 0 errors; Domain.Tests 1963 passed;
  Application.Tests 1100 passed; `has-pending-model-changes` clean. Mutation evidence: `RecorteDeAplicaciones` newest-first order flipped to
  oldest-first is killed by 6 of 9 helper tests; reverted. Rules: tope = new total (0 when proveedor/PV changes, which releases all and sets
  `pagos.id_comprobante_compra = NULL`); redondeo of the compra reversed when it exists and total/proveedor/PV changes (decided in the plan,
  WRITTEN in the final proveedores loop because the reversal locks the proveedor row). Lock order: membership → compra → OCs → pagos
  ascending (`BloquearPagosDeLaEdicionAsync`) → lots → stock → EF write → OC projection → audit → proveedores ascending (redondeo reversal,
  then the CC difference, per proveedor). The migration only widens the trigger; the function does not check empresa since
  `AplicacionesEntreEmpresas`, so a PV-only change is not caught by the DB (service rule). Integration tests (`ComprasEdicionConfirmadaTests`
  E6-S3 region, `AplicacionesPagoEsquemaTests` proveedor backstop) NOT run locally (Docker down): CI `backend-integration` is the proof,
  pending. No race-style test for compra-edit x pago-writer (needs a rendezvous; deadlock-safety rests on the shared compra-first order).
  Route: delegated writer.

- 2026-10-28 E6-S4 (branch `pnader/pagos-e6-s4-costos`, stacked on E6-S3): `dotnet build Ways.slnx` 0 errors; Domain.Tests 1977 passed;
  Application.Tests 1104 passed. Rules: costs = `CalculadorDeEdicionDeCompra.CostosQueCambian` (winner per article/family vs previous version) →
  `LeerFamiliasDeLosArticulosAsync` → `BloquearArticulosConCostoAsync` (unchanged confirm helpers) → later-compra read AFTER the lock
  (`fecha_recepcion` greater, or equal with greater id, confirmed, same exact `ActualizaElCosto` guard with that compra's own IVA flags) →
  `SinLosPisadosPorUnaCompraMasNueva` (a later line on any family member omits the whole family) → one `UPDATE`. Codes: new/changed pairs, all when
  the proveedor changes, after `SaveChanges`. Audit adds `costos_actualizados`, `costos_omitidos_por_compra_mas_nueva` and
  `pagos_desvinculados[{id_pago, id_punto_venta_heredado}]`. Unlink SQL is now a CTE with `RETURNING` and inherits the PV only if live and of the
  pago's empresa; `EscribirAjustesDeProveedorAsync` falls back to the first PV of the pago's empresa (409 `pago_sin_punto_de_venta` if none).
  Mutation evidence: removing the previous-version comparison fails 4 of 14 `CalculadorDeEdicionDeCompraCostosTests`; removing `IgnoreQueryFilters`
  on `cambiaEmpresa` fails the source test; both reverted. Integration tests (`ComprasEdicionConfirmadaTests` S4 region,
  `ComprasEdicionCostosFamiliasTests`) NOT run locally (Docker down): CI `backend-integration` is the proof, pending. Known limits: the later-compra
  read has no race test (needs a rendezvous); the "PV of another empresa" inheritance branch has no integration test (needs a second empresa).
  Route: delegated writer.
  Judgment-day fix (same branch): the unlink CTE now does `COALESCE(pago PV, compra PV if live and same empresa, lowest-id live PV of the pago's
  empresa)`, so an unlinked PV-less pago stays editable; only an empresa with no live PV leaves it NULL. Audit `costos_actualizados` now carries
  `costo_anterior` (read from the locked rows before the UPDATE). New integration test
  `UnPagoSinPuntoDeVentaDesvinculadoPorCambioDeEmpresaTomaUnPuntoDeVentaDeSuEmpresaYSigueEditable` (CI pending, Docker down).
  Known limitations: (a) the later-compra cost guard only sees later compras' current cost lines on currently-live family members (a later compra
  whose cost line was edited away, or on an article that left the family, is not detected); (b) the runtime CC ajuste fallback picks the lowest-id
  live PV of the pago's empresa.

- 2026-10-28 E2-W2 (branch `pnader/pagos-e2-w2-pos`): web `tsc -b` clean; `npm run lint` 0 errors; full `vitest run` 262 files / 8397 tests passed. Route: delegated writer (2+ non-trivial files). RED/GREEN: existing POS tests migrated to the lines contract plus new tests (alta with 2 lines, efectivo-only drawer check, multi-medio listing, multi-medio edit via PUT). Back-office restriction intentionally kept (see E2-W4).

## Next step

E1–E6 and E2-W2 are written and shipped as PRs (#500…#523); `pos-v0.16.0` (#524) is tagged on main once it merges. Open: E2-W4 (lift the back-office single-medio restriction once every register runs 0.16.0), E2-P (sueldo as one pago), C (cleanup of `/api/gastos`) and E7 (cobros).
