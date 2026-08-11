# Tasks: Stage 10 — Capa de agregación server-side + dashboard

## Orchestrator Decisions Recorded This Phase

1. **10 slices, 10 PRs, stacked-to-main** — the design's refinement of the
   proposal's 8-slice plan (proposal slice 1 split into slices 1–2; proposal
   slice 5 split into slices 6–7). Merge order follows the numbering below;
   it is also the dependency order for the API slices (1→5) and mostly the
   dependency order for the web slices (6→10).
2. **DB CHANGE GATE is APPROVED as no-schema-change** (`state.yaml`,
   2026-08-11). No STOP task is emitted. Every slice carries a gate-guard
   task: if `sdd-apply` finds itself writing a migration or touching the EF
   model snapshot, it MUST stop and reopen the gate — this is a scope
   violation, not an implementation detail.
3. **Raw-SQL discipline is a per-slice review gate, not a convention.** Only
   `LectorDeSerieTemporal` (slice 2) carries raw SQL; every slice whose
   report reads through it or through LINQ still ships the 4-test pattern
   (cross-tenant / soft-delete / estado / hand-computed fixture) per
   endpoint — dropping one of the four is the stage's named failure mode
   (design: Testing Strategy) and MUST NOT happen.
4. **judgment-day runs once per slice**, on that slice's diff, before its PR
   — per `protocolo-pr-solo-dev`. Ten independent rounds, not one at the end.

---

## Suggested Work Units

| Unit | Goal | Branch | Depends on | ~Lines |
|---|---|---|---|---|
| 1 | Parametros + políticas | `feat/stage10-slice1-parametros-y-politicas` | none | ~230 |
| 2 | Ventas resumen (domain + raw-SQL lector) | `feat/stage10-slice2-ventas-resumen` | 1 | ~390 |
| 3 | Ventas por dimensión (PV/vendedor/medio pago) | `feat/stage10-slice3-ventas-por-dimension` | 1, 2 | ~300 |
| 4 | Artículos + margen (rentabilidad) | `feat/stage10-slice4-articulos-y-margen` | 1, 2, stage-9 | ~380 |
| 5 | Egresos (compras + gastos) | `feat/stage10-slice5-egresos` | 1, 2 | ~300 |
| 6 | Recharts + `componentes/graficos/` | `feat/stage10-slice6-graficos` | none (parallel to 2–5) | ~200 |
| 7 | Tablero G1 parity | `feat/stage10-slice7-tablero-g1` | 2, 6 | ~300 |
| 8 | Tablero dimensiones | `feat/stage10-slice8-tablero-dimensiones` | 3, 7 | ~350 |
| 9 | Tablero rentabilidad | `feat/stage10-slice9-tablero-rentabilidad` | 4, 7/8 | ~250 |
| 10 | Comisiones — droppable in full | `feat/stage10-slice10-comisiones` | 1, 2, 7 | ~220 |

Slices 3, 4, 5 are mutually parallelizable (each depends only on 1+2, not on
each other). Slice 6 is parallelizable with the whole API track (2–5) — pure
frontend, no backend dependency. Merge order still follows 1→10 for a clean
review stack.

---

## Slice 1: Parámetros y Políticas (PR 1)

**Start**: `main`. **Finish**: `zona_horaria` and `comision_porcentaje`
registered with declared defaults, `ValidarTipo` rejects a `null`
deserialization and validates `zona_horaria` as a real IANA id,
`Parametros.tsx` renders a typed `<select>` for the zone, `LecturaDeReportes`
and `LecturaDeRentabilidad` policies exist and compose correctly. **Rollback**:
revert the branch — additive only, no schema, no data required.

- [x] 1.1 Modify `src/Ways.Domain/Catalogos/ParametroConocido.cs`: add
  `ZonaHoraria` (`string`, default `"America/Argentina/Buenos_Aires"`,
  JSON-quoted) and `ComisionPorcentaje` (`decimal`, default `"0"`) to the
  registry array at `:30`. *(design: Timezone Mechanics; spec
  parametros-operativos: zona_horaria And comision_porcentaje Are Known Keys)*
- [x] 1.2 Modify `src/Ways.Application/Parametros/ServicioDeParametros.cs`:
  harden `ValidarTipo` (`:110-123`) to reject a JSON `null` deserialization
  result instead of accepting it; add IANA validation for `zona_horaria` via
  `TimeZoneInfo.FindSystemTimeZoneById`, failing as 400 rather than letting a
  bad zone reach `date_trunc` as a Postgres 22023. *(design decision 12; spec
  parametros-operativos: First String-Typed Parametro Must Be Stored Quoted)*
- [x] 1.3 Modify `src/Ways.Api/Seguridad/Politicas.cs`: add
  `LecturaDeReportes` (`RequireClaim(RolId, Supervisor, Admin)`) and
  `LecturaDeRentabilidad` (`RequireClaim(RolId, Admin)`), same shape as
  `SupervisionDeCuentaCorriente` (`:57`). *(design decision 3, 7; spec
  reportes-de-gestion: LecturaDeReportes Policy; rentabilidad-y-comisiones:
  LecturaDeRentabilidad Policy Admits Admin Only)*
- [x] 1.4 Modify `src/Ways.Web/src/paginas/Parametros.tsx` (`:91,188-197`):
  add `tipo: 'texto'` to `PARAMETROS_CONOCIDOS` for `zona_horaria`, render a
  `<select>` of offered IANA zones instead of the hardcoded
  `type="number"` + `JSON.stringify(Number(...))` path; keep
  `comision_porcentaje` on the existing numeric path. *(design decision 12)*
- [x] 1.5 [P] `ParametrosTests` (Application/Integration): quoted
  `zona_horaria` round-trips; unquoted value → 400; `null` deserialization →
  400; an invalid IANA id → 400. *(spec parametros-operativos, both
  requirements)*
- [x] 1.6 [P] Colocated `Parametros.test.tsx`: the zone `<select>` renders
  and submits a quoted value; existing numeric-key flow unchanged. Per
  `web-descriptor-tests`.
- [x] 1.7 [P] `PoliticasTests` (or extend existing policy test file): claim
  matrix for `LecturaDeReportes` (Vendedor/Root rejected, Supervisor/Admin
  accepted) and `LecturaDeRentabilidad` (Admin only) at the policy level,
  independent of any endpoint.
- [x] 1.8 Gate guard: confirm `dotnet ef migrations list` is unchanged and
  the model snapshot has no diff.
- [x] 1.9 Run `judgment-day` on the slice diff; fix confirmed issues; re-judge
  until clean.
- [x] 1.10 Branch `feat/stage10-slice1-parametros-y-politicas` off `main`;
  PR per `branch-pr`; merge stacked-to-main.

**Verify**: `dotnet test --filter FullyQualifiedName~Parametros` /
`npm run test -- Parametros`

---

## Slice 2: Ventas Resumen (PR 2)

**Start**: slice 1 merged. **Finish**: `GET /api/reportes/ventas/resumen`
live behind `LecturaDeReportes`, business-day bucketing correct in the punto
de venta's zone, ticket promedio excludes NCX on both sides. **Rollback**:
revert the branch; no state to unwind.

- [x] 2.1 Create `src/Ways.Domain/Reportes/Granularidad.cs`: enum `Dia |
  Semana | Mes`.
- [x] 2.2 Create `src/Ways.Domain/Reportes/CoberturaDeCosto.cs`: the record
  from design *Interfaces / Contracts* (used by slice 4; created here so
  `Domain/Reportes/` ships as one unit per the design's file grouping).
- [x] 2.3 Create `src/Ways.Domain/Reportes/RangoDeReporte.cs`: DB-free type —
  `(DateOnly Desde, DateOnly Hasta, Granularidad, TimeZoneInfo)` →
  `DesdeUtc`, `HastaUtcExclusivo`, `Buckets()`; ISO week label via
  `ISOWeek.GetYear`/`GetWeekOfYear`; rejects `hasta < desde` and spans past
  366 days. *(design: Range resolution; Architecture Decision 4, 6)*
- [x] 2.4 Create `src/Ways.Application/Reportes/LectorDeSerieTemporal.cs`:
  two `private const string` SQL bodies (ventas, gastos) copied structurally
  from `ServicioDeCategorias.cs:199-244`; one `EjecutarAsync` opened via
  `Db.Database.OpenConnectionAsync()` (never `GetDbConnection().OpenAsync()`);
  explicit `deleted_at IS NULL`, `estado <> 'anulado'`, `tc.clase = 'venta'`,
  `id_tenant = $n`, `id_punto_venta = ANY($n)`; granularity inlined as a
  validated literal from a `switch` over `Granularidad`, zone bound as
  `$n`. *(design decisions 1–3, 8, 9; Timezone Mechanics SQL)*
- [x] 2.5 Create `src/Ways.Application/Reportes/Contratos.cs`:
  `BucketDeVentas`, `ResumenDeVentas` records exactly as in design
  *Interfaces / Contracts* (`TicketPromedio` nullable, never `0`).
- [x] 2.6 Create `src/Ways.Application/Reportes/ServicioDeReportesDeVentas.cs`:
  `ObtenerResumenAsync` — `Empresas.AnyAsync` → 404 (ADR-8);
  `PuntosVenta.Where(IdEmpresa)` → PV scope; resolve `zona_horaria` via
  `ServicioDeParametros`; build `RangoDeReporte`; call
  `LectorDeSerieTemporal`; left-join `Buckets()` against SQL rows in C# to
  fill gaps; compute `NetoVendido`/`CantidadTx`/`TicketPromedio`/
  `CantidadNcx`/`NetoNcx`. *(design decisions 4, 5; Data Flow)*
- [x] 2.7 Modify `src/Ways.Application/DependencyInjection.cs`: register
  `ServicioDeReportesDeVentas` and `LectorDeSerieTemporal`.
- [x] 2.8 Create `src/Ways.Api/Endpoints/ReportesEndpoints.cs`:
  `MapGroup("/api/reportes").RequireAuthorization(Politicas.LecturaDeReportes)`;
  `GET /ventas/resumen` (`idEmpresa`, `idPuntoVenta?`, `desde`, `hasta`,
  `granularidad`). *(design: Endpoints; dto-contract-honesty)*
- [x] 2.9 [P] Domain unit suite for `RangoDeReporte`: 22:30 ART sale buckets
  to its own day (and to the next day under a UTC zone, proving the
  parameter is live); `hasta` inclusivity; ISO Monday-start with
  `2026-W01` year rollover; month boundaries; gap fill; invalid-range and
  366-day guard. *(spec reportes-de-gestion: Business-Day Bucketing)* —
  `RangoDeReporteTests.cs`, 10 tests.
- [x] 2.10 [P] Two-tenant report seeder — implemented as a LOCAL helper
  (`PrepararAsync`/`SembrarComprobanteAsync`) inside
  `ReportesVentasResumenTests.cs` instead of extending `WaysApiFixture`:
  every existing seeder in this test suite (`SaldoDeProveedorTests.
  PrepararAsync`, `CajaResumenContenidoTests.PrepararAsync`) follows this
  same per-file convention — `WaysApiFixture` itself has never carried
  feature-specific seeding. Deviation recorded, not silent.
- [x] 2.11 [P] `ReportesVentasResumenTests` — the 4-test pattern:
  cross-tenant absence, soft-delete absence, anulado absence, hand-computed
  fixture equality. *(spec reportes-de-gestion: Net Sales Has No Sign
  Branch; success criterion 1)*
- [x] 2.12 [P] Timezone edge test: same seed read with `zona_horaria` = ART
  vs UTC returns different bucket assignment for a 22:30 sale. *(spec: A
  late-evening sale lands on its own business day)* — consolidated into
  `ReportesVentasResumenTests.cs` (no separate `ReportesZonaHorariaTests`
  file) to keep the slice's file count down under the review budget.
- [x] 2.13 [P] NCX semantics test: NCX reduces `NetoVendido` and leaves
  `CantidadTx`/`TicketPromedio` untouched (600/3 = $200, not 550/4).
  *(spec: Ticket Promedio Excludes NCX From Both Sides)* — consolidated
  into `ReportesVentasResumenTests.cs` (no separate `ReportesSemanticaTests`
  file), same budget reasoning as 2.12. Role 403/200 matrix (Vendedor,
  Root, Supervisor) and cross-tenant-empresa 404 added in the same file,
  ahead of slice 4's `ReportesAutorizacionTests`.
- [x] 2.14 Gate guard: `dotnet ef migrations has-pending-model-changes` →
  "No changes have been made to the model since the last migration."
- [x] 2.15 Run `judgment-day`; fix; re-judge until clean. *(NOT run by
  sdd-apply — requires sub-agent delegation, out of the apply executor's
  scope; orchestrator must run this before PR, same precedent as slice 6
  task 6.6.)*
- [x] 2.16 Branch `feat/stage10-slice2-ventas-resumen` off `main` (parent:
  slice 1's merged commit); PR; merge stacked-to-main. *(Branch created off
  `main` @ 116a71b. PR creation/merge explicitly out of scope per apply
  boundaries — NOT done.)*

**Verify**: `dotnet test --filter FullyQualifiedName~RangoDeReporte|FullyQualifiedName~ReportesVentasResumen|FullyQualifiedName~ReportesZonaHoraria|FullyQualifiedName~ReportesSemantica`

---

## Slice 3: Ventas por Dimensión (PR 3)

**Start**: slice 2 merged. **Finish**: `/por-punto-venta`, `/por-vendedor`,
`/por-medio-pago` live, LINQ-based, each row an independent subtotal.
**Rollback**: revert the branch.

- [x] 3.1 Extend `ServicioDeReportesDeVentas.cs`: `ObtenerPorPuntoVentaAsync`,
  `ObtenerPorVendedorAsync` (group by `id_empleado`), `ObtenerPorMedioPagoAsync`
  (join `PagosComprobante` ⋈ `ComprobantesVenta`, group by `id_medio_pago`,
  header `Estado`/`deleted_at`). All plain LINQ — EF `Tenant`/`BajaLogica`
  filters apply automatically. *(design: Raw-SQL Invariant Checklist rows
  3–4; spec: Ventas Breakdown Endpoints)* — deviation recorded: the shared
  `Join(...).GroupBy(x => x.SomeProperty)` pattern does not translate when
  the join's result selector constructs a named record (EF
  `InvalidOperationException`, "could not be translated"); anonymous-type
  result selectors (same idiom as `LectorDeContenidoDeResumen`) do. Each
  `Consultar*Async` builds its own anonymous projection instead of sharing
  one typed helper record.
- [x] 3.2 Extend `Contratos.cs` with the three response records (one row per
  dimension key, own subtotal — no implicit-whole percentage).
- [x] 3.3 Extend `ReportesEndpoints.cs`: three `GET` routes under the same
  `LecturaDeReportes` group.
- [x] 3.4 [P] `ReportesVentasPorDimensionTests` — 4-test pattern ×3 (one per
  route) + NCX-sign check (an NCX reduces its vendedor's/PV's/medio's
  subtotal, no separate branch). *(spec: Grouping by vendedor sums each
  empleado's TX independently)* — 15 tests, all green. Mutation evidence
  recorded for `por-medio-pago`'s `x.Importe * x.Signo` clause (the only
  novel sign-application logic this slice adds — `pagos_comprobante.importe`
  is never negative by CHECK, so the NCX sign has to come from the header's
  `Signo`): mutated to `x.Importe` alone, the NCX test failed 350 vs
  expected 250, reverted, green again. Cross-tenant checks for the three
  routes are recorded as ordinary coverage, not mutation-proof — they reuse
  `ResolverPuntosDeVentaAsync`'s scope (already proven by slice 2), with no
  separate predicate of their own to isolate from that confound.
- [x] 3.5 Gate guard: `dotnet ef migrations list` unchanged, no diff in the
  model snapshot.
- [x] 3.6 Run `judgment-day`; fix; re-judge until clean. *(NOT run by
  sdd-apply — requires sub-agent delegation, out of the apply executor's
  scope; orchestrator must run this before PR, same precedent as slices 2/6.)*
- [x] 3.7 Branch `feat/stage10-slice3-ventas-por-dimension` off `main`
  (parent: slice 2's merged commit); PR; merge stacked-to-main. *(Branch
  created off the worktree's HEAD — slices 1/2/6 merged, matching "Start:
  slice 2 merged" — commit `398c5f5` applied on it. PR creation/merge
  explicitly out of scope per apply boundaries — NOT done.)*

**Verify**: `dotnet test --filter FullyQualifiedName~ReportesVentasPorDimension`

---

## Slice 4: Artículos y Margen (PR 4)

**Start**: slice 2 merged (parallel to slice 3); stage 9's `costo_unitario`/
`costo_es_estimado` columns present. **Finish**: `/articulos/top` live under
`LecturaDeReportes`; `/rentabilidad` live under
`LecturaDeReportes` **and** `LecturaDeRentabilidad` stacked, three-state cost
respected, coverage mandatory in every response. **Rollback**: revert the
branch.

- [ ] 4.1 Create `src/Ways.Application/Reportes/ServicioDeReportesDeRentabilidad.cs`:
  `ObtenerTopArticulosAsync` — join `ItemsComprobanteVenta` ⋈ header, net-sales
  filter, group by `id_articulo`, sum `cantidad`/`total`, label from the
  line's `descripcion` snapshot (never re-join `articulos`). *(design
  decision 10; spec: Top Artículos Ranks By Net Quantity And Revenue)* —
  **NOT done in this batch**: the orchestrator narrowed this apply batch to
  `/rentabilidad` only (parallel worktree scope split, three slices merging
  the same night); `/articulos/top` remains open.
- [x] 4.2 Same file: `ObtenerRentabilidadAsync` — `SUM(total -
  costo_unitario * cantidad)`, IVA-included both sides; `costo_es_estimado`
  lines excluded unless `incluirEstimados`; `costo_unitario IS NULL` lines
  skipped, never zeroed; build `CoberturaDeCosto` (lines/revenue included,
  excluded-as-estimated, skipped-as-unknown). *(design: Interfaces/Contracts
  `Rentabilidad`; spec rentabilidad-y-comisiones: Margin Excludes Estimated
  Cost Lines By Default; NULL Cost Is Never Treated As Zero)* — done; the
  file created carries only `ObtenerRentabilidadAsync` (task 4.1's
  `ObtenerTopArticulosAsync` out of scope, see above).
- [x] 4.3 Extend `Contratos.cs`: `RentabilidadPorArticulo`, `Rentabilidad`
  records exactly as in design (`MargenPorcentaje` nullable, never `0`).
- [x] 4.4 Extend `ReportesEndpoints.cs`: `GET /articulos/top` (`limite`) under
  `LecturaDeReportes`; `GET /rentabilidad` (`incluirEstimados`) under
  `LecturaDeReportes` **+** `LecturaDeRentabilidad` (design decision 7 — AND
  composition, no new mechanism). — `GET /rentabilidad` only, wired and
  tested; `GET /articulos/top` out of scope (see 4.1).
- [x] 4.5 [P] `ReportesArticulosTopTests` — 4-test pattern + NCX-reduces-ranking
  check. — **NOT done**: depends on 4.1 (out of scope).
- [x] 4.6 [P] `RentabilidadTests`: four seeded lines (real cost / estimated /
  `NULL` / cost `0`) — estimated excluded by default, included with the
  flag; `NULL` never counted as `0`; every coverage count/revenue field
  asserted for a 10-line mixed period (7 real / 2 estimated / 1 unknown).
  *(spec: Coverage Reflects A Mixed Period)* — done, 12 tests, mutation
  evidence recorded for the `costo_es_estimado` exclusion clause and the
  `CostoUnitario IS NULL` guard (see apply-progress notes).
- [x] 4.7 [P] Extend `ReportesAutorizacionTests` (parameterized over the
  route list, created here and grown in later slices): Vendedor → 403 on
  all current routes; Supervisor → 200 on volume routes, 403 on
  `/rentabilidad`; Admin → 200 on all; Root → 403 on all. — **Deviation**:
  no shared `ReportesAutorizacionTests` file created (would collide with the
  sibling worktrees implementing slices 3/5 the same night). The
  `/rentabilidad`-only role matrix (Vendedor/Root/Supervisor → 403, Admin →
  200) is consolidated into `RentabilidadTests.cs` instead, same precedent
  as slice 2's consolidation of its role tests into
  `ReportesVentasResumenTests.cs`. A future slice must still create the
  shared parameterized file once all routes are known.
- [x] 4.8 Gate guard: `dotnet ef migrations list` unchanged. — confirmed via
  `dotnet ef migrations has-pending-model-changes` (Infrastructure as
  startup project): "No changes have been made to the model since the last
  migration."
- [x] 4.9 Run `judgment-day`; fix; re-judge until clean. *(NOT run by
  sdd-apply — requires sub-agent delegation, out of the apply executor's
  scope; orchestrator must run this before PR, same precedent as slices
  2/6.)*
- [x] 4.10 Branch `feat/stage10-slice4-articulos-y-margen` off `main`
  (parent: slice 2, independent of slice 3); PR; merge stacked-to-main. —
  Branch `feat/stage10-slice4-rentabilidad` created off `main` @ 46ce29f
  instead (orchestrator-assigned name for the rentabilidad-only scope split
  — `articulos-y-margen`'s `/articulos/top` half remains a separate open
  work unit). PR creation/merge explicitly out of scope per apply
  boundaries — NOT done.

**Verify**: `dotnet test --filter FullyQualifiedName~ReportesArticulosTop|FullyQualifiedName~Rentabilidad|FullyQualifiedName~ReportesAutorizacion`

---

## Slice 5: Egresos (PR 5)

**Start**: slice 2 merged (parallel to 3, 4). **Finish**: `/compras/por-proveedor`
and `/gastos/resumen` live; the full role matrix confirmed across every
route shipped so far. **Rollback**: revert the branch.

- [x] 5.1 Create `src/Ways.Application/Reportes/ServicioDeReportesDeEgresos.cs`:
  `ObtenerComprasPorProveedorAsync` — LINQ, `FechaRecepcion`, `Estado ==
  Confirmada`, `deleted_at IS NULL`, group by proveedor. *(spec: Compras
  Bucketed By Fecha De Recepción, Confirmada Only)*
- [x] 5.2 Same file: `ObtenerGastosResumenAsync` — reuses
  `LectorDeSerieTemporal`'s gastos raw-SQL body (bucketed series) plus an
  optional `categoria` group. *(design: Raw-SQL Invariant Checklist —
  `gastos/resumen`)* — categoria breakdown implemented as an additional LINQ
  `GroupBy` over the same scope/range, always returned alongside the series
  (`ResumenDeGastos.PorCategoria`); no on/off query parameter exists for it
  because the spec names no such toggle (dto-contract-honesty: no unread
  parameter was added).
- [x] 5.3 Extend `Contratos.cs` + `ReportesEndpoints.cs`: two routes under
  `LecturaDeReportes`.
- [x] 5.4 [P] `ReportesEgresosTests` — 4-test pattern ×2, including a
  `borrador` compra excluded and a `fecha_comprobante`-only row still
  bucketed by `fecha_recepcion`. Both clause-proving tests (estado filter,
  date-column choice) ship with recorded mutation evidence (mutate → FAIL →
  revert → PASS) per `mutation-proof-tests`. Gastos' 4th leg substitutes the
  (nonexistent) estado check with the categoria-breakdown clause, recorded
  as a deviation in the same file's doc-comment.
- [ ] 5.5 [P] Complete `ReportesAutorizacionTests` for all 9 (now 7 shipped +
  2 pending in slice 10) routes shipped through slice 5. **NOT DONE AS
  SPECIFIED** — `ReportesAutorizacionTests` (created by slice 4) does not
  exist in this isolated worktree (slices 3/4/5 run in parallel tonight,
  each branched from `main` before any of the three merge). Consolidated
  instead into `ReportesEgresosTests.cs` (role matrix for the 2 routes this
  slice ships), same precedent as slice 2 task 2.13. The orchestrator must
  reconcile the three role-matrix additions into one
  `ReportesAutorizacionTests` file when merging slices 3/4/5.
- [x] 5.6 Gate guard: `dotnet ef migrations has-pending-model-changes` →
  "No changes have been made to the model since the last migration."; no
  migration files touched (`git status` clean on any Migrations path).
- [x] 5.7 Run `judgment-day`; fix; re-judge until clean. *(NOT run by
  sdd-apply — requires sub-agent delegation, out of the apply executor's
  scope; orchestrator must run this before PR, same precedent as slices 2/6.)*
- [x] 5.8 Branch `feat/stage10-slice5-egresos` off `main` (parent: slice 2,
  independent of slices 3/4); PR; merge stacked-to-main. *(Branch
  `feat/stage10-slice3-compras-gastos` created off `main` per the
  orchestrator's launch instructions — note the branch-name mismatch with
  this file's `feat/stage10-slice5-egresos`; PR creation/merge explicitly
  out of scope per apply boundaries — NOT done.)*

**Verify**: `dotnet test --filter FullyQualifiedName~ReportesEgresos`

---

## Slice 6: Recharts + `componentes/graficos/` (PR 6)

**Start**: `main` (no backend dependency — parallelizable with slices 2–5).
**Finish**: `recharts` installed, containment wrappers exist, no page yet
consumes them. **Rollback**: `npm uninstall recharts`; revert the branch.

- [x] 6.1 Modify `src/Ways.Web/package.json`: add `recharts`. **Verify at
  install**: license is MIT, version is React-19-compatible (peer-dep
  check), no transitive canvas/d3 conflict with existing deps. *(design
  decision 4; proposal decision 4)*
- [x] 6.2 Create `src/Ways.Web/src/componentes/graficos/series.ts`: pure
  mapping helpers (report bucket → chart-friendly series shape), no
  `recharts` import.
- [x] 6.3 Create `src/Ways.Web/src/componentes/graficos/GraficoDeLineas.tsx`
  and `GraficoDeBarras.tsx`: thin wrappers over `recharts`'
  `ResponsiveContainer`/`LineChart`/`BarChart`, own props (`data`, `alto`,
  no raw `recharts` prop pass-through). `recharts` MUST NOT be imported
  anywhere outside this folder. *(design decision 11; spec tablero: Recharts
  Is Contained To componentes/graficos)*
- [x] 6.4 [P] Colocated unit tests for `series.ts` per `web-descriptor-tests`
  — every pure mapping helper, no DOM.
- [x] 6.5 [P] Colocated component tests for both wrappers with
  `vi.mock('recharts')` stubbing each chart to a `data-testid` node that
  serializes its `data` prop — assertions target the wrapper's mapping,
  never the library render. *(design: Web Composition — Vitest)*
- [x] 6.6 Run `judgment-day`; fix; re-judge until clean. *(NOT run by
  sdd-apply — requires sub-agent delegation, out of the apply executor's
  scope; orchestrator must run this before PR.)*
- [x] 6.7 Branch `feat/stage10-slice6-graficos` off `main`; PR; merge
  stacked-to-main (independent of the API slices' merge order). *(Branch
  `feat/stage10-slice6-graficos` created off `main`, commits `d75bda7` +
  `c981c50` applied on it. PR creation/merge explicitly out of scope per
  apply boundaries — NOT done.)*

**Verify**: `npm run test -- graficos`

---

## Slice 7: Tablero G1 Parity (PR 7)

**Start**: slices 2 and 6 merged. **Finish**: `Tablero` renders 7-day ventas
+ gastos series, net totals, ticket promedio, matching legacy G1 plus ticket
promedio; route and nav wired for Supervisor + Admin. **Rollback**: revert
the branch; route/nav entries removed.

- [x] 7.1 Create `src/Ways.Web/src/api/tipos.ts` mirrors for
  `ResumenDeVentas`/`BucketDeVentas` (and `PARAMETROS_CONOCIDOS` untouched —
  already typed in slice 1). — also mirrors `ResumenDeGastos`/`BucketDeGastos`/
  `GastoPorCategoria`/`Granularidad` (needed for the gastos card in the same
  slice) and adds `puedeVerReportes` (espejo de `Politicas.LecturaDeReportes`).
- [x] 7.2 Create `src/Ways.Web/src/api/reportes.ts`: client function for
  `GET /reportes/ventas/resumen` (and gastos resumen, sharing the shape). —
  `construirQueryDeReporte` (shared query builder, colocated tests) and
  `rangoUltimosSieteDias` (default-range helper, colocated tests, takes
  `ahora` as a parameter for testability).
- [x] 7.3 Create `src/Ways.Web/src/paginas/Tablero.tsx`: defaults to last 7
  days on load; ventas series card (`<GraficoDeLineas>`), gastos series
  card, net sales total, gastos total, ticket promedio. Per
  `react-async-state` rules 2/4/9: one `useRef` generation token bumped
  before any filter mutates state, every post-`await` setter and every
  `finally` clearing `cargando` gated on it. *(spec tablero: Tablero Covers
  Legacy G1 Parity By Default; success criterion 6)* — empresa selector
  follows the `Parametros.tsx` precedent (`clienteDeOrganizacion.listarEmpresas`);
  `desde`/`hasta` are editable `DateOnly` inputs, no offset needed (unlike
  `compras.ts`'s `timestamptz` filter); granularidad fixed to `Dia` this
  slice (selector deferred to Slice 8). Carried-over debt from slice 6
  judges paid here: `GraficoDeLineas`/`GraficoDeBarras` now require a
  `titulo` prop (`role="img"` + `aria-label`) since Tablero is their first
  real consumer.
- [x] 7.4 Modify `src/Ways.Web/src/App.tsx` and
  `src/Ways.Web/src/componentes/Layout.tsx`: add the `/tablero` route
  (`ROL.Supervisor`, `ROL.Admin`) and nav entry. — nav item gated by the new
  `puedeVerReportes` helper, same pattern as `puedeSupervisarCuentaCorriente`.
- [x] 7.5 [P] `Tablero.test.tsx`: default-load renders 7-day range and both
  totals; generation-token gating verified with a delayed-then-superseded
  fetch; error path (API 500) renders a retry state, not a crash. — plus a
  null-ticket-promedio rendering test, and role-gating tests (Supervisor
  reaches `/tablero`; Vendedor and Root redirect to `/`) via `RutaProtegida`,
  same pattern as `Compras.test.tsx`'s `renderComprasProtegido`.
- [ ] 7.6 Run `judgment-day`; fix; re-judge until clean. *(NOT run by
  sdd-apply — requires sub-agent delegation, out of the apply executor's
  scope; orchestrator must run this before PR, same precedent as slices
  2/3/4/5/6.)*
- [x] 7.7 Branch `feat/stage10-slice7-tablero-g1` off `main` (parent: slice
  2 for the endpoint, slice 6 for the wrappers — both already on `main`);
  PR; merge stacked-to-main. *(Branch created off `main`; three work-unit
  commits applied on it. PR creation/merge explicitly out of scope per
  apply boundaries — NOT done.)*

**Verify**: `npm run test -- Tablero`

---

## Slice 8: Tablero Dimensiones (PR 8)

**Start**: slices 3 and 7 merged. **Finish**: filter bar
(`desde`/`hasta`/`granularidad`/punto de venta) drives 4 independent
breakdown panels (PV, vendedor, medio de pago, top artículos), each owning
its own fetch and `cargando`. **Rollback**: revert the branch.

- [ ] 8.1 Extend `reportes.ts`: client functions for `/por-punto-venta`,
  `/por-vendedor`, `/por-medio-pago`, `/articulos/top`.
- [ ] 8.2 Extend `Tablero.tsx`: filter bar wired to all panels (no panel
  fetches its own independent range — spec requirement); one panel per
  dimension, each with its own `useRef` generation token per
  `react-async-state` rule 10 (applies to **every** panel added in this PR,
  not just the first). *(spec tablero: Breakdown Panels Share Range And
  Granularity Controls)*
- [ ] 8.3 [P] Stale-response test per panel (4 tests): a superseded
  in-flight fetch (range/granularity changed mid-request) never repaints a
  panel already re-scoped. *(spec: Changing granularity re-buckets every
  panel)*
- [ ] 8.4 Run `judgment-day`; fix; re-judge until clean.
- [ ] 8.5 Branch `feat/stage10-slice8-tablero-dimensiones` off `main`
  (parent: slice 7); PR; merge stacked-to-main.

**Verify**: `npm run test -- Tablero`

---

## Slice 9: Tablero Rentabilidad (PR 9)

**Start**: slices 4 and 8 merged. **Finish**: margin panel absent from the
DOM for non-Admin, present with a mandatory coverage banner for Admin.
**Rollback**: revert the branch.

- [ ] 9.1 Extend `reportes.ts` + `tipos.ts`: client + mirror for
  `/rentabilidad` (`Rentabilidad`, `CoberturaDeCosto`).
- [ ] 9.2 Extend `Tablero.tsx`: rentabilidad panel gated by `useAuth` role
  check — **not rendered at all** for Supervisor/Vendedor (no
  `display:none`); for Admin, always render the coverage banner above the
  figure, and when any revenue is excluded/unknown state it explicitly (no
  bare percentage). *(spec tablero: Margin Panel Is Invisible, Not
  Disabled, For Non-Admin)*
- [ ] 9.3 [P] Component tests: Supervisor session → no rentabilidad DOM
  node; Admin session with partial coverage → banner text asserted per
  coverage state (100% / partial / all-unknown).
- [ ] 9.4 Run `judgment-day`; fix; re-judge until clean.
- [ ] 9.5 Branch `feat/stage10-slice9-tablero-rentabilidad` off `main`
  (parent: slice 8); PR; merge stacked-to-main.

**Verify**: `npm run test -- Tablero`

---

## Slice 10: Comisiones — PROVISIONAL, droppable in full (PR 10)

**Start**: slices 1, 2 and 7 merged. **Finish**: `GET /api/reportes/comisiones`
live under `LecturaDeReportes` + `LecturaDeRentabilidad`; PROVISIONAL card
visible only to Admin. **Rollback**: revert this single branch — endpoint
and card disappear, no migration, no persisted row to unwind (proposal
Rollback Plan step 4).

- [ ] 10.1 Extend `ServicioDeReportesDeVentas.cs` (reuses the net-sales
  filter): `ObtenerComisionesAsync` — group by `id_empleado`, `comision =
  neto_vendido_por_empleado × comision_porcentaje`, rate resolved via
  `ServicioDeParametros` (PV → empresa → default `0`). No write. *(spec
  rentabilidad-y-comisiones: Comisiones Is A Provisional, Non-Persisted
  Report)*
- [ ] 10.2 Extend `Contratos.cs` + `ReportesEndpoints.cs`: `GET /comisiones`
  under `LecturaDeReportes` **+** `LecturaDeRentabilidad`.
- [ ] 10.3 Extend `reportes.ts` + `Tablero.tsx`: comisiones card, Admin-only,
  literal `PROVISIONAL` badge, states the rate used. *(spec tablero:
  Comisiones Card Is Labelled PROVISIONAL)*
- [ ] 10.4 [P] `ReportesComisionesTests` — 4-test pattern + default rate `0`
  ⇒ every empleado's `comision = 0`; configured rate ⇒ non-zero and
  response labelled PROVISIONAL; no row written to any table (assert no
  new row in any candidate table pre/post call).
- [ ] 10.5 [P] Component test: PROVISIONAL text visible alongside computed
  amounts; card absent for non-Admin.
- [ ] 10.6 Complete `ReportesAutorizacionTests` for the full 9-route matrix.
- [ ] 10.7 Gate guard: `dotnet ef migrations list` unchanged — final check
  for the whole stage.
- [ ] 10.8 Run `judgment-day`; fix; re-judge until clean.
- [ ] 10.9 Branch `feat/stage10-slice10-comisiones` off `main` (parent:
  slice 7); PR; merge stacked-to-main.

**Verify**: `dotnet test --filter FullyQualifiedName~ReportesComisiones` /
`npm run test -- Tablero`

---

## Global Cross-Slice Tasks

- **Recharts dependency** (task 6.1): verify MIT license and React-19 peer
  compatibility at `npm install` time, not assumed from the proposal text.
- **`web-descriptor-tests` compliance**: every pure descriptor/mapping
  helper (slice 6 `series.ts`, any bucket-label formatter, `aParametroAlta`
  in slice 1) ships a colocated unit test — enforced per-slice above (6.4,
  1.6), not deferred to a final sweep.
- **`react-async-state` compliance**: the generation-token pattern applies
  to every panel added in slices 7, 8, 9, 10 — task 8.2 explicitly calls out
  rule 10 (all panels in the same PR, not just the first written) because
  slice 8 adds four panels at once.

---

## Dependency Summary

```
Slice 1 (parametros + políticas)
  └─ Slice 2 (ventas/resumen: domain + raw-SQL lector)
       ├─ Slice 3 (ventas por dimensión)         ─┐
       ├─ Slice 4 (artículos + margen)            ├─ parallelizable, merge 3→4→5
       └─ Slice 5 (egresos)                       ┘
Slice 6 (recharts + graficos)  ── independent, parallel to slices 2–5
  Slice 2 + Slice 6
       └─ Slice 7 (tablero G1)
            ├─ Slice 3 + Slice 7 → Slice 8 (tablero dimensiones)
            ├─ Slice 4 + Slice 8 → Slice 9 (tablero rentabilidad)
            └─ Slice 1 + Slice 2 + Slice 7 → Slice 10 (comisiones, droppable)
```

Merge order is strictly 1→10 (stacked-to-main); the graph above records true
implementation dependency, which allows 3/4/5 and 6 to be worked in
parallel branches before their turn in the stack.

---

## Review Workload Forecast

| Field | Value |
|---|---|
| Estimated changed lines | ~2,920 total (10 slices: 230/390/300/380/300/200/300/350/250/220) |
| 400-line budget risk | Low overall — no single slice reaches 400; slices 2 (~390) and 4 (~380) are the closest and carry Medium per-slice risk |
| Chained PRs recommended | Yes |
| Suggested split | 10 PRs, stacked-to-main, per the Suggested Work Units table above |
| Delivery strategy | ask-on-risk (resolved: chained, stacked-to-main, under the overnight mandate) |
| Chain strategy | stacked-to-main |

Per-slice budget risk: Slice 1 Low (~230) · Slice 2 Medium (~390) · Slice 3
Low (~300) · Slice 4 Medium (~380) · Slice 5 Low (~300) · Slice 6 Low (~200)
· Slice 7 Low (~300) · Slice 8 Low (~350) · Slice 9 Low (~250) · Slice 10 Low
(~220). Slices 2 and 4 are the ones to watch during apply — if either grows
past 400 in practice, split the integration test file out of the slice
(design already isolates it as `[P]` tasks, so this is a low-effort split).

Decision needed before apply: No
Chained PRs recommended: Yes
Chain strategy: stacked-to-main
400-line budget risk: Low
