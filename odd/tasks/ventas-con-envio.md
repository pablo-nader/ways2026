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
- [x] T2 — Server: concept line "Envío" in sales (`ConEnvio`, amount from parameter, 21% IVA,
  client/domicilio validation, `DireccionEntrega`), NULL-article tolerance in every reader.
  Route: delegated (one read-only explorer for the reader sweep + one bounded writer).
- [x] T3 — POS web online: toggle defaulted from the PV flag, line + total, close blocked,
  ticket prints domicilio + teléfono. Route: delegated.
- [x] T4 — POS offline: snapshot carries amount + flag, outbox/synthetic ticket with the Envío
  line, server honors the device amount for pre-numbered sales. Route: delegated (one bounded writer across
  backend, snapshot and POS web).

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

- 2026-10-21: T2 done (route: delegated). `SolicitudDeVenta.ConEnvio` (trailing, default false).
  `ReglaDeEnvio` (domain): `envio_requiere_cliente` (Consumidor Final / no client) and
  `envio_requiere_domicilio` (null/blank) are 400s; the line goes through `CalculadorDeTotales` like any
  other (qty 1, no discount/offer/combo/adjustment) so subtotal/total/fiscal breakdown include it. In
  `ServicioDeVentas`: `importe_envio` is resolved in the same parameters query as `tolerancia_pago` and
  `lotes_habilitado` (no extra read for a sale without envío); the 21% alícuota is found by ARCA code 5
  (`alicuotas_iva.codigo_afip`, one extra query only when there is a line) and snapshotted (id + 21);
  `LineaDelPlan.IdArticulo` is now nullable; the Envío item is always LAST, `EsProducto = false`, cost NULL,
  no lot, so stock/FEFO skip it. (Superseded by the 2026-10-21 owner decision below: the Envío line has NO area.) `DireccionEntrega`
  = the request's if present, else the client's domicilio (only with ConEnvio).
  Decisions: (1) importe 0 or no parameter => no line, but validation and `DireccionEntrega` still apply;
  (2) offline pre-numbered sales with ConEnvio use the SERVER parameter for now (T4 adds the device amount
  override); a resend with the same number and the same ConEnvio is idempotent because the Envío item is part
  of the compared identity, and a resend with a different ConEnvio is a 409 `numero_preasignado_con_otro_contenido`;
  (3) `ConEnvio` on a nota de crédito or on a presupuesto conversion is a 400 `envio_no_admitido`
  (dto-contract-honesty; checked right after the tipo is resolved, before the presupuesto is read); remito
  -> factura and presupuestos build their own documents and never carry an Envío line; (4) a nota de crédito
  of a sale with envío returns only the article lines requested and does NOT refund the envío (a NC line is
  chosen by article and the Envío has none), the already-NULL-safe readers skip it; (5) `anulación` reverses
  stock from `movimientos_stock`, so the Envío item is irrelevant to it.
  NULL-article sweep (code reading + tests): ServicioDeVentas (`ResolverDevolucionesDeComboAsync`,
  `ExigirImportesDeLaVentaAsociadaAsync`, `ExigirMismoContenido`, `Proyectar`, `BuscarPorNumeroComprometidoAsync`,
  `ObtenerAsync`, stock loop, lote block, anulación) ok; Reportes: `ServicioDeReportesDeRentabilidad` (see the follow-up: cost 0,
  so it is a real-cost line and adds to the margin), `ServicioDeReportesDeArticulos.LineasVendidas` (top / ventas por artículo) and
  `ServicioDeConsultasDeVentas` (MCP por artículo / por área, grupo, marca) already drop it (acceptable: their
  totals exclude envíos), `ServicioDeLibroIva` ok (reads alícuota + total); Caja `LectorDeContenidoDeResumen`
  ok with a real area; CuentaCorriente `LectorDeConsumosReliquidables` / `ServicioDeReliquidacion` /
  `ReliquidadorDeConsumos` ok (the free-concept line already keeps its historic total); Fiscal
  `ServicioDeFacturacionDeVentas` (mirror copies the NULL), `ComponerLineasFiscalesDesdeItemsAsync` (alícuota
  snapshot + total only), `ComposicionDeTotalesFiscales` ok; `ServicioDeLotes.ListarAsync` ok; Ways.Api has no
  item readers. Evidence: `dotnet build` 0 errors; Domain.Tests 1860 ok; Application.Tests 1060 ok (structural guards
  included); IntegrationTests filtered on Venta/Ventas/NotaDeCredito/Devolucion 1050 ok, and on
  Reportes/Caja/Turno/Cierre/Fiscal/Facturacion/Mcp/SuperficieDeAutorizacion/Reliquid/Rentabilidad 1512 ok
  (the first pass failed 5 `RentabilidadTests` because of a null-article filter that was then dropped, see above).
  Mutation evidence (mutate -> failing test -> revert): removing the `ExigirClienteConDomicilio` call -> 5
  integration tests fail; `envio_no_admitido` guard (each half) -> `ConEnvioEnUnaNotaDeCredito...` fails;
  `DireccionDeEntrega` fallback removed / applied without ConEnvio -> 3 / 1 tests fail; ARCA code 5 -> 4 -> the
  21% test fails; domain rule clauses (domicilio / whitespace / Consumidor Final) -> `ReglaDeEnvioTests` fail.
  Tests: `ReglaDeEnvioTests` (domain), `ComposicionDeTotalesFiscalesTests` (envío at 21% next to 10.5%),
  `VentaConEnvioTests` (integration).

- 2026-10-21 (owner decision, follow-up commit): areas belong to articles, so the Envío line has NO area and its cost
  is 0. Migration `IdAreaNulableEnItemsDeVenta` (DDL only: `items_comprobante_venta.id_area` nullable, composite FK kept,
  no backfill so no RLS concern; `Down` fails on purpose if area-less lines exist). `ItemComprobanteVenta.IdArea`,
  `ItemEmitido.IdArea` and `LineaDelPlan.IdArea` are `int?`. Sites swept for a null area: `LectorDeContenidoDeResumen`
  (ingresos por área: filtered with `IdArea != null`, so the per-area sum is below the shift total by the envío amount,
  stated in its comment and in `IngresoPorArea`), `ServicioDeFacturacionDeVentas` mirror copy (copies the null),
  `ServicioDeFacturacionFiscal` (its `IdArea` is a request input, not an item read), `ObtenerItemsDeTxrAsync` (remito
  items, always an article). Reports by área (`ServicioDeReportesDeArticulos`, MCP `ventas_agrupadas` por área) classify
  by the ARTICLE's current área and already drop the null-article line; rentabilidad has no per-area view; ventas por
  hora uses ticket totals. Rentabilidad: cost 0 makes the Envío a real-cost line, so coverage no longer counts it as "sin
  costo", it adds to the margin and gets a null-article "Envío" row in `PorArticulo`. Web type `ItemEmitido.idArea` is
  `number` in `tipos.ts` and can now be null (T3 must treat it as `number | null`). Mutation evidence: `IdArea` set to the first
  line's area -> 2 tests fail; cost 0 -> NULL -> 2 fail; removing the `IdArea != null` filter in the caja reader -> 1 fails.
  Evidence: `dotnet build` 0 errors; Domain.Tests 1860 ok; Application.Tests 1060 ok; IntegrationTests filtered on Venta|Ventas|NotaDeCredito|Devolucion|Reportes|Caja|Turno|Cierre|Rentabilidad|Mcp|Fiscal|Migracion|Schema|SuperficieDeAutorizacion 2155 ok (sequential).

## Next step

T3 (POS web online): send `conEnvio` in the sale request, show the Envío line from the response, block closing
without a client with domicilio (same two codes), ticket with domicilio + teléfono. T4: device amount override
for pre-numbered sales (`ExigirPreciosOfflineValidos`-style gate, DTO honesty) and the `ExigirMismoContenido` identity
for the device amount.

- 2026-10-21: T3 done (route: delegated; one bounded writer, 2+ non-trivial files). POS web online: checkbox "Con envío"
  in the cart header (`Pos.tsx`); `conEnvioElegido` is `null` until the cashier touches it, so each sale starts from
  `puntoVenta.envioPorDefecto ?? false` and a closed sale or an emptied cart resets it to `null`; the choice is part of the
  borrador (`BorradorDeTicket.conEnvio`). `importe_envio` is read with the existing `GET /parametros/{clave}` pattern,
  generation-gated, only with envío on and connection; the cart shows a non-editable "Envío" row and the total includes it
  (`importeDeEnvioAplicado`, rounded to the peso); the request carries only `conEnvio: true` (`aSolicitudDeVenta`), never
  an amount. Closing is blocked (`motivoDeEnvio`, visible alert) for Consumidor Final / blank domicilio, while the amount
  is unknown or failed to load, and without connection. The three server codes map to clear messages
  (`mensajeDeRechazoDeEnvio`). Decision: a sale with envío never takes the local-first path (`instantaneaDeLaPrevia` is
  forced to `null`) because the outbox would drop the Envío line; offline the box cannot be ticked, and if it is already on
  (PV default) the cobro is blocked with a message and the box can be unticked (T4 lifts all of this). Pure helpers and
  messages live in `src/pos/envio.ts`. Ticket: `ENTREGA A DOMICILIO` block with the word-wrapped `direccionEntrega` and
  `Telefono:` (telefono, celular fallback; `OpcionesDeTicketDeVenta.telefonoDeEntrega`, set by `ShellPos.alEmitirVenta`);
  reimpresión prints the domicilio only. `TelefonoCliente` was NOT added to `ComprobanteEmitido`: its `Proyectar` has four
  copies (ventas, facturación de remitos, cuenta corriente) each needing a client read, out of T3's surface. T2 follow-ups:
  `ItemEmitido.idArea` is `number | null` in `tipos.ts` (no consumer broke); the `ReglaDeEnvio` comment now says cost 0
  and counted in rentabilidad; `importe_envio` is validated in `ServicioDeParametros.ValidarTipo` (>= 0, max 2 decimals,
  <= 999999999999.99; 400 `parametro_importe_envio_invalido`; doc 10 §9 updated).
  Evidence: `dotnet build` 0 errors; Application.Tests 1067 ok; IntegrationTests filtered Parametros|VentaConEnvio|Envio 65 ok
  (the wider Parametros|Venta filter ran 638 ok, 0 failed, and hit the tool timeout before finishing); web `npm run build`
  ok, oxlint no findings in touched files, full vitest 7676 ok. Mutation evidence (mutate -> failing test -> revert): removing
  `motivoDeEnvio === null` from `precondicionesListas` -> 3 tests fail; not clearing the choice after a sale / after vaciar
  -> 2 / 1 fail; the offline "no tildar" guard -> 1 fails; forcing the local queue path with envío -> the online-with-envío
  test fails. Note: these POS web changes reach the registers only after a Ways.Desktop bump + `pos-v*` tag, which needs the
  owner's OK (not done).

- 2026-10-21: T4 done (route: delegated; one bounded writer). Server: `SolicitudDeVenta.ImporteEnvio` (decimal?, trailing) is the
  amount the register charged; honored ONLY with `NumeroPreasignado` + `ConEnvio` (the line price is that amount; 0 = no line).
  Any other combination is 400 `importe_envio_no_admitido`; < 0, > 2 decimals or > numeric(14,2) is 400 `importe_envio_invalido`
  (`ReglaDeEnvio.ExigirImporteDelDispositivo`; the validity predicate `ReglaDeEnvio.ImporteEsValido` is now shared with the
  parameter validation). Without the field (an old device) a pre-numbered envío sale keeps using the parameter.
  Idempotency: the `reenvioDeVentaYaEmitida` lookup moved before the ConEnvio client/domicilio rule and skips it when the number
  already has a comprobante, so a legitimate resend after the client's domicilio was cleared returns the existing comprobante
  (a new number with that client is still 400). Decision: the envío amount gets no clause of its own in `ExigirMismoContenido`:
  with the device amount fixed a parameter change cannot alter the total, and a resend with another amount changes the total and
  with it the payments (the plan stretches them to the total), which are already compared. The explicit clause survived its
  mutation, so it was not shipped; the reasoning is a comment there.
  Snapshot (served from `Ways.Application/Pos`, not `Dispositivos`): `InstantaneaDePos.ImporteEnvio` (importe_envio resolved
  PV > empresa > default, same query as tolerancia_pago with the per-key Where) and `EnvioPorDefecto`; `ClienteDeInstantanea`
  carries trimmed `Domicilio/Telefono/Celular` (key omitted when blank); the legacy format is untouched. POS: the casilla is
  enabled offline; the amount is the server read when online and the snapshot amount offline (`importeDeEnvioVigente`);
  local-first sales with envío go to the outbox with `conEnvio` + `importeEnvio` (the cart amount); the synthetic ticket has the
  Envío line (no idArticulo/area, 21 %, alícuota id sentinel 0) and `direccionEntrega`; `admisibilidadDeVentaOffline` adds
  `envio_sin_cliente_con_domicilio` and `envio_sin_importe` (non-definitive: a client missing from the copy still falls to the
  online path, which never carries `importeEnvio`). The initial casilla value uses the snapshot's `envioPorDefecto` over the PV
  listing read at login. T3 follow-ups: (a) done, `setImporteEnvio(null)` when a new read starts, with a test that fails
  without it; (b) NOT done: ShellPos prints the phone when `direccionEntrega` is present, and a delivery sale with
  `importe_envio` = 0 has no Envío line, so the comprobante alone cannot say it is an envío sale (the POS only fills
  direccionEntrega through ConEnvio, so it is equivalent in practice). Rendición (9.4) needs no change: the queue is counted by
  sale. Docs: §04 (envío rules), §9.3 (snapshot fields and ImporteEnvio).
  Evidence: `dotnet build` 0 errors; Domain.Tests 1870 ok; Application.Tests 1071 ok (structural guards included);
  IntegrationTests filtered on VentaConEnvio|Offline|Preasignad|Dispositivo|Rendicion|Instantanea|Parametros|SuperficieDeAutorizacion
  345 ok; web `npm run build` ok, oxlint no findings in touched files, full vitest 7704 ok. Mutation evidence (mutate -> failing
  test -> revert): removing `ExigirImporteDelDispositivo` -> 1 integration test fails; dropping `&& !reenvioDeVentaYaEmitida` -> the
  resend-after-domicilio-cleared test fails; using the parameter instead of the device amount -> 4 integration tests fail; removing
  the per-key `Where` in the snapshot -> 1 fails; no trim in `TextoOpcional` -> 1 fails; removing `setImporteEnvio(null)` -> the
  stale-amount test fails; not sending `importeEnvio` -> 2 Pos tests fail; dropping the domicilio check in
  `admisibilidadDeVentaOffline` -> 2 fail.

## Next step (after T4)

Owner OK for the POS release (Ways.Desktop bump + `pos-v*` tag) so the registers get T3 + T4. Then judgment-day per slice and the
stacked PRs.
