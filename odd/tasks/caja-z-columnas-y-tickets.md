# Caja Z: columnas facturadas, medios con nombre y tickets reimprimibles

Locator: `odd/tasks/caja-z-columnas-y-tickets.md` · Engram: `odd/caja-z-columnas-y-tickets/tasks`

## Objective

Owner request (2026-10-31) on the closed-shift screens:

1. Histórico de cajas (`/caja/historico`): after Turno / Punto de venta / Apertura / Cierre,
   show **Digitales | Efectivo | Cta. cte. | Total**, the same breakdown as the closing
   ticket, but of what was **invoiced** in the shift (regardless of what was collected,
   discounts or arqueo differences).
2. Caja Z detail (`/caja/turnos/:id/z`): payment methods by name, not `Medio #id`.
3. Caja Z detail: the ticket list carries the same properties as the POS ticket list
   (`VentasDelTurno`) and each ticket can be viewed and reprinted. Never voided.

## Decisions

- Owner decision (2026-10-31): the four columns count **sales and credit notes only**
  (TX / NCX). Current-account receipts (RC) collected in the shift do NOT count, so
  Efectivo / Digitales may differ from the closing ticket, which reports collected money.
- Classification = the closing ticket's (`ClasificadorDeVentasPorMedio`): the shift's
  pinned cash anchor (`TurnoCaja.IdMedioPagoEfectivo`) → Efectivo; `Comportamiento ==
  CuentaCorriente` → Cta. cte.; anything else → Digitales. Amounts = payments net of
  change (vuelto netted on the anchor), so Total = Σ comprobante totals of the shift.
- Esperado / Declarado / Diferencia stay in the API row and the XLSX export; the screen
  replaces them with the four new columns.

- Owner decision (2026-11-01), Histórico columns: scope = TX and NCX of the shift, estado Emitido (RC excluded). A TX's
  `Total` is PRORATED across Efectivo / Digitales / Cta. cte. in proportion to its net payments (importe − vuelto), so a
  shortfall within tolerance or an overpayment without vuelto never breaks the sum; cents are rounded per bucket and the
  remainder goes to the largest share (tie: Efectivo > Digitales > Cta. cte.). A TX with no positive net payments goes
  whole to Efectivo. An NCX (no payments) mirrors the split of its associated TX using the ORIGINAL sale's shift anchor;
  without an associated TX or payments it goes whole to Efectivo. TotalFacturado = Σ buckets = Σ Total of TX + NCX.

## Constraints

- POS front changes reach the tills only with a `Ways.Desktop` bump + `pos-v*` tag.
- Skills: dto-contract-honesty, dangling-fk-read-models (medio dado de baja keeps its
  name), react-async-state, web-test-data-gates, web-descriptor-tests, structural-guard-tests.
- Each PR slice passes judgment-day (project CLAUDE.md), then PR + auto-merge + Auto-fix.

## Delivery

Strategy `auto-chain`, chain `stacked-to-main`. Forecast ~900 authored lines → two slices.

## Tasks

- [x] **T1 — Backend** (delegated: 2+ non-trivial files) — branch `pnader/caja-z-columnas-s1`
  - `FilaDeHistoricoDeCajas` gains Digitales / Efectivo / CuentaCorriente / TotalFacturado,
    one grouped query per page; XLSX export gains the columns.
  - `/caja/turnos/{id}/detalle`: medios carry `Nombre`; tickets carry the
    `VentaDeTurnoListado` properties (cliente, medios, desc./recargo manual, estado fiscal).
  - Checks: integration tests (histórico, detalle), unit tests, structural guards.
- [x] **T2 — Web** (delegated: 2+ non-trivial files) — branch `pnader/caja-z-columnas-s2`
  - Histórico columns; Caja Z medios by name; ticket table with Ver detalle + Reimprimir
    (web: `VistaDeTicket` + print; POS desktop: thermal queue). No Anular.
  - Checks: Vitest for HistoricoDeCajas, CajaZ, extracted components.

## Progress

- Exploration done; owner decided RC exclusion.
- T1 DONE (route: delegated). Detalle de Caja Z: medios con `nombre`, tickets = `VentaDeTurnoListado` (commit ffd6edae).
  Histórico: `digitales`/`efectivo`/`cuentaCorriente`/`totalFacturado` en `FilaDeHistoricoDeCajas` + 4 columnas XLSX
  (dominio `ProrrateoDeFacturadoPorMedio`, lector `LectorDeFacturadoPorTurno`, una carga agrupada por página).
  Verificación: Domain.Tests 2185 OK; Application.Tests 1622 OK (guardas incluidas); integración filtrada (Historico|
  Detalle|VentasPorTurno|Exportacion|Cierre|Visibilidad|SuperficieDeAutorizacion|CajaResumenContenido|AislamientoDeTenant|
  FacturacionEspejoNoDobleConteo) 123 OK. Mutación: quitar `Estado == Emitido` del lector mata
  `ElHistoricoRepartePorCubo...`; quitar IgnoreQueryFilters del nombre de medio mata el test de medio dado de baja.

- S1 judgment-day: round 1 CRITICAL confirmado (tipos.ts con campos requeridos sin fixtures web → tsc rojo);
  fix 704c9452 devuelve tipos.ts al de main (los tipos van con S2); re-juicio A y B sin hallazgos →
  `JUDGMENT: APPROVED`. Info no corregida: export del histórico carga todos los turnos sin lotes; `LineaDeResumen.nombre`
  respeta la baja lógica (la línea desaparece igual); XLSX de detalle lista anulados sin estado; el ancla del turno
  original de una NCX no tiene test que la mate.

- T2 DONE (route: delegated; trigger: 2+ non-trivial files). Histórico: columnas Digitales / Efectivo / Cta. cte. / Total
  (sin orden en el servidor: el backend no las ordena). Caja Z: medios por `nombre`; tickets con las columnas y marcas de
  `VentasDelTurno` + Ver detalle y Reimprimir (nunca Anular). Extraídos `componentes/DetalleDeVenta.tsx` (modal de detalle y
  marcas de estado, ahora compartidos con `VentasDelTurno`) y `lineasDeTicketDeVenta` (mismo armado que `ticketDeVenta`).
  Reimpresión: escritorio por la cola FIFO de `ShellPos.alReimprimirVenta` (prop nueva de `CajaZ`); web con `VistaDeTicket` +
  `window.print()`. En el modal "Cierres de caja" del POS solo se agrega Ver detalle (sin Reimprimir: no tiene seam de impresión).
  Verificación: `npx tsc -b` 0 errores; `npx vitest run` 9223 OK + 1 intermitente (GrillaDeItemsDeCompra, ajeno, pasa solo).

## Next step

Judgment-day de la rebanada S2, luego PR.
