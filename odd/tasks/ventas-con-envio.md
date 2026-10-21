# Ventas con envío (delivery)

## Objective

Let the POS sell for delivery: a "Con envío" toggle on the sale adds an "Envío" line with a
configurable amount, a delivery sale requires a client with an address, and the printed ticket
carries the client's address and phone for the delivery person.

## Problem / why

The owner sells for delivery and today has no way to charge the shipping fee or to hand the
delivery person the client's address. `ComprobanteVenta.DireccionEntrega` exists but nothing
fills it; the ticket prints only the client number and name.

## Decisions (owner, 2026-10-21)

- Envío is a **free-concept line** (`ItemComprobanteVenta.IdArticulo = NULL`, already foreseen
  by the model and doc 10 §04), not a special article and not a separate total field. It never
  touches stock, cost, combos or offers.
- The amount is configurable: parameter `importe_envio` in `parametros` (empresa, with the
  existing per-PV override). No migration for the amount.
- Each punto de venta has a flag "envío por defecto" (new boolean column on `puntos_venta`).
  The POS toggle starts from that flag and the cashier can change it on every sale.
- A delivery sale requires a client that is not Consumidor Final and has a non-empty domicilio;
  otherwise the sale cannot close (POS blocks it, the server rejects it).
- The ticket prints the client's domicilio and teléfono (celular as fallback) when the sale has
  envío.

## Scope

- Backend: PV flag column + edit contract; `importe_envio` known parameter; concept line support
  in `ServicioDeVentas`/`CalculadorDeTotales`/item materialization; `ConEnvio` on
  `SolicitudDeVenta`; server-side validation of client + domicilio; `DireccionEntrega` filled
  from the client's domicilio; IVA of the line at 21%.
- Web back office: PV screen checkbox.
- POS web: "Con envío" toggle, Envío line in the cart and total, close blocked without a valid
  client, ticket with domicilio + teléfono.
- POS offline: the device carries `importe_envio` and the PV flag in its snapshot, queues the
  sale with its envío amount, and the server honors the device amount for pre-numbered sales.
- Docs: doc 10 §04 (concept line now built), §09 parámetros (`importe_envio`), PV section.

## Constraints

- Every path that reads `ItemComprobanteVenta.IdArticulo` must tolerate NULL (devoluciones /
  notas de crédito, reports, offline synthetic ticket, fiscal payload).
- DTO honesty: a device-supplied envío amount is honored only for pre-numbered (offline) sales
  and rejected otherwise.
- POS web changes reach the registers only with a Ways.Desktop bump + `pos-v*` tag (owner's OK).

## Delivery strategy

`ask-on-risk` with the standing chain preference `stacked-to-main`: one PR per task.
Forecast ~1,600 authored lines over four tasks.

## Tasks

- [x] T1 — PV flag `envio_por_defecto` (migration, entity, contracts, PV screen) + known
  parameter `importe_envio` + docs. Route: delegated (2+ non-trivial files).
- [ ] T2 — Server: concept line "Envío" in sales (`ConEnvio`, amount from parameter, 21% IVA,
  client/domicilio validation, `DireccionEntrega`), NULL-article tolerance in every reader.
  Route: delegated.
- [ ] T3 — POS web online: toggle defaulted from the PV flag, line + total, close blocked,
  ticket prints domicilio + teléfono. Route: delegated.
- [ ] T4 — POS offline: snapshot carries amount + flag, outbox/synthetic ticket with the Envío
  line, server honors the device amount for pre-numbered sales. Route: delegated.

## Acceptance criteria

- A PV with the flag on opens every new POS sale with "Con envío" checked; off → unchecked;
  the cashier can toggle it either way.
- With envío on and `importe_envio` = 1500, the sale has one extra line "Envío" for 1500 with
  21% IVA, and the total includes it; with envío off, no line.
- Envío on + Consumidor Final, or a client with empty domicilio → POS blocks closing; the API
  answers 400 with a specific code.
- The sale persists `DireccionEntrega` = client's domicilio; the ticket prints domicilio and
  teléfono.
- Offline sale with envío syncs with the amount the device charged.

## Checks

- Backend unit + integration tests for the new paths (`structural-guard-tests` run in full).
- Vitest for the POS pieces and the PV screen descriptor.

## Progress

- 2026-10-21: exploration done, decisions recorded, document created.
- 2026-10-21: T1 done (route: delegated). Migration `EnvioPorDefectoEnPuntosVenta` (column
  `envio_por_defecto`, no backfill so no RLS concern); `PuntoVentaListado.EnvioPorDefecto` is
  what the POS reads from `GET /api/puntos-venta`; `PuntoVentaEdicion` accepts it (the alta does
  not; it is born false). Known parameter `importe_envio` (decimal, default 0) registered in the
  domain and mirrored in the web editor. Evidence: `dotnet build` 0 errors; Domain.Tests 1852
  ok; Application.Tests 1059 ok; IntegrationTests filtered (Organizacion, Parametros,
  AltaDePuntoVenta, ProyeccionDeOrganizacion, SuperficieDeAutorizacion, AislamientoDeTenant,
  Migracion, Schema) 360 ok; web `npm run build` ok, vitest PuntosVenta/Parametros/api 1686 ok.

## Next step

T2.
