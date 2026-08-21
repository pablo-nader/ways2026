# Tasks: Stage 18 — Etiquetas, carteles y consulta de precios

> `spec.md` (4 delta specs: `etiquetas-y-carteles`, `consulta-de-precios`, `articulos`,
> `operacion-de-pos`) and `design.md` ran in PARALLEL per `state.yaml`. Where they diverge, or
> where `design.md` registered an open tension (T1-T5, `design.md:447-476`), this phase arbitrates
> in favor of ONE party with a cited rationale — precedent: stage-17's own reconciliation block,
> itself carrying forward stage-15's tensions amendment.

## Reconciliaciones

1. **T1 — `copias` stays OUT of the request DTO, ratified in favor of `design`.**
   `design.md:55` (decision 4) keeps `POST /api/etiquetas/datos` to exactly `idPuntoVenta`,
   `idListaPrecio`, `idsArticulo[] | filtro` (`design.md:186-187`, `Contratos.cs`), expanding rows
   into cells client-side via `expandirCeldas(filas, copias)`. Checked against both texts: the
   proposal's own endpoint-input enumeration (decision 12, `proposal.md:356-360`) never lists
   `copias`, and its decision 7 (`proposal.md:252-265`) places copies as a **screen** concern ("per
   row", "aplicar a todos", the preview line) — never as a field the server receives. `spec.md`'s
   "Copies Per Row And The 200-Artículo Cap" requirement (`etiquetas-y-carteles/spec.md:167-183`)
   says copies are selectable 1-99 on screen and that the **response** carries `truncado`; it never
   asserts a request-side `copias` field either. **No actual contradiction exists between the two
   artifacts** — design's letter is simply the more explicit one, and its rationale
   (`dto-contract-honesty` rule 1: a copy is a presentation multiplier, not data; sending it to the
   server would let a caller ask for the same row 99 times) is the most reasoned text on file.
   **Binding**: `SolicitudDeEtiquetas` carries no `copias` field (task 2.12); `expandirCeldas` lives
   in the web layer only (task 3.3).

2. **T2 — the sharpened `A4-2x7` geometry (`design.md:146`) is the implementation truth; the
   rounded "99×38" in `proposal.md`/`spec.md` is a display label, not a second tuple.** Ratified in
   favor of `design`. The proposal fixes the four **ids** and their rounded headline dimensions
   (`proposal.md:29-33`); `spec.md`'s "Fixed Format Descriptors As Data" requirement
   (`etiquetas-y-carteles/spec.md:15-23`) repeats the same rounded label for identification only —
   neither text asserts the mm values are exact to the tenth. Design's table (`design.md:143-148`)
   sharpens `A4-2x7` to **99.1×38.1 mm**, margins **15.15/4.65 mm**, gutter **2.5 mm** — arithmetic
   that closes the A4 page exactly: `2×99.1 + 2×4.65 + 2.5 = 210`, `7×38.1 + 2×15.15 = 297`. The
   rounded proposal numbers do not close (`2×99 = 198 ≠ 210` with no margin/gutter accounted) — a
   descriptor whose numbers do not close the page is a geometry bug the spike would catch
   expensively, on paper. **Binding**: `formatos.ts` ships design's exact tuple (task 1.6); the
   descriptor's `id`/`nombre` stay `A4-2x7` and the UI may still *display* "99×38 mm" as a rounded
   label — no drift between the id and the geometry it drives.

3. **T3 — `Excluidos` is `IReadOnlyList<ArticuloExcluido>` (identity-bearing), ratified in favor of
   `design`.** `spec.md`'s "Artículo Without Vigent Price Never Prints, Exclusion Counted"
   requirement (`etiquetas-y-carteles/spec.md:132-148`) only asserts a **count** ("the screen
   reports 3 excluded"). `design.md:197` (`Contratos.cs`) and its own tension note
   (`design.md:459-461`, T3) go further: the selection list must **mark** which rows were dropped,
   and "a count cannot mark a row, so the DTO carries identity." Not a conflict — identity is a
   strict superset that satisfies the spec's count requirement (`Excluidos.Count`) while also
   satisfying decision 6's fuller text (`proposal.md:237-249`, "such rows appear in the selection
   list marked 'sin precio en esta lista'"), which a count-only reading cannot support alone.
   **Binding**: `DatosDeEtiquetas.Excluidos` ships as `IReadOnlyList<ArticuloExcluido(IdArticulo,
   CodigoInterno, Nombre, Motivo)>` (task 2.12); the screen derives the displayed count from
   `.Count`, never a parallel server field.

4. **AND semantics of the three combined `GET /api/articulos` filters — confirmed explicit, no
   inference left standing.** `state.yaml` flags this as an inference the spec left implicit, but
   `articulos/spec.md:32-35` already states it in words: "Filters combine as AND" ("GIVEN artículos
   matching `idArea` and a disjoint set matching `idMarca` … THEN only artículos matching both
   filters are returned"). Checked against `design.md:219-224`: `ListarAsync` gains
   `idArea/idCategoria/idMarca`, "each guarded by its own `if (… is { } x)`" appending an
   **independent** `.Where(...)` clause to the same `IQueryable` — LINQ's composition of sequential
   `Where` calls is conjunctive by construction, so AND is not a choice the implementation makes,
   it is what the described shape produces. **Binding, made explicit for `mutation-proof-tests`
   rule 3**: the three filters (plus `busqueda`) are four independent conjuncts; slice 2's test
   matrix (tasks 2.7-2.11) enumerates each alone, every pairwise combination, all three/four
   together, and the fully-absent (byte-identical regression) case — mutation target 26 kills any
   single deleted `if` guard via its own asymmetric-seed test, never a shared fixture that could
   mask a missing conjunct.

5. **Spike task split — (a) autonomous build vs. (b) owner-blocked physical verdict.** The spike's
   two exit criteria are NOT equally autonomous. **E1 (geometry, ±1.0 mm / ±1.5 mm)** requires the
   owner's actual printer and the reference die-cut A4 sheet in hand (`design.md:98-102`, "**This
   task requires the owner's printer and paper**; it is human-in-the-loop by nature") — no agent in
   this pipeline can execute it. **E2 (non-regression)** does not share that constraint:
   `git diff --exit-code src/Ways.Web/src/estilos/impresion.css`, the existing
   `CajaZ.test.tsx`/`CuentaCorriente.test.tsx` suites, and a "Guardar como PDF" page-box comparison
   (achievable via a headless/dev browser print, not the owner's hardware) are all runnable in this
   environment. **Split, binding for slice 1**: **1a (autonomous)** builds the calibration-mode
   `HojaDeEtiquetas`, the `spike-alineacion.md` scaffold, and completes E2's three proofs — this
   portion documents its own cut and the slice-1 PR proceeds on it plus the formats/renderer/css
   work. **1b (BLOCKED ON OWNER)** is the physical print run and E1's numeric verdict, registered
   as a task that stays unchecked until the owner performs it. Per `design.md:401-414` (binding
   verify criterion 4), **slice 3 MUST NOT open until E1 is recorded PASS** — unchanged by this
   split, only which task blocks whom is now explicit. **Slice 4 is unaffected**: the proposal's
   own plan (`proposal.md:521-523`) and design's slicing table (`design.md:387-389`) already say
   slice 4 "depends on nothing"; this reconciliation only makes the spike's internal a/b split
   explicit so a blocked (b) does not also block (a) or slice 4.

## Binding Verify Criteria (all slices)

Carried verbatim from `design.md:401-433` and the proposal's DB gate (`proposal.md:383-398`,
`state.yaml` `db_gate_approval`). None of these may be relaxed by any slice.

1. **Zero migrations**: no new file under `src/Ways.Infrastructure/Persistencia/Migraciones/`;
   `dotnet ef migrations has-pending-model-changes` clean, checked at every slice's gate task.
2. **Zero index changes**: `pg_indexes` unchanged from `main`; the three filters ride the existing
   `ix_articulos_area`/`ix_articulos_categoria`/`ix_articulos_marca` (`ArticuloConfiguration.cs:129-131`).
3. **`src/Ways.Api/Seguridad/Politicas.cs` and `src/Ways.Web/src/estilos/impresion.css` do not
   appear in the stage's diff** — `git diff --exit-code` on both against `main`. No file under
   `src/Ways.Infrastructure/` appears either.
4. Spike verdict recorded with numbers in `spike-alineacion.md`, **PASS on both E1 and E2 before
   slice 3 opens**. A FAIL stops the stage; the QuestPDF licence question escalates to the owner,
   never resolved inside a phase.
5. `CajaZ`/`CuentaCorriente`: existing tests green **and unedited**, plus the recorded manual PDF
   page-box comparison (E2).
6. `GET /api/articulos` without the new filters: byte-identical items/ordering/paging/clamp to
   `main`; `idCategoria` on a parent returns descendants (three-level fixture).
7. `soloConOfertaVigente = true` equals `Aplicadas.Count > 0` at `cantidad = 1`, same
   lista/momento — a divergence test against the live resolver, never a re-implementation.
8. The serialized `POST /api/etiquetas/datos` response carries no `costo`/`costoLista`/
   `costoNominal`/`descuentoProveedor`/`idProveedorHabitual`/`proveedor`/`margen` property.
9. Authorization matrix: Vendedor/Supervisor/Admin 200, Root 403 on `POST /api/etiquetas/datos`;
   `SuperficieDeAutorizacionTests` green with exactly **one** new allowlist entry.
10. Command budget: 1-artículo and 200-artículo requests issue the **same** EF command count, ≤ 11.
11. Mutation evidence recorded in each slice's PR body for every target row it owns; structural
    (**S**) rows record the file/state assertion, not a runtime failure.
12. Domain/Application/Integration/vitest suites green; colocated descriptor tests for every new
    pure web helper and both new screens (`web-descriptor-tests`).

---

## Suggested Work Units

| Unit | Goal | Likely PR | Focused test command | Runtime harness | Rollback boundary |
|---|---|---|---|---|---|
| 1 | Spike (1a autonomous + 1b owner-blocked) + `DescriptorDeFormato` + 4 tuples + `HojaDeEtiquetas` + `etiquetas.css` | PR 1 | `npm run test -- formatos\|HojaDeEtiquetas` (Vitest) | Vitest + RTL; E1 needs the owner's printer/paper, N/A in this environment | `git revert`: no consumer exists yet; `impresion.css` absent from the diff |
| 2 | `ServicioDeEtiquetas` + `Contratos.cs` + `POST /api/etiquetas/datos` + 3 filters on `ListarAsync` + `ConstruirDescendientes` | PR 2 | `dotnet test --filter FullyQualifiedName~ServicioDeEtiquetas\|FullyQualifiedName~CadenaDeCategorias` | Real Postgres, `DbCommandInterceptor` command-count harness | `git revert`: 3 params optional/unread by any caller; endpoint has no consumer until slice 3 |
| 3 | `Etiquetas.tsx` — filters, multi-select, copies, selectors, preview, print | PR 3 | `npm run test -- Etiquetas` (Vitest) | Vitest + RTL | `git revert` removes one route/menu entry; API still serves the shape |
| 4 | `ConsultaPrecios.tsx` — scan, two-call resolution, idle reset | PR 4 | `npm run test -- ConsultaPrecios` (Vitest) | Vitest + RTL, `vi.useFakeTimers` | Same — one route, one menu entry |

Total ≈ **~1620 lines naive** (`proposal.md:538`). `Decision needed before apply: No` — `auto-chain`
+ `stacked-to-main` already resolved in `state.yaml`.

---

## Slice 1: Spike (calibración) + formatos + HojaDeEtiquetas + etiquetas.css (PR 1)

**Branch**: `feat/stage18-slice1-spike-y-formatos`. **Start**: `main`. **Finish**: 1a complete
(calibration renderer + `spike-alineacion.md` scaffold + E2's three proofs green) + the four
`DescriptorDeFormato` tuples + `HojaDeEtiquetas.tsx` (grid/cell/poster, `normal`+`calibracion`) +
`etiquetas.css` (named page) + geometry/derived/strike/`d-print-none` tests green. 1b (E1's
physical verdict) is registered as a separate, unchecked, owner-blocked task — see Reconciliación
5. **No consumer exists yet** (`Etiquetas.tsx`/`ConsultaPrecios.tsx` ship in slices 3/4).
**Rollback**: `git revert`; `impresion.css` is not in this slice's diff (verify criterion 3), so
the only shared surface is provably untouched. **Skills required**: `mutation-proof-tests` (targets
1-8 below; no new multi-conjunct request guard in this slice, so rule 3's conjunct enumeration does
not trigger here — the strike rule is a single boolean formula, not a guard chain),
`web-descriptor-tests` (colocated tests for `formatos.ts`, `HojaDeEtiquetas.tsx`). **Done** = 1a
tests pass + `judgment-day` clean round + PR merged; 1b remains open until the owner runs it, and
gates slice 3 only (verify criterion 4), not this PR.

- [x] 1.1 **(1a, autonomous)** Create `src/Ways.Web/src/etiquetas/HojaDeEtiquetas.tsx` in
  `modo="calibracion"`: 0.2 mm hairline box per nominal cell, 6 mm registration cross at each
  cell's top-left origin, `f{row}c{col}` label, a 200 mm horizontal / 280 mm vertical 1 mm-tick
  ruler, a 100.0 × 100.0 mm labelled scale square, driven by the **same** descriptor tuple the real
  sheet uses. *(design.md:67-84, mutation target 6)*
- [x] 1.2 **(1a, autonomous)** Same file, `d-print-none` print-settings instruction block: A4,
  100% scale, "fit to page" OFF, margins none, background graphics ON. *(design.md:84, mutation
  target 8)*
- [x] 1.3 **(1a, autonomous)** Create
  `openspec/changes/stage-18-etiquetas-y-consulta/spike-alineacion.md` — the recording scaffold:
  one row per run (date, browser+version, OS, printer make/model, sheet reference, print
  scale/margin settings, scale-square measurement, per-cell deviation at the 4 corners + centre +
  both last-row ends, last-row cumulative drift, E1 verdict, E2 verdict, evidence path). Empty
  rows, ready for the owner's physical run. *(design.md:98-102)*
- [ ] 1.4 **(1b, BLOCKED ON OWNER — physical printer + reference die-cut A4 sheet required, NOT executed by this apply pass)** Print
  the `A4-3x8` calibration grid at 100% scale on the reference sheet, on at least one target
  browser; measure and record **E1**: every cell origin within ±1.0 mm of nominal (4 corners +
  centre + both last-row ends), last-row cumulative drift within ±1.5 mm, scale-square
  precondition 100.0 ± 0.3 mm (void the run otherwise). Append the row to `spike-alineacion.md`.
  **FAIL path**: STOP — no library swap; escalate the QuestPDF licence question to the owner as a
  blocking commercial decision, never resolved inside this phase. *(proposal.md:147-161,
  design.md:86-92, Reconciliación 5)*
- [ ] 1.5 **(1a, autonomous, PARTIAL — 2/3 proofs done, see Deviation 1 below — kept unchecked, not silently marked done)** E2 non-regression, three proofs, appended to `spike-alineacion.md`'s
  E2 row: `git diff --exit-code src/Ways.Web/src/estilos/impresion.css` clean; `CajaZ.test.tsx` /
  `CuentaCorriente.test.tsx` green and **unedited**; a "Guardar como PDF" page-box comparison of
  each view from `main` vs. the branch, same browser/settings. *(design.md:93-97, mutation target
  1 partial, verify criterion 5)*
- [x] 1.6 Create `src/Ways.Web/src/etiquetas/formatos.ts` — `CampoDeCelda` union,
  `DescriptorDeFormato` (flat, function-free, frozen record), the four tuples using the
  **sharpened** `A4-2x7` geometry (99.1×38.1 mm, margins 15.15/4.65 mm, gutter 2.5 mm —
  Reconciliación 2), plus `celdasPorHoja`/`contarHojas` as pure derived helpers, never stored
  fields. *(design.md:111-148, mutation targets 3, 4)*
- [x] 1.7 [P] `formatos.test.ts` — for each of the four descriptors, assert the emitted mm values
  equal the tuple and `columnas × filas` equals the declared per-sheet count (24/14/1/2).
  *(design.md:315, mutation target 3)*
- [x] 1.8 [P] `formatos.test.ts` — `celdasPorHoja`/`contarHojas`: 24→1 hoja, 25→2, 0→0; assert no
  stored derived field exists on the type (a mutated tuple must move the derived count too).
  *(mutation targets 4, 5)*
- [x] 1.9 Create `src/Ways.Web/src/etiquetas/HojaDeEtiquetas.tsx` `modo="normal"`: pure props-only
  renderer (descriptor + already-expanded `celdas` + `nombreDeLista`), emits geometry as
  `--pagina-ancho`/`--celda-ancho`/`--pitch-x`/`--margen-sup`… custom properties on
  `.hoja-de-etiquetas` (the only projection jsdom can measure); strike rule is
  `celda.ofertas.length > 0`, **never** `precioOriginal !== precioFinal`. *(design.md:162-176,
  mutation target 7)*
- [x] 1.10 [P] `HojaDeEtiquetas.test.tsx` — strike rendered **iff** `ofertas.length > 0`: a
  constructed DTO with distinct prices + empty `ofertas` (no strike) and its mirror (equal prices +
  non-empty `ofertas`, strike). *(design.md:316, mutation target 7)*
- [x] 1.11 [P] `HojaDeEtiquetas.test.tsx` — calibration-mode emits the identical geometry custom
  properties as normal-mode for the same descriptor. *(mutation target 6)*
- [x] 1.12 [P] `HojaDeEtiquetas.test.tsx` — the `d-print-none` instruction block is present.
  *(mutation target 8)*
- [x] 1.13 Create `src/Ways.Web/src/estilos/etiquetas.css` — `@page etiquetas { size: A4; margin: 0
  }` + `.hoja-de-etiquetas { page: etiquetas }` + the grid rules; `impresion.css` stays untouched.
  *(design.md:52, mutation target 1)*
- [x] 1.14 **(S)** Test: the sheet container carries the named-page class/property — assert
  `.hoja-de-etiquetas`'s declared `page: etiquetas` (structural: jsdom does not implement `@page`).
  *(mutation target 1)*
- [x] 1.15 **GATE GUARD** — `git diff --exit-code src/Ways.Web/src/estilos/impresion.css` clean
  (re-asserted from 1.5 as the slice's own gate task); no file under `src/Ways.Infrastructure/` in
  this slice's diff. *(verify criteria 1, 3)*
- [x] 1.16 Mutation evidence recorded in the PR body for targets 1-8 (structural rows 1, 2, 8
  record the file/state assertion, not a runtime failure). *(verify criterion 11)*
- [x] 1.17 `judgment-day` round: two blind review agents, fix confirmed findings, re-judge to a
  clean round.
  **DONE by the orchestrator**: ronda 1 juez B REJECT (1 MAJOR: cruz de registro y hairline sin
  red — mutantes DOM-observables SURVIVED 6/6; 1 MINOR: ticks de 1mm faltantes) → fixes `5bc6a05`
  → re-ronda B APPROVE. Ronda 2 juez A REJECT (2 CRITICAL por LECTURA: el cuadrado de escala caía
  en una SEGUNDA hoja física — 380.2mm de flujo en una página de 297 — falseando el veredicto E1;
  y el gate E2 sin tarea exigible; + 1 WARNING de la tarea 3.1) → fixes `7e86886` (absolute
  anchors + Start del slice 3 con E1 Y E2 + tarea 3.0) → pasada acotada B APPROVE (razonamiento
  del layout físico confirmado) + re-ronda A APPROVE (cero hallazgos). Ronda limpia.
- [x] 1.18 Open PR #1 `feat/stage18-slice1-spike-y-formatos`, merge to `main` after a clean
  `judgment-day` round. **Note in PR body**: task 1.4 (E1's physical verdict) is open and
  owner-blocked; slice 3 will not start until it is recorded PASS.
  **DONE by the orchestrator**: PR #156, merged `e8e9e4b` after the clean round (see 1.17).
  Task 1.4 (E1 physical measurement) remains OPEN, owner-blocked — gates slice 3 only.

### Work Unit Evidence (Slice 1)

| Evidence | Value |
|---|---|
| Focused test command and exact result | `npx vitest run` (`src/Ways.Web`) — **57 test files passed, 926 tests passed** (0 failed), including the two new colocated files `formatos.test.ts` (11 tests) and `HojaDeEtiquetas.test.tsx` (6 tests). One pre-existing/unrelated flake observed in `Vencimientos.test.tsx` on the full-suite run (`Not implemented: navigation to another Document`, a known jsdom limitation — confirmed pre-existing: the file is untouched by this slice's diff, and re-running it in isolation is 9/9 green) |
| Runtime harness command/scenario and exact result | N/A — this slice ships zero fetch/zero backend consumer by design (`HojaDeEtiquetas` is pure props-only, "no consumer exists yet" per the slice's own Finish criterion). `npx tsc -b` clean, `npm run lint` (oxlint) clean (4 pre-existing warnings in untouched files), `dotnet build` clean (confirms zero backend impact — no `.cs` file touched) |
| Rollback boundary | `git revert` the slice-1 commit(s): `src/Ways.Web/src/etiquetas/**`, `src/Ways.Web/src/estilos/etiquetas.css`, `openspec/changes/stage-18-etiquetas-y-consulta/spike-alineacion.md`, and the one-line `css: true` addition to `src/Ways.Web/vite.config.ts`'s `test` block. `impresion.css` and every `.cs` file are untouched (`git diff --exit-code` clean on both, confirmed). No component imports `HojaDeEtiquetas`/`formatos.ts` yet, so the revert removes zero consumers |

### Mutation Evidence (targets 1-8, verify criterion 11)

| # | Slice | Clause | Mutation applied | Test that failed | Reverted |
|---|---|---|---|---|---|
| 1 **S** | 1 | `@page etiquetas` in `etiquetas.css` + `page: etiquetas` on `.hoja-de-etiquetas` | Deleted `page: etiquetas;` from the `.hoja-de-etiquetas` rule | `HojaDeEtiquetas.test.tsx` › "el contenedor lleva la clase `hoja-de-etiquetas` que etiquetas.css usa para declarar `page: etiquetas`" — FAILED (regex on the raw stylesheet text no longer matches) | Yes, reverted, test green again |
| 2 **S** | 1 | `impresion.css` untouched | N/A — asserted via `git diff --exit-code src/Ways.Web/src/estilos/impresion.css` against `main`, run and confirmed clean (0 diff, file byte-identical); the file was never written by this slice so there is no local edit to mutate/revert | `git diff --exit-code` (shell, not a vitest test) | N/A |
| 3 | 1 | Each geometry number of each of the four descriptors | Changed `A4_3X8.celdaMm.alto` from `37.0` to `38.0` | `formatos.test.ts` › "A4-3x8: celda 70.0×37.0 mm..." — FAILED (`expected 38 to be 37`) | Yes |
| 4 | 1 | `celdasPorHoja`/`contarHojas` derived, never stored | Temporarily hardcoded `celdasPorHoja` to always return `24` regardless of input | `formatos.test.ts` › "un descriptor mutado (celdaPorHoja distinto) mueve el conteo derivado" — FAILED (`expected 24 to be 10`) | Yes |
| 5 | 1 | `contarHojas = ceil(celdas / porHoja)` | Changed `Math.ceil` to `Math.floor` | `formatos.test.ts` › "contarHojas: 25 etiquetas en A4-3x8 ⇒ 2 hojas" — FAILED (`expected 1 to be 2`) | Yes |
| 6 | 1 | The calibration grid driven by the same descriptor as the real sheet | Passed `{ ...A4_3X8, celdaMm: { ancho: 50, alto: 20 } }` to the calibration branch only, diverging from the normal-mode descriptor | `HojaDeEtiquetas.test.tsx` › "modo=\"calibracion\" emite exactamente los mismos custom properties..." — FAILED (`--celda-ancho: 50mm` ≠ `70mm`) | Yes |
| 7 | 1 | The strike rule `ofertas.length > 0` | Changed the strike condition to `celda.precioOriginal !== celda.precioFinal` | Both new tests in the "regla de tachado (mutation target 7)" block — FAILED (distinct-price/empty-ofertas case wrongly struck; equal-price/with-oferta case wrongly not struck) | Yes |
| 8 **S** | 1 | The print-settings block's `d-print-none` | Removed the `d-print-none` class from the instruction block | `HojaDeEtiquetas.test.tsx` › "modo=\"calibracion\" muestra el bloque d-print-none..." — FAILED (`toHaveClass('d-print-none')`) | Yes |

### Deviations from Design

1. **Task 1.5 (E2) is PARTIAL, not complete.** Two of its three proofs are green and deterministic
   (`git diff --exit-code` on `impresion.css`; `CajaZ.test.tsx`/`CuentaCorriente.test.tsx` green and
   unedited — both confirmed inside the full `npx vitest run`). The third proof — a "Guardar como
   PDF" page-box comparison of `main` vs. the branch inside a real browser session — was **not**
   executed. It needs an authenticated, fully-running app (API + Postgres) to render `CajaZ`/
   `CuentaCorriente` with real data, and the repo has no E2E harness; installing one (Playwright/
   Puppeteer) would violate the stage's own binding "no new web dependency" constraint
   (`proposal.md:475`). Practically, the risk this proof exists to catch is near-zero for slice 1
   specifically: `HojaDeEtiquetas`/`etiquetas.css` have **zero consumers** in the app yet (verified —
   `hoja-de-etiquetas` only appears in the three files this slice created), so the new named page
   never actually coexists with the global `@page` rule inside a running app until slice 3 mounts
   `Etiquetas.tsx`. Recommendation: re-run this specific proof as part of slice 3's own gate, when
   `HojaDeEtiquetas` gets its first real consumer. Recorded in `spike-alineacion.md`'s E2 section,
   not silently dropped.
2. **`padExternoMm` for `CARTEL-A4`/`CARTEL-A5` is an assumption, not a design-given value.**
   `design.md:143-148`'s table only publishes `padExternoMm` for the two label formats (`A4-3x8` =
   5, `A4-2x7` = 3); the two poster formats have no published value. Set to `5` for both (matching
   the most conservative published value) since posters already carry a generous 10 mm margin that
   likely absorbs the printer's non-printable edge, but this is a judgment call, not a cited number
   — flagged for `judgment-day`/owner review.
3. **`campos`/`escalaDePrecio` per descriptor are not design-given values.** `design.md` defines the
   `DescriptorDeFormato` shape and cites these fields' *purpose* but never their per-format values.
   All four descriptors ship the same full `CampoDeCelda` set (`nombre`, `precioFinal`,
   `precioOriginal`, `codigo`, `unidadVenta`, `nombreDeOferta`); `escalaDePrecio` uses ascending,
   undocumented placeholder values (1.4/1.6/3.5/3.0). Neither is exercised by a mutation target or a
   binding test — slice 3 (`Etiquetas.tsx`, the first real consumer) is the natural place to revisit
   these once actual cell layouts are on screen.
4. **`FilaDeEtiqueta` and `OfertaAplicada` types**: `FilaDeEtiqueta` is defined locally in
   `HojaDeEtiquetas.tsx` (exported) rather than in `api/etiquetas.ts`, because that file does not
   exist yet — it is slice 3's task 3.1 ("DTO mirrors of `SolicitudDeEtiquetas`/`DatosDeEtiquetas`/
   `FilaDeEtiqueta`/`ArticuloExcluido`"). `OfertaAplicada` is reused **verbatim** from the existing
   `api/tipos.ts` (design.md:204: "`Ofertas` reuses `OfertaAplicadaDto` verbatim"). Slice 3 should
   import `FilaDeEtiqueta` from `etiquetas/HojaDeEtiquetas.tsx` instead of redefining it, to avoid two
   competing shapes.
5. **`vite.config.ts` gained one line (`css: true` in the `test` block).** Needed so Vitest stops
   stubbing `.css` imports (including `?raw`) as empty strings — required for task 1.14's structural
   test to read the real `etiquetas.css` text. Scoped to test config only; does not change any
   production build output (`build.outDir`/`sourcemap` untouched).

### Issues Found

None beyond the deviations above.

### judgment-day Slice 1, ronda 1 — juez B (1 MAJOR + 1 MINOR)

| # | Severidad | Hallazgo | Fix | Ciclo mutación |
|---|---|---|---|---|
| 1 | MAJOR | El test de la grilla de calibración (`HojaDeEtiquetas.test.tsx`) no asertaba la cruz de registro (`<span className="cruz-registro">`, `HojaDeEtiquetas.tsx:136`) ni la clase `celda-calibracion` (hook del hairline de 0.2mm) — ambos mutantes sobrevivían 6/6, y `design.md:79-80` exige ambos elementos | `data-testid={\`cruz-registro-f\${fila}c\${columna}\`}` agregado a la cruz (mismo patrón que `cuadrado-de-escala`/`regla-horizontal`); test asserta conteo exacto de cruces == columnas×filas (24, regla 12b), presencia por celda, `toHaveClass('celda-calibracion')`, y que el modo NORMAL no renderiza ninguno de los dos (discriminante de modo) | (a) borrado el `<span className="cruz-registro">` → RED (`getAllByTestId` no encontró coincidencias) → revertido → verde. (b) `celda celda-calibracion` → `celda` → RED (`toHaveClass('celda-calibracion')` falló) → revertido → verde |
| 2 | MINOR | `design.md:82` especifica "1 mm ticks, 10 mm labels"; `ReglaHorizontal`/`ReglaVertical` solo dibujaban marcas cada 10mm | Ticks de 1mm agregados en ambas reglas (201 en la horizontal, 281 en la vertical), diferenciados por clase `regla-tick-mayor`/`regla-tick-menor` (label solo en los mayores, cada 10mm); CSS distingue alto/ancho por clase; test nuevo asserta conteo exacto de ticks totales y por clase | Paso de 1mm mutado a 2mm en la regla horizontal → RED (`toHaveLength(201)` recibió 101) → revertido → verde |

### judgment-day Slice 1, ronda 2 — juez A (2 CRITICAL + 1 WARNING)

| # | Severidad | Hallazgo | Fix | Ciclo mutación |
|---|---|---|---|---|
| 1 | CRITICAL | `ReglaHorizontal` + `ReglaVertical` (`position: relative`) + `CuadradoDeEscala` (sin `position`) vivían en FLUJO NORMAL dentro de `.hoja-de-etiquetas` (297mm de alto) — 0.2mm + 280mm + 100mm = 380.2mm, así que el cuadrado de escala caía en una segunda página impresa donde el dueño puede no verlo, arriesgando un veredicto E1 falso (la precondición de nulidad ±0.3mm exige el cuadrado EN LA MISMA hoja) | `etiquetas.css`: las tres reglas pasan a `position: absolute`. `.regla-horizontal`/`.regla-vertical` anclan en `top:0; left:0` (coincide con `design.md:82`: "a horizontal 200mm ruler (top) and a vertical 280mm ruler (left)"). `.cuadrado-de-escala` ancla en `right:0; bottom:0` — desviación registrada: el design no fija su ubicación, así que se eligió la esquina opuesta a las reglas (sin número mágico de mm, funciona para cualquier `paginaMm`); puede superponerse a la grilla porque el hairline de celda y el contorno del cuadrado son distinguibles a ojo y el design solo exige poder medirlo (design.md:83). Test estructural nuevo en `HojaDeEtiquetas.test.tsx` (mismo patrón que el test de named page existente): asserta `position:\s*absolute` en los bloques CSS de `.regla-horizontal`/`.regla-vertical` y `.cuadrado-de-escala` | Borrado `position: absolute` del bloque `.cuadrado-de-escala` → RED (`toMatch(/position:\s*absolute/)` falló) → `git checkout --` → revertido → verde |
| 2 | CRITICAL | El criterio vinculante 4 exige "PASS on both E1 and E2 before slice 3 opens", pero el **Start** de slice 3 solo nombraba la tarea 1.4 (E1); la tercera prueba de E2 (comparación de page-box "Guardar como PDF" de `CajaZ`/`CuentaCorriente`, diferida en `spike-alineacion.md` §E2) no tenía ninguna tarea dueña en slice 3 | **Start** de slice 3 enmendado para exigir 1.4 (E1) **y** 1.5 (E2 completo, las tres pruebas), citando el criterio vinculante 4. Nueva tarea **3.0** (gate, antes de 3.1): re-correr la tercera prueba de E2 — PDF de `CajaZ`/`CuentaCorriente` antes y después de montar el primer consumidor real de `HojaDeEtiquetas`, comparación de page-box — y registrar el resultado en `spike-alineacion.md` §E2, cerrando la tarea 1.5 | N/A — cambio de documentación (`tasks.md`), no de código ejecutable; el gate en sí se verifica en slice 3 cuando 3.0 corra |
| 3 | WARNING | La tarea 3.1 seguía diciendo "DTO mirrors of `.../FilaDeEtiqueta/...`", contradiciendo la propia recomendación de la desviación 4 de slice 1 ("Slice 3 should import `FilaDeEtiqueta` from `etiquetas/HojaDeEtiquetas.tsx` instead of redefining it") | Tarea 3.1 enmendada: `FilaDeEtiqueta` se **importa** de `etiquetas/HojaDeEtiquetas.tsx` (re-exportable desde `api/etiquetas.ts` si algún call site lo necesita ahí), nunca se redefine, citando la desviación 4 de slice 1 | N/A — cambio de documentación (`tasks.md`) |

---

## Slice 2: `ServicioDeEtiquetas` + endpoint + 3 filtros + tests (PR 2)

**Branch**: `feat/stage18-slice2-datos-de-etiqueta`. **Start**: `main` (independent of slice 1 —
`design.md:387-389` confirms slices 1 and 2 "are also mutually independent and may interleave").
**Finish**: `ConstruirDescendientes` on `CadenaDeCategorias`; three optional filters +
`TamanioMaximoDePagina` on `ServicioDeArticulos`/`ArticulosEndpoints`; `ServicioDeEtiquetas` +
`Contratos.cs` + `EtiquetasEndpoints` + DI + the one `SuperficieDeAutorizacionTests` allowlist
entry; cap/`truncado`/exclusion; no-cost, divergence, budget, and unfiltered-regression tests all
green. **Rollback**: `git revert`; the three query params are optional and unread by any existing
caller, and the endpoint has no consumer until slice 3. **Skills required**: `dto-contract-honesty`
(THE invariant of this stage — the exposure clause on `FilaDeEtiqueta`/`DatosDeEtiquetas`, and the
`copias`-absent contract from Reconciliación 1), `mutation-proof-tests` v1.1 rule 3 — **two new
multi-conjunct guards this slice, both enumerated below**. **Done** = tests green + `judgment-day`
clean round + PR merged.

**Guard enumeration (rule 3)**:
- **`idsArticulo`/`filtro` selection guard** — 4 cells: ids-only (proceed), filtro-only (proceed),
  both present (`400 seleccion_ambigua`), neither present (`400 seleccion_requerida`). All four
  MUST have a dedicated test. *(design.md:63, 182-187, mutation targets 20, 21)*
- **`ListarAsync`'s four optional filters** (`busqueda`, `idArea`, `idCategoria`, `idMarca`) — each
  independently present/absent, composing as AND (Reconciliación 4): every filter alone, every
  pairwise combination, all four together, and all four absent (byte-identical regression). Each
  filter's own `if (… is { } x)` guard is a separate conjunct (mutation target 26).

- [x] 2.1 Modify `src/Ways.Domain/Ofertas/CadenaDeCategorias.cs` — add
  `ConstruirDescendientes(idCategoria, padrePorCategoria)`, same class, same one-query map, same
  `ReglaDeCategorias.ProfundidadMaxima` bound. *(design.md:207-215, mutation targets 15, 16)*
- [x] 2.2 [P] `CadenaDeCategoriasTests` — three-level forest: leaf ⇒ itself only; root ⇒ whole
  subtree; sibling subtree never leaks; corrupt cycle terminates (the visited-gate is the real
  terminator — `ProfundidadMaxima` is depth-in-defense for the ≤3 contract, not the cycle-safety
  mechanism).
  *(design.md:314, mutation targets 15, 16)*
- [x] 2.3 [P] `CadenaDeCategoriasTests` — duality property test: `d ∈ ConstruirDescendientes(c) ⟺
  c ∈ ConstruirAncestros(d)` over every pair of the fixture. *(design.md:212, mutation target 17)*
- [x] 2.4 Modify `src/Ways.Application/Articulos/ServicioDeArticulos.cs` — `public const int
  TamanioMaximoDePagina = 200`; clamp becomes `Math.Clamp(tamanio, 1, TamanioMaximoDePagina)`.
  *(design.md:60, 221, mutation target 18)*
- [x] 2.5 Same file: `ListarAsync` gains `int? idArea, int? idCategoria, int? idMarca` **after**
  the existing parameters, each its own `if (… is { } x) query = query.Where(...)`; `idCategoria`
  loads the `id → id_padre` projection once and filters via `ConstruirDescendientes`. Ordering,
  paging, `busqueda` predicate untouched. *(design.md:219-224, mutation target 26)*
- [x] 2.6 Modify `src/Ways.Api/Endpoints/ArticulosEndpoints.cs` — three additive optional query
  params on the existing `MapGet("/")`. *(design.md:291)*
- [x] 2.7 [P] Integration test: `idArea` alone, asymmetric seed. *(mutation target 26)*
- [x] 2.8 [P] Integration test: `idCategoria` alone on a grandparent returns the grandchild's
  artículo — three-level fixture. *(articulos/spec.md:19-23, mutation targets 15, 26)*
- [x] 2.9 [P] Integration test: `idMarca` alone, asymmetric seed. *(articulos/spec.md:14-17,
  mutation target 26)*
- [x] 2.10 [P] Integration test: the four-way AND matrix — pairwise combinations, all three/four
  together, over disjoint seeds that would move if any filter defaulted. *(articulos/spec.md:32-35,
  Reconciliación 4, mutation target 26)*
- [x] 2.11 [P] Integration test: **byte-identical unfiltered regression** — with all three absent,
  items, order, total and paging identical to the pre-stage path over the same seed.
  *(articulos/spec.md:25-30, mutation target 27)*
- [x] 2.12 Create `src/Ways.Application/Etiquetas/Contratos.cs` — `FiltroDeEtiquetas`,
  `SolicitudDeEtiquetas` (no `Momento`, no `copias` — Reconciliación 1), `FilaDeEtiqueta` (with the
  exposure-clause doc comment naming every field it will never carry), `ArticuloExcluido`
  (identity-bearing — Reconciliación 3), `DatosDeEtiquetas`. *(design.md:178-202)*
- [x] 2.13 Create `src/Ways.Application/Etiquetas/ServicioDeEtiquetas.cs` — resolves
  `idPuntoVenta → idEmpresa`, `idListaPrecio → NombreDeLista` (404 if missing), selection (ids or
  `ListarAsync`-backed filtro), `codigos_barra` batch, `ResolverAsync(lineas @ cantidad=1,
  idEmpresa, momento=IRelojDelSistema.Ahora once)`. *(design.md:226-246)*
- [x] 2.14 Same file: XOR guard — both present ⇒ `400 seleccion_ambigua`; neither ⇒ `400
  seleccion_requerida`. *(design.md:63, mutation target 21)*
- [x] 2.15 Same file: `idsArticulo.Count > TamanioMaximoDePagina` ⇒ `400 seleccion_excedida`
  (never a silent truncation). *(design.md:63, mutation target 20)*
- [x] 2.16 Same file: `IdEmpresa` taken from the punto de venta on every `LineaDeResolucion`,
  `Cantidad = 1m` always. *(design.md:56, mutation targets 9, 10)*
- [x] 2.17 Same file: `PrecioFinal is null` ⇒ row moves to `Excluidos` (with identity), never
  emitted in `Filas`. *(design.md decision 6 restated, mutation targets 11, 12)*
- [x] 2.18 Same file: `soloConOfertaVigente` — post-filter over `Aplicadas.Count > 0`; the coarse
  candidate query does **not** join `ofertas`. *(design.md:57, mutation targets 13, 14)*
- [x] 2.19 Same file: `Truncado = pagina.Total > TamanioMaximoDePagina` from `ListarAsync`'s own
  `Total`, never a second `COUNT`/`Take(cap+1)`. *(design.md:58, mutation target 19)*
- [x] 2.20 Same file: `NombreDeLista` read from `listas_precio` by the server, never taken from the
  request. *(design.md:62, mutation target 23)*
- [x] 2.21 Same file: one `momento` resolved for the whole sheet, echoed in
  `DatosDeEtiquetas.Momento`. *(design.md:61, mutation target 25)*
- [x] 2.22 Create `src/Ways.Api/Endpoints/EtiquetasEndpoints.cs` — `POST /api/etiquetas/datos`
  under `Politicas.OperacionDePos`, nothing stacked. *(design.md:64, 293, mutation target 28)*
- [x] 2.23 Modify `Program.cs`/DI — `AddScoped<ServicioDeEtiquetas>()` + `MapearEtiquetas()`.
  *(design.md:294)*
- [x] 2.24 **(S)** Modify `tests/Ways.IntegrationTests/SuperficieDeAutorizacionTests.cs` — one
  allowlist entry `("POST", "/api/etiquetas/datos")`. *(design.md:64, 295, mutation target 29)*
- [x] 2.25 [P] Integration test: the two 400s of the XOR guard, both directions + the boundary
  case (both absent, both present). *(mutation target 21)*
- [x] 2.26 [P] Integration test: 200/201 explicit-id boundary — 200 ids ⇒ proceeds, 201 ⇒ `400
  seleccion_excedida`. *(mutation target 20)*
- [x] 2.27 [P] Integration test: `cantidad_minima = 3` fixture — the row carries **no** oferta.
  *(mutation target 10)*
- [x] 2.28 [P] Integration test: oferta scoped to another empresa ⇒ absent from a sheet printed
  for a PV of this empresa. *(mutation target 9)*
- [x] 2.29 [P] Integration test: sin-precio fixture, both directions — absent from `Filas`,
  present in `Excluidos` with identity. *(mutation targets 11, 12)*
- [x] 2.30 [P] Integration test: `soloConOfertaVigente` divergence — discriminating fixture (one
  artículo-scoped oferta in window, one categoría-scoped reaching a descendant, one out-of-window,
  one `cantidad_minima = 3`, one another-empresa) — filter result equals live resolver's
  `Aplicadas.Count > 0` exactly. *(design.md:319, mutation targets 13, 14)*
- [x] 2.31 [P] Integration test: 200/201-matching-artículos boundary via filtro — 200 ⇒
  `Truncado = false`, 201 ⇒ `Truncado = true`. *(mutation target 19)*
- [x] 2.32 [P] Integration test: `TamanioMaximoDePagina` mutation couples the listing clamp test
  **and** the `truncado` test — both fail together. *(mutation target 18)*
- [x] 2.33 [P] Integration test: raw `UPDATE` desyncing `listas_precio.nombre` to a sentinel after
  read must surface the sentinel (rule 12a). *(mutation target 23)*
- [x] 2.34 [P] Integration test: pinned-clock momento straddling an oferta's `hora_hasta` — one
  `momento` for the whole sheet. *(mutation target 25)*
- [x] 2.35 [P] Integration test: pairwise-distinct read-back of every positional field of
  `FilaDeEtiqueta`/`DatosDeEtiquetas` (rule 12b); a sibling artículo of the same tenant seeded on
  every listing test (rule 12c). *(mutation target 24)*
- [x] 2.36 [P] Integration test: **exposure clause** — the serialized response, walked
  recursively, contains no property named `costo`/`costoLista`/`costoNominal`/
  `descuentoProveedor`/`idProveedorHabitual`/`proveedor`/`margen`, matched by property **name**,
  never substring (`OfertaAplicadaDto.DescuentoUnitario` legitimately contains "descuento").
  *(design.md:318, mutation target 22)*
- [x] 2.37 [P] Integration test: authorization matrix — Vendedor/Supervisor/Admin 200, Root 403;
  tenant B never sees tenant A's artículos. *(mutation target 28)*
- [x] 2.38 Integration test: `DbCommandInterceptor` command-count harness — 1-artículo and
  200-artículo requests issue the **same** EF command count, ≤ 11 (≤ 10 on the explicit-ids
  path — amended judgment-day Slice 2 ronda 2, juez A SUGGESTION: measured after the CRITICAL
  fix, honest number, not the ≤ 9 estimated before implementation). *(design.md:253-260,
  mutation target 30)*
- [x] 2.39 **GATE GUARD** — `dotnet ef migrations has-pending-model-changes` clean; zero new files
  under `Migraciones/`; `pg_indexes` unchanged from `main` (the three filters ride existing
  indexes, asserted by definition). *(verify criteria 1, 2)*
- [x] 2.40 Mutation evidence recorded in the PR body for targets 9-30 (structural row 29 records
  the file/state assertion). *(verify criterion 11)*
- [ ] 2.41 `judgment-day` round: two blind review agents, fix confirmed findings, re-judge to a
  clean round.
- [ ] 2.42 Open PR #2 `feat/stage18-slice2-datos-de-etiqueta`, merge to `main` after a clean
  `judgment-day` round.

### Work Unit Evidence (Slice 2)

| Evidence | Value |
|---|---|
| Focused test command and exact result | `dotnet test tests/Ways.Domain.Tests --filter FullyQualifiedName~CadenaDeCategoriasTests` — 9/9 green; `dotnet test tests/Ways.IntegrationTests --filter FullyQualifiedName~ArticulosFiltrosTests` — 5/5 green; `dotnet test tests/Ways.IntegrationTests --filter FullyQualifiedName~EtiquetasEndpointsTests` — 21/21 green (19 original + 1 empresa-scoped rewrite + 1 tenant-scoping addition). **Ronda 2 (juez A)**: `EtiquetasEndpointsTests` — 23/23 green (21 + `UnIdExplicitoInexistenteDevuelve400ReferenciaInvalida` + `UnArticuloNoDisponibleEnLaEmpresaDelPvQuedaExcluidoConIdentidadYElHermanoDisponibleSale`, and `TenantBNuncaVeLosArticulosNiElPuntoDeVentaDeTenantA` rewritten for the new 400 contract); `ArticulosFiltrosTests` — 5/5 green; combined filter run — 28/28 green |
| Runtime harness command/scenario and exact result | Real Postgres via `WaysApiFixture` (Testcontainers) for every Integration test above — `db.PuntosVenta`/`db.ListasPrecio`/`db.Articulos`/`db.CodigosBarra`/`db.Ofertas` exercised end to end through `POST /api/etiquetas/datos` and `GET /api/articulos`. `dotnet build src/Ways.Api` clean; full `dotnet test tests/Ways.Domain.Tests` — **545/545** green; full `dotnet test tests/Ways.Application.Tests` — **297/297** green; full `dotnet test tests/Ways.IntegrationTests` (single complete run, no filter) — **1607/1607** green, 11m52s. `dotnet ef migrations has-pending-model-changes` (Infrastructure as both `--project`/`--startup-project`, `Ways.Api` lacks the Design package) — clean. `npx tsc -b`/vitest **N/A**: zero `src/Ways.Web` files touched this slice (`git status --short` confirms). **Ronda 2**: `dotnet build --no-incremental` clean (0 warnings/errores); `dotnet ef migrations has-pending-model-changes` re-run clean; command-budget interceptor re-measured on the explicit-ids path after the CRITICAL fix — **10** EF commands (1-artículo and 200-artículo counts equal) |
| Rollback boundary | `git revert` the slice-2 commit(s): `src/Ways.Domain/Ofertas/CadenaDeCategorias.cs` (additive method only), `src/Ways.Application/Articulos/ServicioDeArticulos.cs` (additive params + promoted constant), `src/Ways.Api/Endpoints/ArticulosEndpoints.cs` (additive query params), `src/Ways.Application/Etiquetas/**` (new), `src/Ways.Api/Endpoints/EtiquetasEndpoints.cs` (new), the one-line `DependencyInjection.cs`/`Program.cs` registrations, the one `SuperficieDeAutorizacionTests.cs` allowlist entry, and the four new/modified test files. The three new `ListarAsync` query params are optional and unread by any existing caller; the endpoint has no consumer until slice 3 |

### Guard Enumeration (rule 3, slice 2)

| Guard | Cells | Test per cell |
|---|---|---|
| `idsArticulo`/`filtro` selection XOR | ids-only (proceed) / filtro-only (proceed) / both (`400 seleccion_ambigua`) / neither (`400 seleccion_requerida`) | `Con200IdsExplicitosProcede` (ids-only proceed); `SoloConOfertaVigenteCoincideExactamenteConElResolverReal` et al. (filtro-only proceed); `AmbosSelectoresPresentesDevuelve400SeleccionAmbigua`; `NingunSelectorPresenteDevuelve400SeleccionRequerida` |
| Explicit-id cap | ≤200 (proceed) / >200 (`400 seleccion_excedida`, never truncated) | `Con200IdsExplicitosProcede`; `Con201IdsExplicitosDevuelve400SeleccionExcedida` |
| `ListarAsync` four filters (`busqueda`, `idArea`, `idCategoria`, `idMarca`) — AND, each an independent conjunct | each alone / pairwise / all four / all four absent | `FiltrarPorIdAreaDevuelveSoloLosArticulosDeEsaArea`; `FiltrarPorIdMarcaDevuelveSoloLosArticulosDeEsaMarca`; `FiltrarPorIdCategoriaEnUnAbueloDevuelveElArticuloDelNieto`; `LosCuatroFiltrosComponenComoAnd` (pairwise + all four); `SinFiltrosElListadoQuedaByteIdenticoAlCaminoPrevio` (all absent) |
| `ServicioDeEtiquetas.ComponerAsync` resolver IdEmpresa scoping | own-empresa oferta applies / other-empresa oferta excluded | `UnaOfertaDeOtraEmpresaNoApareceYUnaDeLaPropiaEmpresaSiAplica` (both directions, one fixture) |

### Mutation Evidence (targets 9-30, verify criterion 11)

| # | Slice | Clause | Mutation applied | Test that failed | Reverted |
|---|---|---|---|---|---|
| 9 | 2 | `IdEmpresa` from the PV on every `LineaDeResolucion` | `LineaDeResolucion(id, idEmpresa, …)` → `LineaDeResolucion(id, null, …)` | `UnaOfertaDeOtraEmpresaNoApareceYUnaDeLaPropiaEmpresaSiAplica` — FAILED (the own-empresa-scoped oferta stopped applying: `CoincideEmpresa(idEmpresaA, null)` is `false`) | Yes |
| 10 | 2 | `Cantidad = 1m` on every `LineaDeResolucion` | `Cantidad: 1m` → `Cantidad: 3m` | `UnaOfertaConCantidadMinimaTresNoAplicaAUnaEtiqueta` — FAILED (the `cantidad_minima=3` oferta wrongly matched) | Yes |
| 11 | 2 | `PrecioFinal is null` ⇒ `Excluidos`, never a row | Replaced the guard with an unconditional row emitting `?? 0m` for both prices | `UnArticuloSinPrecioVigenteQuedaExcluidoConIdentidadYNuncaEnFilas` — FAILED (the no-price artículo appeared in `Filas` at `$0`, `Excluidos` was empty) | Yes |
| 12 | 2 | `Excluidos` carries identity, not just a count | `ArticuloExcluido(datos.Id, datos.CodigoInterno, datos.Nombre, …)` → `ArticuloExcluido(datos.Id, string.Empty, string.Empty, …)` | Same test — FAILED (`CodigoInterno`/`Nombre` empty) | Yes |
| 13 | 2 | `soloConOfertaVigente ⇒ Aplicadas.Count > 0` | `f.Ofertas.Count > 0` → `f.PrecioFinal < f.PrecioOriginal` | Initial fixture PASSED under the mutant (confound: every offer in the fixture also happened to lower the price) — re-routed per rule 3: added an `ImporteFijo = 0m` oferta (applies, `Aplicadas.Count=1`, zero price movement) to `SoloConOfertaVigenteCoincideExactamenteConElResolverReal`; re-ran — FAILED (that artículo wrongly excluded). Reverted the mutant, confirmed GREEN with the strengthened fixture | Yes |
| 14 | 2 | The coarse `ListarAsync` candidate query does **not** join `ofertas` | Added a post-filter in `ResolverPorFiltroAsync` narrowing to articulos with a **direct** `ofertas.id_articulo` row | `SoloConOfertaVigenteCoincideExactamenteConElResolverReal` — FAILED (the categoría-scoped articulo, matched only via ancestor category, has no direct oferta row — wrongly dropped from the candidate set entirely) | Yes |
| 15 | 2 | `ConstruirDescendientes` direction | `return descendientes;` → `return new HashSet<int> { idCategoria };` | `UnaRaizDevuelveTodoElSubarbolYNuncaElArbolHermano` — FAILED (`{1,2,4,3}` expected, got `{1}`) | Yes |
| 16 | 2 | Its `ProfundidadMaxima` bound | For-loop bound `ReglaDeCategorias.ProfundidadMaxima` → literal `1` | `UnaRaizDevuelveTodoElSubarbolYNuncaElArbolHermano` — FAILED (level-2 descendant `Cola` missing from a 3-level fixture) | Yes |
| 17 | 2 | The duality invariant | Same "only-self" mutant as target 15 | `LaDualidadEntreDescendientesYAncestrosSeCumpleParaCadaParDelBosque` — FAILED | Yes |
| 18 | 2 | `TamanioMaximoDePagina` shared by the clamp **and** the cap | `200` → `199` | `Con200IdsExplicitosProcede` **and** `Con200ArticulosMatcheadosPorFiltroTruncadoEsFalse` — BOTH FAILED together (the coupling is the point) | Yes |
| 19 | 2 | `Truncado = pagina.Total > cap` | `>` → `>=` | `Con200ArticulosMatcheadosPorFiltroTruncadoEsFalse` — FAILED (`Truncado` flipped `true` at exactly 200) | Yes |
| 20 | 2 | `idsArticulo.Count > cap` ⇒ `400 seleccion_excedida` | Guarded the whole `if` with `false &&` (never throws) | `Con201IdsExplicitosDevuelve400SeleccionExcedida` — FAILED (200 instead of 400) | Yes |
| 21 | 2 | ids **XOR** filtro | Both guard `if`s replaced with `if (false)` | `AmbosSelectoresPresentesDevuelve400SeleccionAmbigua` **and** `NingunSelectorPresenteDevuelve400SeleccionRequerida` — BOTH FAILED | Yes |
| 22 | 2 | The exposure clause: no cost/proveedor property in `FilaDeEtiqueta` | Added `decimal? CostoNominal = null` to the record | `LaRespuestaSerializadaNoContieneNingunaPropiedadDeCostoOProveedor` — FAILED (`costoNominal` property found by name) | Yes |
| 23 | 2 | `NombreDeLista` read from `listas_precio` by the server | `await db.ListasPrecio…` → hardcoded `"General"` | `NombreDeListaDesincronizadoPorUnUpdateCrudoSurgeElSentinel` — FAILED (sentinel never surfaced) | Yes |
| 24 | 2 | Every positional field of `FilaDeEtiqueta` | Transposed `Nombre`/`CodigoInterno` in the constructor call | `CadaCampoPosicionalDeFilaDeEtiquetaSeLeeDeVueltaConValoresDistintos` — FAILED | Yes |
| 25 | 2 | One `momento` for the whole sheet, echoed in the response | `DatosDeEtiquetas.Momento` arg `momento` → `DateTimeOffset.UtcNow` | `UnMomentoPinneadoSeEchaExactoYGobiernaTodaLaHoja` — FAILED (echoed value ≠ pinned reloj value) | Yes |
| 26 | 2 | Each `if (idArea/idCategoria/idMarca is { } x)` in `ListarAsync` | Deleted the `idMarca` guard | `FiltrarPorIdMarcaDevuelveSoloLosArticulosDeEsaMarca` — FAILED (all 40 returned instead of 12) | Yes |
| 27 | 2 | The unfiltered listing path unchanged | `idArea` guard replaced with an unconditional `Where(a => a.IdArea == (idArea ?? 0))` | `SinFiltrosElListadoQuedaByteIdenticoAlCaminoPrevio` — FAILED (0 items instead of 2) | Yes |
| 28 | 2 | `.RequireAuthorization(Politicas.OperacionDePos)` on the etiquetas group, nothing stacked | Stacked `.RequireAuthorization(Politicas.GestionDeCatalogo)` | `RolesDelPosPuedenComponerLaHoja(Vendedor)` **and** `(Supervisor)` — BOTH FAILED (403 instead of 200) | Yes |
| 29 **S** | 2 | The `("POST", "/api/etiquetas/datos")` allowlist entry | Deleted the entry | `SuperficieDeAutorizacionTests.TodoEndpointNoGetFueraDelAllowlistApilaGestionDeCatalogo` — FAILED | Yes |
| 30 | 2 | The ≤11 command budget (no per-row query) | Replaced the batched `codigos_barra` dictionary lookup with a per-row query inside the `filas` loop | `ElPresupuestoDeComandosEsIgualParaUnArticuloY200Articulos` — FAILED (1-artículo and 200-artículo counts diverged) | Yes |

### Deviations from Design

1. **`ServicioDeEtiquetas`'s filtro path passes `idEmpresa` to `ServicioDeArticulos.ListarAsync`.**
   Not explicitly spelled out in design.md's query-budget breakdown (which lists "filtro →
   ListarAsync (1 categorias + 1 count + 1 page)" without mentioning the param), but
   `ListarAsync` already accepts an optional `idEmpresa` for exactly this purpose
   (`DisponibleEnEmpresa`), it costs zero extra round trips (a correlated `EXISTS` inside the same
   count/select queries), and omitting it would let a filtro-based sheet select articulos not
   actually available at this PV's empresa. Not a design contradiction — a straightforward
   application of an existing, already-budgeted parameter.
2. **PV lookup uses 404, matching the lista's explicit 404 rather than the `400
   referencia_invalida` precedent of `ServicioDePrecios.BuscarListaAsync`.** design.md explicitly
   states "(404 si no existe)" for the missing-lista case; the missing-PV case is unstated. Chose
   404 for both, for internal consistency of this one new endpoint (ADR-8's "same 404 uniform"
   philosophy) rather than importing the other module's 400 convention.
3. **`ArticuloExcluido.Motivo`** ships the literal string `"Sin precio vigente en la lista
   seleccionada."` — design.md names the *shape* (`Motivo` field) but not exact wording; not
   exercised by a specific-string assertion in any test (only non-blank), so this is a judgment
   call, not a cited value.

### Issues Found

None beyond the deviations above.

### judgment-day Slice 2, ronda 1 — juez B (2 MAJOR + 2 MINOR)

| # | Severidad | Hallazgo | Fix | Ciclo mutación |
|---|---|---|---|---|
| 1 | MAJOR | `UnMomentoPinneadoSeEchaExactoYGobiernaTodaLaHoja` usaba `RelojFijo`, que devuelve el MISMO valor en cada lectura de `Ahora` — "resuelto una vez" y "resuelto dos veces" eran indistinguibles, así que el test no probaba realmente el mutation target 25 | `RelojQueAvanza` agregado (test double que arranca en un instante fijo y suma 1 segundo por lectura de `Ahora`, con contador `Lecturas`); `CrearServicioCrudo` toma ahora `IRelojDelSistema` en vez de `DateTimeOffset`; test reescrito con dos artículos, assertando `datos.Momento == primeraLectura` **y** `reloj.Lecturas == 1` (una sola resolución para toda la hoja) | El argumento del echo (`ServicioDeEtiquetas.cs:141`) cambiado de `momento` a una segunda lectura `reloj.Ahora` → RED (`Expected: ...12:00:00...`, `Actual: ...12:00:01...`) → `git checkout --` → revertido → verde |
| 2 | MAJOR | `ServicioDeEtiquetas.cs:136-139` filtra `soloConOfertaVigente` SOLO sobre `Filas`, DESPUÉS de armar `Excluidos` — ningún fixture de `SoloConOfertaVigenteCoincideExactamenteConElResolverReal` combinaba sin-precio con `soloConOfertaVigente=true`, así que un post-filtro mal ubicado (antes del loop, sobre el candidato grueso) pasaba sin que ningún test lo detectara | Fixture ampliado con `idSinPrecioConOferta` (sin precio vigente, CON oferta vigente); nuevas assertions: con `soloConOfertaVigente=true`, ese artículo nunca es una fila pero SIGUE en `Excluidos`, con identidad y motivo (regla 12c) | El filtro movido ANTES del loop que arma `Filas`/`Excluidos` (filtrando `resultados` por `Aplicadas.Count > 0` antes de iterar) → RED (`Assert.Single` no encontró el excluido — el sin-precio desapareció de ambas colecciones) → `git checkout --` → revertido → verde |
| 3 | MINOR | El doc comment de `ConstruirDescendientes` (y la tarea 2.2 de este archivo) acreditaban al bound `ProfundidadMaxima` la terminación ante ciclos corruptos, pero el terminador real es el gate de visitados (`descendientes.Add(hijo)`) — quitar el bound del `for` no hace loopear la función | Doc comment de `ConstruirDescendientes` (`CadenaDeCategorias.cs`) corregido: el gate de visitados es quien termina el ciclo; el bound es defensa-en-profundidad del contrato de profundidad ≤3 (ADR-12). Anotación de la tarea 2.2 en este archivo corregida igual de honesta | N/A — corrección de comentario/documentación, no de código ejecutable |
| 4 | MINOR | No existía ningún test para `POST /api/etiquetas/datos` con `idListaPrecio` inexistente → 404 (el caso "(404 si no existe)" de `design.md:237`) | `IdListaPrecioInexistenteDevuelve404` agregado: `idListaPrecio=-1` con un `idPuntoVenta` válido → asserta 404 | Guard de lista (`ServicioDeEtiquetas.cs:64-68`) mutado para no lanzar (`?? "MUTANTE-juez-B-MINOR-2"` en vez de `?? throw ...`) → RED (`Expected: NotFound, Actual: OK`) → `git checkout --` → revertido → verde |

### judgment-day Slice 2, ronda 2 — juez A (1 CRITICAL + 1 WARNING + 1 SUGGESTION; arbitraje del orquestador sobre el contrato de ids)

Arbitraje vinculante del orquestador para el camino de ids explícitos: (a) un id que NO resuelve
identidad en el tenant (inexistente o cross-tenant) ⇒ `400 referencia_invalida`, paridad con
`POST /api/ofertas/resolver` — nunca un drop silencioso; (b) un id que RESUELVE pero cuyo artículo
no está disponible en la empresa del PV ⇒ `Excluidos` con su identidad y motivo propio (mismo
patrón que la decisión 6 — la identidad se conoce, la exclusión es honesta). El camino por filtro
ya scopea por `DisponibleEnEmpresa` (`ResolverPorFiltroAsync` → `ServicioDeArticulos.ListarAsync`)
y no cambia.

| # | Severidad | Hallazgo | Fix | Ciclo mutación |
|---|---|---|---|---|
| 1 | CRITICAL | `ServicioDeEtiquetas.cs:82-85` (la query de identidad del camino de ids) no aplicaba `ArticuloConsultas.DisponibleEnEmpresa` ni ningún predicado de empresa — un id explícito de un artículo `DisponibleParaTodas=false` sin fila `articulos_empresas` para la empresa del PV llegaba a `Filas` con precio, aunque el camino por filtro lo hubiera excluido | Arbitraje (b) implementado: la query de identidad ahora proyecta `Disponible` con el MISMO `EXISTS` correlacionado que `ArticuloConsultas.DisponibleEnEmpresa` (plegado en la misma consulta, sin roundtrip extra); los ids no disponibles se separan ANTES de armar `lineas` y van a `Excluidos` con identidad + motivo ("No disponible en la empresa del punto de venta."), sin resolver precio. Test nuevo (regla 3, conjunct 12c): `UnArticuloNoDisponibleEnLaEmpresaDelPvQuedaExcluidoConIdentidadYElHermanoDisponibleSale` — fixture de dos direcciones, un artículo `DisponibleParaTodas=false` sin fila para la empresa del PV y un hermano `DisponibleParaTodas=true` por defecto | `Disponible = a.DisponibleParaTodas \|\| db.ArticulosEmpresas.Any(...)` → `Disponible = true` (MUTANTE-juez-A-CRITICAL-1) → RED (el no-disponible apareció en `Filas` con `PrecioFinal = 100,00`) → revertido a mano → verde |
| 2 | WARNING | `ServicioDeEtiquetas.cs:104-107` (antes del fix) descartaba en silencio, vía `idsArticulo.Where(identidad.ContainsKey)`, cualquier id explícito que no resolviera identidad (inexistente o cross-tenant) — sin señal al caller, contrato distinto del guard `referencia_invalida` de `ServicioDeOfertas.ResolverAsync:356-360` | Arbitraje (a) implementado: guard nuevo, condicionado a `hayIds` (el camino por filtro nunca cae acá porque sus ids salen de una consulta al mismo `db.Articulos`), que reusa el MISMO código de dominio `"referencia_invalida"` que `ServicioDeOfertas.ResolverAsync`. `TenantBNuncaVeLosArticulosNiElPuntoDeVentaDeTenantA` reescrito: el id cross-tenant ahora asserta `400 referencia_invalida` en vez de `200` con colecciones vacías (ADR-8: cross-tenant indistinguible de inexistente, mismo 400 uniforme, no filtra existencia). Test nuevo: `UnIdExplicitoInexistenteDevuelve400ReferenciaInvalida` (id `-1`) | Guard `if (hayIds) { ... throw ... }` → `if (false) { ... }` (MUTANTE-juez-A-WARNING-2) → RED en ambos tests (`Expected: BadRequest, Actual: InternalServerError`) → revertido a mano → verde |
| 3 | SUGGESTION | El design fija `≤ 11` general y `≤ 9` en el camino de ids explícitos (`design.md:253-254`), pero `ElPresupuestoDeComandosEsIgualParaUnArticuloY200Articulos` solo asserta `≤ 11` — el presupuesto más angosto del camino de ids nunca estaba bajo prueba | Medido con el interceptor existente tras el fix del CRITICAL (disponibilidad plegada en la MISMA consulta de identidad, sin query extra): el número real es **10**, no 9. Assert ajustado a `<= 10` con comentario explicando la medición; `design.md:253-260` enmendado al mismo número, honesto — el `≤ 9` era una estimación previa a la implementación, no un valor medido | N/A — ajuste de umbral de test guiado por medición directa, no un guard de código; verificado corriendo el harness con el bound temporalmente en `0` para confirmar el conteo real (`10`) antes de fijar el assert definitivo |

---

## Slice 3: `Etiquetas.tsx` (PR 3)

**Branch**: `feat/stage18-slice3-web-etiquetas`. **Start**: PR 1 **and** PR 2 merged, **and** task
1.4 (E1) **and** task 1.5 (E2, all three proofs) recorded PASS in `spike-alineacion.md` — binding
verify criterion 4 requires "PASS on both E1 and E2 before slice 3 opens" (`design.md:401-414`,
`tasks.md` binding verify criterion 4), not E1 alone; judgment-day slice 1 ronda 2 juez A flagged
this Start as under-specified because E2's third proof (the "Guardar como PDF" page-box comparison)
had no owning task in this slice — task 3.0 below closes that gap. **Finish**: filters (búsqueda/área/categoría/marca/con oferta
vigente), the `FacturarRemitos.tsx:134,142-144` multi-select reducer with "elegir todos", per-row
copies 1-99 + "aplicar a todos", format + lista selectors (lista defaulted to the first
`EsDefault` row), the "N etiquetas = M hojas" preview, the excluded-count notice, the
`d-print-none` print-settings block, one `window.print()` **Imprimir** button, route + menu.
**Rollback**: `git revert` removes one route and one menu entry; the API still serves the shape
untouched. **Skills required**: `react-async-state` rules 5/8 (`generacionRef` on every fetch; a
stale `datos` response resolved inside `act` is discarded), `web-descriptor-tests` (colocated
descriptor tests), `mutation-proof-tests` v1.1 rule 3 — **one new multi-branch guard this slice**,
enumerated below.

**Guard enumeration (rule 3)**: the multi-select reducer has 4 transitions — `toggle-uno`,
`elegir-todos`, `limpiar`, and `cambio-de-filtro` (MUST clear the selection so a row that left the
visible list cannot send a phantom id, `FacturarRemitos.tsx:138-140`). All four MUST have a
dedicated test; the print button additionally has a re-entrancy conjunct (pending / not pending).

- [ ] 3.0 **GATE** — close E2's third deferred proof (`spike-alineacion.md`, tarea 1.5) before any
  other slice-3 task starts: print `CajaZ` and `CuentaCorriente` to PDF ANTES of mounting the first
  real consumer of `HojaDeEtiquetas` (this slice's first commit) and again DESPUÉS (once `Etiquetas.tsx`
  is mounted on its route), with the owner's or the environment's real browser, and compare the two
  page-boxes for each view. Record the comparison result in `spike-alineacion.md` §E2, closing task
  1.5 with all three E2 proofs `PASS`. *(binding verify criterion 4, `design.md:93-97`)*
- [ ] 3.1 Create `src/Ways.Web/src/api/etiquetas.ts` — client + DTO mirrors of
  `SolicitudDeEtiquetas`/`DatosDeEtiquetas`/`ArticuloExcluido`. `FilaDeEtiqueta` is **imported** from
  `etiquetas/HojaDeEtiquetas.tsx` (re-exported from here if a call site needs it from `api/etiquetas.ts`)
  — it is **never redefined**, per slice 1's deviation 4: two competing shapes for the same DTO is
  exactly the drift that deviation warns against. *(design.md:296-297, slice 1 deviation 4)*
- [ ] 3.2 [P] `etiquetas.ts.test.ts` — mapper/client descriptor tests. *(web-descriptor-tests)*
- [ ] 3.3 Create `src/Ways.Web/src/etiquetas/expandirCeldas.ts` — pure `expandirCeldas(filas,
  copias)`, `1..99` clamp. *(design.md:55, 169, mutation target 32)*
- [ ] 3.4 [P] `expandirCeldas.test.ts` — copies 1/3/99 (order preserved), 0/100 refusal.
  *(mutation target 32)*
- [ ] 3.5 Create `src/Ways.Web/src/paginas/Etiquetas.tsx` — filters row (búsqueda, área, categoría,
  marca, con oferta vigente) wired to `GET /api/articulos`/`GET /api/catalogos/*`.
  *(design.md:261-269, proposal.md:36-40)*
- [ ] 3.6 Same file: multi-select `useReducer` (`FacturarRemitos.tsx:134,142-144` pattern) —
  `toggle-uno`, `elegir-todos`, `limpiar`, `cambio-de-filtro` clears selection.
  *(design.md:268-269, mutation target 31)*
- [ ] 3.7 Same file: per-row copies input `1..99` + "aplicar a todos" helper, driving
  `expandirCeldas`. *(proposal.md:257-259, mutation target 32)*
- [ ] 3.8 Same file: format selector (the four `formatos.ts` descriptors) + lista selector
  (defaulted to the first `EsDefault` row from `GET /api/listas-precio`). *(design.md:264-265,
  proposal.md:197-217)*
- [ ] 3.9 Same file: "N etiquetas = M hojas" preview using `celdasPorHoja`/`contarHojas`.
  *(proposal.md:258-259, mutation target 33)*
- [ ] 3.10 Same file: excluded-count notice from `DatosDeEtiquetas.Excluidos.Count`
  (Reconciliación 3). *(proposal.md:242-246, mutation target 33)*
- [ ] 3.11 Same file: `d-print-none` print-settings block on the composed sheet; single
  `window.print()` **Imprimir** button, `CajaZ.tsx:87` verbatim, re-entrancy-guarded against a
  double click. *(design.md:266-267, mutation target 34)*
- [ ] 3.12 Same file: `react-async-state` — `generacionRef` bump on every `datos` POST; a stale
  response resolved inside `act` after a newer request started is discarded (rule 5/8).
  *(design.md:268-269)*
- [ ] 3.13 Modify `App.tsx` + `Layout.tsx` — `/etiquetas` route under `RutaProtegida
  rolesPermitidos={[Vendedor, Supervisor, Admin]}` + menu entry. *(design.md:261, 297)*
- [ ] 3.14 [P] `Etiquetas.test.tsx` — reducer: elegir-todos / limpiar / cambio-de-filtro-limpia
  (the phantom-id test — a row deselected by a filter change must not be posted).
  *(mutation target 31)*
- [ ] 3.15 [P] `Etiquetas.test.tsx` — copies test (1/3/99) and the 0/100 refusal, wired through the
  UI. *(mutation target 32)*
- [ ] 3.16 [P] `Etiquetas.test.tsx` — sheet-count preview and excluded-count notice descriptor
  tests. *(mutation target 33)*
- [ ] 3.17 [P] `Etiquetas.test.tsx` — a double click on **Imprimir** issues exactly one
  `window.print()`. *(mutation target 34)*
- [ ] 3.18 [P] `Etiquetas.test.tsx` — a stale `datos` response resolved inside `act` is discarded
  (react-async-state rule 8). *(design.md:325)*
- [ ] 3.19 **GATE GUARD** — `git diff --exit-code src/Ways.Api/Seguridad/Politicas.cs` clean (no
  route/menu change touches authorization). *(verify criterion 3)*
- [ ] 3.20 Mutation evidence recorded in the PR body for targets 31-34.
- [ ] 3.21 `judgment-day` round: two blind review agents, fix confirmed findings, re-judge to a
  clean round.
- [ ] 3.22 Open PR #3 `feat/stage18-slice3-web-etiquetas`, merge to `main` after a clean
  `judgment-day` round.

---

## Slice 4: `ConsultaPrecios.tsx` (PR 4 — independent, may run first if the spike stalls)

**Branch**: `feat/stage18-slice4-consulta-precios`. **Start**: `main` — **depends on nothing**
(`design.md:387-389`, `proposal.md:521-523`). If task 1.4 (E1) is still owner-blocked when this
slice is ready, it ships anyway — the salón screen shares no component with the label engine.
**Finish**: scan input (`autoFocus`+`Enter`, `Pos.tsx:1068-1078` pattern), PV+lista selectors
remembered locally, exactly two calls per scan (`GET /api/articulos/escaneo` →
`POST /api/ofertas/resolver`), struck-original-only-with-offer display, "no encontrado"/"consultá
en caja" paths, idle reset at ~20s, route + menu, `docs/11-programa-post-paridad.md`'s Etapa 18
status block (last slice — orchestrator-authored decisions already answered, per
`proposal.md:429`). **Rollback**: `git revert` — one route, one menu entry. **Skills required**:
`react-async-state` rules 5/8 (the idle-reset effect pattern, `generacionRef`, `clearTimeout` in
cleanup — `design.md:65`), `web-descriptor-tests`, `mutation-proof-tests` v1.1 rule 3 — **two new
guards this slice**, enumerated below.

**Guard enumeration (rule 3)**:
- **Display-branch guard** — 4 mutually exclusive branches: unknown code (`404` ⇒ "no
  encontrado", no resolver call), identified + no vigent price (`PrecioOriginal = null` ⇒
  "consultá en caja", never `$0`), identified + offer applies (`Aplicadas.length > 0` ⇒ struck
  original + final), identified + no offer (one price, no strike). All four MUST have a dedicated
  test. *(consulta-de-precios/spec.md:39-73)*
- **Idle-reset guard** — 3 conjuncts: < 20s elapsed (no reset), = 20s elapsed (reset fires), a new
  scan arriving before 20s (cancels the pending timer, exactly one reset ever fires across two
  scans). *(design.md:65, mutation targets 35, 36)*

- [ ] 4.1 Create `src/Ways.Web/src/paginas/ConsultaPrecios.tsx` — `autoFocus`+`Enter` scan input
  (`Pos.tsx:1068-1078`); PV + lista from the same selectors the POS uses, remembered locally.
  *(design.md:270-274, consulta-de-precios/spec.md)*
- [ ] 4.2 Same file: on scan, `GET /api/articulos/escaneo` → identity; on hit,
  `POST /api/ofertas/resolver` (1 línea @ `cantidad = 1`) → price. Exactly two calls, no third.
  *(design.md:248-250, consulta-de-precios/spec.md:15-25, mutation target 37)*
- [ ] 4.3 Same file: unknown code (404) ⇒ "no encontrado", **no** resolver call issued.
  *(consulta-de-precios/spec.md:57-67, mutation target 37)*
- [ ] 4.4 Same file: identified + `PrecioOriginal = null` ⇒ "consultá en caja", never `$0`.
  *(consulta-de-precios/spec.md:69-72, mutation target 37)*
- [ ] 4.5 Same file: `Aplicadas.length > 0` ⇒ `PrecioOriginal` struck + `PrecioFinal` prominent;
  empty ⇒ one price, no strike. *(consulta-de-precios/spec.md:39-55)*
- [ ] 4.6 Same file: oversized typography for the resolved price, responsive layout, input cleared
  and refocused after each resolution. *(consulta-de-precios/spec.md:119-129)*
- [ ] 4.7 Same file: idle-reset effect — `MS_DE_RESET = 20_000` (exported constant), `setTimeout`
  inside the effect, `clearTimeout` in the returned cleanup, `generacionRef` bump on fire/reset
  (`CompraEditor.tsx:90-110`/`Existencias.tsx:58-79` pattern, never a bare `useRef` timer id).
  *(design.md:65, mutation targets 35, 36)*
- [ ] 4.8 Modify `App.tsx` + `Layout.tsx` — `/consulta-precios` route under `RutaProtegida
  rolesPermitidos={[Vendedor, Supervisor, Admin]}` + menu entry. *(design.md:261, 297,
  consulta-de-precios/spec.md:89-101)*
- [ ] 4.9 [P] `ConsultaPrecios.test.tsx` — exactly two calls per scan, full mocked call log
  asserted (the zero-writes proof). *(design.md:326, mutation target 37)*
- [ ] 4.10 [P] `ConsultaPrecios.test.tsx` — unknown code ⇒ "no encontrado", no resolver call.
  *(mutation target 37)*
- [ ] 4.11 [P] `ConsultaPrecios.test.tsx` — null price ⇒ "consultá en caja", never `$0`.
  *(mutation target 37)*
- [ ] 4.12 [P] `ConsultaPrecios.test.tsx` — strike only with an active offer, both directions.
- [ ] 4.13 [P] `ConsultaPrecios.test.tsx` — `vi.useFakeTimers`: 19.9s no reset / 20.0s reset
  boundary pair. *(mutation target 36)*
- [ ] 4.14 [P] `ConsultaPrecios.test.tsx` — a second scan cancels the first timer; exactly one
  reset fires across two scans. *(mutation target 35)*
- [ ] 4.15 [P] `ConsultaPrecios.test.tsx` — a resolution landing **after** a reset does not
  repaint the previous customer's price (generation bump). *(mutation target 37)*
- [ ] 4.16 [P] `ConsultaPrecios.test.tsx` — no session/anonymous/device-token path exists; a
  request without a valid session is rejected 401 on both consumed endpoints.
  *(consulta-de-precios/spec.md:108-112)*
- [ ] 4.17 Modify `docs/11-programa-post-paridad.md` — Etapa 18 status block, OD1-OD3 recorded as
  answered (last slice of the stage, `proposal.md:429`).
- [ ] 4.18 **GATE GUARD** — zero migrations / `has-pending-model-changes` clean / `Politicas.cs`
  untouched (final confirmation for the whole stage). *(verify criteria 1, 3)*
- [ ] 4.19 Mutation evidence recorded in the PR body for targets 35-37.
- [ ] 4.20 `judgment-day` round: two blind review agents, fix confirmed findings, re-judge to a
  clean round.
- [ ] 4.21 Open PR #4 `feat/stage18-slice4-consulta-precios`, merge to `main` after a clean
  `judgment-day` round.

---

## Review Workload Forecast

| Field | Value |
|-------|-------|
| Estimated changed lines | ~1620 naive (`proposal.md:538`); no schema/concurrency/write-path inflators present, so the usual 1.5-3× multiplier from stages 13-17 is not expected to apply in full — realistic range ~1600-2400 |
| 400-line budget risk | Medium — 3 of 4 slices sit near the cap on the estimate alone (slice 2 especially, given 22 mutation targets) |
| Chained PRs recommended | Yes |
| Suggested split | PR 1 → PR 2 → PR 3 → PR 4, with three pre-authorized cut points inherited from `proposal.md:525-534`/`design.md:392-399` (1a/1b spike split already folded into slice 1's task list; 3a/3b selection+label vs poster+copies; 2a/2b three filters vs composed endpoint) |
| Delivery strategy | auto-chain |
| Chain strategy | stacked-to-main |

```text
Decision needed before apply: No
Chained PRs recommended: Yes
Chain strategy: stacked-to-main
400-line budget risk: Medium
```

`auto-chain` + `stacked-to-main` already resolved in `state.yaml`; no further decision needed
before `sdd-apply`. Slices 1 and 2 are mutually independent and may interleave; slice 3 depends on
**both** 1 (renderer) and 2 (endpoint) **and** on task 1.4's E1 PASS verdict; slice 4 depends on
nothing and may ship first if the spike stalls on the owner. `size:exception` anticipated: **No**.

---

## Summary

**4 slices**, stacked-to-main, merge order `1 → 2 → 3 → 4` (3 gated on 1+2 and on E1 PASS; 4
independent). **37 mutation targets** placed exactly once per design's own Slice column: 1 → 1-8,
2 → 9-30, 3 → 31-34, 4 → 35-37. **Zero migrations, zero index changes, zero policy changes** — the
stage's whole DB/authorization surface is asserted, never introduced.

**Reconciliations this phase**: T1 (copias stays client-side, ratified for `design`), T2 (the
sharpened `A4-2x7` geometry is the implementation truth, ratified for `design`), T3 (`Excluidos`
carries identity, ratified for `design`), the AND semantics of the three combined `articulos`
filters (confirmed explicit in both `spec.md` and `design.md`, no residual inference), and the
spike's autonomous/owner-blocked split (1a delivers and documents its own cut; 1b is registered
open and gates only slice 3, per `design.md`'s existing binding verify criterion 4 — no new gate
was invented here).

**Gap not closed by this phase**: task 1.4 (the physical E1 measurement) has no owner-side
resolution date; `sdd-apply` should treat slice 1's PR as mergeable on 1a+E2 alone, and slice 3 as
blocked until a human appends the E1 verdict to `spike-alineacion.md`.
