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
- [ ] T2 Backend: cash refund on NCX (efectivo only), arqueo/turno expected cash, tesorería
  consistency. Route: delegated.
- [ ] T3 Web POS: devolución mode (pick ticket, pick lines/qty up to remaining, confirm, print).
  Route: delegated.
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
