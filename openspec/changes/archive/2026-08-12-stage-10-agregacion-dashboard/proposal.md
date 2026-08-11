# Proposal: Stage 10 — Capa de agregación server-side + dashboard

## Intent

Implement Etapa 10 of `docs/11-programa-post-paridad.md` (doc-11:98-119) — give the system its
first server-side aggregation layer and a web dashboard on top of it.

Today **the system aggregates nothing**. Every endpoint under `src/Ways.Api/Endpoints/` is a CRUD
handler or a paginated list; the only derived value in the whole API is the saldo de proveedor. The
schema already answers every question this stage asks — `comprobantes_venta` carries `fecha`,
`id_punto_venta`, `id_empleado`, `id_turno_caja`, `total` and `estado`; `items_comprobante_venta`
carries `id_articulo`, `cantidad`, `total` and now `costo_unitario` (stage 9);
`pagos_comprobante` carries the medio de pago; `comprobantes_compra` and `gastos` carry the outflow
side. What is missing is the layer that asks them.

The legacy floor is screen G1 (sales and expenses for the last 7 days). This stage covers it and
goes past it: period/vendedor/punto de venta/medio de pago breakdowns, ticket promedio, top
artículos, compras, gastos and **real margin** — the payoff of stage 9's frozen cost.

This is the stage that turns captured data into decisions. Everything after it in the program gets
justified or discarded by what it shows.

## Scope

### In Scope

- A read-only aggregation layer (`src/Ways.Application/Reportes/`) built on **direct parameterized
  SQL** over the existing tables — no materialized view, no new table, **no migration** (decision 1).
- Explicit per-report endpoints under `/api/reportes/*` (decision 7): ventas resumen (serie
  temporal + totales + ticket promedio), ventas por punto de venta / vendedor / medio de pago, top
  de artículos, rentabilidad (margen), compras por proveedor, gastos.
- Business-day bucketing (`dia` / `semana` / `mes`) in the punto de venta's timezone, resolved
  through a new `ParametroConocido` key — no schema change (decision 2).
- Two new authorization policies: `LecturaDeReportes` and `LecturaDeRentabilidad` (decision 3).
- `src/Ways.Web`: one chart library (**Recharts**, decision 4) behind a thin wrapper, and a
  `Tablero` page that covers G1 and then the breakdown panels.
- Comisiones por vendedor as a **provisional, computed, droppable** report in the last slice
  (decision 5).
- Tests: pure domain tests for range/bucket resolution, integration tests per endpoint (including
  cross-tenant and soft-delete guards), colocated vitest tests per the `web-descriptor-tests` skill.

### Out of Scope

- **Any database change.** No table, no column, no index, no view, no migration (see *Modelo de
  datos propuesto*).
- **Exporting anything.** Excel/CSV/PDF is stage 11, and stage 11 exists precisely because this
  dashboard raises the question. No download endpoint ships here.
- **Activating recargo por medio de pago** (decision 8).
- Persisted commission liquidación, per-empleado commission rates, commission by article or by
  margin (decision 5).
- Comparisons against previous periods, goals/targets, forecasting, alerting, scheduled reports.
- Per-user dashboard layout configuration, saved filters, widget reordering.
- Cross-empresa (consolidated) reporting: every report is scoped to one empresa, like every other
  endpoint in the system.
- Any change to a write path — checkout, compras, gastos, caja and cuenta corriente are untouched.

## Capabilities

### New Capabilities

- `reportes-de-gestion`: the aggregation contract — endpoint shapes, empresa/punto de venta
  scoping, business-day bucketing and timezone resolution, which comprobantes count and with which
  sign, ticket promedio definition, the soft-delete/anulado exclusions, and the `LecturaDeReportes`
  gate.
- `rentabilidad-y-comisiones`: the margin contract — the stage-9 three-state cost (real / estimated
  / unknown), default exclusion of estimated lines with explicit opt-in, mandatory coverage
  reporting instead of silent averaging, the `LecturaDeRentabilidad` gate, and the **provisional**
  commission formula.

### Modified Capabilities

- `parametros-operativos`: two new known keys — `zona_horaria` (string, default
  `America/Argentina/Buenos_Aires`) and `comision_porcentaje` (decimal, default `0`), both
  resolved with the existing punto de venta → empresa → declared-default precedence.

## Approach

One read layer, one endpoint per question, one page. The aggregation lives in
`Ways.Application/Reportes/` as parameterized SQL executed on the same `DbContext` connection, so
**RLS keeps enforcing tenant isolation** (`InterceptorDeContextoDeTenant` sets `app.acceso` /
`app.tenant_id` on every connection open). The pure part — resolving `desde`/`hasta`/granularity/
timezone into UTC boundaries and bucket labels — is a DB-free Domain type, testable like
`PoliticaDeRoles`.

**Binding invariant (raw SQL discipline).** Raw SQL does **not** get EF's global query filters.
Every report query MUST spell out `deleted_at IS NULL` and its estado filter explicitly. This is a
verify criterion, not a convention: each endpoint ships a soft-delete test and a cross-tenant test.

## Autonomous decisions

> The owner delegated these to the orchestrator (2026-08-11, overnight). Each is a founded
> recommendation with a conservative, reversible bias. **Nothing here is expensive to walk back**
> — the stage adds no schema and no persisted state.

**Decision 1 — Direct parameterized SQL. No materialized views.** Three reasons, the first
decisive: (a) **a materialized view is an RLS hole in this architecture**. Every tenant table is
`FORCE ROW LEVEL SECURITY` with `USING (app_es_plataforma() OR id_tenant = app_tenant_actual())`,
but RLS cannot be enabled *on* a matview and a matview does not apply its base tables' policies to
the querying role (`security_invoker` is a *view* feature, not a matview one). An MV over
`comprobantes_venta` would materialize every tenant's rows into an object with no tenant filter —
a cross-tenant leak in a shared-database SaaS. (b) **Freshness.** A dashboard whose "ventas de hoy"
card lags the last ticket destroys the trust of the person who just rang it up; a refresh schedule
is a support burden this stage does not need. (c) **Volume.** The whole point of the existing
`ix_comprobantes_venta_punto_venta_fecha` / `ix_comprobantes_compra_punto_venta_fecha` /
`ix_gastos_punto_venta_fecha` composites is exactly this access pattern, at a volume measured in
thousands of rows per store-year. Reversible: if a report ever becomes slow, a **regular** view or
an indexed rollup is an additive change made against measured evidence.

**Decision 2 — The day cut belongs to the punto de venta, not to the viewer or the server.**
`ComprobanteVenta.Fecha` is `DateTimeOffset` persisted as `timestamptz` from
`IRelojDelSistema.Ahora` (= `DateTimeOffset.UtcNow`), and the web renders it with
`toLocaleString('es-AR')` — i.e. the browser's zone. Bucketing in UTC would move every sale
after 21:00 ART into the next business day: the evening block, systematically. Bucketing in
`TimeZoneInfo.Local` (the pattern `ServicioDeOfertas.cs:478-483` documents as a v1 shortcut) is
worse in production — the container runs UTC, so it silently *is* the UTC cut. Bucketing in the
*viewer's* browser zone is also wrong: the store's business day is a property of the store, not of
whoever is looking. **Resolution:** a new `ParametroConocido` key `zona_horaria` (string, default
`America/Argentina/Buenos_Aires`), resolved punto de venta → empresa → default, applied as
`date_trunc(<granularidad>, fecha AT TIME ZONE <zona>)`. `ParametroConocido` is explicitly
open-ended ("agregar una clave nueva es agregar una entrada acá, no una migración",
`ParametroConocido.cs:11-13`), so this costs two lines and zero schema. Granularity exposed:
`dia`, `semana` (ISO, Monday-start), `mes`. *Gotcha for design*: this is the first `string`-typed
parametro and `ValidarTipo` deserializes the stored value as JSON — the value must be stored
quoted.

**Decision 3 — Two policies; margin starts Admin-only.** `LecturaDeReportes` = **Supervisor +
Admin** for the volume/operational reports; `LecturaDeRentabilidad` = **Admin only** for cost,
margin and commission. Root is out of both, consistent with `GestionDeCatalogo` and
`OperacionDePos` ("root administra tenants, no opera ninguno"). Vendedor is out of both, which
completes stage-9 decision 5 (cost never reaches the cashier). Why Admin-only for margin rather
than Supervisor+Admin: purchase cost is the most socially sensitive number in a small business,
and the asymmetry is total — **widening a policy later is one line; un-showing a number people
have already seen is not a technical problem at all**. `SupervisionDeCuentaCorriente` is the
precedent for adding a narrow policy rather than reusing a broad one.

**Decision 4 — Recharts.** MIT, React 19-compatible, declarative React components (matches the
house component style), renders **SVG** so it works under jsdom/RTL with an explicit width/height
— unlike canvas libraries, which need a `canvas` mock in the existing vitest setup. Nothing in the
repo contradicts it: `package.json` has no chart, canvas, d3 or visualization dependency, and no
license constraint exists. Alternatives rejected: Chart.js/react-chartjs-2 (canvas → test friction),
ECharts (bundle far past what a 4-chart dashboard justifies), visx (a toolkit, not a chart — more
code to write and to review), Nivo (heavier, same job). **Containment:** Recharts is imported only
inside `src/componentes/graficos/`; pages consume our own `<GraficoDeBarras>` / `<GraficoDeLineas>`
props. Swapping the library later touches one folder.

**Decision 5 — Comisiones ship as a PROVISIONAL report, in the last slice, designed to be
deleted.** The doc is explicit that the formula is a business decision (doc-11:118-119) and the
owner is not available to make it. Deferring the whole thing would leave a hole in the stage;
inventing a persisted liquidación would create data the owner never agreed to. The middle path:
compute `comision = neto_vendido_por_empleado × comision_porcentaje`, where the rate is a new
`ParametroConocido` (`comision_porcentaje`, decimal, default `0` — i.e. **off until someone sets
it**), over TX totals net of NCX, excluding anulados, per `id_empleado`. Nothing is persisted, no
schema exists, the endpoint and card are labelled **PROVISIONAL** in the spec and in the UI, and
the whole thing is the last slice of the chain — if the owner rejects the formula, slice 8 is
dropped or reverted with no migration and no data to unwind.

**Decision 6 — Dashboard scope: API first, then web, G1 parity before anything new.** See *Plan de
slices*. Slice 5 is the first thing the owner can look at and it is deliberately the legacy G1
scope (ventas y gastos de los últimos 7 días) plus ticket promedio, so parity is demonstrable
before breadth is added. Rentabilidad is slice 7 and comisiones slice 8 — the two most reversible
items are last on purpose.

**Decision 7 — Explicit per-report endpoints. No generic aggregation/query endpoint.** A generic
`?groupBy=&metric=&filter=` surface cannot be role-gated per dimension (margin has a different
policy than volume — decision 3), cannot be written as Given/When/Then scenarios, cannot be indexed
against, and turns every future report into an untested combination. Typed request/response records
per report, following `dto-contract-honesty` (every accepted parameter has a use or does not
exist). The cost is more endpoints; the benefit is that each one is specifiable, gateable and
testable.

**Decision 8 — Recargo por medio de pago is NOT activated here.** `MedioPago.RecargoPorcentaje`
exists but no write path applies it: no recargo amount is stored anywhere, so there is nothing to
aggregate. Doc-11's backlog line ("Etapa 10 lo expone en los agregados; su activación es un cambio
menor previo") is resolved conservatively — activation is a change to the **checkout write path**,
the most guarded code in the project, and it does not belong in a read-only stage. The medio de
pago report reads `pagos_comprobante.importe` exactly as stored, and will report recargo
automatically the day the amounts include it.

**Decision 9 — Aggregation semantics, pinned (verified in code, not assumed).**
- *Net sales* = `SUM(comprobantes_venta.total)` over `tipos_comprobante.clase = venta`,
  `estado <> 'anulado'`, `deleted_at IS NULL`. **No sign branch is needed**: NCX lines carry a
  negative `cantidad` (`CalculadorDeTotales.cs:5-8,32-42`), so an NCX header total is already
  negative and the sum is net of returns by construction.
- *Ticket promedio* = TX total ÷ TX count, **NCX excluded from both numerator and denominator**. A
  credit note is not a ticket; mixing it into the denominator produces a number that means nothing.
  Net sales and ticket promedio are therefore reported side by side, not derived from each other.
- *Compras* bucket by `fecha_recepcion` with `estado = confirmada` — server-authoritative, never
  NULL on a confirmed compra, the moment stock and cost actually moved, and the column the existing
  `ix_comprobantes_compra_punto_venta_fecha` already leads with. `fecha_comprobante` (the supplier's
  invoice date, `DateOnly`) is the fiscal view and is deferred to stage 11.
- *Margen* = `SUM(total − costo_unitario × cantidad)` over item lines, IVA-included on both sides,
  **binding from stage-9 decision 1**. Lines with `costo_es_estimado = true` are **excluded by
  default** (stage-9 decision 2) behind an explicit `incluirEstimados` flag. Lines with
  `costo_unitario IS NULL` are **never treated as zero** (stage-9 decision 4): every margin
  response carries coverage — how many lines and how much revenue were included, excluded as
  estimated, and skipped as unknown. A margin figure without its coverage is a lie with a decimal
  point.

## Modelo de datos propuesto

> **DB CHANGE GATE.** Presented for the record. **This stage proposes NO database change of any
> kind.**

- **No new table, view, materialized view, enum, column, constraint, index, FK or RLS policy.**
- **No change to any existing schema object.**
- **No migration is generated.** If `sdd-apply` finds itself writing one, the gate reopens.
- **No data statement.** The two new `ParametroConocido` entries are **code** (`ParametroConocido.cs`
  is a typed registry, explicitly documented as extensible without a migration); the corresponding
  `parametros` rows are ordinary tenant data written through the existing ABM, and both keys have
  declared defaults so **zero rows are required** for the stage to work.

**Index review (why none is proposed).** The three access patterns this stage introduces are
already covered by composite indexes whose leading columns match:
`ix_comprobantes_venta_punto_venta_fecha` `(id_punto_venta, id_tenant, fecha)`,
`ix_comprobantes_compra_punto_venta_fecha` `(id_punto_venta, id_tenant, fecha_recepcion)`,
`ix_gastos_punto_venta_fecha` `(id_punto_venta, id_tenant, fecha)`; the item-level join uses
`ix_items_comprobante_venta_comprobante` and groups on `ix_items_comprobante_venta_articulo`.

**Recorded future candidate, deliberately not proposed now:** an empresa-wide (all puntos de venta)
range scan currently reaches `comprobantes_venta` through a per-PV bitmap scan. If a real
`EXPLAIN (ANALYZE)` on production-shaped volume ever shows that hurting, the additive index is
`ix_comprobantes_venta_tenant_fecha (id_tenant, fecha)` — one index-only migration, its own gate,
its own slice. Adding it on speculation would be a schema change bought with no evidence.

## Affected Areas

| Area | Impact | Description |
|---|---|---|
| `src/Ways.Domain/Reportes/` | New | Pure range/bucket/timezone resolution + margin coverage types; DB-free, unit-tested |
| `src/Ways.Domain/Catalogos/ParametroConocido.cs` | Modified | Two entries: `zona_horaria`, `comision_porcentaje` |
| `src/Ways.Application/Reportes/` | New | Read services + parameterized SQL + response contracts |
| `src/Ways.Api/Endpoints/ReportesEndpoints.cs` | New | `/api/reportes/*`, one route per report |
| `src/Ways.Api/Seguridad/Politicas.cs` | Modified | `LecturaDeReportes`, `LecturaDeRentabilidad` |
| `src/Ways.Web/package.json` | Modified | `recharts` (single new runtime dependency) |
| `src/Ways.Web/src/componentes/graficos/` | New | Recharts containment wrappers |
| `src/Ways.Web/src/paginas/Tablero.tsx` | New | Dashboard page + panels |
| `src/Ways.Web/src/api/` | Modified | Report client + `tipos.ts` mirrors |
| `openspec/specs/` | New/Modified | 2 new capabilities + `parametros-operativos` delta |
| `docs/11-programa-post-paridad.md` | Modified | Stage annotation at close |
| **Database** | **Untouched** | See *Modelo de datos propuesto* |
| Every write path (`ServicioDeVentas`, compras, gastos, caja, CC) | **Untouched** | Read-only stage |

## Risks

| Risk | Likelihood | Mitigation |
|---|---|---|
| Raw SQL misses `deleted_at IS NULL` / `estado` and silently inflates a total | **High** | Binding invariant above; a soft-delete and an anulado test per endpoint — a wrong number that looks right is this stage's worst failure |
| Raw SQL leaks across tenants (no EF query filter) | Med | RLS still applies (connection GUCs); a cross-tenant integration test per endpoint proves it rather than assuming it |
| Margin computed over estimated/unknown costs and read as truth | **High** | Decision 9: default exclusion + mandatory coverage in every response + a UI banner, never a bare percentage |
| Timezone bucketing gets it wrong and the evening moves to the next day | Med | Decision 2 + a domain test with a 21:00-ART sale asserting it lands on its own business day |
| Chart library becomes load-bearing and hard to replace | Low | Decision 4 containment: Recharts imported only under `componentes/graficos/` |
| Provisional commission formula treated as agreed policy | Med | Decision 5: default rate `0`, PROVISIONAL label in spec and UI, last slice, nothing persisted |
| Scope creep into stage 11 ("just add a CSV button") | Med | Out of Scope is explicit; no endpoint returns a file |
| Web slices exceed the 400-line review budget | Med | Slices 5-7 split by panel, not by layer; forecast reconciled in `sdd-tasks` |

## Rollback Plan

1. **Before merge**: revert the branch. Nothing external changed.
2. **After merge, before deploy**: revert the commits.
3. **After deploy**: revert. **There is nothing to undo in the database** — no migration, no
   schema object, no written row. Any `parametros` rows an admin created stay as inert data under
   keys the code no longer knows; deleting them is one ABM action.
4. **Partial rollback**: each slice is an independent PR. Dropping slice 8 (comisiones) or slice 7
   (rentabilidad) removes an endpoint and a panel and leaves the rest of the dashboard working.

## Dependencies

- **Stage 9** (`costo-congelado`) — required for the margin dimension only (slice 7 and the margin
  part of slice 3). Every other aggregate is independent of it, so slices 1-6 are not blocked if
  stage 9 is still in flight.
- Recharts (npm, MIT) — the only new third-party dependency in the stage.
- **Stage 11 depends on this one** for the reports it will make downloadable.

## Success Criteria

- [ ] `GET /api/reportes/ventas/resumen` returns, for a date range and granularity, a bucketed
      series plus net sales, TX count and ticket promedio — matching a hand-computed fixture.
- [ ] A sale emitted at 21:30 ART lands in **that** business day's bucket, not the next.
- [ ] An anulado comprobante, a soft-deleted row and another tenant's rows are absent from **every**
      report — one test each, per endpoint.
- [ ] An NCX reduces net sales and does **not** change the ticket promedio denominator.
- [ ] The margin report excludes `costo_es_estimado` lines by default, includes them only with the
      explicit flag, never counts a `NULL` cost as zero, and always returns coverage.
- [ ] A Vendedor is rejected on every `/api/reportes/*` route; a Supervisor is accepted on the
      volume reports and rejected on rentabilidad/comisiones.
- [ ] `Tablero` covers legacy G1 (7 days of ventas and gastos) and adds ticket promedio, and the
      cost/margin panel is invisible — not just disabled — for a non-Admin.
- [ ] `npm run test`, `npm run lint`, `npm run build` green; descriptor/mapping helpers have
      colocated tests per the `web-descriptor-tests` skill.
- [ ] `dotnet ef migrations list` is unchanged — **the stage adds no migration**.
- [ ] Full Domain / Application / Integration suite green with no existing expectation modified.

## Plan de slices

Eight PRs, stacked-to-main, each with its own judgment-day round. Slices 1-4 are API-only, 5-8 are
web (plus the comisiones endpoint in 8). Line estimates include tests.

| # | Branch | Content | ~Lines |
|---|---|---|---|
| 1 | `feat/stage10-slice1-reportes-base` | `Reportes` read layer, range/bucket/timezone domain type, `zona_horaria` parametro, both policies, `GET /reportes/ventas/resumen`, the cross-tenant + soft-delete test pattern | ~380 |
| 2 | `feat/stage10-slice2-ventas-por-dimension` | `/reportes/ventas/por-punto-venta`, `/por-vendedor`, `/por-medio-pago` | ~300 |
| 3 | `feat/stage10-slice3-articulos-y-margen` | `/reportes/articulos/top`; `/reportes/rentabilidad` with the three-state cost, `incluirEstimados`, coverage payload, `LecturaDeRentabilidad` | ~380 |
| 4 | `feat/stage10-slice4-egresos` | `/reportes/compras/por-proveedor`, `/reportes/gastos/resumen` | ~300 |
| 5 | `feat/stage10-slice5-tablero-g1` | Recharts + `componentes/graficos/` wrappers + `Tablero` with the G1-parity cards and ticket promedio | ~400 |
| 6 | `feat/stage10-slice6-tablero-dimensiones` | Range/granularity controls + breakdown panels (PV, vendedor, medio de pago, top artículos) | ~350 |
| 7 | `feat/stage10-slice7-tablero-rentabilidad` | Margin panel, role-gated, with the cost-coverage banner | ~250 |
| 8 | `feat/stage10-slice8-comisiones` | `comision_porcentaje` parametro, `/reportes/comisiones`, PROVISIONAL card — **droppable in full** | ~250 |

Forecast for `sdd-tasks`: total well past 400 lines → **chained PRs required**, `chain_strategy:
stacked-to-main`. Slice 5 is the one at real risk of overflowing; if it does, split the chart
wrappers from the page.

## Deferred / adjacent (recorded, not in scope)

- Exporting any report (stage 11, which this stage motivates).
- `ix_comprobantes_venta_tenant_fecha`, only against measured evidence.
- Recargo por medio de pago activation — a checkout write-path change of its own.
- Compras bucketed by `fecha_comprobante` (the fiscal view) — stage 11.
- Period-over-period comparison, targets, scheduled/emailed reports, alerting (the alert channel is
  stage 12's to open).
- Per-empleado commission rates, commission by article or by margin, persisted liquidación.
- Turno/caja-level reporting (`id_turno_caja` is on the comprobante and ready) — that is Ver Cajas
  and Caja Z, explicitly stage 11 (G2/G3).

## Proposal question round

Recorded for the owner's morning review. Each was resolved autonomously above; correcting any of
them now costs a slice, after apply it costs a slice plus a revert. **None of them touches the
database.**

1. **Should margin be visible to Supervisor, or Admin only?** Assumed **Admin only** (decision 3).
   Widening is a one-line policy change; the reverse is not.
2. **Is a flat percentage over net sales an acceptable *provisional* commission?** Assumed **yes,
   defaulting to 0% (off)** and labelled PROVISIONAL (decision 5). If the real rule is per article
   or over margin, slice 8 is dropped and redone rather than patched.
3. **Is the business day the store's day (a punto de venta setting) or the viewer's day?** Assumed
   **the store's** (decision 2). Everything else in the dashboard's honesty depends on this one.
4. **Should the dashboard show a margin figure at all when part of the cost is estimated?** Assumed
   **yes, but excluded by default and always with coverage stated** (decision 9). The alternative —
   hide margin entirely until every cost is real — is defensible and would only remove a panel.
5. **Is one new npm dependency (Recharts) acceptable, or should charts stay CSS/SVG-only?** Assumed
   **Recharts** (decision 4). A hand-rolled bar chart is fine; a hand-rolled time series with axes,
   tooltips and responsiveness is a library nobody reviewed.
