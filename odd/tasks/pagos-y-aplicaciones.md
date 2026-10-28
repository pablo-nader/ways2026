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
- [ ] E4 — Rounding write-off with `tolerancia_redondeo_pago_proveedor` (default 500).
- [ ] E5 — Pago editing with lines and applications, compensating entries.
- [ ] E6 — Confirmed compra editing (items, total, stock and CC by difference; excess applied
  amount goes back to credit).
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
- [ ] E2-W2 — Web: lines in the POS payment form (`PagosDelTurno`, shared `ModalDeEdicionDePago` one-medio mode). The administration part (form, edit modal in lines mode, history column) shipped with W3 (branch `pnader/pagos-e2-w1-w3-web`).
- [x] E2-W3 — Web: administración (`Pagos`/`ModalDeAltaDePago`/edición with lines, `/api/pagos/administracion`) and pagar compra (`ModalDePagoDeCompra` via `/api/pagos/de-compra/{idCompra}`, result key `pago`; `CompraEditor` and `CuentaCorrienteDeProveedor` updated; `vincular-compra` and `desdePago` read on `/api/pagos`). Personal payments untouched (E2-P).
- [x] E2-C — Cleanup: drop `pagos.id_medio_pago` once every reader uses the items. Done by S3b (column, FK and index
  dropped).
- [ ] E2-P — Pago de sueldo en un solo pago, después del release del POS (and after E2-W2): the personal payment with N
  lines becomes ONE pago with N items and one treasury movement for the total (`referencia` only in the items; the
  desglose already reads `item.referencia ?? pago.detalle`). Deferred because the installed POS bundle reads only
  `idMedioPago`/`importe` from the turno detail `gastos` and `/api/gastos`, so the merge would show 6000 efectivo + 500
  transferencia as one 6500 row under efectivo.

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

## Next step

S1 PR #504 is in CI; E2-S3b is written (branch `pnader/pagos-e2-s3b-plantel`); E2-S4 is written (branch `pnader/pagos-e2-s4-api`); E2-W1 + E2-W3 are written (branch `pnader/pagos-e2-w1-w3-web`); next is E2-W2 (POS payment form with lines).
