# Ofertas solo múltiplos — follow-ups

## Objective

Close the four informational follow-ups left by `ofertas-solo-multiplos` (judgment-day T4, 2026-10-31).

## Problem

- An offline device can charge more than the server, and the grid can grow too large.
- The cart shows a misleading unit price.

## Scope

- [x] F1 — Equal line discount, strict vs common (strict at lower id): the offline curve has no finite breakpoints, so the
  device falls back and charges list price while the server applies the common offer. Offline must match the server.
  (unit/application/web green; integration filter PENDING, see evidence)
- [x] F2 — Family strict ranks by proportional share, but allocation fills groups in line order, so a later line can get
  less than the common offer it beat. Ranking key and allocated discount must agree per line.
  (unit/application/web green; integration filter PENDING, see evidence)
- [x] F3 — Family grid cells are a cartesian product (up to ~1024 per article/list); fallback drops the whole competing
  tier. Bound the grid without under-charging. (unit/application/web green; integration filter PENDING)
- [x] F4 — Cart shows the offer unit price (3750) on a strict line whose total is 11500. Show what is really charged.
  (web green: vitest src/pos + Pos.test 1499/1499, tsc ok, oxlint ok)

## Constraints

- Never under-charge versus the server; server and offline engines must agree on the acceptance cases.
- Acceptance from the parent feature still holds: 4000 article, 2x7500 strict → 1/2/3/4 = 4000/7500/11500/15000; non-strict 3 → 11250.
- POS changes reach registers only with a `pos-v*` release.

## Route per task

- F1–F3: delegated direct (writer trigger: domain engine + escalones + TS offline + tests).
- F4: delegated direct (writer, web cart only, disjoint files).

## Progress

- 2026-10-31: branch `pnader/ofertas-solo-multiplos-seguimientos` from origin/main 7e951e5f.

## F1–F3 evidence and decisions (2026-10-31)

- Route: delegated direct (writer). Not committed yet.
- F1 (decision): the base tie-break gains a step before the lowest id: with equal line key and equal unit discount, the
  common offer beats the strict one. Both charge exactly the same there (only at multiples, the strict covers every
  unit), so the server's charged amounts do not change; only the reported offer at exact multiples does. With it the
  winner no longer alternates with the quantity: `Termina` now treats strict vs common with the same discount as
  terminating with no breakpoints, the curve is exact and the device charges the common offer like the server.
  Representing the old alternation was impossible with a finite curve. Strict vs strict of another group size with the
  same discount still alternates forever and keeps the fallback. Changed test: `ConElMismoDescuentoDeLineaYUnitarioGanaElMenorId`
  (strict id 1 vs common id 2 won the strict) → `ConElMismoDescuentoDeLineaYUnitarioGanaLaComun`, plus
  `ConTodoIgualGanaElMenorId` between two stricts; `UnaCompetenciaQueNoTerminaOPasaElTopeNoTienePuntosDeQuiebre(3750)`
  dropped (now terminates), 3751 kept as `UnaCompetenciaQuePasaElTopeNoTienePuntosDeQuiebre`.
  New: `ConElMismoDescuentoYLaSoloMultiplosDeMenorIdElDispositivoCobraComoElServidor` (summary + total parity at every
  0.001 from 0.001 to 12). No TS test: the device does not rank offers, it only reads the curve.
- F2 (decision): `CoberturaDeOfertas` (and its TS port) distributes a strict family offer's discount `C × d` pro rata by
  units across the lines that carry it: each line its share truncated to the whole peso, the remainder on the last line
  (spilling backwards only if it would cover more units than the line has). Per line that is the ranking key
  `d × C / U` up to the peso. Whole pesos, not cents: each line's net is rounded to the peso, so cent shares would move
  the document total (1 + 1 + 1 at 2x7500: 11499 instead of 11500). The coverage of a family offer is the share divided
  by `d` (a fraction); levels are compared over the product of denominators, and a line carrying a family offer rounds
  its discount to 6 decimals away from zero (identity when the per-unit clamp does not bind), same in C# and TS (bigint).
  Limit kept by decision (T4): the key uses the family quantity, the pool only the lines that chose the offer and units
  outside combos, so when another family line took another base or a combo consumed units the share can be below the
  key. Intentional per-line changes (document totals unchanged): 2+2+1 → [400, 400, 200] (was [500, 500, 0]); 1+1+1 →
  [166, 166, 168] (was [250, 250, 0]); acceptance 1 coca + 2 sprite → line discounts [166, 334], items [3834, 7666],
  total 11500 (was [250, 250], [3750, 7750]); offline payload in `LaVentaOfflineDeFamiliaYConComboCobraComoElServidorYNoSeAudita`
  sends 166/334.
- F3: grid cells are collapsed (`Celdas`): a row equal to the previous kept row in every column is dropped, and within a
  row a column equal to the previous kept one. The device picks the last row and last column that fit, so dropped cells
  are exactly what it already picked; the first column always stays. New cap `MaximoDeCeldasDeFamilia` = 128 on the
  collapsed grid with breakpoints; over it the existing safe fallback (`SinLaPrioridadEnCompetencia`, thresholds only,
  extra row at Qf0 + 0.001) applies, never under-charging. The fallback grid itself has no cap (its size is the
  number of thresholds, as before). The fallback stays coarse (whole competing tier) by design: dropping only part of
  the tier can promote a base with more discount than the server.
  Existing grid tests changed only in the listed rows/columns (collapsed: e.g. rows 2, 2.501, 4, 5.001, 6, 7.501, 8
  instead of 12 with 2.5, 5, 7.5, 10, 10.001); their parity loops are unchanged and green.
- RED: F1 domain tests red before the fix (2 failed); F2 domain tests red before the fix (3 failed); F3 test
  `ColapsadaUnaGrilla…` would produce 168 cells uncollapsed (proven by the mutation below). TS F2 tests were written
  after the TS port; their RED evidence is the mutations below.
- Mutation evidence (each reverted, green after): resolver common-before-strict step skipped → 2 red
  (`ConElMismoDescuentoDeLineaYUnitarioGanaLaComun(1,2)`, `ConElMismoDescuentoYLaSoloMultiplos…`); old `Termina`
  (`estricta.Id > otra.Id`) → curve parity test red; remainder loop never runs → 3 red (`LaFamiliaRepartePesosEnteros…`,
  `NingunaLineaDeFamiliaQuedaDebajoDeLaComun…`, `ElRestoNoCubreMasUnidades…`); remainder cap removed →
  `ElRestoNoCubreMasUnidades…` red; same two TS mutations → 1 and 4 TS tests red; row and column collapse disabled → 5
  red incl. `ColapsadaUnaGrilla…`; row collapse alone disabled → 3 red; cell cap bypassed →
  `PasadoElTopeDeCeldasRigeElRecurso…` red.
- Checks: `dotnet build` 0 errors (51 warnings, NuGet advisories and pre-existing, none in touched files); Domain
  2176/2176; Application 1548/1548 (structural guards included); web `tsc -b --noEmit` exit 0, `vitest run src/pos`
  982/982. Integration filter (`OfertasSoloMultiplosTests` and the other Ofertas/Instantanea/Offline suites): PENDING —
  another session's testcontainers were running on the Docker daemon.
- Outside the allowed surfaces, needs the parent: `docs/10-modelo-de-datos/03-articulos--ofertas.md` lines 77 (family
  remainder "en el orden de las líneas") and 81 (tie-break: add "la común antes que la solo múltiplos"), and
  `09-dispositivos--9.3-venta-offline-instantanea-de.md` line 163 (the non-terminating case is now only strict vs strict
  of another group size) plus the grid collapse/cap. Integration test edits were in `tests/Ways.IntegrationTests/OfertasSoloMultiplosTests.cs`
  (the listed `tests/Ways.Api.IntegrationTests/Ofertas/**` does not exist).
- POS reaches registers only with a `pos-v*` release (F2 changes the device's family split).

## F4 evidence (2026-10-31)

- Route: delegated direct (writer). Files: `pos/precioDeLinea.ts` (pure helper), `pos/PrecioUnitarioDeLinea.tsx`,
  their tests (9 + 2), `paginas/Pos.tsx` (price cell uses the component; added in scope by the parent).
- Partial coverage shows the regular unit price plus the split, e.g. `2 × $ 3.750,00 + 1 × $ 4.000,00`; full
  coverage keeps the previous display. Covered units are derived from `descuentoDeLinea`; a non-integer result
  (a family line under the F2 pro-rata split, e.g. 166 / 250) keeps the previous display by design.
- docs/10 updated by the parent: family split, tie-break, non-terminating case, grid collapse and cap.
