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

- [ ] 2.1 Modify `src/Ways.Application/Reportes/Contratos.cs`:
  `FilaExistencia` gains `Minimo`, `Reposicion`, `Estado`. `dto-contract-honesty`:
  doc-comment each — read straight off the `stock` row already joined,
  `Estado` derived via `ReglaDeReposicion.Clasificar`, never a report-local
  reimplementation of the boundary.
- [ ] 2.2 Modify `src/Ways.Application/Reportes/ServicioDeReportesDeStock.cs`:
  `ObtenerExistenciasAsync`'s projection calls `ReglaDeReposicion.Clasificar(
  s.Cantidad, s.Minimo)` for `Estado`, and includes `s.Minimo`/`s.Reposicion`
  in the existing row shape. *(design decision 2 — existencias classifies
  **every** stocked row; reposición, slice 4, returns **only** `Bajo`)*
- [ ] 2.3 Modify `src/Ways.Application/Reportes/ExportacionDeReportes.cs`:
  `ColumnasExistencias` gains the same 3 columns, same order as the JSON.
- [ ] 2.4 [P] **Mutation target**: the `ReglaDeReposicion.Clasificar` call
  in the existencias projection — hard-code `EstadoDeReposicion.Ok` — the
  `SinMinimo`/`Bajo`/`Ok` row assertions (2.5) must fail.
  *(mutation-proof-tests)*
- [ ] 2.5 [P] Three-state row assertions: `cantidad = minimo` classifies
  `bajo`; `minimo = null, cantidad = 0` classifies `sin_minimo` (never
  `bajo`); `cantidad` above `minimo` classifies `ok`. *(spec
  `reportes-de-gestion`: scenarios "An articulo at or below its minimo
  classifies bajo", "…classifies sin_minimo, never bajo", "…classifies ok")*
- [ ] 2.6 [P] Integration: existencias needs no `idArticulo` (regression,
  re-asserted with the three new columns present). *(spec
  `reportes-de-gestion`: scenario "Existencias needs no idArticulo")*
- [ ] 2.7 [P] Integration: a Supervisor exports existencias with the
  widened table (regression). *(spec `reportes-de-gestion`: scenario "A
  Supervisor exports existencias")*
- [ ] 2.8 [P] Integration: a Supervisor **reads** the reorder columns on
  `GET /existencias` (`200`, columns present) and gets `403` re-confirmed
  on `PUT /minimos` from this route's vantage point. *(spec
  `reportes-de-gestion`: scenario "A Supervisor reads the reorder columns
  but cannot write them" — closes the read half left open by slice 1's
  1.17)*
- [ ] 2.9 [P] Export equality: the widened workbook carries the same
  `minimo`/`reposicion`/`estado` values as the JSON, cell by cell. *(spec
  `reportes-de-gestion`: scenario "The existencias export carries the same
  reorder columns"; `mutation-proof-tests` rule 6)*
- [ ] 2.10 [P] Round-trip: `PUT /api/stock/minimos` then
  `GET /existencias` returns the same persisted pair — the first end-to-end
  test that can exercise both routes together, now that the report exposes
  the fields.
- [ ] 2.11 Gate guard: `has-pending-model-changes` clean, zero migration
  files in the diff.
- [ ] 2.12 Run `judgment-day`; fix; re-judge until clean.
- [ ] 2.13 Branch `feat/stage13-slice2-existencias-minimos` off `main`
  (parent: slice 1); PR; merge stacked-to-main.

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

- [ ] 3.1 Modify `src/Ways.Web/src/api/{tipos,stock}.ts`: mirror
  `EstadoDeReposicion`, `FilaExistencia` (+3 fields), `SolicitudDeMinimos`,
  `MinimosDeStock`; `clienteDeStock.escribirMinimos`.
- [ ] 3.2 Create the pure helper `aSolicitudDeMinimos(idPv, idArticulo,
  minimoTexto, reposicionTexto)` in a colocated module — `'' → null`
  coercion for both inputs.
- [ ] 3.3 Modify `src/Ways.Web/src/paginas/Existencias.tsx`: add columns
  `Mínimo`/`Reposición`/`Estado`; state contract — `generacionRef` (existing,
  read staleness), `filaEnEdicion: number | null` (one `idArticulo`),
  `guardando: number | null` (one `idArticulo` in flight). Save applies the
  authoritative `MinimosDeStock` response via a functional updater from
  `prev` (no post-write refetch — decision 16), gated on its captured
  token, `guardando` reset in a token-gated `finally`.
- [ ] 3.4 Modify `Existencias.tsx`: **add-row** — an articulo lookup over
  `clienteDeArticulos` (the `Transferencias.tsx` picker pattern), appending
  a row with `cantidad = 0` locally, saved through the same `PUT`.
- [ ] 3.5 Modify `Existencias.tsx`: `guardando !== null` disables **every**
  row's "Editar", the PV selector, the download button and the add-row; the
  handler's first line is `if (guardando !== null) return` (beats a
  same-tick double click ahead of the `disabled` re-render). Client-side
  pre-validation mirrors `reposicion_menor_que_minimo` and disables save
  while the aviso is visible — the copy never claims a block the UI does
  not enforce (`react-async-state` rule 7).
- [ ] 3.6 [P] **Mutation target**: remove the `guardando !== null` guard
  from the row-open handler — the "open row B blocked while row A is
  saving" test (3.10) must fail. *(mutation-proof-tests, design decision
  15 — "supersede-during-write mutated across four consecutive review
  rounds in this repo before blocking the window killed the class")*
- [ ] 3.7 [P] Descriptor tests: `aSolicitudDeMinimos` coercion branches —
  `''`, `'0'`, `'2.5'`, `'-1'`, `'1,5'`, both-empty (unmanage).
  *(web-descriptor-tests)*
- [ ] 3.8 [P] Component test: save applies the authoritative response
  **without** a refetch — assert no second `GET` fires after the `PUT`
  resolves.
- [ ] 3.9 [P] Component test: a stale read landing after a save is
  discarded — a stale promise resolved **inside `act`**, asserted
  synchronously after the flush (`react-async-state` rule 7 — the "committed
  write reported as a failure" class this design's decision 16 exists to
  prevent).
- [ ] 3.10 [P] Component test: double-click on save ⇒ exactly one `fetch`;
  open-row-B **blocked** while row A is saving (ties to the 3.6 mutation).
- [ ] 3.11 Gate guard: `has-pending-model-changes` clean, zero migration
  files in the diff (web-only slice — confirms no accidental API/EF drift).
- [ ] 3.12 Run `judgment-day`; fix; re-judge until clean.
- [ ] 3.13 Branch `feat/stage13-slice3-web-minimos` off `main` (parent:
  slices 1+2); PR; merge stacked-to-main.

**Test plan**: mutation target (3.6), coercion descriptors (3.7), no-refetch
(3.8), stale-read-discarded (3.9), double-click + supersede-blocked (3.10).

**Verify**: `npm run test -- Existencias`

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

- [ ] 5.1 Modify `ServicioDeReportesDeStock.cs`: private
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
  untouched (design decision 7). The caller negates: `-neto`.
- [ ] 5.2 Modify `ServicioDeReportesDeStock.cs`: wire `ObtenerReposicionAsync`
  — when `filas.Count > 0`, compute `(desdeUtc, hastaUtc) :=
  ReglaDeReposicion.VentanaDeRotacion(hoy, diasDeRotacion, zona)`, call
  `LeerConsumoAsync(pv, filas.ids, desdeUtc, hastaUtc, ct)`, project
  `ConsumoDiarioPromedio := ReglaDeReposicion.ConsumoDiario(consumo.TryGet(id)
  ? -neto : null, diasDeRotacion)` and `DiasDeCobertura :=
  ReglaDeReposicion.DiasDeCobertura(cantidad, consumoDiarioPromedio)` per
  row. When `filas.Count == 0`, the rotation query is skipped entirely
  (decision 12 — a PV with no minimums costs exactly one query).
- [ ] 5.3 Modify `Contratos.cs`: widen `FilaDeReposicion` with
  `ConsumoDiarioPromedio?`, `DiasDeCobertura?`; add `FilaDeRotacion(IdArticulo,
  Articulo, ConsumoEnVentana, ConsumoDiarioPromedio, MinimoSugerido)` and
  `Rotacion(IdPuntoVenta, Hoy, DiasDeRotacion, DiasCoberturaObjetivo,
  ZonaHoraria, Filas)`. `dto-contract-honesty`: doc-comment that an articulo
  absent from `Rotacion.Filas` means "no qualifying movement", never a row
  with zero-valued fields (design decision 14).
- [ ] 5.4 Modify `ServicioDeReportesDeStock.cs`:
  `ObtenerRotacionAsync(idPuntoVenta, dias?, ct)` — same consumption
  definition and window resolution as 5.1-5.2 (never a second definition),
  one row per articulo with a qualifying movement in the window; an
  articulo with none is **absent**, never present with `minimoSugerido = 0`.
- [ ] 5.5 Modify `ReportesEndpoints.cs`:
  `GET /reportes/stock/rotacion?idPuntoVenta[&dias]`, `Politicas.LecturaDeReportes`.
- [ ] 5.6 [P] **Mutation target**: delete `&& m.IdComprobanteCompra == null`
  from `LeerConsumoAsync`'s filter — the netting-trap test (5.10) must fail.
  *(mutation-proof-tests)*
- [ ] 5.7 [P] **Mutation target**: `VentanaDeRotacion`'s zone conversion —
  replace with `hoy`/edges computed from `reloj.Ahora.UtcDateTime` — the
  midday-UTC boundary test (5.11) must fail. *(mutation-proof-tests, the
  stage-11 slice-9 bug class hardened by `08e7707`)*
- [ ] 5.8 [P] **Mutation target**: `ConsumoDiario`'s `netoConsumido is null
  ⇒ null` — return `0m` instead — the zero-history test (5.12) must fail.
  *(mutation-proof-tests)*
- [ ] 5.9 [P] **Mutation target**: the `filas.Count == 0` early return in
  `ObtenerReposicionAsync` (wired in slice 4, exercised here) — delete it —
  the empty-PV query-count test (5.14) must fail. *(mutation-proof-tests)*
- [ ] 5.10 [P] Integration, named:
  `LaRotacionNoNeteaLaAnulacionDeUnaCompraDentroDeLasVentas` — seed compra
  → confirm → **anular la compra** (`motivo = anulacion` **with**
  `id_comprobante_compra`) → sale → **anular la venta** (`motivo =
  anulacion`, `id_comprobante_compra` null) → assert `consumoEnVentana`
  equals exactly the sale minus its own anulación, all magnitudes distinct.
  *(spec `reposicion-de-stock`: "Rotation Excludes Purchase-Reversal
  Anulaciones And Is Advisory-Only", scenario "A purchase anulación is
  excluded from consumption")*
- [ ] 5.11 [P] Integration, the clock pinned at `2026-08-14T12:00:00Z`, PV
  zone `America/Argentina/Buenos_Aires` (local `09:00`), `dias = 1`. A
  movement at `2026-08-14T02:00:00Z` (local `2026-08-13 23:00`) is
  **outside** the window; one at `2026-08-14T04:00:00Z` (local `01:00`) is
  **inside**. Distinct magnitudes. *(spec: "The rotation window resolves
  in the punto de venta's own zona horaria")*
- [ ] 5.12 [P] Integration: an articulo with no qualifying movement in the
  window shows `minimoSugerido = null` on the reposición report, not `0`.
  *(spec: "A zero-history articulo shows no suggestion rather than a
  suggestion of zero")*
- [ ] 5.13 [P] Integration: a mixed sequence containing `ajuste`,
  `inventario`, `decomiso`, `transferencia` and `reclasificacion`, each with
  a distinct magnitude, leaves `consumoEnVentana` unchanged from the
  sales-only baseline. *(spec: "ajuste, inventario, decomiso, transferencia
  and reclasificacion never count as consumption")*
- [ ] 5.14 [P] Integration, by query count: a PV with hundreds of stocked
  articulos and zero minimums returns zero rows **and issues no query
  against `movimientos_stock`** — `ContadorDeComandos`, exact constant.
  *(spec: "A catalog with no minimo configured anywhere returns zero alert
  rows")*
- [ ] 5.15 [P] Integration: an arbitrary rotation figure never gates the
  alert — an articulo at `cantidad <= minimo` appears independent of its
  rotation value. *(spec: "A wrong rotation figure never gates the alert")*
- [ ] 5.16 [P] Integration: `?dias=60` on `GET /reposicion?idPuntoVenta=7`
  widens the window so a 45-day-old sale contributes; the same `?dias=60`
  on `GET /rotacion?idPuntoVenta=7` shows the articulo with a
  `minimoSugerido` reflecting it. *(spec: "An explicit dias override widens
  the reposición report's window"; "dias overrides the default window on
  the rotacion route too")*
- [ ] 5.17 [P] Integration: `GET /rotacion` omits an articulo with no
  qualifying movement — absence, never a zero row. *(spec `GET
  /api/reportes/stock/rotacion…`: "An articulo with no consumption history
  is absent, not zero")*
- [ ] 5.18 [P] `parametros-operativos` scenario:
  `dias_cobertura_objetivo = 7` and an average daily consumption of `3` ⇒
  `minimoSugerido = 21`, shown as a suggestion, never written to
  `stock.minimo`. *(spec `parametros-operativos`: "dias_cobertura_objetivo
  feeds minimoSugerido, never minimo directly")*
- [ ] 5.19 [P] Integration: after `/reposicion` or `/reposicion` runs for an
  articulo with a computable `minimoSugerido` and no `minimo`, re-read
  `stock.minimo` and assert it is still `NULL` — no automated write ever
  occurs outside `PUT /api/stock/minimos`. *(spec `reposicion-de-stock`:
  "minimoSugerido is never written to minimo automatically")*
- [ ] 5.20 Gate guard: `has-pending-model-changes` clean, zero migration
  files in the diff.
- [ ] 5.21 Run `judgment-day`; fix; re-judge until clean.
- [ ] 5.22 Branch `feat/stage13-slice5-rotacion` off `main` (parent:
  slice 4); PR; merge stacked-to-main.

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

- [ ] 6.1 Modify `src/Ways.Web/src/api/{tipos,reportes}.ts`: mirror
  `FilaDeReposicion` (with the two rotation fields), `Reposicion`,
  `FilaDeRotacion`, `Rotacion`; `clienteDeReportes.reposicion`,
  `clienteDeReportes.rotacion`; `rutasDeExportacion.reposicion`.
- [ ] 6.2 Create the pure helper `agruparPorProveedor(filas) →
  { idProveedor, proveedor, filas }[]` — a **fold over the already-ordered
  list** (no client-side sort — decision 4).
- [ ] 6.3 Create `src/Ways.Web/src/paginas/Reposicion.tsx`: PV selector,
  fetches `clienteDeReportes.reposicion(idPuntoVenta, null)`,
  `BotonDeDescarga` pointing at `rutasDeExportacion.reposicion(idPuntoVenta)`
  (the `Existencias.tsx` shape verbatim). Header per group shows the
  proveedor and its row count; `sugerido` renders `—` when null, never `0`.
- [ ] 6.4 Modify `src/Ways.Web/src/App.tsx` and
  `src/Ways.Web/src/componentes/Layout.tsx`: one route, one nav entry
  alongside `/reportes/stock/vencimientos`.
- [ ] 6.5 [P] Descriptor tests for `agruparPorProveedor`: two proveedores
  in server order, a *Sin proveedor* bucket landing **last**, a single row,
  the empty list. *(web-descriptor-tests)*
- [ ] 6.6 [P] Component test: `sugerido` renders `—` for a null value,
  never `0` — the cell an operator would otherwise misread as "buy
  nothing".
- [ ] 6.7 Gate guard: `has-pending-model-changes` clean, zero migration
  files in the diff.
- [ ] 6.8 Run `judgment-day`; fix; re-judge until clean.
- [ ] 6.9 Branch `feat/stage13-slice6-web-reposicion` off `main` (parent:
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

- [ ] 7.1 Modify `ServicioDeReportesDeStock.cs`:
  `ObtenerResumenDeReposicionAsync(idPuntoVenta, ct)` — calls the **same**
  `ObtenerReposicionAsync` method and folds its own rows: `bajoMinimo :=
  filas.Count`, `sinStock := filas.Count(f => f.Cantidad <= 0)`,
  `sinProveedor := filas.Count(f => f.IdProveedor is null)`. **No second
  aggregation query.** *(design decision 8, 9 — with the field name
  correction recorded in Orchestrator Decision #5 above: `sinProveedor`,
  not design.md's stale `sinSugerencia`)*
- [ ] 7.2 Modify `Contratos.cs`: `ResumenDeReposicion(IdPuntoVenta,
  BajoMinimo, SinStock, SinProveedor)`. `dto-contract-honesty`:
  doc-comment `SinProveedor` as the *Sin proveedor* group count — distinct
  from, and never conflated with, "no `sugerido`" (`reposicion` unset).
- [ ] 7.3 Modify `ReportesEndpoints.cs`:
  `GET /reportes/stock/reposicion/resumen?idPuntoVenta`, `Politicas.LecturaDeReportes`.
- [ ] 7.4 Modify `src/Ways.Web/src/api/{tipos,reportes}.ts`: mirror
  `ResumenDeReposicion` (`sinProveedor`, not `sinSugerencia`);
  `clienteDeReportes.reposicionResumen`.
- [ ] 7.5 Modify `src/Ways.Web/src/paginas/Tablero.tsx`: `PanelDeReposicion`
  cloned from `PanelDeVencimientos` — `usePanelDeReporte`, requires a
  concrete `idPuntoVenta` (neutral copy otherwise), `PanelDeError` + retry,
  `Link` to the report, and **one `data-testid` per metric**:
  `reposicion-tile-bajo-minimo`, `reposicion-tile-sin-stock`,
  `reposicion-tile-sin-proveedor` — the stage-12 slice-15 lesson: a blob
  assertion cannot catch two swapped counts.
- [ ] 7.6 Modify `src/Ways.Web/src/paginas/Existencias.tsx`: add the
  `Sugerido` column, fed by `clienteDeReportes.rotacion(idPuntoVenta)`
  fetched alongside the report and indexed by `idArticulo`; an articulo
  absent from that map renders `—`, never `0`.
- [ ] 7.7 [P] **Mutation target**: the fold of `sinStock`
  (`f.Cantidad <= 0`) — change to `< 0` — the tile test seeded with an
  articulo at exactly `0` (7.8) must fail. *(mutation-proof-tests)*
- [ ] 7.8 [P] Integration: the tile's three counts equal the report's
  folded values over a seed with **all three metrics distinct**
  (`bajoMinimo = 7, sinStock = 2, sinProveedor = 1`), so no two counts can
  be swapped without detection. *(spec `reposicion-de-stock`: "The Tablero
  Tile Reuses The Report Method, Never A Second Aggregation Query",
  scenario "The tile's three counts equal the report's folded values")*
- [ ] 7.9 [P] Integration, discrimination: two rows below minimo — one with
  no `id_proveedor_habitual` and `sugerido = 30`, another with a proveedor
  assigned but `sugerido = null` (`reposicion` unset) — `sinProveedor = 1`,
  counting only the row missing a proveedor, independent of `sugerido`.
  *(spec: "sinProveedor counts the Sin proveedor group, not a missing
  suggestion")*
- [ ] 7.10 [P] Component test: the three `data-testid`s asserted
  individually, not as one blob — the stage-12 slice-15 lesson applied.
- [ ] 7.11 [P] Component test: the `Existencias.tsx` `Sugerido` column
  renders `—` for an articulo absent from the rotation map, never `0`.
- [ ] 7.12 Gate guard: `has-pending-model-changes` clean, zero migration
  files in the diff.
- [ ] 7.13 Run `judgment-day`; fix; re-judge until clean.
- [ ] 7.14 Branch `feat/stage13-slice7-tile-y-sugerencia` off `main`
  (parent: slices 3+4+5); PR; merge stacked-to-main. **If the slice
  overflows at apply time, drop tasks 7.6/7.11 (the `Sugerido` column) per
  the pre-approved degradation above and record the reduction in the PR
  body — never a silent cut.**

**Test plan**: mutation target (7.7), tile≡report with distinct counts
(7.8), sinProveedor discrimination (7.9), per-testid assertions (7.10),
column null-render (7.11).

**Verify**: `dotnet test --filter FullyQualifiedName~ResumenDeReposicion` && `npm run test -- Tablero Existencias`

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
