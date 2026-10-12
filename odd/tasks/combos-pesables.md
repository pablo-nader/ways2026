# Combos with weighable components

Locator: `odd/tasks/combos-pesables.md` · Engram mirror: `odd/combos-pesables/tasks` · Branch: `pnader/combos-productos-pesables-9b821d`

## Objective

A combo component can be an article sold by weight (`UnidadVenta.Peso`), with a quantity in kg up to 3 decimals
(example: 0.200 kg of one cold cut + 0.200 kg of another at a fixed price).

## Problem and why

`odd/tasks/combos.md` (D5) restricted combos to unit articles. The owner sells combos made of weighed products
and cannot load them (request of 2026-10-12). This document amends D5.

## Decisions

- **O1 (owner, 2026-10-12):** the combo consumes exactly the component quantity from a line; the excess stays at
  list price (a 215 g line with a 200 g component: 200 g in the combo, 15 g at list). 400 g fits two combos. Same
  "once per complete set" rule as unit articles. A sale line is never split.
- **A1 (agent, pending owner objection):** the component quantity follows the article's unit of sale
  (`ReglaDeCantidadDeVenta.EsCantidadAdmitida`): integer for Unidad, up to 3 decimals for Peso.
- **A2 (agent):** the "at least two units" rule becomes: at least two components, or a single component with
  quantity >= 2. Detection no longer short-circuits on a document total below 2.
- **A3 (agent):** a family component is valid only if its quantity is admitted by every live member; at sale and
  POS load time a member whose unit does not admit the quantity is not a candidate (the ABM check is point-in-time).
- **A4 (agent):** the combo saving of a line never carries more than 2 decimals: the list amount of the consumed
  portion is rounded to 2 decimals before the distribution, in both engines.
- **A5 (agent):** the TypeScript engine does quantity arithmetic in integer thousandths, never in floats.

## Scope

In: combo validation and ABM, sale detection (online and offline), POS load of a combo, back-office editor, both
engines and their parity suite. No schema change (`combos_componentes.cantidad` is already `numeric(12,3)`).
Out: scale-label barcodes with embedded weight, POS desktop release (separate bump + tag), quotes and delivery notes.

## Tasks

- [x] **T1 — Backend (C#).** Route: delegated writer (2+ non-trivial files). `ReglaDeCombos`, `ResolvedorDeCombos`
  (decimal counters), `CombosEnVenta`, `ServicioDeCombos` (validation + POS candidates), consumed-units contracts
  `int` → `decimal`, `ExigirComboOfflineValido`. Tests flipped and added; C# side of the parity fixtures.
  Checks: `dotnet test` unit + integration for combos/ventas/ofertas and the structural guard tests.
- [x] **T2 — Offline engine and POS (TypeScript).** Route: delegated writer. `motorDeCombos.ts` (thousandths),
  `instantaneaOffline.ts`, `fuenteDeCombos.ts`, `cargaDeCombos.ts`, `SelectorDeMiembrosDeCombo`, parity cases with
  fractional quantities mirrored in C#. Checks: `vitest run` for pos/api/componentes + C# parity test.
- [x] **T3 — Back-office editor.** Route: delegated writer. `combosAbm.ts`, `EditorDeComponentes.tsx`,
  `SelectorDeArticuloDeCombo.tsx`, copy and error messages; descriptor tests. Checks: `vitest run` + `tsc`.
- [ ] **T4 — Delivery.** judgment-day until a clean round, PR with auto-merge and Auto-fix; amend D5 in
  `odd/tasks/combos.md`.

## Acceptance criteria

1. A combo of 0.200 kg of A + 0.200 kg of B at a fixed price can be created from the back office.
2. Selling 0.215 kg of A and 0.200 kg of B applies the combo once; 0.015 kg of A is charged at list price.
3. Selling 0.400 kg of each applies it twice; 0.150 kg of A does not apply it.
4. The offline engine produces the same amounts as the server for those cases (parity suite).
5. Unit-only combos behave exactly as before.

## Delivery

Strategy `ask-on-risk`, chain `stacked-to-main` (cached). Forecast: ~900 authored lines → three slices
(T1, T2, T3), one PR each.

## Progress

- T1 implemented on `pnader/combos-productos-pesables-9b821d` — commit `bc585f3a`. Route: delegated writer. No migration.
  - Engine (`ResolvedorDeCombos`): quantities in integer thousandths (`long`), mirror of A5. `LineaParaCombos` gained
    `UnidadVenta` (last positional parameter, default `Unidad`, so the parity fixtures compile untouched). A line is
    detectable when its unit admits its quantity; a line is a candidate for a component only when its unit admits the
    component quantity (A3); only whole units are taken from a unit line; a weighable line above `int.MaxValue`
    thousandths is ignored, like a unit line above `int.MaxValue` units.
  - A4 as implemented, in this order, per application: `importe = round2(taken × list price)` half away from zero,
    per line; `totalDeLista = Σ importe`; `cobro = APesoEntero(combo price)`; not applied when
    `cobro >= costoConOfertas` (unrounded, as before) or `cobro >= totalDeLista`; `tope = floor(importe)`;
    `parte = clamp(APesoEntero(cobro × importe / totalDeLista), 0, tope)`; remainder from the last line backwards;
    `ahorro = importe − parte`; the line accumulates `ahorro × veces`. With integer quantities and 2-decimal prices
    every number is the one the engine produced before.
  - Rule (`ReglaDeCombos`): quantity > 0, <= 999999, up to 3 decimals (`combo_cantidad_invalida`); minimum per A2
    (`combo_unidades_insuficientes`, code kept, message changed). `UnidadesMinimas` was replaced by
    `CantidadMinimaDeUnComponenteUnico`, `EsCantidadDeComponenteValida` and `AlcanzaElMinimo`.
  - ABM (`ServicioDeCombos`): error code `combo_componente_no_unitario` was **renamed** to
    `combo_cantidad_no_admitida` (the quantity is not admitted by the unit of sale of the article, or of some live
    member of the family). The web client still maps the old code: T3 has to rename it in `combosAbm.ts` and its
    tests. `docs/10-modelo-de-datos.md` still names the old code and the old integer rule (T4).
  - POS load (`CargarParaPosAsync`) and snapshot (`ArmadorDeInstantanea.ArmarCombos`): a candidate / a sellable
    component is one whose unit admits the component quantity. The candidate still carries `cantidad = 1`.
  - Contracts widened `int` → `decimal`: `ConsumoDeLinea.UnidadesConsumidas`, `ComboEnLinea.UnidadesConsumidas`,
    `ComboDeLineaDto.UnidadesConsumidas` (JSON `unidadesConsumidas`), `LineaDeVenta.UnidadesConsumidasPorCombo`
    (JSON `unidadesConsumidasPorCombo`). A unit quantity still serializes without decimals (`1`).
  - Offline: `combo_offline_invalido` now also covers consumed units the article's unit does not admit (a fraction
    of a unit article used to be `cuerpo_invalido` from the binding); units must be in `(0, Cantidad]`.
  - Decision taken by the writer, not in A1–A5: A2 says detection no longer short-circuits below 2. The
    short-circuit was kept only when every line is a unit article (then it is still exact), because three existing
    tests pin that a one-unit sale does not pay the combos query; a document with any weighable line always pays it.
  - Known limit of A4: the rounding is per application and multiplied by the times applied, so a single weighable
    line consumed 100 or more times can drift one peso from `times × combo price`. Unit lines have no drift.
  - Checks: Domain 1376/1376, Application 846/846, Integration 3758/3758 (full, unfiltered). 23 mutations
    killed (16 unit, 7 integration).
- T2 implemented on the same branch — commit `ac620511`. Route: delegated writer.
  - Two tests of `src/Ways.Web/src/paginas/Pos.test.tsx` pinned the old restriction and were flipped (the surface was
    extended by the parent): a weighable article with quantity 1 now enters a combo of integer quantities, as on the
    server, and a weighable family member is a candidate of a component of quantity 2, so the member selector opens.
  - Engine (`motorDeCombos.ts`): quantities in integer thousandths read from the shortest decimal form of the number,
    amounts in `bigint` at `price scale + 3`. Same rules and same A4 order as `ResolvedorDeCombos`. `LineaParaCombos`
    gained a required `unidadVenta` (`UnidadVenta | null | undefined`). `UNIDADES_MINIMAS_DE_COMBO` was replaced by
    `CANTIDAD_MINIMA_DE_UN_COMPONENTE_UNICO`.
  - Divergence found at HEAD and closed: the TypeScript engine did not round the list amount to 2 decimals (A4) nor
    skip on `cobro >= totalDeLista`, so with prices of more than 2 decimals it produced savings of 3+ decimals that the
    server no longer produces. With 2-decimal prices and integer quantities both engines already agreed.
  - Decisions not covered by A1–A5:
    - A missing unit of sale counts as `Peso` in the engine, the snapshot source and the load (the convention of
      `api/cantidadPorUnidad.ts`; the server never sends a missing unit, it only happens in a snapshot older than the
      field). With integer quantities it behaves exactly like `Unidad`.
    - The "saving with more than 2 decimals → drop every combo" guard of `resolverDocumentoOffline` was removed: A4
      makes it unreachable, and its tests could only pass by asserting the old engine. A document with a sub-cent price
      now applies the combo with 2-decimal savings, like the server.
    - The zero-take guard of the C# engine (`if (milesimas == 0) continue`) is not ported: in TypeScript a take of zero
      has no observable effect (no integer division by zero), so no test can kill it; it ships as a comment.
    - Cart merge (`carrito.ts`): the sum is exact to 3 decimals only when both quantities have at most 3 decimals; a
      quantity with more decimals is added as is instead of being rounded into a valid one.
    - Load (`cargaDeCombos.ts`): with a single candidate the component quantity must be admitted by that article's
      unit of sale (`La cantidad de «…» no es válida.`); member picks are validated with each member's unit; the member
      total is compared exactly at 3 decimals. Quantities are shown with a decimal comma (`Jamón × 0,2`).
  - Parity: 8 fractional cases added to `paridadDeCombos.test.ts` and `ParidadDeCombosConElMotorWebTests.cs` with
    identical inputs and numbers (18/18 on each side). No defect found in the C# engine.
  - Checks: `tsc -b` exit 0; `oxlint` exit 0; `vitest run` 5839/5839 (206 files); `dotnet test
    tests/Ways.Domain.Tests` 1384/1384. 27 mutations killed, 1 survivor removed (the zero-take guard above).

- T3 implemented on the same branch. Route: delegated writer.
  - `combosAbm.ts`: a row (`FilaDeComponente`) gained `unidadVenta` (`null` for a family and for a component that comes from
    the server: the listing does not carry the unit). Per-row validation: unit article → integer (`entero entre 1 y 999999`),
    weighable or unknown → > 0, up to 3 decimals, <= 999999 (`mayor a cero, con hasta 3 decimales`), via `esCantidadValida`.
    A decimal comma is accepted by `numeroDeCantidadDeComponente` (decimal only: no sign, exponent or thousands).
    Minimum per A2 (`alcanzaElMinimo`, mirror of `ReglaDeCombos.AlcanzaElMinimo`). The error code
    `combo_componente_no_unitario` became `combo_cantidad_no_admitida`; an old code now falls into the generic form error.
  - Editor: a quantity input of type `number` (min 1, step 1, as before) only for an article known to be sold by unit; for a
    weighable article or a family it is a text input with `inputMode="decimal"` (so the decimal comma works) and a `kg`
    suffix for the weighable. Choosing an article keeps the typed quantity when its unit admits it and resets it to 1 otherwise
    (`cantidadTrasElegirArticulo`). The selector offers every active article and labels weighables `(por peso)`.
  - Summary line: `0,2 kg × Jamón`; `kg` shows for a weighable row and for any non-integer quantity of the listing.
  - Decisions not covered by A1–A5: a saved component that is edited keeps an unknown unit until its article is chosen again,
    so its quantity is validated as "up to 3 decimals" and the server decides (`combo_cantidad_no_admitida`); no `min`/`step`
    attributes on the decimal input (a text input has none), the validation message carries the rule instead.
  - `docs/10-modelo-de-datos.md`: the ReglaDeCombos paragraph and the API error list were updated. Still stale there, outside
    this slice's instruction: the sale-side lines that say weighables are excluded from detection (`CombosEnVenta`, online
    combos, offline snapshot).
  - D5 of `odd/tasks/combos.md` carries a one-line amendment pointing here.
  - Checks: `tsc -b` exit 0; `npm run lint` exit 0; `vitest run` 5895/5895 (206 files). 18 mutations killed.

Next step: T4.
