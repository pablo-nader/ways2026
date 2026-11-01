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

## Constraints

- POS front changes reach the tills only with a `Ways.Desktop` bump + `pos-v*` tag.
- Skills: dto-contract-honesty, dangling-fk-read-models (medio dado de baja keeps its
  name), react-async-state, web-test-data-gates, web-descriptor-tests, structural-guard-tests.
- Each PR slice passes judgment-day (project CLAUDE.md), then PR + auto-merge + Auto-fix.

## Delivery

Strategy `auto-chain`, chain `stacked-to-main`. Forecast ~900 authored lines → two slices.

## Tasks

- [ ] **T1 — Backend** (delegated: 2+ non-trivial files) — branch `pnader/caja-z-columnas-s1`
  - `FilaDeHistoricoDeCajas` gains Digitales / Efectivo / CuentaCorriente / TotalFacturado,
    one grouped query per page; XLSX export gains the columns.
  - `/caja/turnos/{id}/detalle`: medios carry `Nombre`; tickets carry the
    `VentaDeTurnoListado` properties (cliente, medios, desc./recargo manual, estado fiscal).
  - Checks: integration tests (histórico, detalle), unit tests, structural guards.
- [ ] **T2 — Web** (delegated: 2+ non-trivial files) — branch `pnader/caja-z-columnas-s2`
  - Histórico columns; Caja Z medios by name; ticket table with Ver detalle + Reimprimir
    (web: `VistaDeTicket` + print; POS desktop: thermal queue). No Anular.
  - Checks: Vitest for HistoricoDeCajas, CajaZ, extracted components.

## Progress

- Exploration done; owner decided RC exclusion.
- T1 PARCIAL (route: delegated). Hecho: detalle de Caja Z (medios con `nombre`, tickets = `VentaDeTurnoListado`
  vía `ServicioDeVentas.LeerVentasDelTurnoAsync`, nombre de medio dado de baja conservado en tickets, export XLSX con
  nombre de medio y tickets en orden cronológico). Verificación: `dotnet test tests/Ways.Application.Tests` 1621 OK,
  `tests/Ways.Domain.Tests` 2176 OK, integración filtrada (Historico|Detalle|VentasPorTurno|Exportacion|Cierre|
  Visibilidad|SuperficieDeAutorizacion|CajaResumenContenido) 100 OK; mutación (quitar IgnoreQueryFilters) mata el test.
- T1 BLOQUEADO (columnas del histórico): una NCX no lleva pagos (`ValidadorDePagos` regla 0c) y una venta puede quedar
  hasta la tolerancia de pago (default 10) por debajo de su total, o con exceso sin vuelto: Σ pagos != Σ Total. Falta
  decisión del dueño sobre cómo clasificar NCX y el resto.

## Next step

T1 backend writer.
