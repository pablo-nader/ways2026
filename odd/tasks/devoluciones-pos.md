# Devoluciones en el POS

## Objective

Let the cashier return goods from a past ticket in the POS: a standalone NCX voucher (all lines
negative) associated to the original ticket, with the cash refund recorded in the turno. Bottle
deposits (envases) will reuse this later.

## Problem / why

The backend already accepts NCX through `POST /api/ventas`, but:
- the POS never emits one (Pos.tsx hardcodes TX);
- the association is optional and there is no cap on returned quantities (docs/10 04: "no se lleva
  la cuenta de lo ya devuelto");
- NCX lines are priced from today's list, not from what was charged;
- an NCX cannot carry money (`ValidadorDePagos` rule 0c), so a cash refund never reaches the arqueo;
- there is no ticket lookup by number and no NCX ticket template.

## Owner decisions (2026-11-09 / 2026-11-15)

- A return is always its own NCX; an exchange is a separate new sale. No mixed tickets.
- The refund is always cash from the turno's caja, recorded as an outflow in the arqueo.

## Default rules (proposed to the owner, not objected)

- The original ticket is mandatory, picked from a list or searched by number.
- Cap per article: returned quantity <= sold minus already returned (emitted NCX associated to the
  same ticket), checked server-side under a lock on the original.
- Refund amounts come from the original ticket lines (what was charged, combos included).
- Online only.
- An original with an approved or pending CAE (fiscal mirror) cannot be returned until fiscal credit
  notes exist (T8 of facturacion-por-medio-de-pago): 409 with a clear message.
- Stock goes back to the lot of the original sale.
- A "DEVOLUCIÓN" ticket prints with the associated ticket number and opens the cash drawer.
- Annulling a ticket that has emitted NCX associated is rejected (409).

## Tasks

- [x] T1 Backend: mandatory association, per-article cap under lock, prices and lots from the
  original, CAE block, lookup endpoint with returnable quantities, annul guard. Route: delegated
  (one writer; trigger: 2+ non-trivial files).
  Evidence (2026-11-15, branch pnader/devoluciones-pos):
  - Domain: `DevolucionDeTicket` (replaces `DevolucionDeCombos`), `ReglaDeDevoluciones`, associated rule in
    `ReglaDeComprobantes`; `dotnet test tests/Ways.Domain.Tests` 2658 passed. Domain tests were written together with the
    code (no RED run); RED/GREEN was only observed for the integration tests, whose old versions failed first.
  - `dotnet test tests/Ways.Application.Tests` 1706 passed (lock-order and reader guards; allow-list of
    `LectoresDeComprobantesVentaEstructuralesTests` updated).
  - Integration filter `Venta|Ncx|Devolucion|Anulacion|Combo|Fiscal|SuperficieDeAutorizacion|AislamientoDeTenant|RutasDeLaSpa`:
    1891 tests, 1888 passed; the 3 failures were NCX emissions without an associated ticket in
    `InvarianteStockYStockLotesTests`, `DecisionDeFacturacionAlIngerirTests` (outside T1 edit surfaces, still red) and
    `AnulacionTests` (fixed afterwards and re-run: 37 passed). Not re-run as a whole after that fix.
  - Also red, outside the filter and outside the edit surfaces: `ReservaDeNumeracionEndpointsTests`
    (`ReenviarConOtroComprobanteAsociadoBajoElMismoNumeroPreasignadoEs409`: offline NCX is now rejected) and
    `CuentasTesoreriaCierreTests` (`UnaNotaDeCreditoConPagosSeRechazaYSinPagosNoTocaLaCuentaNiElArqueo`: no associated).
  - Mutation evidence (each mutant reverted): cap clause `if (faltan > 0m)` disabled -> 10 domain + 5 integration tests red;
    CAE block disabled in `ReglaDeDevoluciones.Evaluar` -> 4 domain + 4 integration red; annul guard call removed ->
    `UnTicketConDevolucionesEmitidasNoSeAnula` red; call to `ExigirDevolucionVigenteBajoLockAsync` removed -> the
    deterministic race test `DosDevolucionesQueSeSuperponenNoDevuelvenMasDeLoVendido` red (plus the CAE tests, which also run
    under that lock).
  - Lock order of the NCX transaction: turno FOR SHARE -> ticket de origen FOR UPDATE -> (rest of the existing order).
- [x] T2 Backend: cash refund on NCX (efectivo only), arqueo/turno expected cash, tesorería
  consistency. Route: delegated (one writer; trigger: 2+ non-trivial files).
  Evidence (2026-11-15, branch pnader/devoluciones-pos-efectivo, stacked on T1):
  - Judgment-day warnings on T1 fixed first (commit 81f4626c): B-1 each tranche returns to its own original item's lot and an
    explicit `idLote` that is not the tranche's lot is `400 lote_invalido` (test
    `UnaDevolucionQueCubreItemsDeLotesDistintos...`; mutant `idLoteDelCliente != item.IdLote` -> `false` made it red, reverted);
    B-2 already-credited amount is the persisted sum of the emitted NCX items' totals per set, charged to the last item they touched
    (superseded by F1 below; domain tests `AnularUnaNotaIntermedia...` and `LoAcreditadoPorLasNotasSobrevivientes...`, integration test
    `AnularUnaDevolucionIntermedia...`; mutant removing the reconciliation made 2 domain tests red, reverted).
  - Model: no schema change. The NCX carries its refund as `cobros` rows (positive importe, `ck_cobros_importe_no_negativo` stays) and
    the sign comes from the comprobante. `ValidadorDePagos` rule 0c now requires, for a total < 0: only `efectivo` medios
    (`devolucion_solo_en_efectivo`), no vuelto (`devolucion_con_vuelto`), exactly `-total` (`devolucion_reintegro_invalido`, which carries
    `datos.reintegro`). An NCX without pagos is no longer accepted (no product flow emits one; the POS does not return yet), a zero-total
    NCX needs none. `LectorDeMovimientosDelTurno` sums `importe x signo` (sign = total < 0), the single derivation shared by resumen,
    cierre, cierre por retiro, recalculo and detalle. Annulling the NCX needs no code: the esperado excludes anulado comprobantes
    (annulling already requires an open turno). Cuentas de tesoreria only exist for electronic medios, so the refund never reaches them.
  - Readers of `cobros` reviewed: arqueo reader (changed), reports by medio (already signed by tipo), LectorDeFacturadoPorTurno (uses
    the ORIGINAL ticket's cobros, never the NCX's), fiscal decision/mirror reversal (NCX is `NoCorresponde`), cambio de medios (rejects
    signo <= 0), cuentas de tesoreria acreditacion/reconstruccion (only electronic medios).
  - Mutation evidence (each reverted): `importe * signo` -> `importe` made `ElReintegroEnEfectivoBajaElEsperadoDelTurnoYElCierreCuadra`,
    `AnularLaDevolucionDevuelveElEfectivoAlEsperadoDelTurno` and `UnaNotaDeCreditoReintegraEnEfectivoYNoTocaLaCuentaDeTesoreria` red;
    efectivo-only clause disabled -> 2 domain tests red; exact-total clause disabled -> 4 red; no-vuelto clause disabled -> 1 red. An
    earlier version joined `tipos_comprobante` with `IgnoreQueryFilters(["BajaLogica"])`: that operator is query-wide and resurrected
    soft-deleted cobros (caught by `CambioDeMediosDeVentaTests`), hence the sign from the total.
  - `dotnet build`: 0 errors. `dotnet test tests/Ways.Domain.Tests`: 2665 passed. `dotnet test tests/Ways.Application.Tests`: 1706 passed.
  - Integration, run in slices (one filtered run over all names reached ~10 GB in testhost without finishing and was killed):
    NCX/ventas/anulacion/cambio de medios/CuentasTesoreria slice 587 passed; structural guards (`SuperficieDeAutorizacion`,
    `AislamientoDeTenant`, `LectoresDeComprobantes`) plus Arqueo/Cierre/Tesoreria/HistoricoDeCajas/ListadoDeVentas slice 462 passed;
    Caja/Turno/Pagos-de-turno/personal/MercadoPagoVenta slice 464 passed. Not run: the rest of the `Pago` name matches (MercadoPago
    api/webhook/conciliacion/esquemas, aplicaciones de pago, migrations) and `Reportes*`/`Rentabilidad`/`ConsultasMcp`.
  - Test churn: NCX emissions in existing integration tests now carry the efectivo refund (new `ApoyoDeDevoluciones.PostearAsync`
    reads `datos.reintegro` from the rejection); `UnaNotaDeCreditoConPagosSeRechazaYSinPagosNoTocaLaCuentaNiElArqueo` became
    `UnaNotaDeCreditoReintegraEnEfectivoYNoTocaLaCuentaDeTesoreria`.
  - Follow-up (approved): docs/01 updated; `POST /api/ventas/devoluciones/previsualizacion` (OperacionDePos, read-only) returns per-line
    credit and `reintegro` through the same `ResolverDevolucionDelTicketAsync`/`DevolucionDeTicket` path and the same rejections as
    emission; allow-listed in `SuperficieDeAutorizacionTests` and `LectoresDeComprobantesVentaEstructuralesTests`. Integration:
    DevolucionesTests + SuperficieDeAutorizacion + AislamientoDeTenant + RutasDeLaSpa 155 passed; Application.Tests 1706 passed.
  - Judgment-day round 1 (2026-11-15), commits 2e2cbac6 and 840b8a92:
    - F1 (replaces the B-2 "last touched item" attribution): migration `DevolucionPorItemDeOrigen` adds nullable
      `items_comprobante_venta.id_item_comprobante_venta_origen` (composite self-FK with tenant, index, alternate key
      `ak_items_comprobante_venta_id_item_id_tenant`; DDL only, no backfill; the origin is server-written so no 23503/23505 backstop is
      reachable from client input). Taken units and credit per origin item come from the emitted NCX items; NCX rows with a NULL origin
      (older notes) fall back to the per-article unit replay and their credit is rebuilt. `Tomar` is clamped to `[0, Total - acreditado]`.
      Domain tests for both scenarios (same article at two prices, alternating units) plus integration
      `AnularLaDevolucionDelPrimerItemDelMismoArticuloVuelveAAcreditarEseItemYNoElOtro` (refund 50). Mutant "attribute to first item with
      units left" -> 1 domain + 1 integration test red, reverted.
    - F2: `LeerVentasDelTurnoAsync` signs the medio importe by the comprobante total (negative = refund), so the per-medio total of
      "Ventas del turno" and of the closed-turno detail equals the arqueo esperado. Web unchanged except a comment and a vitest case.
      Integration `ElTotalPorMedioDeVentasDelTurnoDescuentaLaDevolucionYCoincideConElEsperado`; mutant dropping the sign -> red
      (-100 expected, 100 actual), reverted.
  - Open: the Web mirror `src/Ways.Web/src/api/pagos.ts` (rule 0c) and its test still encode it (T3).
- [x] T3 Web POS: devolución mode (pick ticket, pick lines/qty up to remaining, confirm, print).
  Route: delegated.
  Evidence (2026-11-15, branch pnader/devoluciones-pos-ui, stacked on T2):
  - Pagos mirror: `validarPagosLocal` rule 0c now mirrors the domain (total < 0: only efectivo money, no vuelto, exactly `-total`; no
    pagos is rejected as `devolucion_reintegro_invalido`; the `Math.max(0, total)` workaround of the cobro cap is gone). RED observed first
    (6 tests of the old rule failed), GREEN 91 passed. Mutants (each reverted): efectivo-only clause -> 1 red, no-vuelto clause -> 1 red,
    exact-total clause -> 2 red.
  - New: `api/devoluciones.ts` (types + client of `devolvibles`/`previsualizacion`), `pos/devolucion.ts` (pure helpers: motivo copy, quantity
    cap and granularity, scan increment, request builder, error copy, recent tickets, availability gate), `pos/ModalDeDevolucion.tsx`
    (container) + `TablaDeLineasDeDevolucion.tsx` / `ListaDeTicketsRecientes.tsx` (presentational), `ticketDeDevolucion` in
    `impresion/plantillas.ts` (title DEVOLUCION, associated ticket, lines with credit, refunded total, drawer pulse in the same job),
    "Devolución" button in the Pos cart header (`Pos.tsx`), `venta_con_devoluciones` copy in `VentasDelTurno.tsx`.
  - Behaviour: online only and with an open turno (button disabled with its reason otherwise, plus a visible offline line); the modal makes
    the whole screen inert (`cajaDeEscritorioOcupada`), so F2/F4/F5-F9 do nothing underneath. Preview is debounced (300 ms), generation-gated,
    and Confirm stays disabled until the preview matches the current selection. Emission uses a synchronous re-entrancy ref and an inert modal.
    `devolucion_desactualizada` / `devolucion_excede_lo_vendido` / `devolucion_reintegro_invalido` and an uncertain result re-read the
    devolvibles; printing goes through the same `encolarImpresion` queue as TX (Desktop only; the web app has no printer).
  - Tests: `pos/devolucion.test.ts` 35, `pos/ModalDeDevolucion.test.tsx` 21 (search -> select -> preview -> confirm body -> print), `Pos.test.tsx`
    "devolución" 5 (offline/closed-turno gating, desktop flow prints, F2 inert), `VentasDelTurno` 1, `plantillas` 2, `pagos` 6.
    Component tests were written after the component (no RED run); evidence is mutation: stale-preview gate removed -> red; re-entrancy ref
    removed -> double click emits twice (red); print call disabled -> red; re-read on `releer` removed -> 2 red; error clearing on selection
    change removed -> red; quantity cap clause disabled -> 3 red; pago importe `reintegro` -> `ticket.total` -> red; scan cap disabled -> red;
    drawer pulse removed from the template -> red; `devolucionAbierta` removed from `cajaDeEscritorioOcupada` -> F2 test red; button
    `title` removed -> 2 red; `venta_con_devoluciones` branch removed -> red. A redundant `previa.firma !== firma` guard in `confirmar`
    survived mutation (it repeats the button's `disabled`) and was deleted.
  - Checks: `npx tsc -b` clean; `npm run lint` no warning in the touched files (only the repo's existing warnings); full `npx vitest run`:
    335 files / 10412 tests passed on the second run. The first full run had 2 failures plus a worker crash under load (one was
    `ModalDeBusquedaDeArticulos` "con más de 100 artículos"; both files pass alone); not reproduced.
  - Open: `LineaDevolvible` has no `unidadVenta`, so the client only knows a por-importe line (integer) and treats the rest as peso (3
    decimals); a fraction of a Unidad article is rejected by the server and shown with its message. No keyboard shortcut (F1-F10 are
    taken or reserved by the legacy). No Rust/Desktop change was needed.
- [ ] T4 POS release (pos-v* tag): needs the owner's OK.

## Acceptance criteria

- An NCX without an associated ticket is rejected.
- Returning more than sold minus already returned is rejected, also under concurrent returns.
- Refund equals what was charged for those units on the original.
- The turno's expected cash drops by the refund; closing the turno reconciles.
- An original with CAE cannot be returned.
- The POS prints a DEVOLUCIÓN ticket.

## Checks

- dotnet unit + integration (Ventas, Turnos/Caja, Tesoreria, structural guards).
- Ways.Web vitest (POS).

## Delivery

ask-on-risk, stacked-to-main slices: T1, T2, T3 each a PR.

## Progress

- 2026-11-15: worktree `.claude/worktrees/devoluciones-pos` on `pnader/devoluciones-pos` from origin/main 150a377b.
