# Tasks: Stage 13 — Stock inteligente (mínimos, alertas y reposición)

## Orchestrator Decisions Recorded This Phase

1. **7 slices, 7 PRs, stacked-to-main** — design.md's final breakdown (not
   the proposal's tentative one, which design re-scoped without renumbering).
   Merge order: `1 → {2 → 3}` and `{4 → 5 → 6}` disjoint and parallel →
   `7` (needs `3`, `4`, `5`). Format reference: the archived
   `2026-08-14-stage-12-lotes-vencimientos/tasks.md` structure — per-slice
   Start/Finish/Rollback, hierarchical task numbering, `[P]` for
   parallelizable test tasks, a Verify line, and a closing Review Workload
   Forecast.
2. **DB gate is `SIN-CAMBIOS-DE-SCHEMA-RATIFICADO`** (`state.yaml`). Every
   slice — including the three web-only slices — carries a gate-guard task:
   `dotnet ef migrations has-pending-model-changes` reports no pending
   changes **and** `git diff --stat` against the stage's base shows **zero**
   files under `src/Ways.Infrastructure/Persistencia/Migraciones/`. If any
   slice finds itself needing a schema change, STOP and reopen the gate —
   the same discipline the stage-12 gate contract used, inverted (that gate
   allowed exactly one migration; this one allows none).
3. **No `size:exception` is anticipated on any slice.** Unlike stage 12,
   nothing here carries an unsplittable migration; the two slices closest to
   the 400-line cap (3 and 4) have pre-identified cut points named in their
   sections below, inherited from design.md's Budget note.
4. **`judgment-day` runs once per slice**, on that slice's diff, before its
   PR — per `protocolo-pr-solo-dev`. Seven independent rounds.
5. **CONFLICT FOUND AND RESOLVED — the tile's third metric name.**
   `design.md` decision 9 and its Interfaces/Contracts section (the
   `ResumenDeReposicion` record and the three tile `data-testid`s) still
   read `sinSugerencia` — that text predates the spec-amendment round.
   `state.yaml`'s `spec` phase note and the ratified
   `specs/reposicion-de-stock/spec.md` ("The Tablero Tile Reuses The Report
   Method…") both name the third metric **`sinProveedor`**, resolved by the
   orchestrator *during* `sdd-spec` specifically because `sinSugerencia`
   conflates two distinct causes (no proveedor vs. no `reposicion`
   configured) behind one number. **The ratified spec is authoritative.**
   Every task below that touches the tile's contract, query fold, or
   `data-testid`s uses `sinProveedor` / `reposicion-tile-sin-proveedor`, not
   design.md's stale `sinSugerencia` / `reposicion-tile-sin-sugerencia`.
   Flagged here so `sdd-apply` does not silently follow the older text.
6. **No `db-error-backstops` skill applies to this stage.** Zero new
   constraints of any kind ship (gate section C of `proposal.md`): no CHECK,
   no unique index, no new FK. The stage's only write is a single
   `INSERT … ON CONFLICT (id_articulo, id_punto_venta) DO UPDATE` against the
   **existing** `stock` PK — a conflict target already covered by
   `ServicioDeStock`'s existing upsert tests from prior stages, so this
   stage adds no new backstop-translation surface. Declared explicitly per
   the instruction to name the absence, not silently skip the skill.
7. **`mutation-proof-tests` compliance**: the eleven named mutation targets
   in design.md's table (12 rows, one of which — slice 2's — is folded into
   the existencias projection) are each placed in exactly one slice below.
   Every one requires recorded apply-time evidence (mutation applied → named
   failing test → reverted → green) in its slice's PR body, per the design's
   binding verify criterion 3.
8. **`dto-contract-honesty` applies at every `Contratos.cs` edit** — slices
   1, 2, 4, 5, 7 each introduce or widen a DTO and must document each new
   field's destination inline (the `PoliticaDeRoles`/`ReglaDeLotes` doc-comment
   convention this repo already uses).
9. **`react-async-state` + `web-descriptor-tests` apply to slices 3, 6, 7**
   — the only web-touching slices. Every new/modified pure helper ships a
   colocated `*.test.ts(x)`, never deferred to a sweep.
10. **Test dates are fixed, never wall-clock-relative.** The rotation-window
    boundary test pins the clock at `2026-08-14T12:00:00Z` (midday UTC,
    exactly as design.md's testing-strategy table specifies) so `hoy` stays
    stable in both UTC and `America/Argentina/Buenos_Aires`. Every other
    date-bearing test (the `?dias=` override, the netting-trap sequence,
    the motivo-exclusion sequence) uses movement timestamps expressed as
    fixed offsets from that same pinned instant, never `DateTime.Now`/
    `DateTimeOffset.UtcNow`.
11. **Doc-11 backlog re-registration (decision 5) is a slice-1 task**, not a
    closing sweep — the same discipline stage 12 used for its own doc-10
    update from within a slice (its task 1.17).
12. **`judgment-day` round 1, slice 4 — judge B's WARNING (inferential) on
    the soft-deleted-proveedor ordering, RESOLVED.** Task 4.1's pinned
    snippet projects `IdProveedor` as the raw FK (`a.IdProveedorHabitual`)
    and orders by `a.IdProveedorHabitual, a.Id` alone. Under that snippet, a
    row whose `id_proveedor_habitual` points at a soft-deleted proveedor
    resolves `Proveedor == null` (name lookup fails, EF's baja-lógica global
    filter) but `IdProveedor` still carries the live FK value — so the row's
    ORDER KEY disagrees with its DISPLAY GROUP: it sorts by FK position
    (mid-list, between whichever real proveedores bracket that FK) while
    displaying as "Sin proveedor". `design.md`'s decision 3 is explicit that
    a soft-deleted proveedor's row "lands under Sin proveedor" — that letter
    is authoritative over the pinned snippet, which the task text itself
    frames as a **draft**, not a locked contract. Left as coded, slice 6's
    `agruparPorProveedor` fold (a plain fold over the already-sorted rows,
    no sort of its own — see slice 6's tasks) would emit a SECOND "Sin
    proveedor" bucket wherever the soft-deleted row's FK happened to sort,
    splitting one logical group into two on screen.
    **Resolution**: `ConstruirQueryDeReposicion` now projects
    `IdProveedor := p == null ? null : (int?)a.IdProveedorHabitual` (the
    dangling FK never travels to the client — `dto-contract-honesty`
    doc-comment updated on `FilaDeReposicion` in `Contratos.cs`) and orders
    `orderby (p == null), a.IdProveedorHabitual, a.Id` — every row with no
    EFFECTIVE proveedor (FK null OR FK pointing at a soft-deleted/missing
    proveedor) falls into ONE trailing bucket; within that bucket, FK then
    Id keeps the order deterministic. **Slice 6 inherits a trivial
    single-null-bucket fold** — `agruparPorProveedor` never has to merge two
    "Sin proveedor" groups because there is structurally only one row-run to
    fold into it. Evidence: mutating the `orderby` back to
    `a.IdProveedorHabitual, a.Id` (dropping the presence key) makes the
    discriminating-seed test's (task 4.9) row-sequence assert FAIL — the
    soft-deleted-proveedor row returns to the middle of the list; reverting
    restores green. See the inline note on task 4.1 below.

---

## Suggested Work Units

| Unit | Goal | Branch | Depends on | ~Lines |
|---|---|---|---|---|
| 1 | Domain rule (`ReglaDeReposicion`), 2 `ParametroConocido` keys, `PUT /api/stock/minimos`, doc-11 re-registration | `feat/stage13-slice1-minimos-api` | none | ~320 |
| 2 | `minimo`/`reposicion`/`estado` on `/existencias` + 3 export columns | `feat/stage13-slice2-existencias-minimos` | 1 | ~230 |
| 3 | `Existencias.tsx` editor grid, add-row, descriptor + component tests | `feat/stage13-slice3-web-minimos` | 1, 2 | ~380 |
| 4 | Reposición read model, `/reposicion` + `/export` sibling (no rotation fields yet) | `feat/stage13-slice4-reposicion` | 1 | ~350 |
| 5 | Rotation (`LeerConsumoAsync`, `VentanaDeRotacion` wiring), rotation columns, `GET /rotacion` | `feat/stage13-slice5-rotacion` | 4 | ~340 |
| 6 | `Reposicion.tsx` grouped by proveedor + download + nav | `feat/stage13-slice6-web-reposicion` | 4, 5 | ~330 |
| 7 | Tablero tile (`/resumen`) + `Sugerido` column on the editor — **the designated droppable slice** | `feat/stage13-slice7-tile-y-sugerencia` | 3, 4, 5 | ~300 |

**Parallelism.** `1` blocks everything. After it merges: `[2 → 3]`
(existencias + its screen) and `[4 → 5 → 6]` (reposición) are genuinely
disjoint — `2`/`4`/`5` share `ServicioDeReportesDeStock.cs`/`Contratos.cs`
and must serialize *within* their front, but `3` and `6` touch only
`src/Ways.Web`. `7` needs `3` (the column's host), `4` (for `/resumen`) and
`5` (for `/rotacion`). Conflict surface between fronts: `ReportesEndpoints.cs`
(one route line per slice) and `tipos.ts` (append-only blocks).

---

## Slice 1: Minimos API (PR 1)

**Start**: `main`. **Finish**: `ReglaDeReposicion` exists as a pure,
DB-free Domain rule; two `ParametroConocido` keys resolve with no migration;
`PUT /api/stock/minimos` writes a threshold with zero `movimientos_stock`
rows, Admin-only; doc-11's backlog row 367 is re-registered.
**Rollback**: revert the branch — the columns stay dormant as they have
been since Etapa 5, no other slice depends on this write path's *code*
existing (only on its *contract*).

- [x] 1.1 Modify `src/Ways.Domain/Catalogos/ParametroConocido.cs`: add
  `DiasRotacion` (`int`, default `30`) and `DiasCoberturaObjetivo` (`int`,
  default `7`), both registered in `PorClave` — without that, `Buscar()`
  rejects them as unknown. *(no migration — stage-10/12 pattern; spec
  `parametros-operativos`)*
- [x] 1.2 Create `src/Ways.Domain/Stock/ReglaDeReposicion.cs`:
  `EstadoDeReposicion` enum (`SinMinimo`/`Bajo`/`Ok`, wire values are the
  C# member names — the `EstadoDeVencimiento` precedent, no naming
  policy); `Clasificar(cantidad, minimo?)` — `minimo is null ⇒ SinMinimo`,
  `cantidad <= minimo ⇒ Bajo`, else `Ok`; `Sugerido(cantidad, reposicion?)`
  — `reposicion is null ⇒ null`, else `Math.Max(0m, reposicion - cantidad)`;
  `ConsumoDiario(netoConsumido?, diasVentana)` — `null` input ⇒ `null`
  output, clamp negative net (returns exceeding sales) to `0m`, never
  `null`; `MinimoSugerido(consumoDiario?, diasCoberturaObjetivo)` — 3-decimal
  rounding; `DiasDeCobertura(cantidad, consumoDiario?)` — `null` on
  null-or-zero consumption; `VentanaDeRotacion(hoy, dias, zona)` — pure
  `(DesdeUtc, HastaUtcExclusivo)` over local-day edges, handling an invalid
  local midnight (advances to the shift instant) and an ambiguous one
  (takes the standard offset); `ExigirVentanaValida(dias, codigo)` —
  rejects `dias <= 0`. *(design decision 1, 7; `PoliticaDeRoles` pattern —
  no `IWaysDbContext` anywhere in this file)*
- [x] 1.3 Modify `src/Ways.Application/Stock/Contratos.cs`: add
  `SolicitudDeMinimos(IdPuntoVenta, IdArticulo, Minimo?, Reposicion?)` and
  `MinimosDeStock(IdPuntoVenta, IdArticulo, Cantidad, Minimo?, Reposicion?,
  Estado)`. `dto-contract-honesty`: doc-comment each field's fate —
  `Minimo`/`Reposicion` on the request go straight to the upsert's `$4`/`$5`;
  on the response they are read back from the same statement's `RETURNING`,
  never re-derived.
- [x] 1.4 Modify `src/Ways.Application/Stock/ServicioDeStock.cs`:
  `EscribirMinimosAsync(SolicitudDeMinimos, ct)` — in-memory validation
  first (`ExigirUmbralValido` on both fields: `>= 0` → `400
  minimo_negativo`; at most 3 decimals → `400 minimo_invalido`;
  `reposicion < minimo` when both set → `400 reposicion_menor_que_minimo`),
  then `ResolverArticuloAsync` (`400 referencia_invalida`) and
  `ResolverPuntoVentaAsync` (`404`, ADR-8), then **one** raw-SQL statement:
  ```sql
  INSERT INTO stock (id_articulo, id_punto_venta, id_tenant, cantidad, minimo, reposicion)
  VALUES ($1, $2, $3, 0, $4, $5)
  ON CONFLICT (id_articulo, id_punto_venta) DO UPDATE
  SET minimo = EXCLUDED.minimo, reposicion = EXCLUDED.reposicion
  RETURNING cantidad, minimo, reposicion
  ```
  — `cantidad` is in the `VALUES` list (create-at-zero) and **absent from
  the `SET` list** (never overwrite a balance). No transaction, no
  `movimientos_stock` insert anywhere in this method. Response classifies
  `Estado` via `ReglaDeReposicion.Clasificar` on the returned row. *(design
  decision 10, 11)*
- [x] 1.5 Modify `src/Ways.Api/Endpoints/StockEndpoints.cs`:
  `PUT /api/stock/minimos`, `.RequireAuthorization(Politicas.GestionDeCatalogo)`
  stacked over the group's `OperacionDePos` — the `/ajustes`,
  `/transferencias`, `/conteos`, `/decomiso` precedent.
- [x] 1.6 Modify `docs/11-programa-post-paridad.md`: re-register the backlog
  row at line 367 (the full conteo snapshot/freeze/variance workflow) to its
  new owner `stage-13b-conteo-por-planilla`, citing proposal decision 5 —
  the carve-out is not complete until the registration is. *(proposal
  decision 5, binding — "a carve-out recorded only in a proposal is a
  carve-out that disappears at the next archive")*
- [x] 1.7 [P] Domain unit suite (`PoliticaDeRoles` pattern, no DB, no
  fixture): `Clasificar` at `cantidad = minimo - 1 / = minimo / = minimo +
  1`, at `minimo = 0` with `cantidad = 0` and `= -1` (negative balances are
  legal), and `minimo = null`; `Sugerido` null-vs-0 and with negative
  `cantidad`; `ConsumoDiario` for `null` / `0` / positive / negative-net
  (clamped to `0`, not `null`); `MinimoSugerido` rounding at 3 decimals;
  `DiasDeCobertura` null on null **and** on zero consumption;
  `VentanaDeRotacion` for a UTC zone, a `-03:00` zone, `dias = 1`, and an
  invalid-local-midnight zone. *(spec: none — pure Domain arithmetic, the
  contract every downstream scenario relies on)*
- [x] 1.8 [P] Application unit: `ExigirVentanaValida` rejects `0` and `-1`
  with their two distinct codes.
- [x] 1.9 [P] **Mutation target**: the `SET` list of the upsert — add
  `cantidad = EXCLUDED.cantidad` → a write over a row with `cantidad = 5`
  must leave the balance unchanged; the mutation must make it change.
  *(spec `stock`: "Writing Reorder Parameters Creates The Stock Row Without
  A Movement", scenario 2; mutation-proof-tests)*
- [x] 1.10 [P] **Mutation target**: `ReglaDeReposicion.Clasificar`'s `<=` →
  `<` — the exact-boundary Domain fact from 1.7 must fail.
  *(mutation-proof-tests)*
- [x] 1.11 [P] **Mutation target**: `Sugerido`'s `reposicion is null` guard
  — return `0m` instead of `null` — the null-vs-zero Domain fact from 1.7
  must fail. *(mutation-proof-tests)*
- [x] 1.12 [P] **Mutation target**: delete
  `.RequireAuthorization(Politicas.GestionDeCatalogo)` on `PUT /minimos` —
  the Supervisor-`403` test (1.16) must fail once the line is gone, because
  the group's `OperacionDePos` alone admits Supervisor **and** Vendedor.
  *(mutation-proof-tests)*
- [x] 1.13 [P] Integration: a minimo write for an articulo with **no**
  `stock` row creates it at `cantidad = 0` with zero `movimientos_stock`
  rows, asserted before and after by `SELECT COUNT(*)`. *(spec `stock`:
  scenario 1; spec `reposicion-de-stock`: PUT requirement, scenario 1)*
- [x] 1.14 [P] Integration: a minimo write over `cantidad = 45` leaves
  `cantidad` unchanged and inserts zero movements. *(spec `stock`: scenario
  2)*
- [x] 1.15 [P] Integration: both fields `null` clears a previously-set pair
  (the unmanage operation).
- [x] 1.16 [P] Integration: the five refusal paths with their HTTP status —
  `400 minimo_negativo`, `400 reposicion_menor_que_minimo`, `400
  minimo_invalido` (both on `minimo` and on `reposicion`, > 3 decimals),
  `400 referencia_invalida` (unknown articulo), `404` (unknown/foreign-tenant
  punto de venta). *(spec `reposicion-de-stock`: PUT requirement, scenarios
  2-5)*
- [x] 1.17 [P] Authorization: Supervisor gets `403` on `PUT /minimos`;
  Vendedor gets `403` on `PUT /minimos`. *(spec `reposicion-de-stock`: PUT
  requirement, scenarios 6-7 — the write half; the read half of scenario 6
  is slice 2's task, once `/existencias` exposes the columns)*
- [x] 1.18 [P] RLS over the **`ways_app`** connection (NOSUPERUSER
  NOBYPASSRLS, `mutation-proof-tests` rule 5): cross-tenant `SELECT`/`UPDATE`
  of `stock.minimo` and a cross-tenant `INSERT` through the upsert, asserting
  row counts for the silent 0-row cases and `42501` where an error is
  actually raised — proves the *new statement* respects `stock`'s existing
  policy, not the schema.
- [x] 1.19 Gate guard: `dotnet ef migrations has-pending-model-changes`
  reports no pending changes; `git diff --stat` against `main` shows zero
  files under `Migraciones/`.
- [x] 1.20 Run `judgment-day` on the slice diff; fix confirmed issues;
  re-judge until clean. *(CLEAN ROUND 2026-08-14. Judge B ran in TWO passes
  because the jd-judge-* agent types lost Bash in the environment —
  (1) full STATIC read-only trace, zero findings; (2) LIVE mutation pass
  via a general-purpose agent under the B-bis mandate: 11 mutations, ZERO
  survivors (clamp, rounding, window exclusivity, invalid-midnight zone
  proven non-decorative, all 3 validation arms, COALESCE full-replace,
  create-at-zero, SET-with-cantidad sample), sweep 28+15+25 green, HEAD
  83f651f intact. Judge A fresh read-only pass over the frozen diff at
  f282081: ZERO findings — verified upsert parameter ordering and SET list
  against sibling upserts, no-schema gate (newest migration still Etapa
  12's), DST arithmetic of VentanaDeRotacion re-derived, dto-contract
  field fates, RLS tests over ways_app with 42501, auth stacking vs the
  /ajustes precedent. BOTH judges APPROVE — JUDGMENT: APPROVED, round 1,
  0 confirmed / 0 suspect / 0 contradictions.)*
- [x] 1.21 Branch `feat/stage13-slice1-minimos-api` off `main`; PR; merge
  stacked-to-main.

**Test plan**: Domain suite (1.7-1.8), 3 mutation targets (1.9-1.12),
zero-movement assertions ×2 (1.13-1.14), unmanage (1.15), five refusal
codes (1.16), Supervisor/Vendedor `403` (1.17), RLS (1.18).

**Verify**: `dotnet test --filter FullyQualifiedName~ReglaDeReposicion|FullyQualifiedName~EscribirMinimos`

---

## Slice 2: Existencias Minimos (PR 2)

**Start**: slice 1 merged. **Finish**: `GET /api/reportes/stock/existencias`
and its `/export` sibling gain `minimo`, `reposicion` and a derived `estado`,
classified by the same `ReglaDeReposicion.Clasificar` the write path
already exercises — never a second definition. **Rollback**: revert the
branch — the report reverts to silent on the two columns, exactly its
pre-stage-13 shape.

- [x] 2.1 Modify `src/Ways.Application/Reportes/Contratos.cs`:
  `FilaExistencia` gains `Minimo`, `Reposicion`, `Estado`. `dto-contract-honesty`:
  doc-comment each — read straight off the `stock` row already joined,
  `Estado` derived via `ReglaDeReposicion.Clasificar`, never a report-local
  reimplementation of the boundary.
- [x] 2.2 Modify `src/Ways.Application/Reportes/ServicioDeReportesDeStock.cs`:
  `ObtenerExistenciasAsync`'s projection calls `ReglaDeReposicion.Clasificar(
  s.Cantidad, s.Minimo)` for `Estado`, and includes `s.Minimo`/`s.Reposicion`
  in the existing row shape. *(design decision 2 — existencias classifies
  **every** stocked row; reposición, slice 4, returns **only** `Bajo`)*
  **APPLY NOTE**: `Clasificar` is a C# static method, not SQL-translatable —
  the LINQ query now materializes the raw join (`ToListAsync`) first and
  classifies in a second, in-memory `Select` (LINQ-to-Objects). No behavior
  deviation: still one query, no second round trip, matches design's "the
  comparison never leaves C#" principle (design §Technical Approach, point 2).
- [x] 2.3 Modify `src/Ways.Application/Reportes/ExportacionDeReportes.cs`:
  `ColumnasExistencias` gains the same 3 columns, same order as the JSON.
- [x] 2.4 [P] **Mutation target**: the `ReglaDeReposicion.Clasificar` call
  in the existencias projection — hard-code `EstadoDeReposicion.Ok` — the
  `SinMinimo`/`Bajo`/`Ok` row assertions (2.5) must fail.
  *(mutation-proof-tests)* **EVIDENCE**: mutated to
  `EstadoDeReposicion.Ok /* MUTATION 2.4 */`, ran
  `LosTresEstadosDeReposicionClasificanCorrectamenteEnExistencias` →
  FAILED (`Assert.Equal() Failure: Values differ Expected: Bajo Actual: Ok`
  at `ExistenciasTests.cs:258`), reverted, ran the full `~Existencias`
  filter → 14/14 green, `git status`/`git diff --stat` clean (revert matched
  committed bytes exactly).
- [x] 2.5 [P] Three-state row assertions: `cantidad = minimo` classifies
  `bajo`; `minimo = null, cantidad = 0` classifies `sin_minimo` (never
  `bajo`); `cantidad` above `minimo` classifies `ok`. *(spec
  `reportes-de-gestion`: scenarios "An articulo at or below its minimo
  classifies bajo", "…classifies sin_minimo, never bajo", "…classifies ok")*
  Implemented as `ExistenciasTests.LosTresEstadosDeReposicionClasificanCorrectamenteEnExistencias`
  (three articulos, all fields distinct — mutation-proof-tests rule 6).
- [x] 2.6 [P] Integration: existencias needs no `idArticulo` (regression,
  re-asserted with the three new columns present). *(spec
  `reportes-de-gestion`: scenario "Existencias needs no idArticulo")*
  `LasExistenciasDe40ArticulosVuelvenSinPedirIdArticulo` extended with
  `Assert.All(...)` over the 40 rows: `Minimo`/`Reposicion` null, `Estado
  == SinMinimo`.
- [x] 2.7 [P] Integration: a Supervisor exports existencias with the
  widened table (regression). *(spec `reportes-de-gestion`: scenario "A
  Supervisor exports existencias")*
  Pre-existing `UnSupervisorExportaLasExistenciasConUnNombreDeArchivoDeterministico`
  still green unmodified — 200 + deterministic filename regression confirmed
  under the widened `FilaExistencia`/export mapper; the cell-level widened
  assertions live in 2.9.
- [x] 2.8 [P] Integration: a Supervisor **reads** the reorder columns on
  `GET /existencias` (`200`, columns present) and gets `403` re-confirmed
  on `PUT /minimos` from this route's vantage point. *(spec
  `reportes-de-gestion`: scenario "A Supervisor reads the reorder columns
  but cannot write them" — closes the read half left open by slice 1's
  1.17)* Implemented as
  `ExistenciasTests.UnSupervisorLeeLasColumnasDeReposicionYEsRechazadoDeEscribirlas`.
- [x] 2.9 [P] Export equality: the widened workbook carries the same
  `minimo`/`reposicion`/`estado` values as the JSON, cell by cell. *(spec
  `reportes-de-gestion`: scenario "The existencias export carries the same
  reorder columns"; `mutation-proof-tests` rule 6)* Extended the existing
  `ElExportEsIgualAlEndpointJsonParaLasDosFilas` (rather than a new test) —
  same two-row equality test now compares all 6 columns per row, with one
  row `SinMinimo` (both new fields null → blank cell) and one `Bajo` (both
  populated), so both `Celda.Cantidad(decimal?)` branches are exercised.
  **judgment-day round 1**: a surviving header-label mutant was found —
  swapping the `"Mínimo"`/`"Reposición"` header titles in `ColumnasExistencias`
  still passed the suite because the equality test only read data cells by
  position from `primeraFilaDeDatos`, never the header row. Closed by adding
  one assert in `ElExportEsIgualAlEndpointJsonParaLasDosFilas` that reads all
  six header texts from the header row (`filaDeEncabezados = 6`) in exact
  order; confirmed the header-swap mutation now fails the suite, reverted,
  suite green again.
- [x] 2.10 [P] Round-trip: `PUT /api/stock/minimos` then
  `GET /existencias` returns the same persisted pair — the first end-to-end
  test that can exercise both routes together, now that the report exposes
  the fields. Implemented as
  `ExistenciasTests.UnRoundTripDeEscrituraYLecturaDevuelveElParPersistido`.
- [x] 2.11 Gate guard: `has-pending-model-changes` clean, zero migration
  files in the diff. **VERIFIED**: `dotnet ef migrations
  has-pending-model-changes --project src/Ways.Infrastructure --startup-project
  src/Ways.Infrastructure` → "No changes have been made to the model since
  the last migration."; `git diff --stat main --
  src/Ways.Infrastructure/Persistencia/Migraciones/` → empty output (zero
  files).
- [x] 2.12 Run `judgment-day`; fix; re-judge until clean. *(CLEAN ROUND 2
  2026-08-14. Round 1: judge B static + LIVE mutation pass — 9 mutations,
  8 killed, 1 SURVIVOR (header-label swap in `ColumnasExistencias`, MAJOR)
  plus a WARNING (seed doc-comment overclaiming all-distinct values). Fix
  commit `067e95c`: six-header-text assert at `filaDeEncabezados = 6` +
  genuinely all-distinct classification seed (minimo {5,null,7}, reposicion
  {20,null,30}), mutation evidence re-recorded. Scoped re-judgment by judge
  B: re-applied the exact mutant → suite FAILS at the new assert; adjacent
  hard-coded-label probe also killed; zero fix-caused defects. Judge A fresh
  read-only pass over the corrected frozen diff at `067e95c`: ZERO findings.
  JUDGMENT: APPROVED — 1 confirmed-and-fixed MAJOR, 1 fixed WARNING, 0
  contradictions.)*
- [x] 2.13 Branch `feat/stage13-slice2-existencias-minimos` off `main`
  (parent: slice 1); PR; merge stacked-to-main.

**APPLY NOTE (Verify line filter mismatch)**: this slice's `Verify` line
below reads `FullyQualifiedName~ExistenciasReport`, but no test class named
`ExistenciasReport*` exists — the actual classes are `ExistenciasTests` and
`ExistenciasExportTests` (same names stage-11 slice 9 already used). Ran
`FullyQualifiedName~Existencias` instead, which matches both real classes
(14 tests, all green) — recorded per instruction to never silently deviate;
not a task, so not re-numbered.

**Test plan**: mutation target (2.4), three-state assertions (2.5),
regressions ×2 (2.6-2.7), Supervisor read (2.8), export equality (2.9),
round-trip (2.10).

**Verify**: `dotnet test --filter FullyQualifiedName~ExistenciasReport`

---

## Slice 3: Web Minimos (PR 3)

**Start**: slices 1+2 merged. **Finish**: `Existencias.tsx` becomes the
per-punto-de-venta stock screen — reorder columns, inline one-row-at-a-time
editing, blocked supersede, add-row, no post-write refetch.
**Rollback**: revert the branch — a web-only change over an unchanged API.

**Budget note (design.md)**: this slice sits closest to the cap. Pre-identified
cut: ship the grid + inline edit first; the articulo add-row (3.4) is the
cut point if the slice overflows during apply.

**APPLY NOTE — budget overflow, cut point NOT exercised (deliberate, recorded
per the instruction to never cut silently)**: the actual diff came in at 6
files changed, **644 insertions(+), 23 deletions(-) = 667 authored changed
lines** (`git diff --shortstat main..HEAD -- src/Ways.Web`), above both the
~380-line estimate and the 400-line budget guard. The pre-identified cut
(drop 3.4, the add-row) was NOT taken: by the time the overflow became
measurable, 3.1-3.11 were already implemented as one cohesive, fully green,
fully committed unit (35 test files, 629/629 vitest green; `dotnet build`
clean; gate guard clean), and un-shipping a working, tested add-row would
have discarded verified work rather than avoided writing it. A meaningful
share of the overflow is test code (`Existencias.test.tsx` alone is +171/-13
— five new tests plus the mutation-target evidence work in 3.6, which itself
needed an extra probe-and-revert cycle once the naive test construction
turned out not to exercise the named clause, per `mutation-proof-tests` rule
3). This is flagged here for the orchestrator's PR-creation step (task
3.13, out of `sdd-apply`'s scope): slice 3, as delivered, needs either a
`size:exception` acknowledgment on this single PR, or a split into two
child PRs (e.g., grid+edit vs. add-row) at PR-creation time — not decided
here, since 3.13 is explicitly the orchestrator's task.

- [x] 3.1 Modify `src/Ways.Web/src/api/{tipos,stock}.ts`: mirror
  `EstadoDeReposicion`, `FilaExistencia` (+3 fields), `SolicitudDeMinimos`,
  `MinimosDeStock`; `clienteDeStock.escribirMinimos`.
- [x] 3.2 Create the pure helper `aSolicitudDeMinimos(idPv, idArticulo,
  minimoTexto, reposicionTexto)` in a colocated module — `'' → null`
  coercion for both inputs.
- [x] 3.3 Modify `src/Ways.Web/src/paginas/Existencias.tsx`: add columns
  `Mínimo`/`Reposición`/`Estado`; state contract — `generacionRef` (existing,
  read staleness), `filaEnEdicion: number | null` (one `idArticulo`),
  `guardando: number | null` (one `idArticulo` in flight). Save applies the
  authoritative `MinimosDeStock` response via a functional updater from
  `prev` (no post-write refetch — decision 16), gated on its captured
  token, `guardando` reset in a token-gated `finally`.
- [x] 3.4 Modify `Existencias.tsx`: **add-row** — an articulo lookup over
  `clienteDeArticulos` (the `Transferencias.tsx` picker pattern), appending
  a row with `cantidad = 0` locally, saved through the same `PUT`.
- [x] 3.5 Modify `Existencias.tsx`: `guardando !== null` disables **every**
  row's "Editar", the PV selector, the download button and the add-row; the
  handler's first line is `if (guardando !== null) return` (beats a
  same-tick double click ahead of the `disabled` re-render). Client-side
  pre-validation mirrors `reposicion_menor_que_minimo` and disables save
  while the aviso is visible — the copy never claims a block the UI does
  not enforce (`react-async-state` rule 7). **APPLY REFINEMENT**: the
  first-line reentrancy guard is `guardandoRef.current !== null`
  (`useRef<number|null>`, mirror of `guardando`), not the literal
  `guardando` (`useState`) named above. Empirically verified with the
  ScratchDisabled probe (see 3.6 evidence) that a `useState`-based guard
  cannot survive a genuine same-tick double click, because React 18
  batches the state commit — two `fireEvent.click`s issued inside one
  `act()` with no render in between would both read the SAME stale
  closure value even with the state check present. The `guardando` state
  itself is kept, unchanged, driving every `disabled` attribute in the
  JSX — this is a targeted refinement of the guard's storage, not a
  change to the disable surface. Same precedent already used in this repo
  (`transfiriendoRef` in `Transferencias.tsx`/`CompraEditor.tsx`).
  **APPLY NOTE — `judgment-day` round 1, slice 3**: this task's disable
  surface ("every row's Editar, the PV selector, the download button and
  the add-row") was infalsifiable as originally shipped — no test asserted
  the `disabled` ATTRIBUTE on any of those surfaces; mutating the Editar
  button's guard from `guardando !== null` down to `guardandoEstaFila`
  (a no-op for any OTHER row) still passed 13/13.
  **CONFIRMED MAJOR (Finding 2)**: closed with a dedicated component test
  that holds a `PUT` in flight and asserts `toBeDisabled()` on the OTHER
  row's Editar button, the PV selector, the download button, the add-row
  search input, and the in-flight row's own Guardar (Finding 6b — see
  below); then resolves the `PUT` inside the test and asserts every one of
  those surfaces re-enables (closes Finding 3's same-scope gap too).
  **EVIDENCE**: mutated the Editar button's `disabled` to
  `/* MUTATION 2 */ guardandoEstaFila`, ran `npx vitest run Existencias -t
  "mientras una fila guarda"` → **FAILED** (`expected element to be
  disabled` / `Received element is not disabled` on row B's Editar
  button). `git checkout -- src/Ways.Web/src/paginas/Existencias.tsx` →
  17/17 green, no `MUTATION` marker left (`grep -rn "MUTATION"` clean).
  **CONFIRMED WARNING (Finding 6b)**: the Guardar button's own `disabled`
  attribute (`!puedeGuardar`) never carried a `guardando` term, so the
  in-flight row's own Guardar stayed clickable while its label read
  "Guardando…". Fixed: `disabled={!puedeGuardar || guardandoEstaFila}`.
  **EVIDENCE**: mutated back to `/* MUTATION 6b */ !puedeGuardar`, ran the
  same "mientras una fila guarda" test → **FAILED** (`Guardando…` button
  `Received element is not disabled`); reverted via `git checkout --` →
  17/17 green.
  **CONFIRMED WARNING (Finding 6a)**: `SelectorDeArticuloParaAlta`'s picker
  effect reset `resultados` on its early-return branch (term < 2 chars) but
  never `buscando` — "Buscando…" could get stuck visible after the term
  shrank back below 2 chars. Fixed with `setBuscando(false)` in that
  branch. **EVIDENCE**: mutated the branch to
  `/* MUTATION 6a */` (dropped the `setBuscando(false)` line), ran
  `npx vitest run Existencias -t "Buscando"` → **FAILED** (`expected
  document not to contain element, found <div class="small text-muted">
  Buscando…</div>`); reverted via `git checkout --` → 17/17 green.
  **REGISTERED, not fixed (Finding 5, WARNING)**: `guardarFila`'s own
  first-line `guardandoRef` re-entrancy guard is defense-in-depth
  unreachable by construction — the window-wide `disabled` attribute this
  task installs already prevents a second concurrent save from ever
  dispatching. Left as-is (belt-and-suspenders, same precedent as
  `transfiriendoRef`/`Transferencias.tsx`); recorded here as known phantom
  coverage, honestly, per the instruction to never hide it.
  **CONFIRMED SUGGESTION (Finding 7)**: `agregarFila` (task 3.4) appended
  the new local row (`cantidad = 0`, unpersisted) BEFORE the `PUT`, and
  `cancelarEdicion` never removed it on cancel — a ghost row stayed in the
  grid. Fixed with `filaLocalSinGuardarRef` (tracks the pending local
  row's `idArticulo`, cleared on a successful save); `cancelarEdicion` now
  filters that row out of `existencias.filas` when it's the one being
  cancelled. **EVIDENCE**: mutated `cancelarEdicion` to
  `/* MUTATION 7 */` (dropped the filter block), ran `npx vitest run
  Existencias -t "cancelar una fila agregada"` → **FAILED** (`expected
  document not to contain element, found <td>Arroz largo fino 1kg</td>`);
  reverted via `git checkout --` → 17/17 green.
  Full suite after all reverts: `npx vitest run` → 633/633 green (35
  files, +4 tests vs. the pre-round-1 629). `npm run build` (`tsc -b &&
  vite build`) → clean.
- [x] 3.6 [P] **Mutation target**: remove the `guardandoRef.current !== null`
  guard from the row-open handler (`abrirFila`) — the "open row B blocked
  while row A is saving, same tick" test must fail. *(mutation-proof-tests,
  design decision 15 — "supersede-during-write mutated across four
  consecutive review rounds in this repo before blocking the window killed
  the class")* **EVIDENCE**: mutated `abrirFila` to
  `/* MUTATION 3.6 */` (guard line removed), ran
  `npx vitest run Existencias -t "abrir la fila B"` → **FAILED** (`expected
  document not to contain element, found <input aria-label="Mínimo de
  Fideos guiseros 500g" ...>` — row B's editor opened despite row A's
  save being in flight, dispatched via two `fireEvent.click` calls inside
  one `act()` so no render/`disabled` update lands between them). Reverted
  the guard line; ran `npx vitest run Existencias` → 13/13 green;
  `git diff --stat -- src/Ways.Web/src/paginas/Existencias.tsx` after
  revert showed no `MUTATION` marker left in the file (`grep -rn
  "MUTATION"` → clean). Full suite re-run after revert: 629/629 green.
  A first attempt at this same test (dispatching the row-B click via a
  single `fireEvent.click` after two AWAITED `userEvent.click`s on
  "Guardar", mirroring `CompraEditor.test.tsx`'s double-click pattern)
  **passed even with the guard removed** — confirmed via an isolated
  probe (`ScratchDisabled.test.tsx`, discarded after use) that jsdom
  silently no-ops `fireEvent.click` on a `disabled` button, so the
  pre-existing `disabled={guardando !== null}` attribute was the actual
  confound killing the mutation's observability, not the removed guard.
  Per `mutation-proof-tests` rule 3 ("kill the confounds, not the
  layers"), the test was re-routed below that confound: both clicks now
  fire inside a single `act()` with zero renders between them, which also
  revealed the `guardando`-state-vs-`guardandoRef` distinction recorded in
  3.5 above.
- [x] 3.7 [P] Descriptor tests: `aSolicitudDeMinimos` coercion branches —
  `''`, `'0'`, `'2.5'`, `'-1'`, `'1,5'`, both-empty (unmanage).
  *(web-descriptor-tests)* Implemented in `stock.test.ts` alongside
  `umbralTextoValido`/`reposicionMenorQueMinimo` (both new pure helpers
  introduced by 3.5's client-side pre-validation).
- [x] 3.8 [P] Component test: save applies the authoritative response
  **without** a refetch — assert no second `GET` fires after the `PUT`
  resolves.
- [x] 3.9 [P] Component test: a stale read landing after a save is
  discarded — a stale promise resolved **inside `act`**, asserted
  synchronously after the flush (`react-async-state` rule 7 — the "committed
  write reported as a failure" class this design's decision 16 exists to
  prevent). Since decision 16 removes the natural read/write race on this
  screen (a save never triggers a report refetch), the test constructs the
  race via a PV round-trip (Centro → Norte, held pending → back to Centro,
  resolved fast) to leave a genuinely stale, still-in-flight Norte read;
  the save happens on the fresh Centro generation; the stale Norte
  response is resolved last, inside `act`, and asserted to never land.
- [x] 3.10 [P] Component test: double-click on save ⇒ exactly one `fetch`;
  open-row-B **blocked** while row A is saving (ties to the 3.6 mutation).
  Implemented as two tests: the double-click case mirrors the
  `CompraEditor.test.tsx` "await click twice" convention; the
  same-tick supersede-blocked case is the dedicated mutation-target test
  from 3.6 (single `act()`, two `fireEvent.click`s, no render between
  them — see 3.6 evidence for why the naive single-`fireEvent.click`
  variant does not exercise the mutation).
  **APPLY NOTE — `judgment-day` round 1, slice 3, CONFIRMED WARNING
  (Findings 3+4)**: the token-gated `finally` in `guardarFila`
  (`if (tokenDeEscrituraRef.current === miToken) { guardandoRef.current =
  null; setGuardando(null) }`) had zero coverage of its own reset — every
  existing test only ever saved ONE row per render tree, so a permanent
  lockup of the whole window after any single save (dropping
  `setGuardando(null)`) passed 13/13 unnoticed. Closed with a sequential
  test: save row A end-to-end, then open/edit/save row B — the SECOND row,
  not the first — and assert the second `PUT`'s payload and the grid
  update both target B (kills the "always-first-row" updater class too,
  Finding 4, without a separate test). **EVIDENCE (a)**: dropped
  `setGuardando(null)` from the `finally` (left `guardandoRef.current =
  null`), ran `npx vitest run Existencias -t "guardar la fila A"` →
  **FAILED** (row B's "Editar" query resolved to zero elements —
  `getAllByLabelText` found nothing for "Mínimo de Fideos guiseros 500g",
  the window stayed disabled after A's save); `git checkout --
  src/Ways.Web/src/paginas/Existencias.tsx` → 17/17 green. **EVIDENCE
  (b)**: hard-coded the save-response updater's row match to `i === 0`
  (`prev.filas.map((f, i) => i === 0 ? ... : f)`) instead of
  `f.idArticulo === resultado.idArticulo`, ran the same test → **FAILED**
  (row B's "Bajo"/"10" never landed — `filaB.getByText('Bajo')` found no
  match, the response was applied to row A instead); reverted via `git
  checkout --` → 17/17 green.
  **APPLY NOTE — `judgment-day` round 2, slice 3 (FINDING 3)**: the
  "doble click en Guardar" test above (`CompraEditor.test.tsx` awaited
  double-click convention) proves the Guardar button's `disabled`
  ATTRIBUTE, not `guardarFila`'s own first-line `guardandoRef` guard —
  `userEvent.click` no-ops on an already-`disabled` button (same confound
  documented in 3.6's evidence for `abrirFila`), so removing that guard
  line still passed 17/17. Round 1's Finding 5 ("defense-in-depth,
  unreachable by construction") was therefore an artifact of that gap, not
  a settled fact. Closed with a dedicated same-tick test (two
  `fireEvent.click`s on Guardar inside one `act()`, zero renders between
  them — same pattern as the 3.6 mutation target) that exercises the guard
  BEFORE the `disabled` attribute re-renders. See FINDING 3 evidence below
  task 3.12.
- [x] 3.11 Gate guard: `has-pending-model-changes` clean, zero migration
  files in the diff (web-only slice — confirms no accidental API/EF drift).
  **VERIFIED**: `dotnet ef migrations has-pending-model-changes --project
  src/Ways.Infrastructure --startup-project src/Ways.Infrastructure` →
  "No changes have been made to the model since the last migration.";
  `git diff --stat main -- src/Ways.Infrastructure/Persistencia/Migraciones/`
  → empty output (zero files); `git status --porcelain` shows only 6
  `src/Ways.Web/**` files modified; `dotnet build --no-restore` → 0
  errors.
- [x] 3.12 Run `judgment-day`; fix; re-judge until clean.
  **ROUND 2 (judgment-day, judge B re-round)**: 1 MAJOR (fix-caused, judge
  B PROVED LIVE) + 2 WARNING confirmed and closed.
  **CONFIRMED MAJOR (Finding 1, fix-caused by round 1's Finding 7 fix)**:
  `cambiarPuntoVenta` cleared `filaEnEdicion` but never
  `filaLocalSinGuardarRef` — `cargar()` replaces the grid wholesale
  (mount, PV change, Reintentar) without touching that ref. Corruption
  sequence proved live by the judge: add articulo X via the picker on PV A
  (ref := X, unsaved) → switch PV before saving → the new PV happens to
  have a PERSISTED row with the same idArticulo X → Editar + Cancelar on
  that persisted row → `cancelarEdicion` matches the stale ref and DELETES
  the real, persisted row. Fixed with a single clear point: `cargar()`
  itself resets `filaLocalSinGuardarRef.current = null` right after its
  `idPuntoVenta === null` guard — since `cargar` is the only function that
  ever replaces `existencias.filas` wholesale (mount effect, PV switch via
  `cambiarPuntoVenta`'s state change, and the Reintentar button), a single
  clear there covers every path that can strand the ref, instead of
  duplicating the clear at each call site. **EVIDENCE**: mutated `cargar`
  to `/* MUTATION 1 */` (dropped the clear line), ran `npx vitest run
  Existencias -t "FINDING 1"` → **FAILED** (`Arroz persistido en Norte`
  gone from the grid — `getByText` found no match, replaced by "No hay
  stock cargado para este punto de venta."); `git checkout --
  src/Ways.Web/src/paginas/Existencias.tsx` → 20/20 green, no `MUTATION`
  marker left.
  **CONFIRMED WARNING (Finding 2, coverage gap on the pre-existing
  success-path clear)**: the `filaLocalSinGuardarRef.current = null` line
  already shipped in `guardarFila`'s success path (round 1, task 3.4/3.6)
  had zero direct coverage of its own removal — dropping it still passed
  17/17. Closed with a dedicated test: add a row via the picker, save it
  (PUT resolves OK), then Editar + Cancelar the now-PERSISTED row — it
  must stay in the grid. **EVIDENCE**: mutated the success-path clear to
  `/* MUTATION 2 */`, ran `npx vitest run Existencias -t "FINDING 2"` →
  **FAILED** (`Arroz largo fino 1kg` gone after Cancelar); reverted via
  `git checkout --` → 20/20 green.
  **CONFIRMED WARNING (Finding 3, stale mutation-proof claim on 3.10)**:
  with Guardar now attribute-`disabled` while in flight, the existing
  "doble click en Guardar" test (3.10) proves the `disabled` ATTRIBUTE,
  not `guardarFila`'s own first-line `guardandoRef` guard — removing that
  guard line still passed 17/17, because `userEvent.click` no-ops on an
  already-disabled button before ever reaching the handler (same confound
  as 3.6's `abrirFila` evidence). Round 1's Finding 5 ("unreachable by
  construction") is retracted as stated: the guard IS reachable in a
  genuine same-tick double click, where the `disabled` attribute has not
  re-rendered yet. Closed with a same-tick test mirroring 3.6's mutation
  target (two `fireEvent.click`s on Guardar inside one `act()`, zero
  renders between them), asserting exactly one PUT. **EVIDENCE**: mutated
  `guardarFila`'s first line to `/* MUTATION 3 */` (guard removed), ran
  `npx vitest run Existencias -t "FINDING 3"` → **FAILED** (`expected
  "vi.fn()" to be called 1 times, but got 2 times`); reverted via `git
  checkout --` → 20/20 green. See the cross-reference note on task 3.10
  above for the corrected framing of the double-click vs. same-tick tests.
  Full suite after all reverts: `npx vitest run` → 20/20 in
  `Existencias.test.tsx` (full repo suite unaffected — web-only change, no
  other file touched). `npm run build` (`tsc -b && vite build`) → clean.
  `git status --porcelain` clean, no stray `MUTATION` markers
  (`grep -rn "MUTATION"` on both touched files → empty).
  **ROUND A (judgment-day, judge A ledger, scoped fix-agent run)**: 1
  CRITICAL + 2 WARNING confirmed and closed.
  **CONFIRMED CRITICAL (Finding 1)**: `filaLocalSinGuardarRef`'s success-path
  clear in `guardarFila` was unconditional — `agregarFila(X)` sets the ref to
  X and appends the ghost row, then opening and saving a DIFFERENT
  preexisting row Y (allowed: `abrirFila` only checks `guardandoRef`, not
  `filaEnEdicion`) cleared the ref to `null` regardless of which row it just
  saved, orphaning X (`cancelarEdicion` matches by identity against
  `filaEnEdicion`, so it could no longer find X to remove it). Fixed by
  gating the clear on identity: `if (fila.idArticulo ===
  filaLocalSinGuardarRef.current) filaLocalSinGuardarRef.current = null`.
  `cancelarEdicion`'s own removal already matched by identity, so no other
  change was needed. **EVIDENCE**: mutated the clear back to unconditional
  (`filaLocalSinGuardarRef.current = null`), ran `npx vitest run
  Existencias -t "FINDING 1 CRITICAL"` → **FAILED** (`Arroz largo fino 1kg`
  still in the document after Cancelar — the ghost survived, proving the
  orphan); reverted via targeted edit (not `git checkout --`, to avoid
  wiping the other two round-A fixes staged in the same uncommitted file) →
  23/23 green in `Existencias.test.tsx`.
  **CONFIRMED WARNING (Finding 2, precedent-matching write-role gate)**:
  `Existencias.tsx` had no client-side write-role gate even though `PUT
  /api/stock/minimos` is Admin-only server-side (`GestionDeCatalogo`) while
  the route itself admits Supervisor+Admin (`Politicas.LecturaDeReportes`).
  Replicated the exact `CompraEditor.tsx:492` precedent: `puedeEscribir =
  usuario !== null && usuario.rolId === ROL.Admin`, hiding the Editar
  buttons, Guardar/Cancelar, and the `SelectorDeArticuloParaAlta` add-row
  when `false`; reading (all columns) stays open to Supervisor. Test
  fixture default switched from Supervisor to Admin (same convention as
  `CompraEditor.test.tsx`) since most of this file's tests exercise write
  actions; the "role gating" describe's Supervisor test now sets the role
  explicitly. **EVIDENCE**: mutated `puedeEscribir` to a hardcoded `true`,
  ran `npx vitest run Existencias -t "FINDING 2"` → **FAILED** (the
  Supervisor-render test found an "Editar" button that should have been
  hidden); reverted via targeted edit → 23/23 green.
  **CONFIRMED WARNING (Finding 3, `disabled` attribute missing on rendered
  result buttons)**: `SelectorDeArticuloParaAlta`'s search-result buttons
  never received the `disabled` prop (only the search input did) — the
  click was stopped by `agregarFila`'s `guardandoRef` guard, but the
  rendered attribute lied. Wired `disabled={disabled}` onto the result
  buttons. Extended the existing full-window "TODA la ventana queda
  attribute-disabled" test (task 3.5) to leave a result visible before
  triggering an in-flight PUT, then assert that result button is
  `toBeDisabled()`. **EVIDENCE**: removed the `disabled` prop from the
  result buttons, ran `npx vitest run Existencias -t "TODA la ventana"` →
  **FAILED** (`Received element is not disabled`); reverted via targeted
  edit → 23/23 green.
  Full suite after all reverts: `grep -rn "MUTATION"` on both touched files
  → empty; `npx vitest run` → 35 files, 639/639 green (full repo suite,
  web-only change — no other file touched); `npm run build` (`tsc -b &&
  vite build`) → clean, 0 type errors.
  **ROUND A FINAL (judgment-day, judge A ledger, scoped fix-agent run)**: 1
  CRITICAL residual confirmed and closed.
  **CONFIRMED CRITICAL (residual of Finding 1)**: round A's identity-gated
  clear in `guardarFila`'s success path fixed the *symptom* (a save on a
  different row no longer orphans the ghost's ref) but not the *class* of
  bug — `agregarFila` still overwrote the single-slot ref without checking
  for a prior ghost, and `SelectorDeArticuloParaAlta` was never gated by
  `filaEnEdicion`: it stayed visible and enabled as soon as the first ghost
  was added (no PUT in flight needed). Sequence: add X via the picker (X
  opens for edit) → without saving or cancelling, add Z via the still-visible
  picker → the ref gets overwritten to Z → X is now permanently orphaned in
  the grid (reopening X + Cancelar fails the identity match against the new
  ref value). **Structural fix** (kills the class, not the symptom):
  condition the picker's render on `filaEnEdicion === null` —
  `{puedeEscribir && filaEnEdicion === null && (...)}`. With that, two
  unsaved ghosts can never coexist: adding one opens it for edit and the
  picker disappears until it is saved or cancelled. No extra ref logic
  added. Rewrote the "TODA la ventana queda attribute-disabled" test (task
  3.5): the picker is now *absent* (not merely `disabled`) as soon as any
  row is in edit, which supersedes the old `disabled`-attribute assertion on
  the picker's input/result buttons — reflected inline in the test's own
  comments. Added the judge's exact required sequence as a new test: add X
  via the picker → without saving/cancelling, assert the picker is gone →
  Cancelar X → X is removed and the picker reappears. **EVIDENCE**: removed
  the `filaEnEdicion === null` condition from the render, ran `npx vitest
  run Existencias -t "agregar X por el picker sin guardar"` → **FAILED**
  (picker still present with X in edit); reverted via `git checkout --` →
  clean working tree, fix restored from the prior commit. Full suite after
  revert: `npx vitest run` → 35 files, 640/640 green (full repo suite,
  web-only change — no other file touched); `npm run build` (`tsc -b &&
  vite build`) → clean, 0 type errors.
  **ROUND A-3 (judgment-day, judge A ledger, scoped fix-agent run,
  additional round)**: 1 CRITICAL residual confirmed and closed — third
  variant of the same class. Round A's structural fix (`filaEnEdicion ===
  null` gates the picker) still leaves a path open: add X via the picker
  (X opens for edit, `filaEnEdicion = X`, picker hidden) → `abrirFila` on
  a persisted row Y only checks `guardandoRef`, not any ghost state, so
  Editar on Y is allowed while X is still unsaved (`filaEnEdicion = Y`, X
  benched) → Cancelar Y: the identity match against
  `filaLocalSinGuardarRef` correctly fails (Y ≠ X), so nothing is removed,
  and `filaEnEdicion` goes back to `null` → the picker's render condition
  (`filaEnEdicion === null`) is now satisfied even though the unsaved
  ghost X is still alive in the grid → the picker reappears → adding a
  second row W lets `agregarFila` overwrite the ref's single slot →  X is
  now permanently orphaned. **Root cause**: the correct render condition
  is "no unsaved ghost exists", not "no row is being edited" — those are
  different predicates, and round A's fix conflated them.
  `filaLocalSinGuardarRef` (a `ref`) cannot govern render either way:
  mutating a ref does not trigger a re-render, so any render-time
  condition has to read committed React state. **Fix**: added
  `filaFantasma` (`useState<number | null>`), an exact state mirror of
  `filaLocalSinGuardarRef` — same pattern already used for
  `guardando`/`guardandoRef` in this same component (the ref is for
  synchronous guards, the state is for render). Every write to the ref
  (`agregarFila`'s set, the identity-gated clear in `guardarFila`'s
  success path, the removal in `cancelarEdicion`, and the reset in
  `cargar`) now has an adjacent write to `filaFantasma`, each with a
  one-line comment naming the mirror. Picker render condition became
  `puedeEscribir && filaEnEdicion === null && filaFantasma === null`.
  Added the judge's exact required sequence as a new test: add X via the
  picker → Editar the persisted row Y → Cancelar Y → assert the picker is
  still absent (`queryByLabelText` null) → reopen X → Cancelar X → X is
  removed and the picker reappears. **EVIDENCE**: reverted the render
  condition to `filaEnEdicion === null` only (leaving `filaFantasma`
  unused), ran `npx vitest run Existencias -t "abrir y cancelar Y"` →
  **FAILED** (picker present after cancelling Y); reverted via `git
  checkout --` → clean working tree, fix restored from the prior commit.
  Full suite after revert: `npx vitest run` → 35 files, 641/641 green
  (full repo suite, web-only change — no other file touched); `npm run
  build` (`tsc -b && vite build`) → clean, 0 type errors.
- [x] 3.13 Branch `feat/stage13-slice3-web-minimos` off `main` (parent:
  slices 1+2); PR; merge stacked-to-main. *(FINAL VERDICT 2026-08-14:
  JUDGMENT: APPROVED at HEAD `678f1c2` after the longest judgment-day of
  the program — judge B round 1 (2 MAJORs: deleted checklist items,
  unfalsifiable full-window disable; +4 deterministic WARNINGs), two
  scoped B re-judgments (round 2 caught a MAJOR fix-caused stale-ref
  corruption LIVE), and three judge-A rounds on the ghost-row mechanism
  (unconditional clear → picker unGated on filaEnEdicion → picker
  reappearing with a benched ghost), closed structurally with the
  filaFantasma state mirror (a ref never governs render). Judge B's final
  hygiene pass over the post-approval delta: 4 re-mutations killed exactly
  their tests, puedeEscribir hollowing ruled out empirically (16 failed
  under a forced false — none vacuous). Both ledgers clean on the final
  candidate.)*

**Test plan**: mutation target (3.6), coercion descriptors (3.7), no-refetch
(3.8), stale-read-discarded (3.9), double-click + supersede-blocked (3.10),
phantom-ref-on-reload + saved-row-cancel + same-tick save guard (3.12 round
2, FINDINGS 1/2/3), ghost-row-identity-gated-clear + write-role gate +
result-button `disabled` attribute (3.12 round A, FINDINGS 1/2/3).

**Verify**: `npm run test -- Existencias` — 25/25 green (full suite:
`npx vitest run` → 35 files, 641/641 green — updated post-`judgment-day`
round A-3, see the apply notes on task 3.12). `npm run lint`
(oxlint) → clean (one pre-existing, unrelated warning in `AuthContext.tsx`).
`npm run build` (`tsc -b && vite build`) → clean.

---

## Slice 4: Reposición (PR 4)

**Start**: slice 1 merged (parallel to slice 2/3's front). **Finish**: the
reposición read model exists — `minimo IS NOT NULL AND cantidad <= minimo`,
LEFT-JOINed to `proveedores`, grouped by `id_proveedor_habitual` — with its
JSON endpoint and `/export` sibling. **No rotation fields yet** on
`FilaDeReposicion`; slice 5 adds them. **Rollback**: revert the branch — no
other front depends on this slice's code, only slices 5-7 depend on its
contract.

**Budget note (design.md)**: the other slice closest to the cap. Pre-identified
cut: the report/export boundary — ship the JSON endpoint (4.1-4.3, 4.5-4.9)
first, the export sibling (4.4, 4.10) is the cut point if this overflows.

- [x] 4.1 Modify `src/Ways.Application/Reportes/ServicioDeReportesDeStock.cs`:
  private `ConstruirQueryDeReposicion(idPuntoVenta)`:
  ```csharp
  from s in db.Stock
  where s.IdPuntoVenta == idPuntoVenta && s.Minimo != null && s.Cantidad <= s.Minimo
  join a in db.Articulos on s.IdArticulo equals a.Id
  join p in db.Proveedores on a.IdProveedorHabitual equals p.Id into candidatos
  from p in candidatos.DefaultIfEmpty()
  orderby a.IdProveedorHabitual, a.Id
  select new FilaCrudaDeReposicion(
      a.Id, a.Nombre, s.Cantidad, s.Minimo!.Value, s.Reposicion,
      a.IdProveedorHabitual, p == null ? null : p.RazonSocial);
  ```
  The LEFT JOIN is **not** filtered by empresa — `id_proveedor_habitual` is
  authoritative regardless of the proveedor's own empresa scoping (design
  decision 3). Postgres orders `NULL` last in `ASC` by default, so no
  explicit `NULLS LAST` is needed for *Sin proveedor* to land last.
  *(apply note — `judgment-day` round 1, decision 12: the snippet above is
  the DRAFT this task pinned, not the shipped code. A soft-deleted
  proveedor's FK disagreeing with its display group (order key ≠ display
  group) is a real defect against design decision 3's "lands under Sin
  proveedor" — fixed by projecting `IdProveedor := p == null ? null :
  (int?)a.IdProveedorHabitual` and ordering `orderby (p == null),
  a.IdProveedorHabitual, a.Id`. See decision 12 above for the full
  rationale and mutation evidence.)*
- [x] 4.2 Modify `ServicioDeReportesDeStock.cs`:
  `ObtenerReposicionAsync(idPuntoVenta, dias?, ct)` —
  `ResolverContextoAsync` → `(idEmpresa, zonaId, hoy)`; `diasDeRotacion :=
  dias ?? dias_rotacion` through `ExigirVentanaValida` (`400
  dias_rotacion_invalido`); run `ConstruirQueryDeReposicion`; if empty,
  return `Reposicion(…, [])` **without** touching `movimientos_stock`
  (decision 12 — the rotation call itself is slice 5's territory, but the
  empty-set short-circuit is wired here so slice 5 only has to fill it in);
  project `Sugerido` via `ReglaDeReposicion.Sugerido(cantidad, reposicion)`
  per row — pure, no rotation dependency.
  *(apply note: design's Application Service Surfaces section labels
  `ResolverDiasRotacionAsync` "privados, slice 5" alongside
  `ResolverDiasCoberturaAsync`, but resolving `dias ?? dias_rotacion` is
  literally required by this task's text — added the resolver here,
  `ResolverDiasAlertaAsync`-shaped, and left `ResolverDiasCoberturaAsync`
  for slice 5, whose only consumer — `MinimoSugerido` — doesn't exist yet.
  Not a scope deviation: the design's grouping note and this task's literal
  requirement disagree, and the task text governs.)*
  *(judgment-day round 1, confirmed MAJOR #2: the `ExigirVentanaValida` call
  wired here shipped with ZERO test coverage — every existing test omitted
  `?dias=`, so deleting the guard call left all 8 tests green, and the
  `DiasDeRotacion` echo was equally unasserted (a hard-coded value would
  have passed too). Closed by adding
  `UnDiasDeRotacionInvalidoEsRechazadoConCuatrocientos` (`?dias=0` → 400
  `dias_rotacion_invalido`) and
  `LaRespuestaEcoaElDiasDeRotacionEfectivamenteResuelto` (`?dias=45` echoes
  `45`; `dias` omitted echoes the `dias_rotacion` default `30`) to
  `ReposicionReporteTests.cs`. Mutation evidence: deleting the
  `ExigirVentanaValida` call makes the first new test FAIL (200 instead of
  400); reverting restores green.)*
- [x] 4.3 Modify `src/Ways.Application/Reportes/Contratos.cs`:
  `FilaDeReposicion(IdArticulo, Articulo, Cantidad, Minimo, Reposicion?,
  Sugerido?, IdProveedor?, Proveedor?)` — no rotation fields in this slice;
  `Reposicion(IdPuntoVenta, Hoy, DiasDeRotacion, ZonaHoraria, Filas)`.
  `dto-contract-honesty`: doc-comment `Sugerido` as null-not-zero when
  `Reposicion` unset; doc-comment that `ConsumoDiarioPromedio`/
  `DiasDeCobertura` are **added by slice 5**, not omitted by oversight.
- [x] 4.4 Modify `src/Ways.Application/Reportes/ExportacionDeReportes.cs`:
  one `De(Reposicion, ctx)` mapper — the aggregate cap shape (guard on
  `TablaExportable.Filas.Count` after mapping, no `COUNT(*)`), the same
  method backs both the JSON and the export (design decision 13 — no
  `ObtenerReposicionParaExportacionAsync` twin).
- [x] 4.5 Modify `src/Ways.Api/Endpoints/ReportesEndpoints.cs`:
  `GET /reportes/stock/reposicion?idPuntoVenta[&dias]` and
  `GET /reportes/stock/reposicion/export?…&formato=xlsx`, both under
  `Politicas.LecturaDeReportes` (inherited).
- [x] 4.6 [P] **Mutation target — DISPROVEN, evidence recorded, not a
  silent skip**: `s.Minimo != null` — deleted it, ran the seeded-articulo
  test (`minimo = null, cantidad = 0`), and the row stayed **absent** —
  the mutation did NOT make it appear. Root cause confirmed via
  `ConstruirQueryDeReposicion(...).ToQueryString()` on both versions:
  Npgsql translates `s.Minimo != null` to an additive `s.minimo IS NOT
  NULL`, but `s.cantidad <= s.minimo` alone already excludes every
  `minimo`-NULL row through SQL's three-valued logic (`x <= NULL` is
  always unknown, for any `x`) — the explicit null check is row-admission
  REDUNDANT for this query shape, so no seed can turn it into a
  discriminating mutation target (`mutation-proof-tests` rule 3 exhausted:
  the confound is SQL's own NULL semantics, not another layer to route
  around). The clause stays in the code for documentary intent (design
  decision 1, "minimo NULL ⇒ unmanaged") — its doc-comment in
  `ConstruirQueryDeReposicion` and the test's doc-comment in
  `ReposicionReporteTests.UnArticuloSinMinimoNuncaApareceEnLaReposicion`
  were both corrected to state this plainly instead of the disproven
  mutation-target claim. The scenario itself (spec `reposicion-de-stock`:
  "Minimo Is A Fixed, Owner-Set Reorder Point…", scenario 1) is still
  covered as ordinary spec coverage — just not as mutation-proof evidence.
  *(spec `reposicion-de-stock`: "Minimo Is A Fixed, Owner-Set Reorder
  Point…", scenario 1; mutation-proof-tests)*
- [x] 4.7 [P] **Mutation target**: `candidatos.DefaultIfEmpty()` → inner
  join — the *Sin proveedor* row must disappear once mutated.
  *(mutation-proof-tests)*
- [x] 4.8 [P] **Mutation target**: `orderby a.IdProveedorHabitual, a.Id` —
  delete the first key — the row-sequence assertion (9.9's *Sin proveedor*
  no longer last) must fail. *(mutation-proof-tests)*
- [x] 4.9 [P] Discriminating-seed integration test, one PV, every field of
  every row asserted with different values per row/column, row order
  asserted as a sequence: an articulo `cantidad = minimo` (**appears**);
  `cantidad = minimo + 0.001` (absent); `minimo = null, cantidad = 0`
  (absent); `minimo = 0, cantidad = 0` (**appears**); below-minimo with
  `id_proveedor_habitual = null` (**appears**, `proveedor` null, ordered
  **last**); below-minimo whose proveedor is soft-deleted (appears under
  *Sin proveedor*); below-minimo **at another PV** (absent); below-minimo
  **of another tenant** (absent); `reposicion` unset (`sugerido` **null**).
  *(spec `reposicion-de-stock`: "Minimo Is A Fixed…" scenarios 1-2, "The
  Low-Stock Boundary Is Inclusive" all 3 scenarios, "Reposición Report Is
  The Alert And The Purchase Suggestion…" scenarios 1-3;
  mutation-proof-tests rules 4 and 6)*
- [x] 4.10 [P] Export equality: identical query string on `/reposicion` and
  `/reposicion/export`, every cell of every row compared, including the
  *Sin proveedor* row's empty proveedor cell and a null `sugerido` cell
  rendering **empty, not `0`**; plus a cap refusal (`TopeDeFilas` lowered)
  that **refuses rather than truncates**. *(spec `reposicion-de-stock`:
  "The Reposición Export Sibling Is Catalog-Bounded, Never Truncated", both
  scenarios; mutation-proof-tests rule 6)* *(judgment-day round 1, confirmed
  MAJOR #1: the row-equality test read only data rows starting at row 7 and
  never asserted row 6 (`FilaDeTituloDeTabla` in `ExportadorXlsx.cs`), so a
  header-label swap in `ColumnasReposicion` (`ExportacionDeReportes.cs`)
  shipped with zero coverage — all 3 export tests stayed green under the
  mutation. Closed by adding the header-row assertion (reads cells
  `(6,1)..(6,7)`, compares against the 7 `ColumnasReposicion` titles in
  order) to `ElExportEsIgualAlEndpointJsonEnTodasLasColumnasIncluidasLasCeldasVacias`,
  the same pattern `ExistenciasExportTests.ElExportEsIgualAlEndpointJsonParaLasDosFilas`
  already established on `main`. Mutation evidence: swapping two titles in
  `ColumnasReposicion` makes the new header assert FAIL; reverting restores
  green.)*
- [x] 4.11 [P] Authorization: a Vendedor gets `403` on the reposición
  report and its export. *(spec `reposicion-de-stock`: "Reposición Report…",
  scenario "A Vendedor is rejected from the reposición report and its
  export")*
- [x] 4.12 Gate guard: `has-pending-model-changes` clean, zero migration
  files in the diff. *(confirmed: `dotnet ef migrations has-pending-model-changes`
  from `src/Ways.Infrastructure` → "No changes have been made to the model
  since the last migration."; `git diff --stat main --
  src/Ways.Infrastructure/Persistencia/Migraciones/` → empty)*
- [x] 4.13 Run `judgment-day`; fix; re-judge until clean. *(CLEAN ROUND 2
  2026-08-14. Round 1: judge B static + LIVE mutation pass — 10 mutations
  plus a live re-run of the 4.6 disproof (confirmed honest), 8 killed, 2
  SURVIVORS: (M10, MAJOR) ColumnasReposicion header swap — no test read the
  workbook header row; (M9, MAJOR) deleting ExigirVentanaValida passed 8/8 —
  no test sent `?dias=` at all. Plus 1 WARNING (inferential): soft-deleted
  proveedor sorted mid-list at its raw-FK position with null name,
  contradicting design decision 3's "lands under Sin proveedor" — resolved
  as Orchestrator Decision #12 (dangling FK projected null + presence-first
  ordering → single trailing bucket). Fix commit `da25a70`: 7-header assert
  at row 6, `?dias=0`→400 `dias_rotacion_invalido` + echo tests (45 and
  default 30), decision-12 projection/ordering with seed resequenced.
  Scoped re-judgment by judge B: all 4 re-mutations killed (headers, guard,
  hard-coded echo probe, presence-key drop), zero fix-caused defects,
  10/10 green. Judge A fresh read-only pass over the corrected frozen diff:
  ZERO findings (verified FilaDeTituloDeTabla=6, the soft-delete query
  filter as pre-existing infra, and the 4.6 disproof's three-valued-logic
  reasoning). JUDGMENT: APPROVED — 2 confirmed-and-fixed MAJORs, 1 WARNING
  resolved by decision #12, 0 contradictions.)*
- [x] 4.14 Branch `feat/stage13-slice4-reposicion` off `main` (parent:
  slice 1); PR; merge stacked-to-main.

**Test plan**: 3 mutation targets (4.6-4.8) — **4.6 disproven with
recorded evidence** (SQL NULL semantics make `s.Minimo != null`
row-admission redundant; see 4.6's note), 4.7-4.8 confirmed — discriminating
seed (4.9), export equality + cap refusal (4.10), authorization (4.11).

**Verify**: `dotnet test --filter FullyQualifiedName~ReposicionReport`

---

## Slice 5: Rotación (PR 5)

**Start**: slice 4 merged. **Finish**: `LeerConsumoAsync` exists as the
one private consumption primitive with two callers; `ObtenerReposicionAsync`
gains `ConsumoDiarioPromedio`/`DiasDeCobertura` per row;
`GET /reportes/stock/rotacion` exists as the standalone feed for
`minimoSugerido`. **Rollback**: revert the branch — `FilaDeReposicion`
reverts to slice 4's shape (no rotation columns), `/reposicion` still
functions.

- [x] 5.1 Modify `ServicioDeReportesDeStock.cs`: private
  `LeerConsumoAsync(idPuntoVenta, idsArticulo?, desdeUtc, hastaUtcExclusivo,
  ct)`:
  ```csharp
  var query = db.MovimientosStock
      .Where(m => m.IdPuntoVenta == idPuntoVenta
               && m.CreadoEl >= desdeUtc && m.CreadoEl < hastaUtcExclusivo
               && (m.Motivo == MotivoStock.Venta
                   || (m.Motivo == MotivoStock.Anulacion && m.IdComprobanteCompra == null)));
  if (idsArticulo is not null) query = query.Where(m => idsArticulo.Contains(m.IdArticulo));
  return await query.GroupBy(m => m.IdArticulo)
      .Select(g => new { IdArticulo = g.Key, Neto = g.Sum(m => m.Cantidad) })
      .ToDictionaryAsync(x => x.IdArticulo, x => x.Neto, ct);
  ```
  Plain LINQ over `db.MovimientosStock` — no raw SQL, `LectorDeSerieTemporal`
  untouched (design decision 7). The caller negates: `-neto`. Implemented
  verbatim as the pinned snippet, returning `IReadOnlyDictionary<int, decimal>`.
- [x] 5.2 Modify `ServicioDeReportesDeStock.cs`: wire `ObtenerReposicionAsync`
  — when `filas.Count > 0`, compute `(desdeUtc, hastaUtc) :=
  ReglaDeReposicion.VentanaDeRotacion(hoy, diasDeRotacion, zona)`, call
  `LeerConsumoAsync(pv, filas.ids, desdeUtc, hastaUtc, ct)`, project
  `ConsumoDiarioPromedio := ReglaDeReposicion.ConsumoDiario(consumo.TryGet(id)
  ? -neto : null, diasDeRotacion)` and `DiasDeCobertura :=
  ReglaDeReposicion.DiasDeCobertura(cantidad, consumoDiarioPromedio)` per
  row. When `filas.Count == 0`, the rotation query is skipped entirely
  (decision 12 — a PV with no minimums costs exactly one query). Wired as
  written; `zona` resolved via `TimeZoneInfo.FindSystemTimeZoneById(zonaId)`.
- [x] 5.3 Modify `Contratos.cs`: widen `FilaDeReposicion` with
  `ConsumoDiarioPromedio?`, `DiasDeCobertura?`; add `FilaDeRotacion(IdArticulo,
  Articulo, ConsumoEnVentana, ConsumoDiarioPromedio, MinimoSugerido)` and
  `Rotacion(IdPuntoVenta, Hoy, DiasDeRotacion, DiasCoberturaObjetivo,
  ZonaHoraria, Filas)`. `dto-contract-honesty`: doc-comment that an articulo
  absent from `Rotacion.Filas` means "no qualifying movement", never a row
  with zero-valued fields (design decision 14).
- [x] 5.4 Modify `ServicioDeReportesDeStock.cs`:
  `ObtenerRotacionAsync(idPuntoVenta, dias?, ct)` — same consumption
  definition and window resolution as 5.1-5.2 (never a second definition),
  one row per articulo with a qualifying movement in the window; an
  articulo with none is **absent**, never present with `minimoSugerido = 0`.
  Resolves `dias_cobertura_objetivo` via a new private
  `ResolverDiasCoberturaAsync` (design's private-surfaces grouping, slice 5).
  A soft-deleted articulo whose ledger still has qualifying movements is
  filtered out of `Filas` via the EF global filter on `Articulo` — same
  inherited trade-off as `ExistenciasTests.UnArticuloEliminadoNuncaApareceEnLasExistencias`
  (design: Open Questions), documented inline rather than crashing on a
  missing name.
- [x] 5.5 Modify `ReportesEndpoints.cs`:
  `GET /reportes/stock/rotacion?idPuntoVenta[&dias]`, `Politicas.LecturaDeReportes`.
- [x] 5.6 [P] **Mutation target**: delete `&& m.IdComprobanteCompra == null`
  from `LeerConsumoAsync`'s filter — the netting-trap test (5.10) must fail.
  *(mutation-proof-tests)* **EVIDENCE**: mutated to
  `(m.Motivo == MotivoStock.Anulacion /* MUTATION 5.6 */)`, ran
  `LaRotacionNoNeteaLaAnulacionDeUnaCompraDentroDeLasVentas` → FAILED
  (`Assert.Equal() Failure: Expected: 5 Actual: 20` — the compra's −15
  reversal negated back in as +15 consumption, 8−3+15=20), reverted,
  `git status`/`git diff --stat` clean, suite green again.
- [x] 5.7 [P] **Mutation target**: `VentanaDeRotacion`'s zone conversion —
  replace with `hoy`/edges computed from `reloj.Ahora.UtcDateTime` — the
  midday-UTC boundary test (5.11) must fail. *(mutation-proof-tests, the
  stage-11 slice-9 bug class hardened by `08e7707`)* **EVIDENCE**: the
  wiring-level equivalent of this mutation is the zone argument
  `ObtenerRotacionAsync` passes into `VentanaDeRotacion` (the pure function
  itself is already unit-proven by `ReglaDeReposicionTests`) — mutated
  `TimeZoneInfo.FindSystemTimeZoneById(zonaId)` to `TimeZoneInfo.Utc /*
  MUTATION 5.7 */` in that call, ran
  `LaVentanaDeRotacionResuelveElBordeEnLaZonaHorariaDelPuntoDeVenta` →
  FAILED (`Expected: 9 Actual: 22` — the "outside" movement at 02:00Z fell
  inside the shifted UTC-only window), reverted, clean diff, suite green.
- [x] 5.8 [P] **Mutation target**: `ConsumoDiario`'s `netoConsumido is null
  ⇒ null` — return `0m` instead — the zero-history test (5.12) must fail.
  *(mutation-proof-tests)* **EVIDENCE**: mutated
  `ReglaDeReposicion.ConsumoDiario` to `netoConsumido is null ? 0m /*
  MUTATION 5.8 */ : …`, ran
  `UnArticuloSinHistoriaDeConsumoMuestraNulosDeRotacionEnLaReposicionNuncaCero`
  → FAILED (`Assert.Null() Failure: Expected: null Actual: 0`), reverted,
  clean diff, suite green.
- [x] 5.9 [P] **Mutation target**: the `filas.Count == 0` early return in
  `ObtenerReposicionAsync` (wired in slice 4, exercised here) — delete it —
  the empty-PV query-count test (5.14) must fail. *(mutation-proof-tests)*
  **EVIDENCE**: deleted the `if (crudas.Count == 0) { return …; }` guard
  (the empty branch still yields empty `Filas`, but now issues one extra
  query filtering `LeerConsumoAsync` by an empty id list), ran
  `UnPuntoDeVentaSinMinimosNoConsultaMovimientosStock` → FAILED
  (`Assert.Equal() Failure: Expected: 6 Actual: 7`), reverted, clean diff,
  suite green.
- [x] 5.10 [P] Integration, named:
  `LaRotacionNoNeteaLaAnulacionDeUnaCompraDentroDeLasVentas` — seed compra
  → confirm → **anular la compra** (`motivo = anulacion` **with**
  `id_comprobante_compra`) → sale → **anular la venta** (`motivo =
  anulacion`, `id_comprobante_compra` null) → assert `consumoEnVentana`
  equals exactly the sale minus its own anulación, all magnitudes distinct.
  *(spec `reposicion-de-stock`: "Rotation Excludes Purchase-Reversal
  Anulaciones And Is Advisory-Only", scenario "A purchase anulación is
  excluded from consumption")* Compra leg driven through the real
  `POST /api/compras` → `/confirmar` → `/anular` flow (genuine
  `id_comprobante_compra` FK, same pattern as
  `ComprasAnulacionYConcurrenciaTests`); sale + its anulación seeded directly
  on the ledger (`SembrarMovimientoAsync`, same precedent as that file's
  `ReducirStockComoVentaAsync` — no FK requirement on a null comprobante).
  Magnitudes 15 (compra, excluded)/8 (venta)/3 (anulación de venta)/5 (net)
  all distinct.
- [x] 5.11 [P] Integration, the clock pinned at `2026-08-14T12:00:00Z`, PV
  zone `America/Argentina/Buenos_Aires` (local `09:00`), `dias = 1`. A
  movement at `2026-08-14T02:00:00Z` (local `2026-08-13 23:00`) is
  **outside** the window; one at `2026-08-14T04:00:00Z` (local `01:00`) is
  **inside**. Distinct magnitudes. *(spec: "The rotation window resolves
  in the punto de venta's own zona horaria")* Magnitudes 13
  (outside)/9 (inside), single articulo, `factoryConRelojFijo` pattern
  (`VencimientosReporteTests` precedent).
- [x] 5.12 [P] Integration: an articulo with no qualifying movement in the
  window shows `minimoSugerido = null` on the reposición report, not `0`.
  *(spec: "A zero-history articulo shows no suggestion rather than a
  suggestion of zero")* **APPLY NOTE**: `FilaDeReposicion` has no
  `minimoSugerido` field (design's own Interfaces/Contracts section — that
  field lives only on `FilaDeRotacion`); the task text's `minimoSugerido`
  is stale relative to the pinned `Contratos.cs` record. Implemented as
  the accurate translation of the same spec scenario onto the actual
  reposición fields: `ConsumoDiarioPromedio`/`DiasDeCobertura` both `null`
  (never `0`) for an articulo under mínimo with zero qualifying movements.
  `GET /rotacion`'s own absence behavior for the identical fact is covered
  separately by 5.17.
- [x] 5.13 [P] Integration: a mixed sequence containing `ajuste`,
  `inventario`, `decomiso`, `transferencia` and `reclasificacion`, each with
  a distinct magnitude, leaves `consumoEnVentana` unchanged from the
  sales-only baseline. *(spec: "ajuste, inventario, decomiso, transferencia
  and reclasificacion never count as consumption")* Baseline venta 20;
  five excluded motivos at 1/2/3/4/5.
- [x] 5.14 [P] Integration, by query count: a PV with hundreds of stocked
  articulos and zero minimums returns zero rows **and issues no query
  against `movimientos_stock`** — `ContadorDeComandos`, exact constant.
  *(spec: "A catalog with no minimo configured anywhere returns zero alert
  rows")* 200 seeded articulos; calls `ServicioDeReportesDeStock.ObtenerReposicionAsync`
  directly (manual `WaysDbContext` + interceptors, `VentasCheckoutTests`
  precedent) — exact constant is **6** (2 from `ResolverContextoAsync`, 2
  from `ResolverDiasRotacionAsync`, 1 from `ConstruirQueryDeReposicion`;
  `ServicioDeParametros.ResolverAsync` itself issues 2 — the PV-ownership
  `AnyAsync` guard plus the `parametros` row read), confirmed by a real run
  before recording the assertion, not derived-and-assumed.
- [x] 5.15 [P] Integration: an arbitrary rotation figure never gates the
  alert — an articulo at `cantidad <= minimo` appears independent of its
  rotation value. *(spec: "A wrong rotation figure never gates the alert")*
  Seeded a deliberately large qualifying sale (500) to prove a big rotation
  figure still doesn't suppress or alter row inclusion.
- [x] 5.16 [P] Integration: `?dias=60` on `GET /reposicion?idPuntoVenta=7`
  widens the window so a 45-day-old sale contributes; the same `?dias=60`
  on `GET /rotacion?idPuntoVenta=7` shows the articulo with a
  `minimoSugerido` reflecting it. *(spec: "An explicit dias override widens
  the reposición report's window"; "dias overrides the default window on
  the rotacion route too")* Sale magnitude 60, chosen so `consumoDiarioPromedio
  = 60/60 = 1` exactly at `dias=60` — asserted absent/null at the default
  30-day window on both routes, present/non-null at `dias=60` on both.
- [x] 5.17 [P] Integration: `GET /rotacion` omits an articulo with no
  qualifying movement — absence, never a zero row. *(spec `GET
  /api/reportes/stock/rotacion…`: "An articulo with no consumption history
  is absent, not zero")* Two articulos in the same PV, only one with a
  qualifying movement — the other's absence asserted directly (not an
  empty-response coincidence).
- [x] 5.18 [P] `parametros-operativos` scenario:
  `dias_cobertura_objetivo = 7` and an average daily consumption of `3` ⇒
  `minimoSugerido = 21`, shown as a suggestion, never written to
  `stock.minimo`. *(spec `parametros-operativos`: "dias_cobertura_objetivo
  feeds minimoSugerido, never minimo directly")* Consumo total 90 over the
  default 30-day window ⇒ `consumoDiarioPromedio = 3`; default
  `dias_cobertura_objetivo = 7` ⇒ `minimoSugerido = 21`; re-reads
  `stock.minimo` after the call and asserts it is still `NULL`.
- [x] 5.19 [P] Integration: after `/reposicion` or `/reposicion` runs for an
  articulo with a computable `minimoSugerido` and no `minimo`, re-read
  `stock.minimo` and assert it is still `NULL` — no automated write ever
  occurs outside `PUT /api/stock/minimos`. *(spec `reposicion-de-stock`:
  "minimoSugerido is never written to minimo automatically")* **APPLY
  NOTE**: task text literally says "after `/reposicion` or `/reposicion`
  runs" (repeats the same route) — interpreted as `/reposicion` or
  `/rotacion`, the only reading consistent with 5.16 immediately above
  (which names both routes together) and with this task's own placement
  right after 5.18's single-route write-check. Implemented calling BOTH
  routes for the same articulo (qualifying movement seeded, `minimo`
  left `NULL`) and re-reading `stock.minimo` as `NULL` after each call.
- [x] 5.20 Gate guard: `has-pending-model-changes` clean, zero migration
  files in the diff. **VERIFIED**: `dotnet ef migrations
  has-pending-model-changes --project src/Ways.Infrastructure --startup-project
  src/Ways.Infrastructure` → "No changes have been made to the model since
  the last migration."; `git diff --stat main --
  src/Ways.Infrastructure/Persistencia/Migraciones/` → empty output (zero
  files).
- [x] 5.21 Run `judgment-day`; fix; re-judge until clean. *(CLEAN 2026-08-14:
  judge B's scoped re-judgment re-applied all 6 named mutations — every one
  killed by exactly its dedicated test with the predicted failure mode
  (incl. the live 500 `error_interno` confirmation of the soft-delete
  guard) — zero fix-caused defects beyond one prose slip corrected by the
  orchestrator; judge A's fresh read-only pass returned ZERO findings,
  independently re-deriving the netting predicate against the raw-ADO
  write paths of ventas/compras, the window arithmetic, the query-count-6
  claim, and every DTO field's fate. JUDGMENT: APPROVED. Round 1, juez B —
  7 findings confirmed and closed: #1 MAJOR
  (`ResolverDiasCoberturaAsync`/`ObtenerRotacionAsync` never routed
  `dias_cobertura_objetivo` through `ReglaDeReposicion.ExigirVentanaValida`,
  making the designed 400 unreachable and letting a stored `<= 0` value
  fabricate a zero/negative `minimoSugerido` — fixed by wrapping the
  resolved value with `ExigirVentanaValida(_, "dias_cobertura_invalido")`
  at the single resolution point; test
  `UnDiasDeCoberturaObjetivoInvalidoEsRechazadoConCuatrocientos`); #2
  WARNING (the `FilaDeRotacion` clamp-to-zero contract had no test; test
  `UnaVentanaConDevolucionesNetasPositivasClampeaElConsumoAZeroNuncaNegativo`);
  #3 WARNING (window boundaries untested at the exact instant; test
  `LaVentanaDeRotacionIncluyeElBordeInferiorYExcluyeElBordeSuperiorExactos`);
  #4 WARNING (`DiasDeCobertura(f.Cantidad, …)` wiring untested; test
  `LaCoberturaDeDiasSeCalculaSobreCantidadNuncaSobreMinimo`); #5 WARNING
  (soft-deleted articulo with qualifying history untested — the
  `nombres.ContainsKey` guard was already correct, just unproven; test
  `UnArticuloDadoDeBajaConHistoriaCalificadaDesapareceDeLaRotacionSinReventar`);
  #6 SUGGESTION (dead `Contexto.Vendedor` setup — closed with test
  `UnVendedorEsRechazadoDelReporteDeRotacion`, mirror of 4.11); #7
  SUGGESTION (incoherent narrative in the 5.10 doc-comment — corrected to
  the real observed value, Expected 5 / Actual 20). Five of the 6 new
  integration tests confirmed FAIL under their named mutation and PASS on
  revert (the sixth, the Vendedor-403 mirror, is an auth-gate test with no
  named mutation). Filtered suite `~Rotacion|~Reposicion` green: 60/60
  (29 `Ways.Domain.Tests` + 31 `Ways.IntegrationTests`, up from 25
  integration baseline + 6 new).)*
- [x] 5.22 Branch `feat/stage13-slice5-rotacion` off `main` (parent:
  slice 4); PR; merge stacked-to-main.

**APPLY NOTE (Verify line, no mismatch this time)**: unlike slices 2 and 4,
this slice's `Verify` filter runs clean as literally written —
`~RotacionReport` matches the new `RotacionReporteTests` class (all 10
tests); `~LeerConsumoAsync` matches nothing extra (the method is private,
no test name contains that substring) but the `|` OR keeps the filter
non-empty. Also ran the wider `~Rotacion|~Reposicion` filter (25 tests,
covering the slice-4 `ReposicionReporteTests`/`ReposicionExportTests`
regression surface widened by this slice) and the Domain
`~ReglaDeReposicion` suite (28 tests) — both green, see Work Unit Evidence
in the apply summary.

**Test plan**: 4 mutation targets (5.6-5.9), netting trap (5.10),
midday-UTC boundary (5.11), zero-history (5.12), motivo exclusions (5.13),
empty-PV query count (5.14), rotation-never-gates (5.15), `?dias=` override
×2 routes (5.16), rotacion-absence (5.17), `dias_cobertura_objetivo`
(5.18), no-silent-write (5.19).

**Verify**: `dotnet test --filter FullyQualifiedName~LeerConsumoAsync|FullyQualifiedName~RotacionReport`

---

## Slice 6: Web Reposición (PR 6)

**Start**: slices 4+5 merged. **Finish**: `Reposicion.tsx` exists, grouped
by proveedor habitual, with a download button and a nav entry.
**Rollback**: revert the branch — a new, additive screen; nothing else
depends on it.

- [x] 6.1 Modify `src/Ways.Web/src/api/{tipos,reportes}.ts`: mirror
  `FilaDeReposicion` (with the two rotation fields), `Reposicion`,
  `FilaDeRotacion`, `Rotacion`; `clienteDeReportes.reposicion`,
  `clienteDeReportes.rotacion`; `rutasDeExportacion.reposicion`.
- [x] 6.2 Create the pure helper `agruparPorProveedor(filas) →
  { idProveedor, proveedor, filas }[]` — a **fold over the already-ordered
  list** (no client-side sort — decision 4). *(colocated at
  `src/Ways.Web/src/paginas/agruparPorProveedor.ts` — same "standalone pure
  helper module in `paginas/`" precedent as `etiquetaParaValorFaltante.ts`;
  groups CONSECUTIVE rows sharing `idProveedor`, never a second sort, since
  the server already guarantees a single trailing *Sin proveedor* run)*
- [x] 6.3 Create `src/Ways.Web/src/paginas/Reposicion.tsx`: PV selector,
  fetches `clienteDeReportes.reposicion(idPuntoVenta, null)`,
  `BotonDeDescarga` pointing at `rutasDeExportacion.reposicion(idPuntoVenta)`
  (the `Existencias.tsx` shape verbatim). Header per group shows the
  proveedor and its row count; `sugerido` renders `—` when null, never `0`.
  **APPLY NOTE**: `rutasDeExportacion.reposicion` takes `(idPuntoVenta,
  dias)` (mirroring `vencimientos`'s signature, since the backend route
  accepts `dias` too) — the page always calls it with `dias = null`, exactly
  like `clienteDeReportes.reposicion(idPuntoVenta, null)`; no `dias` control
  is exposed in this slice's UI (design's Reposicion.tsx composition note
  never asks for one). Columns shipped: Artículo, Cantidad, Mínimo,
  Reposición, Sugerido — the two rotation fields
  (`consumoDiarioPromedio`/`diasDeCobertura`) are on the DTO for slice 7's
  `Sugerido` column on `Existencias.tsx`, never displayed here (design's
  Reposicion.tsx section only asks for `sugerido`).
- [x] 6.4 Modify `src/Ways.Web/src/App.tsx` and
  `src/Ways.Web/src/componentes/Layout.tsx`: one route, one nav entry
  alongside `/reportes/stock/vencimientos`. Route `/reportes/stock/reposicion`,
  same `RutaProtegida rolesPermitidos={[ROL.Supervisor, ROL.Admin]}` gate as
  `/reportes/stock/vencimientos`; nav entry "Reposición" placed immediately
  after "Vencimientos" under the same `puedeVerReportes` gate.
- [x] 6.5 [P] Descriptor tests for `agruparPorProveedor`: two proveedores
  in server order, a *Sin proveedor* bucket landing **last**, a single row,
  the empty list. *(web-descriptor-tests)* Implemented in
  `agruparPorProveedor.test.ts` — 4 tests, all four scenarios covered.
- [x] 6.6 [P] Component test: `sugerido` renders `—` for a null value,
  never `0` — the cell an operator would otherwise misread as "buy
  nothing". Implemented in `Reposicion.test.tsx` (`'sugerido renderiza —
  cuando es null, nunca 0'`), plus 4 more component tests (first-PV load
  with no `?dias=`, group header text, download button wiring, empty state).
- [x] 6.7 Gate guard: `has-pending-model-changes` clean, zero migration
  files in the diff. **VERIFIED**: `dotnet ef migrations
  has-pending-model-changes --project src/Ways.Infrastructure
  --startup-project src/Ways.Infrastructure` → "No changes have been made to
  the model since the last migration."; `git diff --stat main --
  src/Ways.Infrastructure/Persistencia/Migraciones/` → empty output (zero
  files). Web-only slice, as expected.
- [x] 6.8 Run `judgment-day`; fix; re-judge until clean. *(CLEAN
  2026-08-15: re-ronda B aprobada — 4 re-mutaciones muertas, mocks sin
  fugas; juez A fresh: 0 severos, 1 WARNING cerrado test-only con
  evidencia de mutación falsy-coercion. JUDGMENT: APPROVED.)*
  **Ronda 1, juez B**: 4 hallazgos confirmados, los 4 cerrados —
  (MAJOR) `Reposicion.test.tsx` el test del botón de descarga no clickeaba
  ni asertaba la ruta, solo existencia (fix: replica el precedente
  `Vencimientos.test.tsx` — click + `apiDescargarMock` matchea
  `/reportes/stock/reposicion/export?idPuntoVenta=`); (WARNING)
  `agruparPorProveedor.test.ts` ningún test discriminaba la clave del fold
  (`idProveedor` vs. nombre) por fixtures biyectivos (fix: test nuevo con
  dos proveedores de igual nombre e id distinto, consecutivos → dos
  grupos); (WARNING) el gate de generación de `Reposicion.tsx` (stale
  response) sin cobertura (fix: test de respuesta desactualizada, mismo
  patrón que `Vencimientos.test.tsx` — promesa diferida + `act` síncrono,
  mutation-proof-tests regla 7); (WARNING) sin tests de rol/ruta para
  `/reportes/stock/reposicion` (fix: replica los dos tests de rol de
  `Vencimientos.test.tsx` — Supervisor llega, Vendedor redirige a Inicio).
  **Limitación de suite registrada** (igual que en `Vencimientos.test.tsx`
  y demás siblings): los tests de rol montan `RutaProtegida` directo en un
  `MemoryRouter` propio del test, no a través de `App.tsx` — la
  invisibilidad de la entrada de nav en `Layout.tsx` para roles sin acceso
  queda sin test unitario en ningún sibling de esta suite; no se agregó
  cobertura Layout-level para Reposición porque ningún sibling la tiene
  (no es una regresión de este slice).
  **Ronda 2, juez A**: 0 severos, 1 WARNING cerrado (test-only) —
  `Reposicion.test.tsx:151-169` ('sugerido renderiza — cuando es null,
  nunca 0') solo contrastaba `null` ('—') contra `8` ('8'), nunca un
  `sugerido = 0` genuino: un regression a `!valor ? '—' : ...` en
  `formatearCantidadNullable` (Reposicion.tsx) habría pasado la suite
  renderizando '—' para un 0 real (fix: test hermano nuevo, fixture
  `idArticulo: 3, sugerido: 0`, assert `getByText('0')` +
  `queryByText('—')` ausente; evidencia: mutado `valor === null` →
  `!valor`, el nuevo assert falló solo; revertido, suite completa verde
  otra vez — 626/626).
- [x] 6.9 Branch `feat/stage13-slice6-web-reposicion` off `main` (parent:
  slices 4+5); PR; merge stacked-to-main.

**Test plan**: grouping descriptors (6.5), null-sugerido render (6.6).

**Verify**: `npm run test -- Reposicion`

---

## Slice 7: Tile Y Sugerencia (PR 7) — the designated droppable slice

**Start**: slices 3+4+5 merged. **Finish**: the Tablero tile
(`/reposicion/resumen`) and the `Sugerido` column on `Existencias.tsx`.
**Rollback**: revert the branch — the tile and the column are the assistive
layer; the fixed `minimo` remains fully usable without them.

**Pre-approved degradation** (proposal, decision-11-of-stage-12 pattern):
if this slice overflows, **ship `/reposicion/resumen` + the tile and drop
the `Sugerido` column** (i.e. drop `GET /rotacion`'s web consumer, tasks
7.6/7.11). Decision 14 makes this a clean cut: the suggestion lives behind
its own endpoint and column, so dropping it removes code never shipped
rather than retracting a published DTO field.

- [x] 7.1 Modify `ServicioDeReportesDeStock.cs`:
  `ObtenerResumenDeReposicionAsync(idPuntoVenta, ct)` — calls the **same**
  `ObtenerReposicionAsync` method and folds its own rows: `bajoMinimo :=
  filas.Count`, `sinStock := filas.Count(f => f.Cantidad <= 0)`,
  `sinProveedor := filas.Count(f => f.IdProveedor is null)`. **No second
  aggregation query.** *(design decision 8, 9 — with the field name
  correction recorded in Orchestrator Decision #5 above: `sinProveedor`,
  not design.md's stale `sinSugerencia`)*
- [x] 7.2 Modify `Contratos.cs`: `ResumenDeReposicion(IdPuntoVenta,
  BajoMinimo, SinStock, SinProveedor)`. `dto-contract-honesty`:
  doc-comment `SinProveedor` as the *Sin proveedor* group count — distinct
  from, and never conflated with, "no `sugerido`" (`reposicion` unset).
- [x] 7.3 Modify `ReportesEndpoints.cs`:
  `GET /reportes/stock/reposicion/resumen?idPuntoVenta`, `Politicas.LecturaDeReportes`.
- [x] 7.4 Modify `src/Ways.Web/src/api/{tipos,reportes}.ts`: mirror
  `ResumenDeReposicion` (`sinProveedor`, not `sinSugerencia`);
  `clienteDeReportes.reposicionResumen`.
- [x] 7.5 Modify `src/Ways.Web/src/paginas/Tablero.tsx`: `PanelDeReposicion`
  cloned from `PanelDeVencimientos` — `usePanelDeReporte`, requires a
  concrete `idPuntoVenta` (neutral copy otherwise), `PanelDeError` + retry,
  `Link` to the report, and **one `data-testid` per metric**:
  `reposicion-tile-bajo-minimo`, `reposicion-tile-sin-stock`,
  `reposicion-tile-sin-proveedor` — the stage-12 slice-15 lesson: a blob
  assertion cannot catch two swapped counts.
  **APPLY NOTE**: both `PanelDeVencimientos` and `PanelDeReposicion` render
  a "Ver reporte" link with identical text, so the two pre-existing
  `screen.getByRole('link', { name: 'Ver reporte' })` assertions (one in
  the vencimientos tile describe block, one new in the reposición block)
  became ambiguous once both panels render together. Both scoped with
  `within(screen.getByText(<título>).closest('div')!)` — not a task,
  recorded per the instruction to never silently deviate.
- [x] 7.6 Modify `src/Ways.Web/src/paginas/Existencias.tsx`: add the
  `Sugerido` column, fed by `clienteDeReportes.rotacion(idPuntoVenta)`
  fetched alongside the report and indexed by `idArticulo`; an articulo
  absent from that map renders `—`, never `0`.
  **APPLY NOTE**: `/rotacion` is fetched via `Promise.all` alongside
  `/existencias` in the same `cargar()` call, same generation token — but
  with its OWN `.catch` that degrades to an empty `Map` rather than
  rejecting the combined promise (`react-async-state` rule 6: a secondary
  feed's failure never turns a successful primary load into an error).
  `agregarFila`'s new local row is naturally absent from the rotation map
  fetched at load time, so it renders `—` until the next reload — no
  special-casing needed.
- [x] 7.7 [P] **Mutation target**: the fold of `sinStock`
  (`f.Cantidad <= 0`) — change to `< 0` — the tile test seeded with an
  articulo at exactly `0` (7.8) must fail. *(mutation-proof-tests)*
  **EVIDENCE**: mutated `ObtenerResumenDeReposicionAsync`'s `sinStock` fold
  to `f.Cantidad < 0 /* MUTATION 7.7 */`, ran `dotnet test --filter
  FullyQualifiedName~LosTresConteosDelTileIgualanElFoldeoDelReporte` →
  **FAILED** (`Assert.Equal() Failure: Expected: 2 Actual: 1` — the
  exactly-zero articulo stopped counting as `sinStock`); reverted, ran
  `--filter FullyQualifiedName~ReposicionReporteTests` → 9/9 green,
  `grep -rn "MUTATION"` on the touched files left only the pre-existing
  "MUTATION TARGET" comment headers from slice 4, no inline marker.
- [x] 7.8 [P] Integration: the tile's three counts equal the report's
  folded values over a seed with **all three metrics distinct**
  (`bajoMinimo = 7, sinStock = 2, sinProveedor = 1`), so no two counts can
  be swapped without detection. *(spec `reposicion-de-stock`: "The Tablero
  Tile Reuses The Report Method, Never A Second Aggregation Query",
  scenario "The tile's three counts equal the report's folded values")*
  Implemented as `ReposicionReporteTests.LosTresConteosDelTileIgualanElFoldeoDelReporte`
  (also the 7.7 mutation-target test — same seed serves both purposes).
- [x] 7.9 [P] Integration, discrimination: two rows below minimo — one with
  no `id_proveedor_habitual` and `sugerido = 30`, another with a proveedor
  assigned but `sugerido = null` (`reposicion` unset) — `sinProveedor = 1`,
  counting only the row missing a proveedor, independent of `sugerido`.
  *(spec: "sinProveedor counts the Sin proveedor group, not a missing
  suggestion")* Implemented as
  `ReposicionReporteTests.SinProveedorCuentaElGrupoSinProveedorNoElSugeridoAusente`.
- [x] 7.10 [P] Component test: the three `data-testid`s asserted
  individually, not as one blob — the stage-12 slice-15 lesson applied.
  Implemented in `Tablero.test.tsx`, describe block "Tablero — tile de
  reposición (stage-13-stock-inteligente, Slice 7)".
- [x] 7.11 [P] Component test: the `Existencias.tsx` `Sugerido` column
  renders `—` for an articulo absent from the rotation map, never `0`.
  Implemented in `Existencias.test.tsx`, describe block "Existencias —
  columna Sugerido (stage-13-stock-inteligente, Slice 7)": one test
  discriminates present-vs-absent (`minimoSugerido` value vs. `—`,
  indexed by cell position — `Mínimo`/`Reposición` also render `—` on
  the same rows, so a text-only query would be ambiguous); a second
  proves the `/rotacion`-failure degradation from 7.6's APPLY NOTE.
- [x] 7.12 Gate guard: `has-pending-model-changes` clean, zero migration
  files in the diff. **VERIFIED**: `dotnet ef migrations
  has-pending-model-changes --project src/Ways.Infrastructure
  --startup-project src/Ways.Infrastructure` → "No changes have been made
  to the model since the last migration."; `git diff --stat main --
  src/Ways.Infrastructure/Persistencia/Migraciones/` → empty output (zero
  files).
- [x] 7.13 Run `judgment-day`; fix; re-judge until clean. *(CLEAN
  2026-08-16: re-ronda B aprobada — 3 re-mutaciones muertas, incluida la
  verificación en vivo del cierre del sibling vencimientos/resumen; juez A
  fresh: 0 severos, 1 WARNING (matriz Supervisor-aceptado sin las rutas
  nuevas) + 1 SUGGESTION (nombres "NueveRutas" caducos) cerrados test-only
  en `9507f8f` con evidencia de mutación de política apilada. JUDGMENT:
  APPROVED.)*
  **Ronda 1, juez B**: 3 hallazgos confirmados, los 3 cerrados — (MAJOR)
  `ReposicionReporteTests.SinProveedorCuentaElGrupoSinProveedorNoElSugeridoAusente`
  (7.9) tenía cardinalidades simétricas (1 fila sin proveedor, 1 fila sin
  sugerido) — mutar el fold `IdProveedor is null` → `Sugerido is null`
  sobrevivía; fix: tercera fila bajo mínimo con proveedor asignado y
  `reposicion` null (grupo "sin sugerido" pasa a 2, "sin proveedor" queda
  en 1), más los otros dos counts del resumen (`bajoMinimo`/`sinStock`)
  asertados con magnitudes distintas entre sí. (WARNING, autorizado con
  extensión de matriz) `/reportes/stock/reposicion/resumen` no tenía test
  de autorización — `.AllowAnonymous()` sobrevivía 9/9 en
  `ReportesAutorizacionTests`; fix test-only: sumadas las DOS rutas de
  resumen de stock (`stock/reposicion/resumen`, `stock/vencimientos/resumen`)
  a `TodasLasRutas`, con un `RutaCon` que arma `?idPuntoVenta=` para ellas en
  vez de `idEmpresa`/`desde`/`hasta`/`granularidad`. El sibling
  `stock/vencimientos/resumen` tenía el MISMO gap preexistente desde la
  slice 6 — cerrado de paso, también test-only. (WARNING)
  `Existencias.test.tsx` el fixture de rotación traía una sola fila —
  mutar la clave del map a "siempre `rotacion.filas[0].idArticulo`" era
  indistinguible del fold correcto; fix: segunda fila presente en la
  grilla y en `/rotacion` (otro artículo, `minimoSugerido` distinto),
  ambas celdas de Sugerido asertadas con valores distintos por fila.
  Evidencia de mutación real por hallazgo (mutar → build/test falla →
  revert → verde) en los commits de esta ronda.
  **Ronda 1, juez A**: 0 hallazgos severos. (WARNING, autorizado, cerrado
  test-only) `RutasSinLecturaDeRentabilidad` (la `TheoryData` del único
  test Supervisor-aceptado) no había sido extendida con
  `stock/reposicion/resumen` ni `stock/vencimientos/resumen` — una
  regresión que hiciera `Forbidden` al Supervisor en esas rutas pasaría
  inadvertida; fix: sumadas las dos rutas a la lista. Evidencia de
  mutación real: stackear `.RequireAuthorization(Politicas.LecturaDeRentabilidad)`
  sobre el `MapGet` de `reposicion/resumen` → build → el test
  Supervisor-aceptado FALLA para esa ruta (`Expected: OK / Actual:
  Forbidden`) → `git checkout --` → 44/44 verde. (SUGGESTION, autorizado,
  cerrado test-only) los nombres `UnVendedorEsRechazadoEnLasNueveRutas` /
  `UnRootEsRechazadoEnLasNueveRutas` / `UnAdminEsAceptadoEnLasNueveRutas`
  / `UnSupervisorEsAceptadoEnLasSieteRutasSinLecturaDeRentabilidad` ya no
  describían las 11/9 entradas reales; renombrados sin el numeral
  (`...EnTodasLasRutasDeReportes`,
  `UnSupervisorEsAceptadoEnLasRutasSinLecturaDeRentabilidad`).
- [x] 7.14 Branch `feat/stage13-slice7-tile-y-sugerencia` off `main`
  (parent: slices 3+4+5); PR; merge stacked-to-main. *(Degradation NOT
  exercised — the slice landed under budget with 7.6/7.11 shipped.)*
  **If the slice
  overflows at apply time, drop tasks 7.6/7.11 (the `Sugerido` column) per
  the pre-approved degradation above and record the reduction in the PR
  body — never a silent cut.**

**Test plan**: mutation target (7.7), tile≡report with distinct counts
(7.8), sinProveedor discrimination (7.9), per-testid assertions (7.10),
column null-render (7.11).

**APPLY NOTE (Verify line filter mismatch, same class as slice 2's)**: this
slice's `Verify` line below reads `FullyQualifiedName~ResumenDeReposicion`,
but no test method matches that substring — the two new tests are
`ReposicionReporteTests.LosTresConteosDelTileIgualanElFoldeoDelReporte` and
`.SinProveedorCuentaElGrupoSinProveedorNoElSugeridoAusente` (named for what
they prove, per this repo's convention, not for the endpoint under test).
Ran `--filter FullyQualifiedName~ReposicionReporteTests` instead, which
matches the whole class (9 tests: the 7 pre-existing slice-4 tests plus
these 2, all green) — recorded per instruction to never silently deviate;
not a task, so not re-numbered.

**Verify**: `dotnet test --filter FullyQualifiedName~ReposicionReporteTests` && `npm run test -- Tablero Existencias`

---

## Global Cross-Slice Tasks

- **`dto-contract-honesty` compliance**: every new DTO field named in
  design.md's Interfaces/Contracts section has a documented destination —
  enforced per-slice above (1.3, 2.1, 4.3, 5.3, 7.2), including the explicit
  note that rotation fields are **absent by design** in slice 4 (added by
  slice 5), never a silent omission.
- **`mutation-proof-tests` compliance**: the twelve named mutation targets
  in design.md's table are each placed in exactly one slice above (§
  Orchestrator Decision #7); every one requires recorded apply-time
  evidence in its slice's PR body.
- **`react-async-state`/`web-descriptor-tests` compliance**: slices 3, 6
  and 7 are the only web-touching slices; every new/modified pure helper
  ships a colocated descriptor test in the same slice.
- **`db-error-backstops`**: **not applicable to this stage** — zero new
  constraints ship (Orchestrator Decision #6).
- **Checkout-budget protection (proposal decision 8, design decision 17)**:
  no task in any slice touches a file under `src/Ways.Application/{Ventas,
  Compras}/` or `ServicioDeStock`'s existing transfer/ajuste/conteo/decomiso
  paths. `VentasCheckoutTests`' query-count constants (`16`/`17`) stay
  byte-for-byte untouched — this is a **binding verify criterion** to be
  confirmed by `sdd-verify`, not a task, precisely because there is no task
  that could have moved them.
- **`ways_owner` testcontainer-superuser weakness** (state.yaml, carried
  from prior stages): slice 1's RLS test (1.18) runs over the **`ways_app`**
  connection specifically to route around it — the repo-wide weakness stays
  open, not reopened here.

---

## Dependency Summary

```
Slice 1 (minimos-api)
  ├─ Slice 2 (existencias-minimos) ── Slice 3 (web-minimos) ─┐
  └─ Slice 4 (reposicion) ── Slice 5 (rotacion) ── Slice 6   │
                    │                     │      (web-reposicion)
                    └──────────┬──────────┘
                               ▼
                    Slice 7 (tile-y-sugerencia)
                    needs: 3 (column host), 4 (/resumen), 5 (/rotacion)
```

Merge order: `1 → {2 → 3}` and `{4 → 5 → 6}` in either interleaving,
stacked-to-main → `7` last (downstream of 3, 4 and 5 in every valid merge
order). The conflict surface between the two fronts is
`ReportesEndpoints.cs` (one route line per slice, disjoint routes) and
`tipos.ts` (append-only blocks) — no shared method body is touched by both
fronts before slice 7 merges them.

---

## Review Workload Forecast

| Field | Value |
|-------|-------|
| Estimated changed lines | ~2 250 total (7 slices: 320/230/380/350/340/330/300) |
| 400-line budget risk | Medium — slices 3 and 4 sit closest to the cap; pre-identified cuts named in each slice's Budget note (slice 3: the add-row boundary; slice 4: the report/export boundary) |
| Chained PRs recommended | Yes |
| Suggested split | 7 PRs, stacked-to-main, per the Suggested Work Units table above |
| `size:exception` anticipated | No — unlike stage 12, no slice carries an unsplittable migration |
| Delivery strategy | `auto-chain` (already resolved, `state.yaml`) |
| Chain strategy | `stacked-to-main` |
| Decision needed before apply | No — already resolved |

Per-slice budget risk: 1 Low (~320) · 2 Low (~230) ·
3 **Medium (~380)** · 4 **Medium (~350)** · 5 Low (~340) · 6 Low (~330) ·
7 Low (~300, and the designated droppable slice — its own overflow
mitigation is dropping the `Sugerido` column, tasks 7.6/7.11, per the
pre-approved degradation, not splitting the PR). As in every prior stage,
overflow is expected to come from **test depth** — the discriminating-seed
integration tests (4.9), the multi-mutation slices (5, with four named
targets) and the component-test-heavy web slices (3, 6, 7) — not from
scope creep.

Decision needed before apply: No
Chained PRs recommended: Yes
Chain strategy: stacked-to-main
400-line budget risk: Medium
