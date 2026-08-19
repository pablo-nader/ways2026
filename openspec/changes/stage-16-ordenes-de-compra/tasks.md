# Tasks: Stage 16 — Órdenes de compra

## Orchestrator Decisions Recorded This Phase

> `spec.md` and `design.md` ran in PARALLEL (state.yaml). Where they diverge, `state.yaml`'s
> OD7 (spec tensions) and OD8 (design tensions) are authority — both ratify **design** in every
> case cited below. Precedent: stage-14/15 "conflict found and resolved" numbering.

1. **6 slices, stacked-to-main, adopted verbatim from `design.md`'s ratified Slicing table**
   (`design.md:471-484`), which is itself the proposal's tentative plan re-scoped by OD8/T1's
   decision 10 (backstops move to slice 1). Merge order `1 → 2 → 3 → 4 → 5 → 6`. Slice 1 owns
   the only migration and blocks everything; 3 depends on 2 for the entity surface; 4 depends on
   3 for the projection; 5 depends on 3 for the derivation; 6 depends on 5.
2. **DB gate — `db_gate: UNA-MIGRACION-APROBADA`** (`state.yaml`): slice 1 carries **exactly
   one** new migration, `OrdenesDeCompraEtapa16`, matching proposal gate §A-§D verbatim (1 enum
   type/5 values, 2 tables/9 FKs/4 CHECKs/11 named indexes + 1 implicit AK index, 1 additive
   ALTER, RLS last on both new tables, **zero data statements**). Slices 2-6 each carry a
   gate-guard task requiring `dotnet ef migrations has-pending-model-changes` clean and zero new
   files under `Migraciones/`. **Binding count**: total new indexes = **12** (7 on
   `ordenes_compra` incl. the implicit AK + 4 on `items_orden_compra` + 1 on
   `comprobantes_compra`) — any other count reopens the gate per `state.yaml`'s
   `db_gate_approval`.
3. **Pre-authorized cut points**, inherited verbatim from `design.md:490-505`: `1a`/`1b` at the
   table/link boundary; `3a`/`3b` at the write-path boundary (link+confirm vs anulación); `5a`/`5b`
   at the read boundary (list vs detail); `6` may drop the `Reposicion.tsx` action (API still
   serves it). **Never degraded**: lock-then-re-read-then-update, zero-extra-statements, the
   `_numero` ordering trap, the manual-close short-circuit.
4. **CONFLICT FOUND AND RESOLVED #1 — the `enviar` concurrency criterion.**
   `ordenes-de-compra/spec.md`'s Requirement "Enviar Assigns The Own Number…" states two
   concurrent `enviar` calls "for OCs at the same punto de venta" must both succeed with distinct
   numbers, and separately (sequential, not concurrent) that re-sending an already-`enviada` OC is
   `409`. It never states the **concurrent same-OC** case. `design.md` flagged this ambiguity
   itself (Open Question T1, `design.md:533-537`) and resolved it: the no-409 guarantee applies
   only to **two distinct OCs** of one PV; two concurrent `enviar` of the **same** OC must produce
   one `200` + one `409`, burning the loser's number. **`state.yaml` OD8/T1 is authoritative**
   (ratified in favor of design). Tasks 2.14-2.15 implement both shapes as two separate binding
   tests.
5. **CONFLICT FOUND AND RESOLVED #2 — ligadura state-gating has no named domain codes in the
   spec.** `ordenes-de-compra/spec.md`'s "Ligadura Invariant" requirement states only the
   tenant/proveedor/punto-de-venta match; it never states which OC `estado`s accept a link.
   `spec.md`'s own OD7/T3 (state.yaml, spec phase) anticipated exactly this: *"los códigos de
   dominio sin nombre… los NOMBRA el design — si no lo hace, tasks los reconcilia."* `design.md`
   decision 8 (`design.md:63`, Open Question T2 `design.md:538-541`) DOES name them: linkable
   `enviada`/`recibida_parcial`/`cerrada`; refused `borrador` → `orden_compra_no_enviada` (409),
   `anulada` → `orden_compra_anulada` (409). **`state.yaml` OD8/T2 ratifies design's "cerrada
   stays linkable" call.** Adopted verbatim; tasks 3.9-3.11 add the state-gated link tests the
   spec's scenario list does not enumerate.
6. **CONFLICT FOUND AND RESOLVED #3 — an OC with zero items.** Neither `spec.md` names nor
   forbids sending an empty OC. `design.md` decision 7 (`design.md:62`, Open Question T6
   `design.md:555-557`) refuses it at `enviar` (`orden_compra_sin_items`, 400) because the
   derivation's `NOT EXISTS` is vacuously true and the first projection would read `cerrada` for
   an order nobody placed. **`state.yaml` OD8/T6 ratifies.** Task 2.13 implements and tests the
   guard (mutation target #17).
7. **CONFLICT FOUND AND RESOLVED #4 — `CompraDetalle` response shape.** `proposal.md`'s Affected
   Areas table for `ComprasEndpoints.cs` originally implied "no response shape changes" beyond the
   request gaining `idOrdenCompra`. `design.md` decision (Open Question T7, `design.md:558-561`)
   corrects this under `dto-contract-honesty` rule 2: a request-only field cannot satisfy the
   round-trip assertion, so `CompraDetalle` gains exactly one nullable field,
   `IdOrdenCompra`. **`state.yaml` OD8/T7 ratifies.** Task 3.4 implements; task 3.16 is the
   round-trip test.
8. **`mutation-proof-tests` compliance**: the **34** named mutation targets in `design.md:427-469`
   are each placed in exactly one slice, per design's own "Slice" column: 1 → 9 (targets 1-9),
   2 → 8 (10-17), 3 → 13 (18-30), 4 → 3 (31-33), and target 34 (a compound row) is split by its own
   sub-clauses across 4 (the authorization `.RequireAuthorization` route stack), 5 (pagination
   tiebreaker + per-filter + `Desvio` null branch), and 6 (the two web gating branches + the
   pre-load exclusion filter) — no sub-clause duplicated, none dropped. Every target requires
   apply-time evidence (mutation applied → named failing test → reverted → green) recorded in its
   slice's PR body.
9. **`db-error-backstops` applies across three slices, per design decision 10 (`design.md:65`,
   which OVERRODE the proposal's original slice-2 placement — `state.yaml` OD8 ratifies).** All
   **6** `ManejadorDeErrores.cs` branches (2 exact-name `23505` incl. the `_numero` ordering trap +
   4 exact-name `23514`) ship in **slice 1**, proven out-of-band by raw insert (no call site
   needed yet). Slice 2 then carries only the **concurrency proof** of the numbering assigner
   (decision 4 above). Slice 3 carries the client-reachable FK 9 pre-check + race
   (`ExigirOrdenLigableAsync`). FK 2/FK 3/FK 8 pre-checks land in slice 2's draft write path.
10. **`react-async-state` + `web-descriptor-tests` apply to slice 6 only** — the single
    web-touching slice.
11. **`dto-contract-honesty` applies at slice 3** (`SolicitudDeCompra`/`CompraDetalle` gain
    `IdOrdenCompra`, decision 7 above) **and slice 5** (`ContratosDeOrdenDeCompra.cs` — every new
    read/write DTO, the `Cobertura` list never a fabricated per-line split, decision 13).
12. **`work-unit-commits` applies to every slice.**
13. **Testing convention — fixed clock and asymmetric seeds.** Every date-bearing test pins
    `RelojFijo(2026-08-19T12:00:00Z)` (mediodía UTC); at least one listing/offset test additionally
    sends the real client offset `-03:00` (never `Z`) and asserts both the returned rows and the
    displayed período (`mutation-proof-tests` rule 10, the stage-14 verify W2 / PR #129 lesson —
    only this shape can see a raw-ADO UTC-normalization regression). Every fixture uses
    **deliberately desynchronized ids** (`id_tenant`, `id_proveedor`, `id_punto_venta`,
    `id_articulo`, `id_orden_compra` never coincidentally equal or sequentially aligned) so a
    mutant that swaps one identity column for a numerically-similar one cannot pass by accident.
14. **Archive-phase carryover, registered so it is not read as an omission from this phase's
    scope.** `state.yaml`'s spec-phase OD7/T1 is **binding for `sdd-archive`, not `sdd-apply`**:
    the Purpose prose of the LIVE `openspec/specs/reposicion-de-stock/spec.md` (not the delta in
    this change folder) must be updated together with the delta fusion, because the delta format
    cannot express a Purpose-level correction. No task below touches the live spec tree. Similarly,
    `docs/11-programa-post-paridad.md`'s Etapa 16 status block is explicitly "orchestrator, outside
    the phase" per `proposal.md:760` — no task here either.
15. **Process rule (stage-12/14/15 discipline): every deviation `sdd-apply` takes from this plan
    is registered IN `tasks.md`** — as a task-level note or a new numbered decision appended to
    this section — never left to verify-phase archaeology.
16. **`judgment-day` round 1 (juez B) confirmed 1 MAJOR on task 1.26's gate guard — the count-only
    test let a column-order mutant survive.** `ElConteoTotalDeIndicesNuevosEsExactamenteDoce`
    asserts only `indexname` against `pg_indexes` (names and count), never column order. The judge
    mutated `ix_ordenes_compra_proveedor` from `(id_proveedor, id_tenant)` to `(id_tenant,
    id_proveedor)` — defeating FK 3's prefix coverage per the proposal audit (`proposal.md:594-
    597`, "No index is led by `id_tenant` except 1 … and 6") — and the test stayed green. Closed
    tests-only (production code is correct — the gap was coverage, not a defect): added
    `LasDefinicionesDeLosIndicesCompuestosRespetanElOrdenDeColumnasDelContrato`
    (`OrdenesCompraSchemaTests.cs`) asserting the full `pg_indexes.indexdef` DDL — exact column
    order — of every composite index new to this slice (`ix_ordenes_compra_punto_venta_fecha`,
    `ix_ordenes_compra_proveedor`, `ux_ordenes_compra_numero` incl. its `UNIQUE`/partial `WHERE`,
    the AK's implicit unique index, both `items_orden_compra` FK-support indexes,
    `ux_items_orden_compra_orden`, `ix_comprobantes_compra_orden_compra`), plus a loop asserting no
    composite index is led by `id_tenant` except the one that carries it by design
    (`ux_ordenes_compra_numero`). Mutation evidence: committed the test first (`9c0b29e`), then
    swapped the columns in `OrdenCompraConfiguration.cs`'s live EF model first — build green,
    ALL tests still green, because the fixture applies the pre-generated migration file
    (`20260819042145_OrdenesDeCompraEtapa16.cs`), not the live model, so an `IEntityTypeConfiguration`
    edit alone never reaches the test database; reverted that no-op attempt. Repeated the judge's
    exact swap on the actual DDL instead — `CreateIndex(name: "ix_ordenes_compra_proveedor", …
    columns: new[] { "id_tenant", "id_proveedor" })` in the migration file — the new test failed
    (`Assert.Equal` expected `["id_proveedor","id_tenant"]`, got `["id_tenant","id_proveedor"]`),
    reverted with `git checkout -- src/`, rebuilt, full `OrdenesCompraSchemaTests` green (19/19)
    after revert, `git status` clean.
17. **Slice 2 apply-phase decisions and deviations (decision 15 discipline).**
    - **OD9 (orchestrator, launch prompt for this phase): the FK 2/FK 3 pre-check resolvers of
      `ServicioDeOrdenesDeCompra` are PRIVATE and PROPER to that class**, copying the FORM of
      `ServicioDeCompras.ResolverProveedorAsync`/`ResolverPuntoVentaAsync` (both `private` there,
      so there is nothing to reuse by composition) instead of promoting them to a shared helper —
      same criterion `ServicioDeGastos` already applies against `ServicioDeCompras` for the same
      pair of resolutions. Followed verbatim; `ResolverProveedorAsync`/`ResolverPuntoVentaAsync`/
      `ExigirArticulosExistentesAsync` in `ServicioDeOrdenesDeCompra.cs` are `private`, simplified
      to existence checks (`AnyAsync` → `ErrorDominio.NoEncontrado`/`referencia_invalida`) since the
      draft path never needs the resolved entity's other columns the way `ServicioDeCompras` does.
    - **Response DTO deviation**: this slice introduces `OrdenDeCompraBorrador`
      (`ContratosDeOrdenDeCompra.cs`) instead of populating design's single `OrdenDeCompraDetalle`
      early — the latter's `Cobertura`/`TotalEstimado`/`TotalReal`/`DesvioTotal`/
      `ComprobantesLigados` fields cannot be honestly filled before the reception book exists
      (slice 3) and the read model that computes them (slice 5, task 5.1 — which still *creates*
      `OrdenDeCompraDetalle`, unmodified from the original plan). `dto-contract-honesty` rule 1: a
      field that would always be `null`/empty in this slice is not a contract, it's filler.
    - **DELETE endpoint / `EliminarAsync` NOT implemented**, despite being named in this phase's
      launch prompt ("PUNTOS CLAVE"/"ARTEFACTOS"). Neither `tasks.md` (this file, before this
      phase), `design.md`'s API Surface table (7 routes: `GET /`, `GET /{id}`, `POST /`,
      `PUT /{id}`, `POST /{id}/enviar`, `POST /{id}/cerrar`, `POST /{id}/anular` — no `DELETE`) nor
      `design.md`'s `File Changes`/authorization-matrix task (4.11, "the five new non-GET routes")
      name a delete route. Adding a sixth write route not covered by the slice-4 authorization
      allowlist test would silently break that binding count. Treated as a launch-prompt/artifact
      conflict resolved in favor of the authoritative SDD artifacts (`sdd-apply`'s contract: follow
      design decisions, don't freelance); flagged here for the orchestrator to decide whether a
      future task should add it.
    - **Process note**: the apply-phase host process did not die mid-cycle this slice (unlike
      slice 1's task 1.39-adjacent note) — all 8 mutation-evidence cycles (targets #10-#17) ran
      end-to-end in one pass, each verified FAIL → revert → green before proceeding to the next.
      Docker Desktop was already healthy at the start of this phase (verified with `docker info`
      before the first test run).
18. **`judgment-day` round 1 (juez B) confirmed 2 MAJOR on task 2.26's `ServicioDeOrdenesDeCompra`
    test coverage — tests-only, production code correct in both cases.**
    - **MAJOR 1 (regla 12c)**: the replace-set `DELETE`
      (`ServicioDeOrdenesDeCompra.cs:111`, `Where(i => i.IdOrdenCompra == id)`) widened to
      unscoped `db.ItemsOrdenCompra` survives 11/11 — no test seeded a SIBLING OC of the same
      tenant. Closed by widening `ItemsSeReemplazanCompletosEnUnRequestAgregandoYQuitando`: seeds
      a second OC of the same tenant with its own discriminant items (quantities 77/88 on both
      articles), then asserts after both `PUT`s on the first OC that the sibling's two items are
      untouched (exact count + identity + quantity). Mutation evidence: widened the `DELETE` to
      `db.ItemsOrdenCompra.ToListAsync(ct)` (no `Where`) → `dotnet build --no-incremental` clean →
      the sibling assertion FAILED (`Assert.Equal` expected 2, actual 0 — the sibling's items were
      deleted by the unscoped replace-set) → `git checkout -- src/` → `git status` clean.
    - **MAJOR 2 (dto-contract-honesty)**: `SolicitudDeOrdenDeCompra.FechaEsperada`/`.Observaciones`
      were never asserted as persisted — forcing both to `null` in `CrearBorradorAsync`/
      `EjecutarActualizacionAsync` survives 11/11. Closed with a new fact,
      `FechaEsperadaYObservacionesSePersistenYSeActualizanEnElReplaceSet`: sends both fields with
      real discriminant values on `CREATE` (`DateOnly(2026,9,15)` — the field is `DateOnly?`, no
      offset applies — + a discriminant observation string), asserts the response AND a direct DB
      read match; then changes both on `PUT` to different discriminant values, asserts the new
      truth; then clears both to `null` on a third `PUT`, asserts the DB reflects `null` for both.
      Mutation evidence: forced `FechaEsperada = null` / `Observaciones = null` (dropping
      `solicitud.FechaEsperada`/`NormalizarOpcional(solicitud.Observaciones)`) in both
      `CrearBorradorAsync` and `EjecutarActualizacionAsync` → build clean → the new test FAILED
      (`Assert.Equal` expected `15/09/2026`, actual `null`) → `git checkout -- src/` → `git status`
      clean.
    - Both cycles rebuilt clean after revert; full `ServicioDeOrdenesDeCompraTests` green (12/12,
      the extra test being the new fact) with `git status` clean. Committed as `8b15fa2`
      (`test(stage16-slice2): endurece replace-set y FechaEsperada/Observaciones (judgment-day juez
      B)`).

19. **`judgment-day` round 1 (juez A): 1 WARNING documental registrado + 1 CRITICAL REFUTADO
    con evidencia.**
    - **WARNING (drift de nombre, este registro es el cierre)**: design.md:243 nombra el rechazo
      de pre-lectura del enviar como `orden_compra_ya_enviada`; el código shipped usa
      `orden_compra_no_enviable` y REUSA el mismo código para el caso 0-filas de la carrera del
      PUT que muda de PV. El nombre del código es DELIBERADAMENTE más general que el del design:
      cubre ambas causas reales de no-enviabilidad (estado ≠ borrador y la reclasificación por
      carrera) con una sola verdad; ningún spec ni test pinea el string del design. El design
      queda como texto stale en esa línea; este registro es la desviación que faltaba.
    - **CRITICAL REFUTADO**: el juez A reportó que el diff "borra" la extensión de la regla 12c
      de mutation-proof-tests. Falso positivo del método de congelado: el orquestador commiteó
      esa extensión a main (87d612d) DESPUÉS de que esta rama naciera (dcb517f), y el diff de
      dos puntos `main..HEAD` muestra el avance de main como reversa. Evidencia: `git log
      main..HEAD --name-only` no contiene ningún archivo de `.claude/` (la rama jamás tocó la
      skill) y el diff three-dot `main...HEAD` (merge-base) trae solo los 7 archivos reales del
      slice. El merge three-way preserva la regla en main. Regla de proceso nueva: los diffs de
      judgment se congelan con `main...HEAD`.

24. **Slice 5 apply-phase decisions and deviations (decision 15 discipline).**
    (Renumerada por el orquestador: el apply la registró como "20", colisionando con la 20 del
    slice 3 — la secuencia real llegaba a 23.)
    - **Pre-load (tasks 5.4/5.12) is NOT a backend method — deviation from this file's own prior
      wording, resolved in favor of `design.md`.** `design.md:347-351` (the Web section) and its
      Testing Strategy row for "Web (vitest)" place the reposición→OC mapping — `{IdArticulo,
      Sugerido} → {IdArticulo, CantidadPedida}`, filtering `sugerido = null`, blocking the `"Sin
      proveedor"` bucket — entirely inside `Reposicion.tsx` (slice 6), posting an ordinary
      `SolicitudDeOrdenDeCompra` to the ALREADY-EXISTING `POST /` (slice 2). `design.md`'s own File
      Changes table for `ServicioDeOrdenesDeCompra.cs` (`:376`) lists only "Draft CRUD, `enviar`,
      `cerrar`, `anular`, list + detail read model" — no pre-load method — and mutation target #34's
      row places "the `sugerido !== null` filter" under slices "4-6" (the web branch), never 5.
      This file's own tasks 5.4/5.12 (drafted at `sdd-tasks` time) implied a backend method; treated
      as a tasks-drafting/design mismatch resolved in favor of the authoritative `design.md` (same
      "`sdd-apply`'s contract: follow design decisions, don't freelance" criterion slice 2 already
      applied to the DELETE-endpoint mismatch, decision 17 above). No backend code added for the
      pre-load; `POST /` needs zero modification.
    - **The cobertura derivation is a SEPARATE LINQ query, never a shared SQL fragment with
      `EscriturasDeOrdenDeCompra`'s raw-ADO derivation — resolves the launch prompt's own open
      question ("¿reusable? — si duplicás la derivación, el design lo prohíbe... extraé o
      reusá").** `design.md`'s Testing Strategy explicitly names TWO derivations and a fidelity
      test as the seam between them: "Integration — projection fidelity | For every fixture: the
      stored `estado` equals `ProyectorDeEstadoDeOrden.Proyectar(...)` recomputed from the **read
      model's own** cobertura numbers | The one assertion that keeps the raw-ADO derivation and
      the LINQ derivation from drifting" (`design.md:421`) — this sentence only makes sense if the
      two derivations are independent implementations, not shared SQL. Decision 12 above
      ("verdad única") governs the **stored `estado` column** (never re-derived by the read
      model), not the underlying quantity computation, which legitimately has two
      implementations cross-checked by task 5.9's fidelity test rather than unified by code
      sharing. Sharing the raw-ADO CTE text would also be structurally awkward: the write side
      only needs two aggregated booleans (`completa`/`algoRecibido`); the read side needs full
      per-artículo rows including recibido-no-pedido (`Pedida = 0`, decision 13) and the price
      comparison, a materially different shape. Both derivations independently add `deleted_at IS
      NULL` (no entity in this repo has a global EF query filter for soft-delete — verified by
      grepping `HasQueryFilter` across `Configuraciones/`, zero hits — so the LINQ side needed its
      own explicit filter, mirroring the raw-ADO defense-in-depth rather than inheriting it).
    - **`Pendiente`/`TotalEstimado`/`TotalReal`/`DesvioTotal` formulas — design gaps filled by
      implementation, registered here since neither `design.md` nor the spec pins the exact
      arithmetic.** `Pendiente = Math.Max(Pedida - Recibida, 0)` — never negative on an
      over-delivery, matching `design.md:346`'s own use of `Pendiente > 0` to gate `CompraEditor.tsx`'s
      pre-fill (a negative value would be a nonsensical gate condition). `CostoEstimado`/`CostoReal`
      per artículo are cantidad-weighted averages over only the comparable lines (a line with
      `CostoUnitarioEstimado IS NULL` contributes no zero to the estimated average; an artículo with
      zero linked confirmed lines has no real average) — `Desvio` is `null` unless BOTH sides exist
      (never a partial or fabricated value). `TotalEstimado`/`TotalReal` sum only the artículos whose
      own `CostoEstimado`/`CostoReal` is non-null (`dto-contract-honesty`: mixing real terms with
      fabricated zeros would misstate the total), `null` when zero terms qualify; `DesvioTotal`
      follows the same "both totals present, denominator non-zero" gate as the per-artículo `Desvio`.
    - **Test tipo de comprobante choice**: `C-FB` (`DiscriminaIva = false`) used for every receiving
      comprobante in this slice's tests, not `C-FA` (used by slices 1/4's fixtures) — deliberate,
      to keep `CalculadorDeCompra.CalcularCostoEfectivoDesdeItem`'s arithmetic IVA-free
      (`total/cantidad`) so the price-deviation assertions (`+12%`, weighted averages) land on exact
      decimal values instead of IVA-rounded ones. Not a production code path change — `DiscriminaIva`
      is read per-line from each linked comprobante's own tipo either way (decision 14).
    - **`judgment-day` NOT run** (task 5.19) — same executor-boundary reason as every prior slice.
      Full solution test suite run once end-to-end post-implementation (`dotnet test`, no filter):
      **2216/2216 green** (526 Domain + 291 Application + 1399 Integration), confirming the
      non-regression criterion (mutation target #34's trailing row) holds across the whole tree, not
      only the `ComprasConfirmar`/`ComprasAnular` suites named there.

25. **`judgment-day` round confirmed 3 CRITICAL (juez B) on `OrdenesCompraLecturaTests.cs` —
    closed with focused fixes, tests-only, producción intacta.** Tercera ocurrencia registrada en
    el programa de una violación de la regla 12b de `mutation-proof-tests` (design.md:420, "every
    projected money/date field asserted with per-row discriminating values") — las dos previas
    incluyen stage-15 slice 4 ("15-s4"); esta, la tercera, en esta slice.
    - **CRITICAL 1**: `CoberturaDeArticulo.Pendiente` solo se asserteaba en `0` (7-7 en el fixture
      rico y `Math.Max(0-1,0)` en el recibido-no-pedido) — un `var pendiente = 0m;` fijo en
      `ObtenerCoberturaAsync` sobrevivía 11/11. **Fix**: nuevo test dedicado
      `CoberturaPendienteEsPositivaCuandoLaRecepcionNoCompletaLoPedido` (pedida 7, recibida 5 ⇒
      Pendiente 2, asserteado explícitamente).
    - **CRITICAL 2**: `OrdenDeCompraDetalle.TotalEstimado`/`TotalReal`/`DesvioTotal` jamás se
      asserteaban con valores positivos (un solo `Assert.Null` en el fixture "nunca cotizada") — un
      `12345m` fijo sobrevivía. **Fix**: en el fixture rico
      (`CoberturaPorArticuloDiscriminaCorrectamenteYLaProyeccionCoincideConLaColumna`) se agregaron
      los tres agregados calculados a mano: `TotalEstimado = 700m` (100×7), `TotalReal = 834m`
      (112×7 + 50×1), `DesvioTotal = 19.14m` ((834-700)/700×100) — pairwise-distintos.
    - **CRITICAL 3**: `IdProveedor`/`IdPuntoVenta` del detalle (dos `int` posicionales adyacentes
      en un record de 17 parámetros) jamás se leían de vuelta — un SWAP de ambos en el constructor
      de `OrdenDeCompraDetalle` sobrevivía 197/197. **Fix**: nuevo test integral
      `DetalleDevuelveCadaCampoPosicionalConSuVerdad` que assertea ambos ids contra
      `IdProveedor2`/`IdPuntoVenta2` (desincronizados, con una precondición `Assert.NotEqual` que
      falla ruidosamente si algún día colisionaran) y, por ser la misma causa raíz (ningún campo
      posicional leído de vuelta), extiende la lectura a los campos del detalle hasta ahora sin
      cobertura: `Numero`, `FechaEnvio`, `FechaEsperada`, `FechaCierre`, `CierreManual`,
      `Observaciones`, `Estado` — y a `Estado` de la fila de `PaginaDeOrdenesDeCompra`, todos con
      valores distintos entre sí.
      **Corrección (decisión 26, judgment-day ronda 2, juez A — CRITICAL 2)**: la frase original de
      este punto ("extiende la lectura a TODOS los campos ... del detalle y de la fila del listado")
      era falsa para la fila del listado — solo `Estado` quedó cubierto ahí; `IdProveedor`/
      `IdPuntoVenta` de `OrdenDeCompraListada` (el mismo swap posicional, pero en el `Select` de
      `ListarAsync`) seguían sin ningún assert y ese swap pasaba 13/13. Ver decisión 26 para el fix.
    - **Evidencia de mutación** (tres ciclos, `dotnet build --no-incremental` + test filtrado +
      `git checkout -- src/` + rebuild entre cada uno, `git status` limpio en cada punto): (a)
      `pendiente = 0m` fijo → `CoberturaPendienteEsPositivaCuandoLaRecepcionNoCompletaLoPedido`
      FALLA (`Expected: 2, Actual: 0`) → revert verificado; (b) `totalEstimado = 12345m` fijo →
      `CoberturaPorArticuloDiscriminaCorrectamenteYLaProyeccionCoincideConLaColumna` FALLA
      (`Expected: 700, Actual: 12345`) → revert verificado; (c) swap de
      `IdProveedor`/`IdPuntoVenta` en el constructor de `OrdenDeCompraDetalle` →
      `DetalleDevuelveCadaCampoPosicionalConSuVerdad` FALLA (`Expected: 2, Actual: 4`) → revert
      verificado. Corrida final completa del archivo: **13/13 verde**, `git status` con solo el
      archivo de test modificado.

26. **`judgment-day` ronda 2 (juez A) confirmó 1 CRITICAL de producción + 1 CRITICAL de
    tests/docs, 1 WARNING, 1 SUGGESTION — cerrados con fixes acotados.**
    - **CRITICAL 1 (producción)**: `TotalEstimado` (`ObtenerDetalleAsync`) fabricaba costo —
      agregaba desde `Cobertura` (`CostoEstimado` por-artículo × `Pedida` TOTAL del artículo), pero
      `CostoEstimado` ya es un promedio ponderado que promedia SOLO las líneas cotizadas de ese
      artículo, mientras que `Pedida` suma TODAS sus líneas, cotizadas o no — extrapolación
      silenciosa alcanzable con un POST de dos líneas del mismo artículo, una cotizada y otra sin
      costo (p.ej. 3 unidades a 100 + 4 sin costo ⇒ el bug daba 100×7=700 en vez de los 300
      reales). **Fix**: `TotalEstimado` se recalcula a NIVEL LÍNEA — `sum(CostoUnitarioEstimado *
      CantidadPedida)` sobre `items` (ya materializado en `ObtenerDetalleAsync`) SOLO para las
      líneas con costo seteado; `null` cuando ninguna lo tiene. `CoberturaDeArticulo.CostoEstimado`
      (el promedio por-artículo, display) NO se toca — sigue siendo el criterio correcto para esa
      columna. **Verificado y descartado el mismo patrón en `TotalReal`/`DesvioTotal`**: `CostoReal`
      y `Recibida` por artículo (`ObtenerCoberturaAsync`, `recibidoPorArticulo`) derivan siempre de
      la MISMA población de `itemsRecibido` para ese artículo (mismo `GroupBy`, mismo `Cantidad`
      agregado) — no existe una línea "recibida sin costo" que desacople ambos lados como pasaba en
      el lado estimado, así que `TotalReal` se deja agregando desde `Cobertura` sin cambios.
    - **CRITICAL 2 (tests + docs)**: `OrdenDeCompraListada.IdProveedor`/`IdPuntoVenta` jamás se
      asserteaban (el swap de ambos en el `Select` de `ListarAsync` pasa 13/13) y la decisión 25
      afirmaba falsamente cobertura de "la fila del listado" cuando solo `Estado` estaba cubierto.
      **Fix**: `DetalleDevuelveCadaCampoPosicionalConSuVerdad` ahora assertea ambos ids de la fila
      del listado contra `IdProveedor2`/`IdPuntoVenta2` (desincronizados, discriminantes); la
      afirmación de la decisión 25 corregida in situ (ver arriba).
    - **WARNING**: `ComprobantesLigados` (fixture rico de la cobertura) solo se asserteaba con
      `Count >= 4`, dejando pasar un comprobante extra o de menos en silencio. **Fix**: assert de
      conjunto exacto contra los 5 ids esperados (los cuatro confirmados + el borrador).
    - **SUGGESTION**: variable muerta `pasadoManana` eliminada de
      `CadaFiltroIgnoradoDevolveriaDeMasConSemillasAsimetricas`.
    - **Tests nuevos obligatorios del CRITICAL 1**: `TotalEstimadoSumaSoloLasLineasCotizadasSin
      ExtrapolarAlPromedioDelArticulo` (caso mixto: 3 unidades cotizadas a 100 + 4 sin costo del
      mismo artículo ⇒ `TotalEstimado = 300`, jamás 700 por promedio); el caso todo-sin-costo ⇒
      `null` ya existía (`UnaLineaNuncaCotizadaReportaNoComparableNuncaCero`) y sigue verde.
    - **Evidencia de mutación** (dos ciclos, commit primero, `dotnet build --no-incremental` + test
      filtrado + `git checkout -- src/...` + rebuild entre cada uno, `git status` limpio en cada
      punto — la producción NUEVA es el estado ya committeado, el mutante es la fórmula/Select
      viejos): (a) revertida la fórmula de `TotalEstimado` a `promedio × pedida total` →
      `TotalEstimadoSumaSoloLasLineasCotizadasSinExtrapolarAlPromedioDelArticulo` FALLA (`Expected:
      300, Actual: 700`) → revert verificado; (b) swap de `IdProveedor`/`IdPuntoVenta` en el
      `Select` de `ListarAsync` → `DetalleDevuelveCadaCampoPosicionalConSuVerdad` FALLA (`Expected:
      2, Actual: 4`, el nuevo assert de la fila del listado) → revert verificado. Corrida final
      completa del archivo: **14/14 verde**; regresión filtrada `Compras`/`OC`: **254/254 verde**;
      `git status` limpio.

**Not a new conflict, no action required** (already resolved in earlier phases): T3 (spec OD7) —
the `comprobantes-compra` mirroring is the stage-15 pattern, not duplication; T4 (spec OD7) — the
word-budget overage is a house precedent, no action; T5 (spec OD7) — `cuenta-corriente-de-
proveedores` verified untouched, task 4.13 confirms by `git diff --stat`; T3/T4/T9/T10 (design
OD8) — `FOR SHARE` is validation-only outside a transaction (task 3.2 note), the `ServicioDeGastos`
citation correction is prose-only, `algoRecibido` sourced from the reception side (task 3.7,
mutation target #25), and the anulación guard stays lock-free (task 4.7, mutation target #33).

---

## Suggested Work Units

| Unit | Goal | Likely PR | Focused test command | Runtime harness | Rollback boundary |
|---|---|---|---|---|---|
| 1 | Migration (type, 2 tables, 9 FKs, 4 CHECKs, 12 indexes, ALTER, RLS last) + entities + `ProyectorDeEstadoDeOrden` + EF configs + `MapEnum` + 6 `ManejadorDeErrores` branches + doc 10 | PR 1 | `dotnet test --filter FullyQualifiedName~OrdenesCompraSchema\|FullyQualifiedName~ProyectorDeEstadoDeOrden` | Testcontainers Postgres 17, `ways_app` NOSUPERUSER NOBYPASSRLS | Revert branch: `DROP` FK→col→both tables→type, no dependent object, no data migration to undo |
| 2 | `ServicioDeOrdenesDeCompra` draft CRUD + `enviar` (own numbering) + backstop concurrency proof | PR 2 | `dotnet test --filter FullyQualifiedName~ServicioDeOrdenesDeCompra` | Real Postgres, forced rendezvous (two `enviar` tasks) | Revert branch: endpoints/service disappear, schema untouched |
| 3 | `idOrdenCompra` on compra draft + `ExigirOrdenLigableAsync` + `EscriturasDeOrdenDeCompra` + guarded calls in `ConfirmarAsync`/`AnularAsync` | PR 3 | `dotnet test --filter FullyQualifiedName~EscriturasDeOrdenDeCompra\|FullyQualifiedName~ServicioDeComprasLigadura` | Real Postgres, forced rendezvous (confirm×confirm, anular×confirmar) | Revert branch: call sites disappear, unlinked confirms already proven byte-identical |
| 4 | `POST /cerrar` + `POST /anular` + `anulada` refusal inside confirm + 409/authorization matrix | PR 4 | `dotnet test --filter FullyQualifiedName~OrdenesCompraCierreYAnulacion` | Real Postgres, `SuperficieDeAutorizacionTests` allowlist | Revert branch: endpoints disappear, no dependent write path |
| 5 | Paginated list + detail read model (cobertura, price deviation) | PR 5 | `dotnet test --filter FullyQualifiedName~OrdenesCompraLectura` | Real Postgres, `RelojFijo` tied-fecha fixture | Revert branch: read endpoints disappear, no write-side impact |
| 6 | Web: list/detail/draft screens + client + `Reposicion.tsx` action + `CompraEditor.tsx` pre-load + `Compras.tsx` link | PR 6 | `npm run test -- OrdenesDeCompra` (Vitest) | Vitest + RTL, no browser required | Revert branch: screens/routes disappear, API still serves the shape |

Total ≈ **2 540 lines**. `Decision needed before apply: No` — `auto-chain` + `stacked-to-main`
already resolved in `state.yaml`.

---

## Slice 1: Schema + Backstops (PR 1)

**Start**: `main`. **Finish**: `estado_orden_compra` + both tables + the `comprobantes_compra`
ALTER exist with standard RLS, 12 total new indexes, the 6 `ManejadorDeErrores` branches proven
out-of-band; doc 10 carries both tables. No write path calls anything yet (slice 2). **Rollback**:
`ALTER TABLE comprobantes_compra DROP CONSTRAINT fk_comprobantes_compra_orden_compra` → `DROP
COLUMN id_orden_compra` → `DROP TABLE items_orden_compra` → `DROP TABLE ordenes_compra` → `DROP
TYPE estado_orden_compra` — no dependent object in that order (proposal Rollback Plan). **Done** =
tests green + `judgment-day` clean round + PR merged.

**Budget note**: pre-authorized split `1a` (type + both tables + entities + configs + RLS/CHECK
tests) / `1b` (the ALTER + the 6 backstops + doc 10) if this slice overflows — decision 3 above.

- [x] 1.1 Migration `OrdenesDeCompraEtapa16`: `CREATE TYPE estado_orden_compra AS ENUM
  ('borrador','enviada','recibida_parcial','cerrada','anulada')`. *(proposal.md:514-517, gate §A)*
- [x] 1.2 Same migration: `CREATE TABLE ordenes_compra` — 16 columns exactly per §B (`numero
  bigint NULL`, `fecha_emision` **no DEFAULT**, `fecha_envio/fecha_esperada/fecha_cierre NULL`,
  `id_empleado_cierre NULL`); `pk_ordenes_compra`. *(proposal.md:540-558)*
- [x] 1.3 Same migration: 5 named FKs on `ordenes_compra` — `fk_..._tenant`, `fk_..._punto_venta`
  (composite), `fk_..._proveedor` (composite), `fk_..._empleado` (simple), `fk_..._empleado_cierre`
  (simple, nullable) — all RESTRICT. `ak_ordenes_compra_id_orden_compra_id_tenant UNIQUE
  (id_orden_compra, id_tenant)`. *(proposal.md:566-576)*
- [x] 1.4 Same migration: `ck_ordenes_compra_envio_completo` and `ck_ordenes_compra_cierre` exactly
  per §B's table. *(proposal.md:577-578)*
- [x] 1.5 Same migration: 6 named indexes on `ordenes_compra` — `ix_..._tenant`,
  `ix_..._punto_venta_fecha`, `ix_..._proveedor`, `ix_..._empleado` (simple), `ix_..._empleado_cierre`
  (simple), `ux_ordenes_compra_numero` **UNIQUE PARTIAL** `WHERE numero IS NOT NULL` — plus the
  implicit AK index (7 total). Zero EF-autogenerated FK-support index beyond these.
  *(proposal.md:584-597)*
- [x] 1.6 Same migration: `CREATE TABLE items_orden_compra` — 11 columns exactly per §C (no
  `cantidad_recibida`); `pk_items_orden_compra`. *(proposal.md:606-617)*
- [x] 1.7 Same migration: 3 named FKs on `items_orden_compra` — `fk_..._tenant`,
  `fk_..._orden_compra` (composite, against §B's AK), `fk_..._articulo` (composite); `ck_..._
  cantidad_positiva`, `ck_..._costo_no_negativo`. *(proposal.md:630-634)*
- [x] 1.8 Same migration: 3 named indexes on `items_orden_compra` — `ix_..._tenant`,
  `ix_..._orden_compra`, `ix_..._articulo`, `ux_items_orden_compra_orden` **UNIQUE**
  `(id_orden_compra, orden)` (4 total). *(proposal.md:641-644)*
- [x] 1.9 Same migration: `ALTER TABLE comprobantes_compra ADD COLUMN id_orden_compra integer
  NULL` + `fk_comprobantes_compra_orden_compra` composite, MATCH SIMPLE + explicit
  `ix_comprobantes_compra_orden_compra` (named by hand, never EF-autogenerated). Metadata-only, no
  rewrite. *(proposal.md:652-663, gate §D)*
- [x] 1.10 Migration ordering verified in the generated file: `CREATE TYPE` → `CREATE TABLE
  ordenes_compra` (+AK/FKs/CHECKs/indexes) → `CREATE TABLE items_orden_compra` → `ALTER TABLE
  comprobantes_compra` → `HabilitarRlsDeTenant` on **both** new tables, **LAST**.
  *(proposal.md:721-724)* — DEVIATION registered: `dotnet ef migrations add` emitted the ALTER's
  `AddColumn` BEFORE both `CreateTable`s and the FK/index AFTER all other indexes; hand-reordered
  the `Up()` body to the gate's exact sequence (type → ordenes_compra + its indexes →
  items_orden_compra + its indexes → ALTER comprobantes_compra column+FK+index → RLS on both new
  tables last). No semantic change, only statement order — verified by a clean apply against a
  fresh Testcontainers Postgres 17 in the integration run.
- [x] 1.11 Create `src/Ways.Domain/Compras/EstadoOrdenCompra.cs` — 5 values, member order = native
  type order. *(design.md:146)*
- [x] 1.12 Create `src/Ways.Domain/Compras/ProyectorDeEstadoDeOrden.cs` — the pure five-arm rule,
  `PoliticaDeRoles` pattern, no database. *(design.md:148-156, decision 4)*
- [x] 1.13 Create `src/Ways.Domain/Compras/OrdenCompra.cs` · `ItemOrdenCompra.cs` — `EntidadTenant`
  ⇒ `EntidadBase`, standard tenant filter + `EstamparTenant()`, like `ComprobanteCompra`.
  *(design.md:158-160, gate §B/§C)*
- [x] 1.14 Create `OrdenCompraConfiguration.cs` · `ItemOrdenCompraConfiguration.cs` — shaped on
  `ComprobanteCompraConfiguration.cs:17-136` / `ItemComprobanteCompraConfiguration.cs:16-143`; all
  support indexes declared by hand with doc-10 names. *(design.md:370)*
- [x] 1.15 Modify `ComprobanteCompraConfiguration.cs` — `IdOrdenCompra` + FK 9 +
  `ix_comprobantes_compra_orden_compra` (named, never autogenerated). *(design.md:371, mutation
  target #9)*
- [x] 1.16 Modify `WaysDbContext.cs` + `IWaysDbContext.cs` — two new `DbSet`s. *(design.md:372,
  374)*
- [x] 1.17 Modify `WaysDbContextFactory.cs` **and** `DependencyInjection.cs` —
  `MapEnum<EstadoOrdenCompra>` in **both** builders, never also `HasPostgresEnum`.
  *(design.md:373, mutation target #8)* — DEVIATION registered: `WaysApiFixture.cs` (test-only,
  three `MapEnum` blocks: `ConfigurarNpgsqlDePrueba`, `CrearContextoDeAplicacion`,
  `CrearContextoDeOwner`) also needed `MapEnum<EstadoOrdenCompra>` added — not named by design.md's
  File Changes table (production-only), but the integration test host fails to resolve the enum
  without it. No production behavior change.
- [x] 1.18 Modify `docs/10-modelo-de-datos.md` — both tables (§5-adjacent), `comprobantes_compra.
  id_orden_compra`, "Estado (Etapa 16)" annotation. Landed from inside this slice (stage-12
  task-1.17 discipline). *(proposal.md:65-66, design.md:387)*
- [x] 1.19 Modify `src/Ways.Api/Seguridad/ManejadorDeErrores.cs` — 6 exact-name branches: (a)
  `ux_ordenes_compra_numero` → `numero_de_orden_duplicado`, 409, **placed ABOVE**
  `ClasificarUnicidad`'s generic `_numero` branch (`:180-182`) — third occurrence of the ordering
  trap; (b) `ux_items_orden_compra_orden` → `orden_de_item_duplicado`, 409; (c)
  `ck_ordenes_compra_envio_completo` → `orden_compra_envio_incompleto`, 409; (d)
  `ck_ordenes_compra_cierre` → `orden_compra_cierre_incoherente`, 409; (e)
  `ck_items_orden_compra_cantidad_positiva` → `cantidad_pedida_invalida`, 409/400; (f)
  `ck_items_orden_compra_costo_no_negativo` → `costo_estimado_invalido`, 409/400.
  *(design.md:65-66, decisions 10-11; proposal.md:679, 688-689, gate §E)* — status codes for (e)/(f)
  resolved to 400, mirroring `ck_items_comprobante_compra_cantidad_positiva`/`..._costo_no_negativo`
  in `ClasificarCheckDeCompras` (both client-reachable, service validates first with the same
  domain code, this branch is only the schema backstop); (a)/(b) explicit 409 per proposal §E's own
  table.
- [x] 1.20 [P] Domain unit — `ProyectorDeEstadoDeOrden` full truth table: 2 estados × cierreManual
  × completa × algoRecibido; `anulada` terminal from every input; manual close never moved;
  `completa` beats `algoRecibido`. *(design.md:412)*
- [x] 1.21 [P] Integration — RLS on `ways_app` (NOSUPERUSER NOBYPASSRLS): cross-tenant `SELECT`
  returns **0** rows on both new tables; `INSERT` with a foreign `id_tenant` refused `42501`.
  *(design.md:418, mutation targets #1-#2)*
- [x] 1.22 [P] Integration — both CHECKs by raw insert, both directions, asserting SQLSTATE
  `23514`: `ck_ordenes_compra_envio_completo` (numero without fecha_envio; fecha_envio without
  numero); `ck_ordenes_compra_cierre` (fecha_cierre with estado ≠ cerrada; closer without
  fecha_cierre). *(design.md:419, mutation targets #3-#4)*
- [x] 1.23 [P] Integration — both item CHECKs by raw insert: `cantidad_pedida <= 0`,
  `costo_unitario_estimado < 0` — SQLSTATE `23514`. *(design.md:419, mutation target #5)*
- [x] 1.24 [P] Integration — `ux_items_orden_compra_orden` by raw insert (server-assigned `orden`,
  race-test exemption documented, same family as `ux_items_comprobante_compra_orden`).
  *(proposal.md:680)*
- [x] 1.25 [P] Integration — **the ordering trap, raw out-of-band insert**: a duplicate `numero`
  at one punto de venta resolves to SQLSTATE `23505` **translated to `numero_de_orden_duplicado`**,
  never the `ux_clientes_numero`/generic `_numero` family. *(design.md:65, decision 11; binding
  gate test (c))* — implemented as TWO complementary tests: a raw Postgres proof (SQLSTATE +
  exact `ConstraintName`, `OrdenesCompraSchemaTests.cs`) and a unit-style `ManejadorDeErrores`
  proof asserting the actual translated domain code through both the EF (`DbUpdateException`) and
  raw-ADO (bare `PostgresException`) paths (`ManejadorDeErroresOrdenesDeCompraTests.cs`, mirroring
  the `ManejadorDeErroresComprasTests` precedent) — the SQL-level test alone cannot see the
  ordering trap (the trap lives in C# switch-arm order, not in Postgres), so the second file is
  the one that actually proves decision 11 end-to-end without needing slice 2's HTTP endpoint.
- [x] 1.26 [P] Integration — `pg_indexes` shows **exactly 12** new indexes and no unnamed
  EF-generated FK support index; `has-pending-model-changes` clean. *(design.md:419, mutation
  target #9)*
- [x] 1.27 [P] `db-error-backstops` exemption tests — FK 1/FK 6 (`_tenant`), FK 4/FK 5
  (`_empleado*`), FK 7 (items→OC): generic `23503` mapping, one SQLSTATE test per exempt FK.
  *(proposal.md:685-687)*
- [x] 1.28 [P] **Mutation target #1** — `HabilitarRlsDeTenant("ordenes_compra")` → delete → cross-
  tenant count + `42501` test (1.21) must fail. **Evidence**: commit `1088a37` → deleted the line
  in the migration → `dotnet build --no-incremental` (clean) → both RLS tests FAILED
  (`UnaSesionDeOtroTenantNoVeLasOrdenesDeCompraPorSelect`: expected 0 rows, actual 1;
  `UnInsertConIdTenantAjenoEnOrdenesCompraSeRechaza`: expected `42501`, actual `23503` — an
  unrelated FK, not RLS) → `git checkout -- src/` → `git status` clean (verified with `git`
  directo) → rebuild → both tests green.
- [x] 1.29 [P] **Mutation target #2** — `HabilitarRlsDeTenant("items_orden_compra")` → delete →
  same, child table (1.21) must fail. **Evidence**: same cycle — mutated →
  `UnaSesionDeOtroTenantNoVeLosItemsDeOrdenDeCompraPorSelect` FAILED (expected 0, actual 1) →
  reverted, `git status` clean → rebuilt → green.
- [x] 1.30 [P] **Mutation target #3** — `ck_ordenes_compra_envio_completo` → delete → raw-insert
  `23514` test (1.22) must fail, both directions. **Evidence**: deleted the
  `table.CheckConstraint` call in the migration's `CreateTable` → both
  `UnNumeroSinFechaDeEnvioViolaLaCheckDeEnvioCompleto`/`UnaFechaDeEnvioSinNumeroViolaLaCheckDeEnvioCompleto`
  FAILED ("No exception was thrown") → reverted, clean → green.
- [x] 1.31 [P] **Mutation target #4** — `ck_ordenes_compra_cierre` → delete → raw-insert `23514`
  test (1.22) must fail, both directions. **Evidence**: same cycle — both
  `UnaFechaDeCierreConEstadoNoCerradaViolaLaCheckDeCierre`/`UnCierreManualSinFechaDeCierreViolaLaCheckDeCierre`
  FAILED → reverted, clean → green.
- [x] 1.32 [P] **Mutation target #5** — either item CHECK → delete → its raw-insert `23514` test
  (1.23) must fail. **Evidence**: deleted `ck_items_orden_compra_cantidad_positiva` →
  `UnaCantidadPedidaNoPositivaViolaLaCheck` FAILED → reverted, clean → green.
- [x] 1.33 [P] **Mutation target #6** — `HasFilter("numero IS NOT NULL")` on `ux_ordenes_compra_
  numero` → delete → two drafts (numero NULL) in one PV ⇒ spurious `23505` test must fail.
  **FINDING, not a pass** (mutation-proof-tests rule 3, "kill the confound, don't accept a false
  pass"): mutated (dropped `filter:` from the migration's `CreateIndex`, making the unique index
  full instead of partial) → ran `DosBorradoresSinNumeroEnElMismoPuntoDeVentaConvivenSinConflicto`
  → **test still PASSED under the mutation** (no exception on the second insert). Root cause,
  verified empirically against real Postgres: standard SQL/Postgres unique-index semantics never
  compare `NULL = NULL` as equal, so a FULL unique index over `(id_tenant, id_punto_venta,
  numero)` already tolerates unlimited `numero IS NULL` rows — identical to the partial index's
  behavior for this exact fixture. The design.md wording for this target ("spurious `23505`") does
  not hold; reverted immediately (`git checkout -- src/`, clean confirmed), no synthetic assertion
  added to force a false positive. The partial filter stays implemented (matches
  `ux_comprobantes_compra_numero_externo`'s precedent and is the correct, storage-efficient shape:
  a filtered index excludes NULL rows from the index structure entirely), but its removal is not
  independently provable via a NULL-numero fixture — registered here as an investigated,
  non-actionable finding rather than a fabricated pass.
- [x] 1.34 [P] **Mutation target #7** — the exact-name `ux_ordenes_compra_numero` branch **above**
  `ClasificarUnicidad` → move it below → the ordering-trap test (1.25) must fail (translated code
  becomes `numero_duplicado`). **Evidence**: moved the switch arm below the
  `ClasificarUnicidad(ux)` generic arm in `ManejadorDeErrores.cs` →
  `ManejadorDeErroresOrdenesDeCompraTests.UxOrdenesCompraNumeroGanaLaRamaNuevaAntesQueLaFamiliaGenericaDeNumero`
  FAILED on both theory cases (EF `DbUpdateException` path and raw-ADO path): expected
  `numero_de_orden_duplicado`, actual `numero_duplicado` → reverted, clean → green (16/16 in the
  file).
- [x] 1.35 [P] **Mutation target #8** — `MapEnum<EstadoOrdenCompra>` in either builder → delete →
  that builder's path fails / `has-pending-model-changes` dirty (1.26). **Evidence**: baseline
  `dotnet ef migrations has-pending-model-changes` clean → deleted the `MapEnum<EstadoOrdenCompra>`
  line in `WaysDbContextFactory.cs` → rebuilt (compiles fine, it's a runtime call) → same CLI
  command now reports "Changes have been made to the model since the last migration" → reverted,
  clean → CLI clean again.
- [x] 1.36 [P] **Mutation target #9** — explicit `ix_comprobantes_compra_orden_compra` name → drop
  `HasDatabaseName` → `pg_indexes` audit (1.26) must fail (an EF `IX_…` appears). **Evidence**:
  renamed the migration's `CreateIndex` for that index to the EF default-convention name
  (`IX_comprobantes_compra_id_orden_compra_id_tenant`, simulating `HasDatabaseName` dropped) →
  `ElConteoTotalDeIndicesNuevosEsExactamenteDoce` FAILED (`Assert.NotNull` on the hand-named index
  lookup returned null) → reverted, clean → green.
- [x] 1.37 Gate guard (**VINCULANTE**, `state.yaml` db_gate_approval): `git diff --stat main --
  src/Ways.Infrastructure/Persistencia/Migraciones/` shows **exactly one** new migration
  (`20260819042145_OrdenesDeCompraEtapa16.cs`/`.Designer.cs`, `WaysDbContextModelSnapshot.cs`
  updated as expected — no other migration file); `dotnet ef migrations has-pending-model-changes`
  clean (verified); final new index count = **12** (verified empirically against `pg_indexes` by
  `ElConteoTotalDeIndicesNuevosEsExactamenteDoce`); **zero** data statements anywhere in the
  migration (`grep -c "migrationBuilder.Sql(" ` on the file = 0). Gate holds, no deviation.
- [ ] 1.38 Run `judgment-day` on the slice diff; fix confirmed issues; re-judge until clean. **NOT
  RUN by `sdd-apply`** — `judgment-day` is an orchestrator-level dual-review protocol this executor
  cannot invoke (the executor contract forbids launching sub-agents/reviewers). Left for the
  orchestrator to run before merge.
- [ ] 1.39 Branch `feat/stage16-slice1-schema` off `main`; PR; merge stacked-to-main. **PARTIAL**:
  the worktree was already provisioned on `feat/stage16-slice1-schema` off `main` (`eecd5cf`)
  before this phase started — branching is done. PR creation/merge is explicitly out of scope
  (`NO pushees` instruction) — left for the orchestrator.
- [ ] **Process note (decision 15 discipline)**: the apply-phase host process died mid mutation-
  evidence-cycle for target #1 (first attempt); the orchestrator verified the worktree, confirmed
  the in-flight mutation had already been reverted via `git checkout -- src/` back to commit
  `1088a37`, and directed a clean restart of the cycle — done, all 9 targets re-run end-to-end from
  a verified-clean tree. Separately, Docker Desktop was found down (daemon unreachable) at the
  start of the mutation-evidence phase; restarted (`Start-Process 'Docker Desktop.exe'`), waited
  for health, then proceeded — the one test run that hit this (target #1's first attempt) reported
  `[FAIL]` for an infrastructure reason (`Docker is either not running or misconfigured`), not
  genuine mutation evidence, and was discarded/re-run once Docker was confirmed healthy.

**Test plan**: Domain truth table (1.20); RLS (1.21); both CHECKs both directions (1.22-1.23);
both `23505` families incl. race exemption (1.24); the ordering trap (1.25); index-count audit
(1.26); FK exemptions (1.27); 9 mutation targets (1.28-1.36).

**Verify**: `dotnet test --filter FullyQualifiedName~OrdenesCompraSchema|FullyQualifiedName~ProyectorDeEstadoDeOrden`

---

## Slice 2: Borrador + Envío (PR 2)

**Start**: slice 1 merged. **Finish**: `ServicioDeOrdenesDeCompra` draft CRUD (replace-set) +
`enviar` exist; `numero` consumed only at `enviar`, per PV; two concurrent `enviar` proven both
ways (distinct OCs / same OC). **Rollback**: revert branch — the service and its two endpoints
disappear, schema unused for these paths, nothing to repair. **Done** = tests green +
`judgment-day` clean round + PR merged.

**Budget note**: no pre-authorized split named for this slice; if it overflows, split at the
draft-CRUD / `enviar` boundary and register the new cut here (decision 15).

- [x] 2.1 Create `src/Ways.Application/Compras/ContratosDeOrdenDeCompra.cs` — the write records
  (`SolicitudDeOrdenDeCompra`, `LineaDeOrdenSolicitada`, `ItemDeOrden`); `orden` is server-assigned
  1..N, never accepted from the request (no `orden` field exists on the request records at all —
  stronger than "accepted but overwritten"). *(design.md:166-174, mutation target #14)* —
  **DEVIATION registered (decision 17 below)**: this file also defines `OrdenDeCompraBorrador`, a
  slice-2-scoped response record (header + items only) instead of design's single
  `OrdenDeCompraDetalle` (which needs `Cobertura`/`TotalEstimado`/`TotalReal`/`DesvioTotal`/
  `ComprobantesLigados` — all derived from the reception book that doesn't exist until slice 3/5).
  `OrdenDeCompraDetalle` is created in slice 5 (task 5.1) exactly as originally planned.
- [x] 2.2 Create `src/Ways.Application/Compras/ServicioDeOrdenesDeCompra.cs` — `CrearBorradorAsync`
  (`INSERT`, `estado='borrador'`, `numero NULL`). *(proposal.md:266, design.md:376)*
- [x] 2.3 Same file: `ActualizarBorradorAsync` — full replace-set under `SELECT … FOR UPDATE …
  WHERE estado = 'borrador'`, `RemoveRange`/`AddRange` items, the `BloquearBorradorAsync` pattern.
  *(proposal.md:267, mutation targets #10, #15)*
- [x] 2.4 Same file: `EnviarAsync` — outside the caller's transaction, wrapped in
  `db.Database.CreateExecutionStrategy()`, call `AsignadorDeNumeroComprobante.
  AsignarComprometidoAsync(db, idTenant, idPuntoVenta, "OC")`; then `EstrategiaSinReintento ⇒
  UPDATE ordenes_compra SET numero, fecha_envio, estado='enviada' WHERE id AND tenant AND
  estado='borrador' AND id_punto_venta = $pv RETURNING numero`; 0 rows ⇒ reclassify under read
  (409, `orden_compra_no_enviable`), number stays burnt. *(design.md:36-40, 244-250, mutation
  targets #11-#13, #16)*
- [x] 2.5 Guard: refuse `enviar` on an OC with zero items — `orden_compra_sin_items`, 400 —
  mirroring `compra_sin_items`. *(design.md:62, decision 7; mutation target #17; conflict #3
  above)*
- [x] 2.6 Every raw-ADO parameter through `ParametrosDeComando.Agregar`/`AgregarNulo` — no hand-
  built parameter without `ToUniversalTime()`. *(design.md, mutation target #16)*
- [x] 2.7 Create `src/Ways.Api/Endpoints/OrdenesDeCompraEndpoints.cs` — `POST /`, `PUT /{id}`,
  `POST /{id}/enviar`, grouped under `OperacionDePos`, stacking `GestionDeCatalogo` on writes —
  gate copied verbatim from `ComprasEndpoints.cs:20-22, 76-109`. Registered in `Program.cs`
  (`app.MapearOrdenesDeCompra()`, after `MapearCompras()`) and `Ways.Application/
  DependencyInjection.cs` (`AddScoped<ServicioDeOrdenesDeCompra>()`). *(design.md:301-307,
  decision 16)* — **DEVIATION registered (decision 17 below)**: no `DELETE /api/ordenes-compra`
  endpoint/`EliminarAsync` was implemented — not named in `tasks.md`/`design.md`'s API Surface (7
  routes total, no DELETE) nor in the slice-4 authorization matrix (task 4.11 pins exactly 5 write
  routes). Adding one would silently break both binding counts.
- [x] 2.8 [P] Integration — `PUT` on a `borrador` OC replaces items exactly (add + remove in one
  request, no stale row). `ServicioDeOrdenesDeCompraTests.
  ItemsSeReemplazanCompletosEnUnRequestAgregandoYQuitando`. *(ordenes-de-compra/spec.md:52-55)*
- [x] 2.9 [P] Integration — `PUT` on a non-`borrador` OC is rejected `409`.
  `ServicioDeOrdenesDeCompraTests.EditarUnaOrdenNoBorradorEsRechazada409`.
  *(ordenes-de-compra/spec.md:57-60, mutation target #10)*
- [x] 2.10 [P] Integration — `enviar` on a fresh PV assigns `numero = 1`, sets `fecha_envio`,
  `estado = enviada`. `ServicioDeOrdenesDeCompraTests.
  EnviarAsignaElPrimerNumeroParaUnPuntoDeVentaFresco`. *(ordenes-de-compra/spec.md:73-76)*
- [x] 2.11 [P] Integration — re-sending an already-`enviada` OC is rejected `409`, `numero` not
  reassigned. `ServicioDeOrdenesDeCompraTests.
  ReenviarUnaOrdenYaEnviadaEsRechazada409SinReasignarNumero`. *(ordenes-de-compra/spec.md:83-86)*
- [x] 2.12 [P] Integration — `enviar` on an OC with no items is rejected `orden_compra_sin_items`,
  400. `ServicioDeOrdenesDeCompraTests.EnviarUnaOrdenSinItemsEsRechazadaConOrdenCompraSinItems400`.
  *(conflict #3 above, mutation target #17)*
- [x] 2.13 [P] Integration — the `-03:00` offset test on `fecha_envio`: `RelojFijo` returning the
  same instant as `2026-08-19T12:00:00Z` but expressed with real offset `-03:00` persists the
  exact fixed instant (a hand-built, non-normalized parameter would 500 on the offset≠0-vs-
  timestamptz Npgsql rejection — see the `datetimeoffset-utc-npgsql` memory note). `ServicioDeOrdenesDeCompraTests.
  EnviarConOffsetMenosTresPersisteElInstanteFijoExacto`. *(decision 13 above, mutation target #16)*
- [x] 2.14 [P] **Binding gate test (b), part 1 — VINCULANTE** — two concurrent `enviar` on **two
  distinct** OCs at one punto de venta ⇒ two distinct `numero` values, **neither** response `409`.
  `ServicioDeOrdenesDeCompraTests.
  DosEnviarConcurrentesDeOrdenesDistintasEnElMismoPuntoDeVentaDanNumerosDistintosSin409`.
  *(ordenes-de-compra/spec.md:78-81; design.md decision T1; conflict #1 above)*
- [x] 2.15 [P] **Binding gate test (b), part 2 — VINCULANTE** — two concurrent `enviar` on the
  **same** OC ⇒ one `200` + one `409`, the loser's number burnt (never reassigned) — verified by a
  follow-up `enviar` on a fresh OC of the same PV jumping straight to `numero = 3` (1 = winner, 2 =
  burnt by the loser). `ServicioDeOrdenesDeCompraTests.
  DosEnviarConcurrentesDeLaMismaOrdenDanUn200YUn409ConNumeroQuemado`. *(design.md T1, conflict #1
  above)*
- [x] 2.16 [P] Integration — concurrent `PUT`-moves-the-PV race: a `PUT` relinking the OC's punto
  de venta between the pre-read and the `enviar` lock leaves the number correctly scoped to the
  **old** series (0 rows under the mismatched `WHERE`, reclassified `409 orden_compra_no_enviable`)
  — forced deterministically with a `DbTransactionInterceptor` pausing right after
  `EjecutarEnvioAsync`'s `BeginTransactionAsync` (same pattern as `ComprasAnulacionYConcurrenciaTests.
  InterceptorDePausaTrasIniciarLaTransaccion`), never left to real timing. `ServicioDeOrdenesDeCompraTests.
  UnPutQueMuevePuntoDeVentaConcurrenteConEnviarReclasificaA409YElNumeroQuedaEnLaSerieVieja`.
  *(design.md:61, mutation target #11)*
- [x] 2.17 [P] **Mutation target #10** — `WHERE estado = 'borrador'` in the draft lock → delete →
  `PUT` on an `enviada` OC ⇒ expected 409 (2.9) must fail. **Evidence**: deleted `AND estado =
  'borrador'::estado_orden_compra` from `BloquearBorradorAsync`'s SQL → `dotnet build
  --no-incremental` (clean) → `EditarUnaOrdenNoBorradorEsRechazada409` FAILED (expected `Conflict`,
  actual `OK`) → `git checkout -- src/` → `git status` clean → rebuilt → green (1/1).
- [x] 2.18 [P] **Mutation target #11** — `AND id_punto_venta = $pv` in the `enviar` UPDATE →
  delete → the concurrent-`PUT`-moves-the-PV test (2.16) must fail. **Evidence**: deleted `AND
  id_punto_venta = $5` from `EnviarHeaderAsync`'s SQL → build clean →
  `UnPutQueMuevePuntoDeVentaConcurrenteConEnviarReclasificaA409YElNumeroQuedaEnLaSerieVieja` FAILED
  (the `enviar` succeeded — `200`, `estado: Enviada`, `idPuntoVenta: 4` — instead of the expected
  `409`, proving the number landed in the relinked PV's series) → reverted, clean → green (1/1).
- [x] 2.19 [P] **Mutation target #12** — `AsignarComprometidoAsync` → replace with `MAX(numero) +
  1` → two concurrent `enviar` on one PV (2.14) ⇒ same number / `23505` must surface. **Evidence**:
  replaced the call with a non-atomic `db.OrdenesCompra.Where(...).MaxAsync()+1` LINQ read → build
  clean → `DosEnviarConcurrentesDeOrdenesDistintasEnElMismoPuntoDeVentaDanNumerosDistintosSin409`
  FAILED (`Assert.NotEqual` — one response was `Conflict`, proving the racy MAX+1 let both draw the
  same number and collide on `ux_ordenes_compra_numero`) → reverted, clean → green (1/1).
- [x] 2.20 [P] **Mutation target #13** — the assigner call moved **inside** the `enviar`
  transaction → nested-transaction failure / the burnt-number semantics test (2.15) must fail.
  **Evidence**: moved `AsignarComprometidoAsync` (which itself opens `BeginTransactionAsync`) to
  right after `EjecutarEnvioAsync`'s own `BeginTransactionAsync`, on the same `DbContext` → build
  clean → `DosEnviarConcurrentesDeLaMismaOrdenDanUn200YUn409ConNumeroQuemado` FAILED (`Assert.Equal`
  expected 1 winner, actual 0 — the nested transaction attempt broke BOTH concurrent requests, no
  200 survived) → reverted, clean → green (1/1).
- [x] 2.21 [P] **Mutation target #14** — server-assigned `orden` 1..N replaced with a non-
  incrementing value (the request DTO carries no `orden` field to "take" — the closest faithful
  mutation is defeating the server-side sequential assignment itself) → `ux_items_orden_compra_orden`
  ⇒ `orden_de_item_duplicado` must surface on a multi-item replace-set. **Evidence**: changed
  `MaterializarItems`'s `Orden = orden++` to a constant `Orden = 1` → build clean →
  `ItemsSeReemplazanCompletosEnUnRequestAgregandoYQuitando` FAILED with `409
  orden_de_item_duplicado` (the exact translated code from `ManejadorDeErrores`'s slice-1 backstop,
  confirming both the service-level defense and the schema backstop end-to-end) → reverted, clean →
  green (1/1).
- [x] 2.22 [P] **Mutation target #15** — `RemoveRange(itemsExistentes)` in the replace-set →
  delete → the per-line count/identity assertion (2.8) must fail. **Evidence**: deleted the
  `db.ItemsOrdenCompra.RemoveRange(itemsExistentes)` line (and its now-unused read) in
  `EjecutarActualizacionAsync` → build clean →
  `ItemsSeReemplazanCompletosEnUnRequestAgregandoYQuitando` FAILED with `409
  orden_de_item_duplicado` (the stale row from the first PUT collided with the new set's `orden=1`
  under `ux_items_orden_compra_orden`) → reverted, clean → green (1/1).
- [x] 2.23 [P] **Mutation target #16** — `ParametrosDeComando.Agregar` on `fecha_envio` → hand-
  built parameter without `ToUniversalTime()` → the `-03:00` offset test (2.13) must fail.
  **Evidence**: replaced the `ParametrosDeComando.Agregar(comando, momento)` call in
  `EnviarHeaderAsync` with a hand-built `DbParameter` (`Value = momento` directly, no
  normalization) → build clean → `EnviarConOffsetMenosTresPersisteElInstanteFijoExacto` FAILED with
  a `500 error_interno` (Npgsql's offset≠0-vs-`timestamptz` rejection surfacing as an unhandled
  exception, not a domain 409/400 — exactly the documented failure mode) → reverted, clean → green
  (1/1).
- [x] 2.24 [P] **Mutation target #17** — the `orden_compra_sin_items` guard → delete → an empty OC
  projects straight to `cerrada` (2.12 regresses). **Evidence**: deleted the `tieneItems` check and
  its `throw` in `EnviarAsync` → build clean →
  `EnviarUnaOrdenSinItemsEsRechazadaConOrdenCompraSinItems400` FAILED (expected `BadRequest`,
  actual `OK` — an empty OC now sends successfully) → reverted, clean → green (1/1).
- [x] 2.25 Gate guard: `dotnet ef migrations has-pending-model-changes` clean; zero new files under
  `Migraciones/`. Verified two ways: (a) `dotnet ef migrations has-pending-model-changes --project
  src/Ways.Infrastructure/Ways.Infrastructure.csproj --startup-project
  src/Ways.Infrastructure/Ways.Infrastructure.csproj` → *"No changes have been made to the model
  since the last migration."* (running it with `Ways.Api` as the startup project errors — that
  project doesn't reference `Microsoft.EntityFrameworkCore.Design`, a pre-existing environment
  quirk unrelated to this slice); (b) the in-process
  `ServicioDeOrdenesDeCompraTests.NoHayCambiosPendientesDeModeloRespectoDeLaMigracionDeLaSlice1`
  asserting `db.Database.HasPendingModelChanges() == false` — green. `git diff --stat main --
  src/Ways.Infrastructure/Persistencia/Migraciones/` shows no new file.
- [ ] 2.26 Run `judgment-day`; fix confirmed issues; re-judge until clean. **NOT RUN by
  `sdd-apply`** — same executor-contract carve-out as slice 1 task 1.38: this executor cannot
  launch sub-agents/reviewers. Left for the orchestrator to run before merge.
- [ ] 2.27 Branch `feat/stage16-slice2-borrador-y-envio` off `main` (parent: slice 1); PR; merge
  stacked-to-main. **PARTIAL**: the worktree was already provisioned on
  `feat/stage16-slice2-borrador-envio` off `main` (`dcb517f`, slice 1 already merged) before this
  phase started — branching is done, naming differs by a hyphen from this task's literal
  `-borrador-y-envio` (cosmetic, not re-branched to avoid losing the provisioned worktree — flagged
  for the orchestrator). PR creation/merge is explicitly out of scope (`NO pushees` instruction) —
  left for the orchestrator.

**Test plan**: replace-set (2.8-2.9); enviar happy/blocked paths (2.10-2.12); the `-03:00` offset
(2.13); the two binding concurrency tests (2.14-2.15) — **VINCULANTES**; the PV-relink race (2.16);
8 mutation targets (2.17-2.24); the gate guard regression check (2.25 evidence).

**Verify**: `dotnet test --filter FullyQualifiedName~ServicioDeOrdenesDeCompra` — 11/11 green
(`OrdenesCompraSchemaTests`/`ManejadorDeErroresOrdenesDeCompraTests` from slice 1 re-run clean,
34/34; the full `Compras` regression suite, 137/137).

---

## Slice 3: Ligadura + Proyección (PR 3)

**Start**: slice 2 merged. **Finish**: a compra draft may link to a matching OC; confirming/
annulling a linked comprobante projects the OC's `estado` inside the same transaction, at lock
position 2; an unlinked confirm/anular emits **zero** extra statements; the pinned lock order and
both races are proven. **Rollback**: revert branch — the guarded calls and `ExigirOrdenLigableAsync`
disappear, `ServicioDeCompras`'s engine returns to its pre-stage shape exactly. **Done** = tests
green + `judgment-day` clean round + PR merged.

**Budget note**: pre-authorized split `3a` (link + `FOR SHARE` guard + projection class + confirm
call + confirm×confirm race) / `3b` (anulación call + estado regression + anular×confirmar race)
if this slice overflows — decision 3 above.

- [x] 3.1 Create `src/Ways.Application/Compras/EscriturasDeOrdenDeCompra.cs` — static class,
  `ProyectarEstadoAsync` (lock → short-circuit → derive → conditional `UPDATE … RETURNING`, 3
  statements) and `BloquearYExigirNoAnuladaAsync` (defense-in-depth guard). *(design.md:78-99,
  decisions 1-2)*
- [x] 3.2 Same file, statement 1: `SELECT estado::text, (id_empleado_cierre IS NOT NULL) FOR
  UPDATE`; `anulada` OR manual close ⇒ return **without** statements 2/3. *(design.md:102-108,
  mutation targets #18, #26, #27)*
- [x] 3.3 Same file, statement 2: the derivation CTE — `pedido`/`recibido` grouped by
  `id_articulo` on **both** sides, `c.estado = 'confirmada'`, `deleted_at IS NULL` on both joined
  tables, `algoRecibido` sourced from the **reception** side. *(design.md:110-129, mutation targets
  #22-#25)*
- [x] 3.4 Same file, statement 3: `UPDATE ordenes_compra SET estado, fecha_cierre (CASE, regresión
  limpia NULL), updated_at WHERE … AND estado = $anterior RETURNING`, **skipped** when projected ==
  current. *(design.md:131-139, mutation targets #19, #28)*
- [x] 3.5 Modify `ServicioDeCompras.cs`'s `ConfirmarHeaderAsync` — widen `RETURNING` to add
  `id_orden_compra`. *(design.md:44-46, mutation target #20)*
- [x] 3.6 Modify `ServicioDeCompras.cs`'s `MarcarAnuladaAsync` — same widening.
  *(design.md:44-46)*
- [x] 3.7 Modify `ServicioDeCompras.cs`'s `EjecutarConfirmarAsync` — after step 1 (header lock),
  before lotes: `if (encabezado.IdOrdenCompra is { } idOc) { BloquearYExigirNoAnuladaAsync; }` at
  lock position 2, before `proveedores`. *(design.md:214-221, mutation targets #21, #29)* —
  **DEVIATION registered (decision 20 below)**: the literal pinned signatures (design's Interfaces/
  Contracts — `BloquearYExigirNoAnuladaAsync` has NO `momento` parameter) mean this call site issues
  **both** `BloquearYExigirNoAnuladaAsync` (its own `SELECT … FOR UPDATE`) **and then**
  `ProyectarEstadoAsync` (which takes the **same** row lock again as its own statement 1) — two
  redundant-but-safe re-locks of the same row within the transaction, not one shared lock. Postgres
  re-acquiring `FOR UPDATE` on a row already locked by the same transaction is a documented no-op
  (re-verifies visibility, does not block, cannot deadlock against itself); this reading matches the
  two separate public method signatures design pins verbatim and their doc-comment framing ("ANTES
  DE QUE LA PROYECCIÓN ESCRIBA").
- [x] 3.8 Modify `ServicioDeCompras.cs`'s `EjecutarAnulacionAsync` — same guarded call at position
  2, after the (unmoved) audit step. *(design.md:229-236)*
- [x] 3.9 Modify `ServicioDeCompras.cs`'s draft path (both `CrearBorradorAsync` and
  `ActualizarBorradorAsync`) — accept `idOrdenCompra`, call `ExigirOrdenLigableAsync` (`SELECT …
  FOR SHARE`) validating tenant + proveedor + punto de venta + linkable estado
  (`enviada`/`recibida_parcial`/`cerrada`; refuse `borrador` → `orden_compra_no_enviada` 409,
  `anulada` → `orden_compra_anulada` 409). *(design.md:63, conflict #2 above, mutation target #30)*
- [x] 3.10 Modify `src/Ways.Application/Compras/Contratos.cs` — `SolicitudDeCompra` gains `int?
  IdOrdenCompra`; **no route/policy change**. *(design.md:206-208)* — **DEVIATION (cosmetic)**: the
  field lives in `Contratos.cs` (the actual repo filename for these DTOs), not
  `ComprasEndpoints.cs` as the task literally names — `ComprasEndpoints.cs` itself needed zero
  changes since the endpoint already threads the whole `SolicitudDeCompra`/`CompraDetalle` record
  through unmodified.
- [x] 3.11 Modify `Contratos.cs`'s response contract — `CompraDetalle` gains `int?
  IdOrdenCompra`. *(design.md:206-208, conflict #4 above)*
- [x] 3.12 [P] Integration — **binding gate test (a): zero-extra-statements**.
  `UnConfirmSinOrdenLigadaNoTocaNingunaOrdenDeCompraExistente` (behavioral: a sibling OC's `estado`/
  `updated_at` stay byte-identical) + `EscriturasDeOrdenDeCompraLockOrderTests.
  LasLlamadasAEscriturasDeOrdenDeCompraEnConfirmarNuncaOcurrenFueraDelGuardNulo` (structural: the
  calls never appear outside the null-check guard). `ComprasLifecycleTests`/
  `ComprasAnulacionYConcurrenciaTests` green and **unedited** in the diff (verified: 87/87, `git
  status` shows neither file touched). *(comprobantes-compra/spec.md:50-54, 75-79; design.md,
  Testing Strategy)* — **DEVIATION registered (decision 20 below)**: a literal EF
  `DbCommandInterceptor`/`ContadorDeComandos` "command count" is structurally blind to this slice's
  raw-ADO statements (same empirically-proven limitation `ServicioDeComprasLockOrderTests`'s own
  doc-comment already recorded for the lock-order proof) — `mutation-proof-tests` rule 3 escape
  hatch applied, same as the one task 3.24 pre-authorized. **Corrected (decision 21 below,
  judgment-day round 2, WARNING closed)**: the two nets are NOT redundant covers of the same
  mutant — each catches a DIFFERENT one, and neither alone is complete. The structural lock-order
  test catches the LITERAL unconditional-call mutant (`?? 0`), but that mutant never actually
  reaches the byte-identical asserts of the behavioral test — it dies earlier with a `500`
  (`BloquearYLeerAsync`'s FK-invariant throw on the sentinel id `0`), so those two specific asserts
  are dead code for that mutant. The byte-identical asserts' real, independently-provable target is
  a REALISTIC soft mutant — one that resolves the OC "by coincidence" (proveedor + PV of the
  header) instead of the real FK and finds a legitimately existing row — which the structural test,
  by construction, cannot see (the call site still has a syntactically valid guard). The behavioral
  test was strengthened with a same-proveedor/same-PV sibling plus an EF-seeded "landmine" receipt
  (a confirmed, FK-linked reception the production code never re-derives against because the
  unlinked confirm's `id_orden_compra` is `NULL`) so that soft mutant becomes observable instead of
  a silent idempotent no-op — verified by mutation (see decision 21).
- [x] 3.13 [P] Integration — a borrador draft links to a matching (`enviada`) OC; persisted.
  *(comprobantes-compra/spec.md:14-17)*
- [x] 3.14 [P] Integration — a mismatched proveedor/PV/tenant cannot link, refused before any
  write. *(ordenes-de-compra/spec.md:196-204, comprobantes-compra/spec.md:19-22)*
- [x] 3.15 [P] Integration — linking to a `borrador` OC ⇒ `409 orden_compra_no_enviada`; linking to
  an `anulada` OC ⇒ `409 orden_compra_anulada`; linking to a `cerrada` OC succeeds. *(conflict #2
  above)*
- [x] 3.16 [P] Integration — the link is frozen once confirmed; `CompraDetalle.IdOrdenCompra`
  round-trips exactly what was set at draft time. *(comprobantes-compra/spec.md:24-27, conflict #4
  above)*
- [x] 3.17 [P] Integration — confirming a linked reception moves the OC to `recibida_parcial` in
  the same transaction. *(ordenes-de-compra/spec.md:109-112, comprobantes-compra/spec.md:39-43)*
- [x] 3.18 [P] Integration — confirming the remainder closes the OC automatically,
  `id_empleado_cierre IS NULL`. *(ordenes-de-compra/spec.md:114-117)*
- [x] 3.19 [P] Integration — confirming against an `anulada` OC is refused `409
  orden_compra_anulada`, no write. *(ordenes-de-compra/spec.md:185-188, comprobantes-
  compra/spec.md:45-48)*
- [x] 3.20 [P] Integration — annulling the only reception of an automatically-closed OC returns it
  to `enviada`. *(ordenes-de-compra/spec.md:119-122, comprobantes-compra/spec.md:65-68)*
- [x] 3.21 [P] Integration — **derivation fidelity** (rule 11): two OC lines of one artículo (3+4
  ⇒ 7 pedidas, plus a DISCRIMINANT 5-of-7 partial receive that over-delivery alone could not have
  proven), an artículo received but never ordered, a soft-deleted reception, a linked `borrador`
  reception, a reception of another OC of the same proveedor — every fixture asserted via the
  resulting projected `estado` (the only slice-3-observable artifact; per-artículo `Recibida`/
  `Pendiente` numbers are a slice-5 read-model concern, not yet built). *(design.md, Testing
  Strategy; ordenes-de-compra/spec.md:124-129; decision 13 above — desynchronized ids)*
- [x] 3.22 [P] Integration — **the two races**: confirm × confirm of two receptions of one OC (both
  commit, no deadlock, resulting estado = the sum of both, never only one) — **DONE**,
  `DosConfirmacionesConcurrentesDeDosRecepcionesDeUnaOrdenNuncaSeSobreescriben`. **DEVIATION
  registered (decision 20 below)**: "anular OC × confirmar reception in both orders" is **NOT
  implementable in this slice** — `POST /{id}/anular` for an OC and
  `ServicioDeOrdenesDeCompra.AnularAsync` are slice-4 tasks (4.2/4.3); no OC-anulación write path
  exists anywhere in this slice's scope to race against. Deferred to slice 4, where the endpoint
  will exist. *(ordenes-de-compra/spec.md:131-135, design.md Concurrency guarantees)*
- [x] 3.23 [P] Integration — a fault injected after the projection leaves the OC untouched
  (fault-point test) — **confirm path DONE** (`UnaFallaDespuesDeLaProyeccionEnConfirmarDejaLaOrdenSinCambios`,
  a zero-item borrador linked to an OC trips `compra_sin_items` in step 2, AFTER step 1.b's
  projection already ran inside the aborted transaction). **Anular path NOT implemented** — every
  guard in `EjecutarAnulacionAsync` after its own OC-projection call (1.6) is pre-existing,
  unmodified stage-8/12 stock-negative logic; constructing a fault there specific to THIS slice's
  call-ordering claim would duplicate `ComprasAnulacionYConcurrenciaTests`' own stock-negative
  coverage without adding a new signal, so it was not duplicated here — registered as a gap, not a
  fabricated pass. *(design.md, Testing Strategy)*
- [x] 3.24 [P] Integration — the pinned lock order holds — **DONE via the `mutation-proof-tests`
  rule 3 escape hatch, invoked**: `EscriturasDeOrdenDeCompraLockOrderTests` asserts the lock-order
  claim by SOURCE TEXT (same reasoning as `ServicioDeComprasLockOrderTests`'s own precedent — a
  `DbCommandInterceptor` cannot see this slice's raw-ADO statements). The behavioral rendezvous test
  (3.22) does **not** independently discriminate a same-path reorder (see decision 20 below,
  mutation target #21 finding) — the source-text test is the actual functioning detector.
  *(design.md:268-282)*
- [x] 3.25 [P] **Mutation target #18** — `SELECT … FOR UPDATE` (statement 1) → delete, keep
  derive+update → confirm×confirm rendezvous (3.22) ⇒ stale estado. **Evidence**: deleted `FOR
  UPDATE` from `BloquearYLeerAsync`'s SQL → `dotnet build --no-incremental` clean →
  `DosConfirmacionesConcurrentesDeDosRecepcionesDeUnaOrdenNuncaSeSobreescriben` FAILED (one
  concurrent confirm surfaced `500 error_interno` — the loser's conditional `UPDATE … WHERE estado =
  $5` no longer matched under the lockless race) → `git checkout -- src/` → rebuilt → green.
- [x] 3.26 [P] **Mutation target #19** — the derivation folded into one `UPDATE … FROM (SELECT
  …)` → same rendezvous (3.22) ⇒ `EvalPlanQual` stale snapshot. **Evidence**: replaced
  `ProyectarEstadoAsync`'s derive+update tail with a single self-referential `WITH pedido, recibido
  UPDATE ordenes_compra … FROM …` (statement 1's lock kept intact, isolating this claim from #18) →
  build clean → the rendezvous test FAILED (`409 orden_compra_cierre_incoherente` — the simplified
  merged statement also dropped `fecha_cierre` handling, so the observed failure mode is a CHECK
  violation rather than a pure stale-read assertion; recorded honestly, not reshaped into a cleaner
  narrative) → `git checkout -- src/` → rebuilt → green.
- [x] 3.27 [P] **Mutation target #20** — `id_orden_compra` read from `preLectura` instead of the
  widened `RETURNING` → confirm under a concurrent `PUT` that relinks the draft must fail. **New
  test added**: `ConfirmarUsaElIdOrdenCompraVistoBajoElLockNoElDePreLectura` (`DbTransactionInterceptor`
  pausing `EjecutarConfirmarAsync` right after `BeginTransactionAsync`, same pattern as
  `ServicioDeOrdenesDeCompraTests.InterceptorDePausaTrasIniciarLaTransaccion`) — a borrador linked to
  OC-A is relinked to OC-B by a concurrent `PUT` while paused; OC-B must close, OC-A must stay
  untouched. **Evidence**: threaded `preLectura.IdOrdenCompra` into `EjecutarConfirmarAsync` in place
  of `encabezado.IdOrdenCompra` → build clean → test FAILED (OC-B stayed `Enviada` — the projection
  wrongly targeted stale OC-A instead) → `git checkout -- src/` → rebuilt → green.
- [x] 3.28 [P] **Mutation target #21** — OC lock moved after the `proveedores` lock. **FINDING,
  not a clean pass** (mutation-proof-tests rule 3): moved the guarded block to right after the
  `proveedores` lock in `EjecutarConfirmarAsync` → the SOURCE-TEXT test
  (`ElGuardDeLaOrdenDeCompraEstaEnPosicion2…`) FAILED immediately, as expected. The BEHAVIORAL
  rendezvous test (3.22) **stayed green under this exact mutation** — verified empirically (rule 2):
  a confirm×confirm reordering of the SAME code path takes both locks in the SAME relative order on
  both sides (`proveedores → OC` for both concurrent callers), which cannot form a genuine
  lock-cycle with itself; a real deadlock needs a *different* call path taking the two locks in the
  opposite order, and no such path exists inside this slice (only slice 4's `AnularAsync` would be a
  candidate, and it already takes `OC` **before** any stock/proveedores work per its own guarded
  call). Design's "confirm × confirm rendezvous ⇒ deadlock/timeout" phrasing for this target does
  not hold for the confirm×confirm sub-case specifically; the source-text test is the real,
  functioning detector. Reverted (`git checkout -- src/`), rebuilt, both tests green.
- [x] 3.29 [P] **Mutation target #22** — `c.estado = 'confirmada'` widened to any estado → a
  linked `borrador` reception moves the OC (3.21 fixture) must fail. **Evidence**: replaced the
  filter with `AND true` → build clean →
  `UnaRecepcionEnBorradorLigadaNoCuentaParaLaDerivacion` FAILED (expected `RecibidaParcial`, actual
  `Cerrada` — the unconfirmed borrador's quantity leaked into the derivation) → reverted, clean →
  green.
- [x] 3.30 [P] **Mutation target #23** — `ic.deleted_at IS NULL` deleted → the soft-deleted-
  reception fixture (3.21) must fail. **Evidence**: dropped `ic.deleted_at IS NULL` from the
  `recibido` CTE's `WHERE` → build clean →
  `UnItemDeRecepcionSoftDeleteadoDejaDeContarEnLaDerivacion` FAILED (expected `RecibidaParcial`,
  actual `Cerrada` — the soft-deleted item's quantity still counted) → reverted, clean → green.
- [x] 3.31 [P] **Mutation target #24** — `GROUP BY id_articulo` on the ordered side matched line-
  to-line → the duplicate-OC-lines fixture must fail. **New DISCRIMINANT test added**
  (`DosLineasDelMismoArticuloComparanContraLaSumaNoContraCadaLineaIndividual`, 3+4=7 pedidos vs. 5
  recibidos — the pre-existing 8-vs-7 over-delivery test does NOT discriminate this mutation, since
  8 exceeds every individual line too; 5 exceeds neither individual line (3, 4) but IS less than the
  correct sum (7), so only the correct grouping reports `RecibidaParcial`). **Evidence**: changed
  `GROUP BY i.id_articulo` to `GROUP BY i.id_articulo, i.id_item` in the `pedido` CTE → build clean →
  the new test FAILED (expected `RecibidaParcial`, actual `Cerrada` — each individual line read
  "covered" against the shared 5-unit receipt) → reverted, clean → green.
- [x] 3.32 [P] **Mutation target #25** — `algoRecibido` sourced from the ordered side's coalesced
  sum instead of the reception side → the pure-substitution fixture (OC stays `enviada`) must
  fail. **Evidence**: changed `algo_recibido` to `SUM(r2.recibida) FROM pedido p2 JOIN recibido r2
  ON r2.id_articulo = p2.id_articulo` (inner join against ordered lines only) → build clean →
  `UnaEntregaPorSustitucionNuncaPedidaMuevaAOrdenARecibidaParcial` FAILED (expected
  `RecibidaParcial`, actual `Enviada` — the substitution delivery, received but never ordered,
  became invisible) → reverted, clean → green.
- [x] 3.33 [P] **Mutation target #26** — `id_empleado_cierre IS NOT NULL` short-circuit deleted →
  annulling a reception of a manually-closed OC reopens it. **New test added**
  (`AnularUnaRecepcionDeUnaOrdenCerradaManualmenteNoLaReabre`, OC seeded `Cerrada`+
  `IdEmpleadoCierre` directly by EF — `POST /{id}/cerrar` is slice 4). **FINDING**: deleting ONLY the
  C# early-return (`|| lockeado.CierreManual`) is a FALSE PASS by itself —
  `ProyectorDeEstadoDeOrden.Proyectar`'s OWN domain logic ALSO checks `cierreManual` first
  (defense-in-depth, already exhaustively truth-tabled in slice 1), so the no-op branch still lands
  on `Cerrada` even without the early-return. Routed below the confound (rule 3): mutated the
  early-return AND hardcoded `cierreManual: false` in the `Proyectar` call together → build clean →
  the new test FAILED (`409` instead of `200`/stayed-`Cerrada`) → reverted BOTH lines
  (`git checkout -- src/`), clean → green.
- [x] 3.34 [P] **Mutation target #27** — `estado = 'anulada'` terminal short-circuit deleted.
  **INVESTIGATED, NOT independently provable via a single-layer mutation** (mutation-proof-tests
  rule 3, same class as stage-16 slice-1 target #6's precedent): deleted ONLY the C# early-return's
  `estadoActual == EstadoOrdenCompra.Anulada ||` clause → new test
  `AnularUnaRecepcionLigadaAUnaOrdenYaAnuladaNoLaResucita` (OC seeded `Anulada` directly by EF)
  **STAYED GREEN** — `ProyectorDeEstadoDeOrden.Proyectar`'s own first arm
  (`estadoActual is Anulada ⇒ Anulada`) already redundantly protects the terminal rule, and since
  the recomputed `nuevoEstado` still equals `estadoActual`, statement 3 is skipped as a no-op either
  way — same final observable state. The invariant IS proven, exhaustively, by slice 1's domain
  truth-table unit test (`ProyectorDeEstadoDeOrdenTests`, "`anulada` terminal from every input") —
  this is a genuine two-layer defense, not a production gap. Not pursued further (a combined
  mutation like target #26's would require also lying about `estadoActual` to `Proyectar`, at which
  point the test would stop exercising this specific early-return at all). Reverted, clean → green
  (unchanged, since nothing was left mutated).
- [x] 3.35 [P] **Mutation target #28** — `fecha_cierre = NULL` on regression kept as old value →
  `ck_ordenes_compra_cierre` ⇒ `23514` (3.20 regresses). **Evidence**: changed the `CASE` to `ELSE
  fecha_cierre` (keep the pre-update value) instead of `ELSE NULL` → build clean →
  `AnularLaUnicaRecepcionDeUnaOrdenCerradaAutomaticamenteLaDevuelveAEnviada` FAILED (`409`, the
  `ck_ordenes_compra_cierre` CHECK rejected the regression that left a stale `fecha_cierre` on a
  non-`cerrada` row) → reverted, clean → green. Strengthened the base test with an explicit
  `Assert.Null(final.FechaCierre)` in the same pass.
- [x] 3.36 [P] **Mutation target #29** — `if (encabezado.IdOrdenCompra is { } idOc)` called
  unconditionally → the zero-extra-statements proof (3.12) must fail. **Evidence**: replaced the
  guard with an unconditional block (`idOc = encabezado.IdOrdenCompra ?? 0`) → build clean → BOTH
  detectors failed: the structural test
  (`LasLlamadasAEscriturasDeOrdenDeCompraEnConfirmarNuncaOcurrenFueraDelGuardNulo`, "no se encontró
  el guard nulo") and the behavioral test (`UnConfirmSinOrdenLigadaNoTocaNingunaOrdenDeCompraExistente`,
  `500` — the sentinel id `0` broke the FK invariant) → reverted, clean → both green.
- [x] 3.37 [P] **Mutation target #30** — `id_proveedor`/`id_punto_venta` equality dropped in
  `ExigirOrdenLigableAsync` → the cross-proveedor/cross-PV link test (3.14) must fail. **Evidence**:
  ran BOTH conjuncts independently. Dropping the `id_proveedor` check → build clean →
  `UnProveedorNoCoincidenteNoPuedeLigar` FAILED (expected `BadRequest`, actual `Created`) → reverted.
  Dropping the `id_punto_venta` check → build clean → `UnPuntoDeVentaNoCoincidenteNoPuedeLigar`
  FAILED (same shape) → reverted. `git status` clean after both, rebuilt, both green.
- [x] 3.38 [P] `db-error-backstops` — FK 9 (`fk_comprobantes_compra_orden_compra`) client-
  reachable test: **DONE** for the primary path (`LigarAUnaOrdenInexistenteEsRechazadaComo404` —
  `ExigirOrdenLigableAsync`'s own 404 pre-check catches an invalid `idOrdenCompra` before any write;
  under normal operation this makes the raw `23503`/generic-mapping backstop unreachable, the same
  "backstop of last resort" posture as other exempted FKs in this repo). **The race sub-clause
  ("linking to an OC being annulled concurrently") is NOT implemented** — same reason as 3.22's
  deferred half: no OC-anulación write path exists in this slice to race against; deferred to slice
  4. Generic `23503` → `referencia_invalida` mapping itself needed zero new code (already covers any
  `fk_*`-prefixed constraint since slice 1). *(design.md:325)*
- [x] 3.39 Gate guard: `dotnet ef migrations has-pending-model-changes` clean (verified); zero new
  files under `Migraciones/` (`git diff --stat main -- .../Migraciones/` empty); `git diff --stat`
  confirms no file under `src/Ways.Application/Ventas/` or `src/Ways.Application/Stock/` appears
  (verified empty). Gate holds, no deviation.
- [x] 3.40 Run `judgment-day` on the slice diff; fix confirmed issues; re-judge until clean. **NOT
  RUN by `sdd-apply`** — same executor-contract carve-out as slice 1 task 1.38 / slice 2 task
  2.26: this executor cannot launch sub-agents/reviewers. Left for the orchestrator to run before
  merge.
- [x] 3.41 Branch `feat/stage16-slice3-ligadura-y-proyeccion` off `main` (parent: slice 2); PR;
  merge stacked-to-main. **PARTIAL**: the worktree was already provisioned on
  `feat/stage16-slice3-ligadura` off `main` (`8b720d3`, slice 2 already merged) before this phase
  started — branching is done, naming differs by dropping `-y-proyeccion` (cosmetic, not re-branched
  to avoid losing the provisioned worktree — flagged for the orchestrator). PR creation/merge is
  explicitly out of scope (`NO pushees` instruction) — left for the orchestrator.

20. **Slice 3 apply-phase decisions and deviations (decision 15 discipline).**
    - **Double lock in `EjecutarConfirmarAsync`'s guarded block**: the literal pinned interface
      (two separate public methods, `BloquearYExigirNoAnuladaAsync` carrying NO `momento` parameter)
      means the confirm call site issues `BloquearYExigirNoAnuladaAsync` followed by
      `ProyectarEstadoAsync`, each taking its OWN `SELECT … FOR UPDATE` on the same OC row —
      Postgres re-acquiring a lock already held by the same transaction is a documented, harmless
      no-op (verifies visibility, never blocks, cannot self-deadlock). Followed literally rather
      than collapsed into a single shared-lock helper, since the design's own interface pins two
      distinct signatures with distinct throwing semantics.
    - **`ContadorDeComandos`/`DbCommandInterceptor` cannot prove "zero extra statements" for this
      slice** — same empirically-recorded limitation `ServicioDeComprasLockOrderTests`'s own
      doc-comment already established for the lock-order proof (raw `conexion.CreateCommand()`
      statements never enter EF's command pipeline, with or without the guard executing). Resolved
      via the `mutation-proof-tests` rule 3 escape hatch: a source-text structural proof (the calls
      appear ONLY inside the null-check guard) plus a behavioral sibling-untouched proof (an
      unrelated OC's `estado`/`updated_at` stay byte-identical across an unlinked confirm).
    - **Mutation target #21's "deadlock/timeout" claim does not hold for the confirm×confirm
      sub-case** — verified empirically (mutation-proof-tests rule 2, "run it, don't reason it"):
      reordering the OC guard to after the `proveedores` lock leaves the BEHAVIORAL rendezvous test
      green, because both concurrent confirmations traverse the SAME code path and therefore take
      the two locks in the SAME relative order on both sides — no lock-order inversion, no cycle.
      The SOURCE-TEXT test is the actual functioning detector for this target; registered so it is
      not read as a gap.
    - **Mutation target #27 investigated, found non-independently-provable via a single-layer
      mutation** — same class as stage-16 slice-1 target #6's "investigated finding, not a
      fabricated pass": `ProyectorDeEstadoDeOrden.Proyectar`'s own domain-level terminal check on
      `Anulada` redundantly protects the invariant this slice's C# early-return also protects,
      so deleting only the early-return produces the same final observable state (no-op skip).
      Mutation target #26 needed the SAME combined treatment (early-return deleted AND
      `cierreManual` lied about to `Proyectar`) to become independently provable — both cycles run
      together, evidence recorded under 3.33 above. Neither is a production gap: both invariants are
      separately, exhaustively proven at the domain-unit level by slice 1's
      `ProyectorDeEstadoDeOrdenTests` truth table.
    - **Two race sub-clauses deferred to slice 4** (tasks 3.22, 3.38): "anular OC × confirmar
      reception" and "linking to an OC being annulled concurrently" both require an OC-anulación
      write path (`POST /{id}/anular`, `ServicioDeOrdenesDeCompra.AnularAsync`) that does not exist
      anywhere in this slice's scope — those are slice-4 tasks 4.2/4.3. Registered here rather than
      silently narrowed; the guard code they would exercise (`BloquearYExigirNoAnuladaAsync`'s 409,
      the no-lock `EXISTS` read of decision 9) IS already fully implemented and unit/structurally
      covered — only the CONCURRENT race proof against a not-yet-existing endpoint is deferred.
    - **Mutation target #23's fault-point (task 3.23) covers only the CONFIRM path** — the ANULAR
      path's every post-projection guard is pre-existing stage-8/12 stock-negative logic already
      covered by `ComprasAnulacionYConcurrenciaTests`; duplicating it here would not add a new
      signal specific to this slice's ordering claim, so it was not duplicated (registered as an
      intentional gap, not fabricated coverage).
    - **Mutation target #19's evidence surfaced via a CHECK-constraint violation, not a pure
      stale-snapshot assertion** — the temporary merged-statement mutation used for evidence dropped
      `fecha_cierre` handling for simplicity (a genuinely different bug from the EvalPlanQual
      staleness the target names), so the observed `409 orden_compra_cierre_incoherente` confirms
      the test suite rejects this SQL shape, but does not, on its own, isolate the exact stale-read
      mechanism in prose. Recorded honestly per mutation-proof-tests rule 2 rather than reshaped.

21. **Judgment-day round 2, WARNING closed — the 3.12 doc-comment overclaimed the byte-identical
    asserts were "the" behavioral proxy for zero-extra-statements, when they were dead code for the
    literal mutant already recorded under 3.36.** The judge (round-2 blind reviewer B) flagged that
    `UnConfirmSinOrdenLigadaNoTocaNingunaOrdenDeCompraExistente`'s doc-comment and this file both
    called the test "the byte-identical sibling" proof without noting that, under the literal
    `?? 0` mutant, execution never reaches lines 723-724 at all — `ConfirmarHeaderAsync`'s caller
    dies first with a `500` from `BloquearYLeerAsync`'s FK-invariant throw, and the test's failure
    (correctly recorded under 3.36) actually comes from `ConfirmarCompraAsync`'s generic
    `StatusCode == OK` assert, not from the two byte-identical lines. Verified by mutation
    (`mutation-proof-tests` rule 2, run before this fix and again after):
    - **Before the fix**: applied a REALISTIC soft mutant to `EjecutarConfirmarAsync` — resolve
      `idOc` via `encabezado.IdOrdenCompra ?? <lookup ordenes_compra by id_tenant/id_proveedor/
      id_punto_venta, most recent>` instead of the literal `?? 0` — against the pre-fix test (sibling
      already at the same proveedor/PV by the existing test helper defaults, no landmine). Build
      clean → the pre-fix test **PASSED** (`0` failures) — the mutant is caught by neither net: the
      structural test can't see it (syntactically valid guard argument) and the byte-identical
      asserts see a genuine idempotent no-op (`ProyectarEstadoAsync` on the sibling derives the same
      state it already has, so statement 3 never runs) — confirming the WARNING.
    - **The fix**: added an EF-seeded "landmine" — a `Confirmada` receipt FK-linked to the sibling
      OC that covers its full pedido, written directly (bypassing `ServicioDeCompras`/
      `EscriturasDeOrdenDeCompra` entirely, same pattern as `SembrarOrdenAnuladaAsync`). Under
      correct production code the sibling is never re-derived (`id_orden_compra` of the unlinked
      confirm is `NULL`) so it stays stale-but-untouched forever — asserts pass. Re-ran the SAME
      soft mutant against the strengthened test → build clean → the test **FAILED** at
      `Assert.Equal(hermanaAntes.Estado, hermanaDespues.Estado)` (`Enviada` vs `Cerrada` — the
      landmine's `completa = true` closed the sibling once the mutant's coincidental lookup handed
      it to `ProyectarEstadoAsync`) → `git checkout -- src/` → `git status` clean → rebuild → green.
    - Doc-comments in `ServicioDeComprasLigaduraTests.cs` (task 3.12 test) and this task's line 3.12
      above corrected to name BOTH nets and what each one independently catches, instead of implying
      the byte-identical asserts alone are sufficient.

**Test plan**: zero-extra-statements (3.12); link happy/blocked paths incl. state-gating
(3.13-3.16); the projection scenarios (3.17-3.20); derivation fidelity (3.21); confirm×confirm race
(3.22, anular×confirmar deferred to slice 4); fault point, confirm path (3.23); lock order (3.24);
13 mutation targets (3.25-3.37, two registered as investigated/non-actionable defense-in-depth
findings rather than fabricated passes — #27 alone, #21's behavioral half); FK 9 primary path (3.38,
race deferred to slice 4).

**Verify**: `dotnet test --filter FullyQualifiedName~EscriturasDeOrdenDeCompra|FullyQualifiedName~ServicioDeCompras`
— 25/25 green in `ServicioDeComprasLigaduraTests` + `EscriturasDeOrdenDeCompraLockOrderTests`
combined with slices 1-2's re-run suites (177/177 across all Compras/OrdenesCompra/
SuperficieDeAutorizacion/GastosLigadosACompra integration tests; 289/289 in `Ways.Application.Tests`).

---

## Slice 4: Cierre + Anulación (PR 4)

**Start**: slice 3 merged. **Finish**: `POST /cerrar` and `POST /anular` exist; the 409 matrix and
the authorization matrix are proven; the anulación guard reads `comprobantes_compra` lock-free.
**Rollback**: revert branch — both endpoints disappear, the projection (slice 3) is unaffected.
**Done** = tests green + `judgment-day` clean round + PR merged.

- [x] 4.1 Same `ServicioDeOrdenesDeCompra.cs`: `CerrarAsync` — `UPDATE … SET estado='cerrada',
  fecha_cierre=$m, id_empleado_cierre=$actor WHERE … AND estado IN ('enviada','recibida_parcial')
  RETURNING`. *(design.md:262-263, ordenes-de-compra/spec.md:139-141)* — **DEVIATION registered
  (decision 22 below)**: `OrdenDeCompraBorrador` gains `FechaCierre`/`IdEmpleadoCierre` (previously
  absent from that slice-2 response DTO) — `CerrarAsync` is the write path that makes these two
  fields honest for the first time (`dto-contract-honesty` rule 1).
- [x] 4.2 Same file: `AnularAsync` — statement 1 `SELECT estado FOR UPDATE` (first and only lock);
  statement 2 the derived-received-zero guard (any artículo `> 0` ⇒ `409
  orden_compra_con_recepciones`); statement 3 the linked-`borrador` `EXISTS` guard, **WITHOUT any
  row lock** (decision 9); statement 4 `UPDATE … estado='anulada' WHERE … estado IN
  ('borrador','enviada') RETURNING`. *(design.md:252-259, decision 9, mutation target #33)* — the
  three failing guards (statement 1's estado check, statement 2, statement 3) all throw the SAME
  domain code, `orden_compra_con_recepciones` — the spec's own literal contract
  (ordenes-de-compra/spec.md:162-164, "otherwise 409 orden_compra_con_recepciones") pins one code
  for every refusal shape, same deliberate-generality criterion as decision 19's
  `orden_compra_no_enviable`. `CerrarAsync`'s own 0-row case uses `orden_compra_no_cerrable` (not
  literally named by spec/design; chosen following the same naming convention as
  `orden_compra_no_enviable`/`orden_compra_no_editable`).
- [x] 4.3 Modify `OrdenesDeCompraEndpoints.cs` — add `POST /{id}/cerrar`, `POST /{id}/anular`,
  same policy stack. *(design.md:306-307)*
- [x] 4.4 [P] Integration — a supplier order closed manually stamps `fecha_cierre` +
  `id_empleado_cierre`. *(ordenes-de-compra/spec.md:145-148)*
  `OrdenesCompraCierreYAnulacionTests.CerrarManualmenteEstampaFechaCierreYEmpleado`.
- [x] 4.5 [P] Integration — a manually-closed OC does not reopen when its reception is annulled.
  *(ordenes-de-compra/spec.md:150-153, comprobantes-compra/spec.md:70-73)*
  `UnaOrdenCerradaManualmenteNoSeReabreAlAnularSuRecepcion`.
- [x] 4.6 [P] Integration — closing an already-`cerrada` OC is rejected `409`.
  *(ordenes-de-compra/spec.md:155-158)* — implemented as a `[Theory]` covering all three
  non-cerrable states (`borrador`, `cerrada`, `anulada`),
  `CerrarUnaOrdenFueraDeEnviadaORecibidaParcialEsRechazada409`, seeded directly by EF (same
  criterion as `ServicioDeComprasLigaduraTests.SembrarOrdenAnuladaAsync` — the row's state is what
  the guard interprets, not the path that produced it).
- [x] 4.7 [P] Integration — an OC whose only reception was later annulled CAN itself be annulled
  (derived quantity zero). *(ordenes-de-compra/spec.md:169-173)*
  `UnaOrdenCuyaUnicaRecepcionFueAnuladaPuedeAnularseElla`.
- [x] 4.8 [P] Integration — an OC with an effective (not-annulled) reception CANNOT be annulled ⇒
  `409 orden_compra_con_recepciones`. *(ordenes-de-compra/spec.md:175-178)*
  `UnaOrdenConRecepcionEfectivaNoPuedeAnularse409` — **FINDING (mutation-proof-tests rule 3,
  registered here)**: this realistic end-to-end fixture does NOT discriminate statement 2's guard
  in isolation — any real confirmed reception already moves the estado out of
  `('borrador','enviada')` via `EscriturasDeOrdenDeCompra`, so statement 1 alone already rejects
  (confound, verified by mutation). Routed below the confound with a second test,
  `UnaOrdenEnviadaConUnaRecepcionConfirmadaLigadaDirectaNoPuedeAnularse409` (EF-seeded OC `enviada`
  + a directly-seeded confirmed comprobante, an estado combination the real projection never
  produces but that statement 2 must still catch) — this second test is the actual mutation
  target #33 (guard "a") discriminator.
- [x] 4.9 [P] Integration — an OC with a still-confirmable linked `borrador` draft CANNOT be
  annulled ⇒ `409 orden_compra_con_recepciones`. *(ordenes-de-compra/spec.md:180-183)*
  `UnaOrdenConBorradorLigadoConfirmableNoPuedeAnularse409` — no confound here (statements 1/2 both
  pass cleanly, only statement 3 can reject).
- [x] 4.10 [P] Integration — **the no-lock `EXISTS` proof**: adding `FOR SHARE` to the linked-draft
  guard's read reproduces the anular×confirmar deadlock; without it, both orders resolve to one
  `200` + one `409`, never a deadlock. *(design.md:64, decision 9, mutation target #33)* — **this
  is deferred race #1 from slice 3 (decision 20's "anular OC × confirmar reception", tasks 3.22/
  3.38), paid off here (decision 22 below).** Two shipped tests force BOTH lock-acquisition orders
  via `DbTransactionInterceptor` (`AnularPierdeCuandoConfirmarComitePrimeroMientrasAnularEstaPausada`,
  `AnularPierdeCuandoIntentaPrimeroMientrasConfirmarEstaPausada`) against the REAL HTTP endpoints —
  **FINDING**: given a genuinely pre-existing linked `borrador` draft, the race is NOT
  bidirectional in outcome — `anular` loses in BOTH orders (via statement 1 if `confirmar` wins the
  OC lock first, via statement 3 if `anular` wins it first but still finds the draft `borrador`,
  invisible-uncommitted confirm notwithstanding under READ COMMITTED). Both orderings verified
  empirically, both green. The "adding `FOR SHARE` reproduces a deadlock" half was verified with a
  TEMPORARY raw-two-connection test (removed after evidence capture, not shipped — it duplicates
  Postgres locking semantics rather than exercising production code): mutating statement 3's read
  to add `FOR SHARE` and forcing the exact lock cycle (A holds OC `FOR UPDATE`, wants comprobante
  `FOR SHARE`; B holds comprobante via an uncommitted `UPDATE`, wants OC `FOR UPDATE`) → Postgres's
  deadlock detector fired and one side threw within the 15s window; the SAME scenario without
  `FOR SHARE` (statement 3 as shipped) completed both sides cleanly, no exception, confirming
  decision 9's "lock-free" claim empirically (`mutation-proof-tests` rule 2).
- [x] 4.11 [P] Integration — **authorization matrix**: Vendedor 200 on both GETs, `403` on all
  five writes (`POST`, `PUT`, `enviar`, `cerrar`, `anular`); Supervisor same; Admin 200 on all;
  tenant B never sees tenant A's OCs. `SuperficieDeAutorizacionTests` allowlist gains the five
  new non-GET routes. *(ordenes-de-compra/spec.md:232-240, design.md:309-310)* —
  `VendedorEsRechazadoEnLasCincoRutasDeEscrituraDeOrdenesDeCompra`,
  `SupervisorEsRechazadoEnLasCincoRutasDeEscrituraDeOrdenesDeCompra`,
  `AdminEjerceElCicloCompletoDeEscrituraDeOrdenesDeCompra`,
  `CerrarYAnularUnaOrdenDeOtroTenantEsRechazadaComo404` (tenant isolation, ADR-8 404). **No
  `SuperficieDeAutorizacionTests` allowlist edit was needed**: `OrdenesDeCompraEndpoints.cs` already
  stacks `GestionDeCatalogo` on every write route (slices 2 and 4), so the generic
  `TodoEndpointNoGetFueraDelAllowlistApilaGestionDeCatalogo` walk covers the two new routes for
  free — confirmed by mutation (target #34a, task 4.17) that this generic guard actually fires.
  GETs are not yet implemented (slice 5), so the "200 on both GETs" half of this task is deferred
  there by construction — no route to test yet.
- [x] 4.12 [P] Integration — non-regression: `ComprasConfirmarTests`/`ComprasAnularTests` green
  and unedited in the diff (repeated per binding verify criterion 3). *(design.md:513-514)* —
  these names refer to `ComprasLifecycleTests`/`ComprasAnulacionYConcurrenciaTests` (same mapping
  task 3.12's doc-comment already used). `git diff --stat` against both files: empty. Full
  `Ways.IntegrationTests` regression run: 222/222 green (`Compras`/`OrdenesCompra`/
  `SuperficieDeAutorizacion`/`ManejadorDeErrores` filter).
- [x] 4.13 [P] Confirm `cuenta-corriente-de-proveedores`/`saldo-de-proveedor` untouched by
  `git diff --stat` (spec OD7/T5). *(not-a-new-conflict note above)* — `git diff --stat` against
  `src/Ways.Application/CuentaCorriente/`: empty.
- [x] 4.14 [P] **Mutation target #31** — `WHERE estado IN ('enviada','recibida_parcial')` in
  `cerrar` widened → closing a `borrador`/`anulada` OC succeeds (must fail the test). **Evidence**:
  deleted the `AND estado IN (...)` clause from `CerrarHeaderAsync`'s SQL → `dotnet build
  --no-incremental` (clean) → all three `CerrarUnaOrdenFueraDeEnviadaORecibidaParcialEsRechazada409`
  theory cases FAILED (`borrador`: wrong domain code, the request fell through to an unrelated
  service-level 409; `cerrada`/`anulada`: `200` instead of `409`, the row's real estado came back
  `Cerrada` in the response body) → `git checkout -- src/` → `git status` clean → rebuilt → green
  (3/3).
- [x] 4.15 [P] **Mutation target #32** — `id_empleado_cierre = $actor` on manual close written
  NULL → the "a manually closed OC is not reopened" test (4.5) must fail. **Evidence**: replaced
  `ParametrosDeComando.Agregar(comando, idEmpleado)` with
  `ParametrosDeComando.AgregarNulo(comando, null)` in `CerrarHeaderAsync` → build clean → BOTH
  `UnaOrdenCerradaManualmenteNoSeReabreAlAnularSuRecepcion` (expected `Cerrada`, actual `Enviada` —
  the projection's `cierre_manual` short-circuit reads `id_empleado_cierre IS NOT NULL`, now false,
  so annulling the reception walked the OC back) AND `CerrarManualmenteEstampaFechaCierreYEmpleado`
  (expected employee id `3`, actual `null`) FAILED → `git checkout -- src/` → clean → rebuilt →
  green (2/2).
- [x] 4.16 [P] **Mutation target #33** — either the derived-received-zero guard or the linked-
  `borrador` `EXISTS` guard deleted → `409 orden_compra_con_recepciones` test (4.8/4.9) must fail,
  one test per guard; adding `FOR SHARE` to the `EXISTS` read → the anular × confirmar rendezvous
  (4.10) deadlocks. **Evidence, guard "a" (statement 2)**: neutered the call
  (`if (false && await TieneRecepcionConfirmadaAsync(...))`) → build clean →
  `UnaOrdenConRecepcionEfectivaNoPuedeAnularse409` **stayed green** (confound, see task 4.8's
  finding above) → the isolated discriminator,
  `UnaOrdenEnviadaConUnaRecepcionConfirmadaLigadaDirectaNoPuedeAnularse409`, FAILED (`200
  Anulada` instead of `409`) → reverted, clean → both green. **Evidence, guard "b" (statement 3)**:
  same neutering pattern on `TieneComprobanteLigadoEnBorradorAsync`'s call → build clean →
  `UnaOrdenConBorradorLigadoConfirmableNoPuedeAnularse409` FAILED (`200 Anulada` instead of `409`,
  no confound this time) → reverted, clean → green. **Evidence, `FOR SHARE` deadlock**: see task
  4.10's write-up above (temporary raw-connection test, both directions verified, removed after
  capture).
- [x] 4.17 [P] **Mutation target #34a** — `.RequireAuthorization(Politicas.GestionDeCatalogo)`
  dropped from any of the five write routes → its own 403-matrix test (4.11) must fail. **Evidence**:
  removed the `.RequireAuthorization(Politicas.GestionDeCatalogo)` call from the `/cerrar` route in
  `OrdenesDeCompraEndpoints.cs` → build clean → THREE tests failed simultaneously:
  `VendedorEsRechazadoEnLasCincoRutasDeEscrituraDeOrdenesDeCompra` and
  `SupervisorEsRechazadoEnLasCincoRutasDeEscrituraDeOrdenesDeCompra` (expected `Forbidden`, actual
  `OK` on the `/cerrar` assertion) AND the pre-existing generic
  `SuperficieDeAutorizacionTests.TodoEndpointNoGetFueraDelAllowlistApilaGestionDeCatalogo`
  (`"Endpoint(s) de escritura sin GestionDeCatalogo y fuera del allowlist: POST
  /api/ordenes-compra/{id:int}/cerrar"`) → reverted, clean → all three green again.
- [x] 4.18 Gate guard: `dotnet ef migrations has-pending-model-changes` clean; zero new files under
  `Migraciones/`; `Politicas.cs` unchanged (`git diff --stat`). Verified: `git diff --stat --
  src/Ways.Infrastructure/Persistencia/Migraciones/ src/Ways.Api/Seguridad/Politicas.cs` empty;
  `OrdenesCompraCierreYAnulacionTests.NoHayCambiosPendientesDeModeloEnLaSlice4` green (in-process
  `HasPendingModelChanges() == false`). Gate holds, no deviation.
- [x] 4.19 Run `judgment-day`; fix confirmed issues; re-judge until clean. **NOT RUN by
  `sdd-apply`** — same executor-contract carve-out as slices 1-3: this executor cannot launch
  sub-agents/reviewers. Left for the orchestrator to run before merge.
- [x] 4.20 Branch `feat/stage16-slice4-cierre-y-anulacion` off `main` (parent: slice 3); PR; merge
  stacked-to-main. **PARTIAL**: the worktree was already provisioned on
  `feat/stage16-slice4-cierre-anulacion` off `main` (`a89e7c0`, slice 3 already merged) before this
  phase started — branching is done, naming differs by dropping `-y-` (same cosmetic deviation
  pattern as slices 2/3, not re-branched to avoid losing the provisioned worktree — flagged for the
  orchestrator). PR creation/merge is explicitly out of scope (`NO pushees` instruction) — left for
  the orchestrator.

**Test plan**: cierre happy/blocked (4.4, 4.6); non-reopening (4.5); anulación book-governed rule
(4.7-4.9); the no-lock proof (4.10); authorization matrix (4.11); non-regression (4.12-4.13); 4
mutation targets incl. 34a (4.14-4.17).

**Verify**: `dotnet test --filter FullyQualifiedName~OrdenesCompraCierreYAnulacion|FullyQualifiedName~SuperficieDeAutorizacion`
— 21/21 green in `OrdenesCompraCierreYAnulacionTests` (includes
`ConfirmarUnaRecepcionLigadaAUnaOrdenRealmenteAnuladaPorElEndpointEsRechazada409`, verifying the
existing slice-3 guard `EscriturasDeOrdenDeCompra.BloquearYExigirNoAnuladaAsync` against an OC
anulada by THIS slice's real `POST /anular` endpoint, not EF-seeded — the first end-to-end path
that can produce this combination without bypassing `ExigirOrdenLigableAsync`), combined with
slices 1-3's re-run suites (223/223 across all `Compras`/`OrdenesCompra`/
`SuperficieDeAutorizacion`/`ManejadorDeErrores` integration tests; 11/11 in
`Ways.Application.Tests`; 58/58 in `Ways.Domain.Tests`).

22. **Slice 4 apply-phase decisions and deviations (decision 15 discipline).**
    - **Domain code convention for `AnularAsync`/`CerrarAsync`**: the spec's own literal contract
      (ordenes-de-compra/spec.md:162-164) pins a SINGLE code, `orden_compra_con_recepciones`, for
      every anulación refusal — the estado check (statement 1), the derived-received guard
      (statement 2) and the linked-borrador guard (statement 3) all throw it. This is the SAME
      deliberate-generality criterion decision 19 already established for
      `orden_compra_no_enviable` (one code covering multiple real causes, none of them individually
      named by spec/design), not a fresh judgment call. `CerrarAsync`'s 0-row rejection uses
      `orden_compra_no_cerrable` — not literally named anywhere, chosen by the same naming
      convention as its two slice-2 siblings (`orden_compra_no_enviable`/`orden_compra_no_editable`).
    - **`OrdenDeCompraBorrador` DTO extension**: gains `FechaCierre`/`IdEmpleadoCierre` (both
      `DateTimeOffset?`/`int?`, additive trailing positional fields, the one call site
      (`Proyectar`) updated in the same commit). Not named by design's Interfaces/Contracts (which
      predates the slice-2 `OrdenDeCompraBorrador` deviation entirely and only knows about the
      single `OrdenDeCompraDetalle`), but `dto-contract-honesty` rule 1 applies the other direction
      here: `CerrarAsync` is the write path that makes these two fields real for the first time, so
      leaving them off the response the cierre call itself returns would hide the fact this exact
      request just wrote. `OrdenDeCompraDetalle` (slice 5) already plans to carry them; this is not
      a duplicate decision, just an earlier honest population of the same two columns on the
      narrower slice-2 DTO.
    - **The two deferred races from slice 3 (decision 20's tasks 3.22/3.38), paid off here.** Both
      required an OC-anulación write path that did not exist before this slice.
      - **"anular OC × confirmar reception" (task 4.10)**: implemented as two `DbTransactionInterceptor`-
        forced orderings against the REAL HTTP endpoints (`AnularPierdeCuandoConfirmarComitePrimero
        MientrasAnularEstaPausada`, `AnularPierdeCuandoIntentaPrimeroMientrasConfirmarEstaPausada`).
        **FINDING, registered honestly rather than reshaped (mutation-proof-tests rule 2)**: given a
        genuinely pre-existing linked `borrador` draft, this race is NOT bidirectional in outcome —
        `anular` loses in BOTH lock-acquisition orders (via statement 1 if `confirmar` wins the OC
        lock first and commits its projection before `anular` reads; via statement 3 if `anular`
        wins the OC lock first, because statement 3's lock-free read still sees the comprobante as
        `borrador` — `confirmar`'s own uncommitted `UPDATE` is invisible under READ COMMITTED to a
        plain `SELECT`). Task 4.9's own guard dominates this fixture regardless of timing; both
        orderings were verified empirically (both green), not reasoned. The design's Concurrency
        Guarantees prose ("the annulment loses" as one of two named outcomes) describes the general
        defense-in-depth mechanism, which this fixture only ever exercises through one of its two
        branches — same class of honest finding as slice 3's target #21.
      - **"linking to an OC being annulled concurrently" (FK 9's race sub-clause, task 3.38)**: this
        is a DIFFERENT, genuinely bidirectional race — `ExigirOrdenLigableAsync`'s `FOR SHARE`
        (under `ActualizarBorradorAsync`'s own transaction, a real TOCTOU guard per design T3)
        against `AnularAsync`'s statement-1 `FOR UPDATE` on the SAME OC row. Two tests force both
        orders (`AnularGanaLaCarreraCuandoElPutQueLigaEstaPausado`,
        `ElPutQueLigaGanaLaCarreraCuandoAnularEstaPausada`): whichever transaction takes the row
        lock first wins outright (the other sees the loser's already-committed fact and rejects
        accordingly — `orden_compra_anulada` if anular won, `orden_compra_con_recepciones` via
        statement 3 if the link won) — never a deadlock (`FOR SHARE`/`FOR UPDATE` contend on ONE
        resource, no cycle is reachable). Both orderings verified empirically, both green.
    - **`FOR SHARE`-on-statement-3 deadlock evidence (mutation target #33, third clause)** was
      captured with a TEMPORARY test using two raw ADO connections (not the production call sites —
      it duplicates Postgres locking primitives directly to force the exact lock cycle
      deterministically) and REMOVED after capturing the evidence: mutating statement 3 to add
      `FOR SHARE` and forcing (A holds OC `FOR UPDATE`, wants comprobante `FOR SHARE`; B holds
      comprobante via an uncommitted `UPDATE`, wants OC `FOR UPDATE`) reproduced Postgres's deadlock
      detector firing (one side threw) within the 15s window; the identical scenario against the
      SHIPPED statement 3 (no `FOR SHARE`) completed both sides cleanly. Not kept as a permanent
      test — a raw-SQL-literal test that never calls `ServicioDeOrdenesDeCompra` would only assert
      Postgres's own locking semantics, not this codebase's behavior. **Correction (judgment-day
      round, juez B, MAJOR — see decision 23)**: this bullet originally claimed the two shipped
      interceptor tests above (`AnularPierdeCuandoConfirmarComitePrimeroMientrasAnularEstaPausada`/
      `AnularPierdeCuandoIntentaPrimeroMientrasConfirmarEstaPausada`) "already give the permanent
      regression coverage" for this invariant. That is FALSE: both interceptor tests pause the
      `DbTransactionInterceptor` immediately after `BeginTransactionAsync`, BEFORE either
      transaction's first statement runs — they never recreate the row-contention window a deadlock
      needs, so they regress the RESULT of the race (which side's estado guard wins), not the
      absence of locking on statements 2/3. The permanent structural regression for THIS invariant
      is `ServicioDeOrdenesDeCompraLockFreeGuardsTests` (decision 23), not these two tests.
    - **Confound found and fixed (mutation-proof-tests rule 3, task 4.8/4.16 guard "a")**: the
      realistic end-to-end fixture for "an OC with an effective reception cannot be annulled"
      (`UnaOrdenConRecepcionEfectivaNoPuedeAnularse409`) does not, by itself, discriminate
      statement 2's guard — any real confirmed reception already walks the estado out of
      `('borrador','enviada')` via `EscriturasDeOrdenDeCompra`'s projection, so statement 1's own
      check already rejects before statement 2 ever runs, verified by mutation (neutering statement
      2 left this test green). Routed below the confound with a second, EF-seeded test
      (`UnaOrdenEnviadaConUnaRecepcionConfirmadaLigadaDirectaNoPuedeAnularse409`) that produces an
      estado/book combination the real projection never creates but that statement 2 must still
      catch honestly. The realistic test is kept (it documents true end-to-end behavior); the
      EF-seeded test is the actual mutation-evidence discriminator for that guard.
    - **Process note**: Docker Desktop was already healthy at the start of this phase (`docker info`
      verified before the first test run, same discipline as prior slices); the apply-phase host
      process did not die mid-cycle — all 4 mutation-evidence cycles (targets #31/#32/#33/#34a) ran
      end-to-end in one session, each verified FAIL → revert → green before proceeding to the next,
      `git status` clean after every revert.

23. **`judgment-day` round confirmed 1 MAJOR (juez B) on decision 22's interceptor-tests claim —
    closed with a permanent structural test.** Decision 22's `FOR SHARE`-on-statement-3 bullet
    claimed the two shipped `DbTransactionInterceptor` tests
    (`AnularPierdeCuandoConfirmarComitePrimeroMientrasAnularEstaPausada`/
    `AnularPierdeCuandoIntentaPrimeroMientrasConfirmarEstaPausada`) "already give the permanent
    regression coverage" for the lock-free invariant on statements 2/3 of `AnularAsync`
    (`TieneRecepcionConfirmadaAsync`/`TieneComprobanteLigadoEnBorradorAsync`,
    `ServicioDeOrdenesDeCompra.cs:447-491`). **FALSE, confirmed by inspection**: both interceptor
    tests pause the interceptor right after `BeginTransactionAsync`, before either transaction's
    first statement — they never create the row-contention window a deadlock needs, so they
    regress the RESULT of the race (which side's estado guard wins), never the presence/absence of
    row locking on these two statements. Left as documented, a deliberate "safety" reintroduction
    of `FOR SHARE`/`FOR UPDATE` on either guard would ship real deadlock risk with zero red test.
    **Fix**: added `ServicioDeOrdenesDeCompraLockFreeGuardsTests` (source-text assertion, same
    technique as `EscriturasDeOrdenDeCompraLockOrderTests`/`ServicioDeComprasLockOrderTests` —
    mutation-proof-tests rule 3: the absence of a deadlock is a non-event, inherently untestable
    behaviorally, so the structural confound is the documented escape hatch) — extracts each
    method's real body via `IndexOf`/substring (not a duplicated SQL literal) and asserts neither
    contains `FOR SHARE` nor `FOR UPDATE`. Mutation evidence (both guards, both lock clauses):
    reintroducing `FOR SHARE` in statement 3's `EXISTS` or `FOR UPDATE` in statement 2's `EXISTS`
    made the corresponding new test fail on a clean `--no-incremental` build; `git checkout -- src/`
    plus a rebuild returned to green, `git status` clean between cycles. Decision 22's bullet is
    corrected in place (struck through in effect, replaced with this cross-reference) rather than
    rewritten silently, per decision 15's honesty discipline; the one-shot raw-ADO deadlock capture
    described there remains historical evidence, not a claim about the interceptor tests.

---

## Slice 5: Lectura (PR 5)

**Start**: slice 4 merged (design lists slice 5 as depending on 3 for the derivation; merged after
4 per the stacked-to-main chain strategy). **Finish**: paginated list + detail read model (per-
artículo cobertura, received-not-ordered, price deviation with honest nulls). **Rollback**: revert
branch — read endpoints disappear, no write-side impact. **Done** = tests green + `judgment-day`
clean round + PR merged.

**Budget note**: pre-authorized split `5a` (paginated list) / `5b` (detail + cobertura +
deviation) if this slice overflows — decision 3 above.

- [x] 5.1 Same `ContratosDeOrdenDeCompra.cs`: `CoberturaDeArticulo`, `OrdenDeCompraDetalle`,
  `OrdenDeCompraListada`, `PaginaDeOrdenesDeCompra` — per-artículo cobertura list, never a
  fabricated per-line split. *(design.md:176-192, decision 13, `dto-contract-honesty`)*
- [x] 5.2 Same `ServicioDeOrdenesDeCompra.cs`: `ListarAsync` — `ConstruirQuery` with
  `idProveedor`/`idPuntoVenta`/`estado`/`desde`/`hasta` filters, `ORDER BY fecha_emision DESC,
  id_orden_compra DESC`, `Skip/Take`, `pagina = Math.Max(pagina,1)`, `tamanio =
  Math.Clamp(tamanio,1,200)`. *(design.md:70, 201-204, mutation target #34b)*
- [x] 5.3 Same file: `ObtenerDetalleAsync` — items + the per-artículo cobertura (own LINQ
  derivation, read-only) + price deviation via `CalculadorDeCompra.
  CalcularCostoEfectivoDesdeItem`, `null` never `0` when `costo_unitario_estimado IS NULL`.
  *(design.md:67-69, decision 14, ordenes-de-compra/spec.md:242-249)* — the cobertura derivation is
  **deliberately separate** from `EscriturasDeOrdenDeCompra.DerivarAsync`'s raw-ADO CTE (decision 20
  below), never a shared SQL fragment.
- [x] 5.4 **DEVIATION (decision 20 below): NOT a backend method.** `design.md`'s own Web section
  (`design.md:347-351`) and its Testing Strategy row ("Web (vitest)": "a `sugerido === null` row is
  excluded from the pre-load") place the reposición→OC mapping entirely in `Reposicion.tsx`
  (client-side filter + field mapping, posting a plain `SolicitudDeOrdenDeCompra` to the existing
  `POST /`). `design.md`'s own File Changes table for `ServicioDeOrdenesDeCompra.cs` (`:376`) lists
  only "Draft CRUD, `enviar`, `cerrar`, `anular`, list + detail read model" — no pre-load method.
  Mutation target #34's own row places "the `sugerido !== null` filter" under slices "4-6" (the web
  branch), never slice 5. No `PreCargarDesdeReposicionAsync` was added; `POST /` needs no
  modification — it already accepts an ordinary `SolicitudDeOrdenDeCompra` (slice 2).
- [x] 5.5 Modify `OrdenesDeCompraEndpoints.cs` — `GET /` (paginated) and `GET /{id}` under
  `OperacionDePos` only (no write policy). *(design.md:301-302)*
- [x] 5.6 [P] Integration — pagination with `fecha_emision` tied on every row (RelojFijo) ⇒ page 2
  repeats and skips nothing (the `ThenByDescending(o => o.Id)` tiebreaker). *(design.md:70,
  mutation target #34b)* — `OrdenesCompraLecturaTests.
  PaginacionConFechaEmisionEmpatadaNoDuplicaNiSalteaFilas`.
- [x] 5.7 [P] Integration — each filter (`idProveedor`/`idPuntoVenta`/`estado`/`desde`/`hasta`)
  with asymmetric seeds — an ignored filter must not silently return extra rows. *(design.md:199,
  mutation target #34b)* — `OrdenesCompraLecturaTests.
  CadaFiltroIgnoradoDevolveriaDeMasConSemillasAsimetricas`.
- [x] 5.8 [P] Integration — sibling OC of the same tenant seeded on every listing/detail test with
  its own items (rule 12c) — a raw EF write desyncing `estado` to a sentinel must surface the
  sentinel (rule 12a). *(design.md, Testing Strategy; design decision 12)* — `OrdenesCompraLecturaTests.
  DetalleLeeElEstadoDeLaColumnaSinRederivarloConUnaDesincronizacionCruda` (sentinel: `enviada` OC
  with zero recepciones force-written to `RecibidaParcial` — a value the real derivation could never
  produce for that fixture — GET returns the sentinel; sibling OC unaffected).
- [x] 5.9 [P] Integration — **projection fidelity**: for every derivation fixture, the stored
  `estado` equals `ProyectorDeEstadoDeOrden.Proyectar(...)` recomputed from the read model's own
  cobertura numbers. *(design.md, Testing Strategy — the raw-ADO/LINQ drift proof)* —
  `OrdenesCompraLecturaTests.
  CoberturaPorArticuloDiscriminaCorrectamenteYLaProyeccionCoincideConLaColumna`: rule-11 discriminant
  fixture (two OC lines of one artículo 3+4⇒7 pedidas; a split reception 2 then 5; an artículo
  received-never-ordered; a soft-deleted reception line excluded; a comprobante ligado still
  `borrador` excluded; a reception of another OC of the same proveedor excluded) plus the
  recomputation assertion.
- [x] 5.10 [P] Integration — a price increase between order and invoice is surfaced (`+12%`), not
  blocked. *(ordenes-de-compra/spec.md:251-255)* — `OrdenesCompraLecturaTests.
  UnAumentoDePrecioEntreOrdenYFacturaSeSurfaceaNoSeBloquea`.
- [x] 5.11 [P] Integration — a never-quoted line reports *no comparable*, never `0`.
  *(ordenes-de-compra/spec.md:257-260, mutation target #34b)* — `OrdenesCompraLecturaTests.
  UnaLineaNuncaCotizadaReportaNoComparableNuncaCero`.
- [x] 5.12 **DEVIATION (decision 20 below), same as 5.4**: the pre-load exclusion of `sugerido =
  null` rows and the `"Sin proveedor"` bucket's missing action are client-side behaviors
  (`Reposicion.tsx`, slice 6) — `design.md`'s Testing Strategy places both assertions under "Web
  (vitest)", not backend integration. No backend test added here; the slice-6 web descriptor tests
  own this assertion (mutation target #34's "pre-load exclusion test").
- [x] 5.13 [P] Integration — `GET /api/reportes/stock/reposicion`'s response shape and figures
  unchanged before/after this stage. *(ordenes-de-compra/spec.md:280-283, reposicion-de-
  stock/spec.md's "byte-identical" scenario)* — `OrdenesCompraLecturaTests.
  ReposicionMantieneSuShapeYSusFigurasSinCambios`.
- [x] 5.14 [P] Integration — the offset boundary: a listing sent at the real client `-03:00` (never
  `Z`) asserts both the returned rows and the displayed período. *(decision 13 above)* —
  `OrdenesCompraLecturaTests.ListadoConOffsetMenosTresAsertaFilasYPeriodoMostrado`.
- [x] 5.15 [P] **Mutation target #34b (part 1)** — `.ThenByDescending(o => o.Id)` deleted → the
  tied-fecha pagination test (5.6) must fail. **Evidence**: deleted the `.ThenByDescending(o =>
  o.Id)` line in `ListarAsync` → `dotnet build --no-incremental` (clean) →
  `PaginacionConFechaEmisionEmpatadaNoDuplicaNiSalteaFilas` FAILED (`Assert.Equal` expected
  `[3, 2]`, actual `[1, 2]` — page 1 repeated the lowest id instead of the two highest) → `git
  status` clean revert (no diff — exact restore) → rebuilt → green.
- [x] 5.16 [P] **Mutation target #34b (part 2)** — any single `if (filtro is { } x)` conjunct
  deleted → its asymmetric-seed test (5.7) must fail. **Evidence**: deleted the `estado` filter's
  `if` block in `ConstruirQuery` → build clean →
  `CadaFiltroIgnoradoDevolveriaDeMasConSemillasAsimetricas` FAILED (`Assert.DoesNotContain` — the
  `borrador` OC leaked into the `estado=Enviada` result set) → `git status` clean revert → rebuilt →
  green.
- [x] 5.17 [P] **Mutation target #34b (part 3)** — the `Desvio` null branch replaced with `0` →
  the no-comparable test (5.11) must fail. **Evidence**: replaced the `: null` fallback with `:
  0m` in `ObtenerCoberturaAsync` → build clean →
  `UnaLineaNuncaCotizadaReportaNoComparableNuncaCero` FAILED (`Assert.Null` expected `null`, actual
  `0`) → `git status` clean revert → rebuilt → green.
- [x] 5.18 Gate guard: `dotnet ef migrations has-pending-model-changes` clean (verified via
  `db.Database.HasPendingModelChanges()`, same pattern as slices 2/4 —
  `OrdenesCompraLecturaTests.NoHayCambiosPendientesDeModeloRespectoDeLaMigracionDeLaSlice1`); zero
  new files under `Migraciones/` (`git diff --stat main -- src/Ways.Infrastructure/Persistencia/
  Migraciones/` empty). Gate holds, no deviation.
- [x] 5.19 Run `judgment-day` on the slice diff; fix confirmed issues; re-judge until clean. **NOT
  RUN by `sdd-apply`** — same executor-boundary reason as every prior slice (1.38/2.27/3.x/4.x):
  `judgment-day` is an orchestrator-level dual-review protocol this executor cannot invoke. Left for
  the orchestrator to run before merge.
- [x] 5.20 Branch `feat/stage16-slice5-lectura` off `main` (parent: slice 4); PR; merge
  stacked-to-main. **PARTIAL**: the worktree was already provisioned on
  `feat/stage16-slice5-lectura` off `main` (`6f8a25f`, slice 4 merged) before this phase started —
  branching is done. PR creation/merge is explicitly out of scope (`NO pushees` instruction) — left
  for the orchestrator.

**Test plan**: pagination + tiebreaker (5.6); filters (5.7); read-model rules 12a/12c (5.8);
projection fidelity (5.9); price deviation incl. honest nulls (5.10-5.11); pre-load (5.12);
stage-13 byte-identity (5.13); offset boundary (5.14); 3 mutation sub-targets (5.15-5.17).

**Verify**: `dotnet test --filter FullyQualifiedName~OrdenesCompraLectura`

---

## Slice 6: Web (PR 6)

**Start**: slice 5 merged. **Finish**: list/detail/draft screens ship with the reception entry
point, the `Reposicion.tsx` "generar OC" action (Admin-gated), and the `Compras.tsx` link.
**Rollback**: revert branch — screens/routes disappear, the API still serves the shape (isolated,
non-retracting per design's Pre-approved degradation). **Done** = tests green + `judgment-day`
clean round + PR merged.

**Budget note**: pre-approved degradation — if this slice overflows, ship list + detail + draft
and drop the `Reposicion.tsx` action; a documented reduction, never silent (decision 3 above).

- [ ] 6.1 Create `src/Ways.Web/src/api/ordenesDeCompra.ts` (+ `.test.ts`) — client + pure mappers;
  `tipos.ts` mirrors the read/write DTOs. *(design.md:353, 385)*
- [ ] 6.2 Create `src/Ways.Web/src/paginas/OrdenesDeCompra.tsx` (+ `.test.tsx`) — route
  `/ordenes-compra`, `RutaProtegida rolesPermitidos={[Vendedor,Supervisor,Admin]}` (the read gate);
  filters + pager. *(design.md:330-333)*
- [ ] 6.3 Create `src/Ways.Web/src/paginas/OrdenDeCompra.tsx` (+ `.test.tsx`) — draft editor +
  detail with cobertura table (`—` never `0` when `null`) + `enviar`/`cerrar`/`anular` actions +
  "Registrar recepción" → `navigate('/compras/nueva?idOrdenCompra=' + id)`. `key={idOrden ??
  'nueva'}` on the subtree; `generacionRef` per fetch; generation bumps before each write;
  post-write refetch has its own `try/catch`; first-line re-entrancy guard + full-window disable
  on `enviar`/`cerrar`/`anular`. *(design.md:334-343, `react-async-state` rules 2,3,6,8,9)*
- [ ] 6.4 Modify `src/Ways.Web/src/paginas/CompraEditor.tsx` — read `idOrdenCompra` from
  `useSearchParams`, pre-fill proveedor/PV/OC and one line per artículo with `Pendiente > 0`;
  `key={idNumerico ?? 'nuevo-' + (idOrdenCompra ?? 's')}`. *(design.md:344-346)*
- [ ] 6.5 Modify `src/Ways.Web/src/paginas/Reposicion.tsx` — per-group "Generar OC" button
  rendered only when `grupo.idProveedor !== null` **and** `useAuth().usuario.rolId === ROL.Admin`;
  posts `filas.filter(f => f.sugerido !== null)` mapped `{IdArticulo, Sugerido} →
  {IdArticulo, CantidadPedida}`; `"Sin proveedor"` renders without the action. *(design.md:347-351,
  decision 16, mutation target #34c)*
- [ ] 6.6 Modify `src/Ways.Web/src/paginas/Compras.tsx` — show the linked OC with a link to it.
  *(design.md:352)*
- [ ] 6.7 Modify `src/Ways.Web/src/App.tsx` — two new routes.
  *(design.md:File Changes)*
- [ ] 6.8 [P] `web-descriptor-tests` — colocated tests for every new pure helper (reposición→OC
  mapper, cobertura formatter, filter builder) and both screens' descriptors. *(design.md:359)*
- [ ] 6.9 [P] Vitest — the `"Sin proveedor"` bucket offers no action; a Supervisor session renders
  no action. *(mutation target #34c)*
- [ ] 6.10 [P] Vitest — a `sugerido === null` row is excluded from the pre-load.
  *(mutation target #34c)*
- [ ] 6.11 [P] Vitest — a double click on `enviar`/`cerrar`/`anular` issues exactly one POST
  (`react-async-state` rule 9).
- [ ] 6.12 [P] Vitest — a stale response is discarded, resolved **inside `act`** (`react-async-
  state` rule 7).
- [ ] 6.13 [P] Vitest — pager disabled at the edges.
- [ ] 6.14 [P] `react-async-state` rule 10 — grep every recovery path added on one screen and
  replicate it in its sibling in the same commit; record the grep evidence in the PR body.
- [ ] 6.15 [P] **Mutation target #34c (part 1)** — the `grupo.idProveedor !== null` branch deleted
  → the "Sin proveedor" no-action test (6.9) must fail.
- [ ] 6.16 [P] **Mutation target #34c (part 2)** — the `rolId === ROL.Admin` branch deleted → the
  Supervisor no-action test (6.9) must fail.
- [ ] 6.17 [P] **Mutation target #34c (part 3)** — the `sugerido !== null` filter deleted → the
  pre-load exclusion test (6.10) must fail.
- [ ] 6.18 Gate guard: `dotnet ef migrations has-pending-model-changes` clean (no schema drift from
  this slice); `npm run build` clean.
- [ ] 6.19 Run `judgment-day`; fix confirmed issues; re-judge until clean.
- [ ] 6.20 Branch `feat/stage16-slice6-web` off `main` (parent: slice 5); PR; merge
  stacked-to-main.

**Test plan**: descriptor tests (6.8); gating branches (6.9); pre-load exclusion (6.10); double-
click guard (6.11); stale-response discard (6.12); pager edges (6.13); 3 mutation sub-targets
(6.15-6.17).

**Verify**: `npm run test -- OrdenesDeCompra`

---

## Review Workload Forecast

| Field | Value |
|-------|-------|
| Estimated changed lines | ~2 540 total (6 slices: 500/410/470/320/360/480) |
| 400-line budget risk | High — slices 1, 2, 3 and 6 sit at or above the cap on the estimate alone; the programme's own record (stages 13-15 came in 1.5-3x their naive estimate from test depth) says they *will* exceed it |
| Chained PRs recommended | Yes |
| Suggested split | 6 PRs, stacked-to-main, per the Suggested Work Units table above |
| `size:exception` anticipated | No — the four pre-authorized cut points (decision 3 above) absorb it; a **7-8 PR outturn is the expected case**, not the exception |
| Delivery strategy | `auto-chain` (already resolved, `state.yaml`) |
| Chain strategy | `stacked-to-main` |
| Decision needed before apply | No — already resolved |

Decision needed before apply: No
Chained PRs recommended: Yes
Chain strategy: stacked-to-main
400-line budget risk: High

Per-slice budget risk: 1 **High (~500)** · 2 **High (~410)** · 3 **High (~470)** · 4 Medium
(~320) · 5 Medium (~360) · 6 **High (~480)**. Overflow is expected to come from test depth, not
scope creep: slice 1 carries 4 constraint families + the ordering trap + 9 mutation targets;
slice 3 carries derivation fidelity + two race families + fault points + 13 mutation targets;
slice 6 carries two full screens + descriptor tests + the gating matrix. **Never degraded**: the
projection's lock-then-re-read-then-update discipline, the zero-extra-statements proof, the
`_numero` ordering-trap assertion, and the manual-close short-circuit — those split, never trim.
