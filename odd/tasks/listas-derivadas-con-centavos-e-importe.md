# Derived lists keep cents, by-amount articles follow the list, F5–F8 without snapshot

## Objective

1. A derived price list (`ModoLista.Derivada`, base × porcentaje) keeps its percentage with 2 decimals in the
   unit price. Line and document totals keep rounding to the whole peso (unchanged).
2. An article sold by amount (`UnidadVenta.Importe`) applies the client's derived-list percentage to the typed
   amount (derived -10%, typed 1500 → 1350). A fixed list leaves the amount unchanged.
3. The F5–F8 by-amount shortcuts work in the POS when there is no snapshot (web POS always; desktop before the
   first sync).

## Problem / why

- Since `279befb2` (whole-peso sales) `ResolvedorDePrecios.ResolverPrecioDerivado` rounds the unit price to the
  peso: $1 at -10% → 0.90 → $1, so the employees list stops discounting cheap articles (owner report 2026-10-14).
- By-amount lines are priced at the typed amount with no list (`ServicioDeOfertas.ResolverPorImporte`,
  `instantaneaOffline.preciosVigentesDeLasLineas`), so an employee buying "1500 of X" pays 1500.
- The shortcuts read only `sincronizacionOffline.instantanea`; `GET /api/pos/instantanea` requires a device
  claim, so in the browser it answers 403 (swallowed) and every F5–F8 shows `MENSAJE_DE_ATAJO_SIN_DATOS`.

## Scope / constraints

- Server is the price authority; the offline engine must produce the same figure (snapshot carries list
  metadata: id, modo, porcentaje; an old stored snapshot without it falls back to the amount unchanged).
- Credit notes of by-amount lines refund what was charged: the stored unit price is the post-percentage price;
  the return must not re-apply the percentage nor fail the match against the original sale.
- Offers and combos keep skipping by-amount lines. Remitos, presupuestos and the online store keep rejecting them.
- No schema change (`precio_unitario` is `numeric(14,2)`).

## Tasks

- [x] T1 — Derived unit price with 2 decimals (`ResolvedorDePrecios`, doc-comments, tests incl. integration
  expectations that assumed whole pesos). Route: delegated writer (writer trigger: domain + tests + integration).
- [x] T2 — By-amount lines apply the derived-list percentage: server resolver, snapshot `listas`, offline engine,
  online preview (`previaDeLinea`, `calcularSubtotalPrevia`), credit-note match, tests. Route: delegated writer.
- [ ] T3 — F5–F8 fall back to the server when there is no snapshot for the point of sale. Route: delegated writer
  in its own worktree/branch (independent of T1/T2).

## Acceptance criteria

- Derived -10% over base 1.00 → unit 0.90; 3 units → line 3 (2.70 rounded); 10 units → 9.
- Client on derived -10%, by-amount line typed 1500 → unit 1350, total 1350, online and offline alike; client on a
  fixed list → 1500.
- A credit note of that line refunds 1350 and is accepted.
- F5 in the web POS with no snapshot adds the by-amount article with the typed amount.

## Checks

- `dotnet test` for Domain/Application unit suites; integration tests touched (Docker, never concurrent).
- Vitest for `src/Ways.Web` (focused files, then full suite once, never two vitest runs concurrently).

## Progress

- 2026-10-14: diagnosis done; branch `pnader/derivadas-con-centavos-e-importe` from origin/main.

- 2026-10-14 T1 done — commit `5796b42f`. `ResolverPrecioDerivado` rounds to 2 decimals (away from zero);
  line gross/net and totals stay whole pesos in `CalculadorDeTotales`. Tests: the domain theory now pins
  0.90 / 112.5 / 87.5 / 1116.67 / 0.945→0.95; new `CalculadorDeTotales` case (0.90 × 3 → 3, × 10 → 9);
  `CatalogoPublicoTests` expectations moved from whole pesos to cents (38.5, 31.5, 0.17, 0.14, 52.5, 0.23).
  Route: delegated writer.
- 2026-10-14 T2 done — commits `ff4e2213` (server) and `28d0b6b8` (web/POS).
  - Server: `ServicioDeOfertas.ResolverPorImporte` applies the line's list through
    `ResolvedorDePrecios.ResolverPrecioDerivado` (Fija unchanged; missing list `referencia_invalida`; derived without
    porcentaje `precio_derivado_invalido`). One extra query (list modo/porcentaje) only when the batch has an importe
    line. Ingestion follows `PrecioOriginal`, no change needed in `ServicioDeVentas` for sales.
  - Credit notes: design = a document with `tipo.Signo < 0` resolves its importe lines with
    `ResolverDocumentoConImporteCobradoAsync` (the amount is taken as is). The credit note carries the stored unit
    price (1350), which matches the sale row in `ExigirImportesDeLaVentaAsociadaAsync` unchanged and is refunded
    exactly; the typed amount (1500) is rejected with `importe_devolucion_inexistente`. No web credit-note flow
    exists for importe lines (the POS never emits NC), so nothing to change on the web side.
  - Snapshot: `InstantaneaDePos.Listas` (`idListaPrecio`, `modo`, `porcentaje`), omitted when empty and absent in the
    legacy format.
  - Offline engine: `preciosVigentesDeLasLineas` applies the derived percentage with exact decimal arithmetic
    (`productoRedondeado`, 2 decimals, half away from zero). Fallback design: a list missing from the snapshot (old
    stored copy, dropped list, malformed derived) makes `todasLasLineasTienenPrecioOffline` false for that line, so
    the POS prices online and the server decides; the engine itself returns no price for the line instead of the
    undiscounted amount. `comprobanteOfflineSintetico` follows (returns null without the list).
  - Online preview: `previaDeLinea` uses the resolved server price for importe lines; `calcularSubtotalPrevia` waits
    for prices.
  - Evidence: Domain 1695, Application 1036, targeted integration 872 (price, offer, snapshot, sale, combos, authorization
    surface), web vitest 7404, tsc clean, oxlint no findings in touched files;
    mutation proof: reverting the credit-note branch fails
    `LaDevolucionDeUnImporteConListaDerivadaReembolsaLoCobradoSinReaplicarElPorcentaje`.
  - Risk: a desktop POS still running the previous bundle (no `listas` logic) charges the typed amount offline while
    the server now records the discounted price on sync; the POS release must go out with this change.

- 2026-10-14 judgment-day round 1 — commit `5bde41e1`. Fixes JD1 (offline sale re-derived a by-amount price with today's
  list) and JD2 (idempotent resend compared against a re-derived price).
  - Offline origin = `NumeroPreasignado is not null` (only a device may send it; web/online sales keep the T2
    derived-list behavior). `importe` + `precioUnitario` is accepted only there (online keeps
    `importe_con_precio_no_admitido`): the device price is stored as-is and audited against the derived price; an
    `importe`-only offline line (previous bundle) is stored at the typed amount with no list and no audit. Credit notes
    unchanged. Web: the queued by-amount line carries `precioUnitario` from `precioDeLineaPorImporte`.
  - Resend compares charged prices, so it no longer depends on the current list percentage; `ExigirMismoContenido`,
    `ItemEmitido`, `LineaDeVenta` docs and doc 10 state what is stored/compared.
  - Evidence: integration `VentaPorImporteTests` 49 + touched sale/offline/offer/authorization classes 373 green;
    mutation (offline branch off + list always applied) fails the (a) offline-without-price test and the (d) resend test.
    Domain 1695, Application 1036, web vitest src/pos 871 and src/api 1599, tsc clean.
