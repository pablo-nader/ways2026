# Design: Stage 10 — Capa de agregación server-side + dashboard

## Technical Approach

The stage adds one read-only vertical: `Ways.Domain/Reportes/` (pure range/bucket/coverage types),
`Ways.Application/Reportes/` (read services + contracts), one endpoint file, two policies, two
`ParametroConocido` entries, one contained chart folder and one page. No write path, no schema, no
migration.

The load-bearing design move is **narrowing the raw-SQL surface to the two queries that actually
need it**. The proposal's binding decision is "no materialized view"; the *mechanism* of the
aggregation is a design call, and the verified house pattern for every existing derived read
(`ServicioDeSaldoDeProveedor.cs:30-43`, `LectorDeContenidoDeResumen.cs:56-97`) is **EF Core LINQ
`GroupBy`** — which carries the `BajaLogica` and `Tenant` query filters
(`WaysDbContext.cs:332,355`) for free. Raw SQL loses both. Since only the *time-bucketed series*
needs `date_trunc(… AT TIME ZONE …)`, seven of the nine reports are plain LINQ aggregates and the
top risk of the stage collapses onto **one** shared SQL code path.

`Database.SqlQuery<T>()` / `FromSqlRaw<T>()` are **unusable in this repo** — with this model they
blow up `NavigationExpandingExpressionVisitor` with `IndexOutOfRangeException`
(`InicializadorDeBaseDeDatos.cs:122-132`, restated in `ServicioDeCategorias.cs:15-17`). The raw path
is therefore ADO.NET over `db.Database.GetDbConnection()`, opened with
**`Db.Database.OpenConnectionAsync()`** (never `GetDbConnection().OpenAsync()`), because only the
former runs the EF interceptor pipeline that sets `app.acceso` / `app.tenant_id`
(`InterceptorDeContextoDeTenant.cs:20-21`). Skipping it fails *silently* to zero rows, not loudly —
the exact trap `ServicioDeCategorias.cs:18-26` documents.

## Architecture Decisions

| # | Decision | Alternatives considered | Rationale |
|---|---|---|---|
| 1 | **EF LINQ `GroupBy` is the default aggregation mechanism; raw ADO.NET is used only by `LectorDeSerieTemporal`** (ventas + gastos series). A refinement of proposal decision 1, not a reversal: no matview, no new table, still parameterized SQL where SQL is needed | (a) Raw SQL for all nine reports, as the proposal's prose implies; (b) LINQ for all nine, expressing the bucket with `EF.Functions.DateTruncate` | (a) multiplies the stage's #1 risk (a query silently missing `deleted_at IS NULL` inflates a total that *looks right*) by nine and discards the query filters the codebase already relies on. (b) is not verifiable here: the `date_trunc(field, source, tz)` translation is provider/PG-version dependent, and a mistranslation would move the day cut without failing anything. Two SQL constants and one executor is the smallest surface that buys the bucketing |
| 2 | **`Ways.Application/Reportes/LectorDeSerieTemporal.cs` owns the entire raw surface**: two `private const string` SQL bodies (ventas, gastos), one `EjecutarAsync`, one parameter binder — copied structurally from `ServicioDeCategorias.cs:199-244` | A SQL builder per report service | One file is one review target and one grep target for the invariant checklist below. A builder per service is four places for the same omission to happen |
| 3 | **Granularity is inlined as a validated literal; the timezone is a bound parameter** — `date_trunc('day' \| 'week' \| 'month', timezone($n, cv.fecha))` where the literal comes from a `switch` over the parsed `Granularidad` enum, never from request text | Bind the granularity as `date_trunc($1, …)` too | The enum is closed and parsed before the query is built, so there is no injection vector and the plan stays stable. The zone is tenant *data* and must be a parameter. `timezone(text, timestamptz)` is used over the `AT TIME ZONE` infix form because the function form is unambiguous with a positional parameter |
| 4 | **Bucket gaps are filled in C#**, from `RangoDeReporte.Buckets()`, left-joined against the SQL rows | `generate_series` in SQL | A day with no sales must render as `0`, not vanish from the chart. Doing it in the pure Domain type makes gap-filling unit-testable without a database and keeps the SQL to one `GROUP BY` |
| 5 | **The report zone is resolved once per request, at the scope the caller asked for**: with `idPuntoVenta` → PV→empresa→default; without it → empresa→default, ignoring per-PV overrides. The resolved zone is **echoed in every response** (`zonaHoraria`) | Bucket each punto de venta in its own zone and merge the series | Merged buckets from two zones are not the same day, so the column labelled "martes" would mean two different intervals. One report, one day definition, stated in the payload — a number whose day cut is invisible is not auditable |
| 6 | **Buckets are returned as store-local `DateOnly` + a text label, never as an instant** | Return the bucket start as `timestamptz` | The web renders dates with `toLocaleString('es-AR')`, i.e. the *browser's* zone (`Caja.tsx:47-48`). Shipping an instant hands the browser a second chance to re-shift the day cut the server just fixed |
| 7 | **`LecturaDeReportes` gates the group; `LecturaDeRentabilidad` is stacked on top of it** for `/rentabilidad` and `/comisiones` | A single policy with a per-endpoint role check inside the handler | ASP.NET composes authorization metadata with **AND** (`Politicas.cs:36-37`), so stacking Admin-only over Supervisor+Admin yields Admin-only with no new mechanism — the same composition the repo already uses for `OperacionDePos` + `GestionDeCatalogo`. Both are plain `RequireClaim(ClaimsWays.RolId, …)` policies; the repo has **no authorization handlers** to imitate |
| 8 | **Empresa scoping is an explicit `id_punto_venta = ANY($n)` / `Where(pv => pv.IdEmpresa == …)` predicate, plus an explicit `id_tenant = $n` in raw SQL** — RLS is the third layer, not the first | Rely on RLS alone | The composite indexes lead with `id_punto_venta` (`ComprobanteVentaConfiguration.cs:94-95`), so the explicit predicate is simultaneously the index key and the isolation belt. Root is excluded from both policies, but a platform-mode connection would see every tenant — the explicit predicate means that is a 0-row report, not a leak |
| 9 | **`tipos_comprobante.signo > 0` is the TX/NCX discriminator**, not the `codigo` string | Match `codigo = 'TX'`; infer from `id_comprobante_asociado IS NOT NULL` | `signo` is the column the write path itself reads (`ServicioDeVentas.cs:753,774`) and the seed sets `-1` for NCX (`InicializadorDeBaseDeDatos.cs:78`). `id_comprobante_asociado` is optional even on an NCX (`ReglaDeComprobantes.ValidarComprobanteAsociado`), so it is not a discriminator |
| 10 | **Top artículos groups on `items_comprobante_venta.id_articulo` but labels from the line's `descripcion` snapshot** | Join `articulos` for the current name | doc-10 principle 6: the line snapshot is immutable. A renamed article must not retroactively rename last month's report, and a soft-deleted article must still appear in the period it sold in |
| 11 | **Recharts is imported only under `src/componentes/graficos/`; wrappers take a plain data array plus explicit `alto`** | Import Recharts in the page | Proposal decision 4's containment. Under jsdom `ResponsiveContainer` measures 0 and renders nothing, so tests mock `recharts` and assert on the wrapper's *pure* mapping helpers — the library never enters the assertion surface |
| 12 | **`zona_horaria` forces the `Parametros` ABM to become type-aware** — the editor currently hardcodes `type="number"` and `JSON.stringify(Number(valorTexto))` (`Parametros.tsx:91,188-197`). For a string key that produces `"null"`, and `JsonSerializer.Deserialize("null", typeof(string))` **returns null without throwing**, so `ValidarTipo` (`ServicioDeParametros.cs:110-123`) would accept it | Document the quoting rule and let the admin type raw JSON | A parametro silently storable as `null` is exactly the failure class this stage exists to avoid. Slice 1 (a) adds `tipo: 'texto'` to `PARAMETROS_CONOCIDOS` and renders a `<select>` of offered zones, (b) hardens `ValidarTipo` to reject a `null` deserialization, and (c) validates `zona_horaria` against `TimeZoneInfo.FindSystemTimeZoneById` at write time, so an invalid zone fails at the ABM with a 400 instead of at report time with a Postgres 22023 → 500 |

## Timezone Mechanics

**Registry.** Two entries in `ParametroConocido.cs`, added to the array at `:30`:

```csharp
public static readonly ParametroConocido ZonaHoraria =
    new("zona_horaria", typeof(string), "\"America/Argentina/Buenos_Aires\"");   // JSON-quoted

public static readonly ParametroConocido ComisionPorcentaje =
    new("comision_porcentaje", typeof(decimal), "0");
```

`ValorPorDefecto` is returned verbatim by `ResolucionDeParametros.Resolver` (`:23`), so the default
must already be valid JSON — hence the embedded quotes. Readers call
`JsonSerializer.Deserialize<string>(valor)`, never `valor` raw.

**Bucketing SQL** (the only shape, parameterized identically for `gastos`):

```sql
SELECT date_trunc('day', timezone($1, cv.fecha))::date AS bucket, ...
FROM comprobantes_venta cv JOIN tipos_comprobante tc ON tc.id_tipo_comprobante = cv.id_tipo_comprobante
WHERE cv.deleted_at IS NULL AND cv.estado <> 'anulado'::estado_comprobante
  AND tc.clase = 'venta'::clase_comprobante
  AND cv.id_tenant = $2 AND cv.id_punto_venta = ANY($3)
  AND cv.fecha >= $4 AND cv.fecha < $5
GROUP BY 1 ORDER BY 1
```

`timestamptz AT TIME ZONE` yields a zone-less local `timestamp`; truncating that and casting to
`date` produces the store-local bucket key directly — no round trip back to an instant (decision 6).

**ISO week.** `date_trunc('week', …)` in Postgres is already Monday-start. The label is produced in
C# from the bucket date (`ISOWeek.GetYear` / `GetWeekOfYear` → `2026-W33`), not with `to_char`, so
the label rule is unit-testable in `RangoDeReporte` alongside the boundary rule.

**Range resolution** lives in `Ways.Domain/Reportes/RangoDeReporte.cs`, DB-free like
`PoliticaDeRoles`: `(DateOnly desde, DateOnly hasta, Granularidad, TimeZoneInfo)` →
`DesdeUtc`, `HastaUtcExclusivo` (start of `hasta + 1 day`, local), and `Buckets()`. It rejects
`hasta < desde` and a span past 366 days (bounded aggregate ⇒ no pagination).

## Raw-SQL Invariant Checklist (binding)

Every query in the stage must satisfy its row. LINQ rows get `deleted_at`/`id_tenant` from the query
filters; the estado column is **never** automatic.

| Report | Mechanism | Soft delete | Estado | Scope predicate | Date column |
|---|---|---|---|---|---|
| `ventas/resumen` | **raw** | `cv.deleted_at IS NULL` | `<> 'anulado'` + `tc.clase = 'venta'` | `id_tenant` + `id_punto_venta = ANY` | `cv.fecha` |
| `gastos/resumen` | **raw** (series) | `g.deleted_at IS NULL` | — (no estado column) | `id_tenant` + `id_punto_venta = ANY` | `g.fecha` |
| `ventas/por-punto-venta` \| `por-vendedor` | LINQ | filter | `Estado != Anulado` + clase venta | `IdPuntoVenta` in set | `Fecha` |
| `ventas/por-medio-pago` | LINQ (`PagosComprobante` ⋈ `ComprobantesVenta`) | filter (both) | idem, on the header | idem | header `Fecha` |
| `articulos/top`, `rentabilidad` | LINQ (`ItemsComprobanteVenta` ⋈ header) | filter (both) | idem, on the header | idem | header `Fecha` |
| `compras/por-proveedor` | LINQ | filter | `Estado == Confirmada` | idem | `FechaRecepcion` |
| `comisiones` | LINQ | filter | `<> Anulado` + `Signo > 0` | idem | `Fecha` |

## Interfaces / Contracts

```csharp
public enum Granularidad { Dia, Semana, Mes }

public sealed record BucketDeVentas(string Etiqueta, DateOnly Inicio, decimal Neto, int CantidadTx, decimal? TicketPromedio);

public sealed record ResumenDeVentas(
    DateOnly Desde, DateOnly Hasta, Granularidad Granularidad, string ZonaHoraria,
    IReadOnlyList<BucketDeVentas> Serie,
    decimal NetoVendido, int CantidadTx, decimal? TicketPromedio, int CantidadNcx, decimal NetoNcx);

/// Cobertura del costo (stage-9 tres estados). Cada campo lo consume el banner de rentabilidad.
public sealed record CoberturaDeCosto(
    int LineasTotales, int LineasConCostoReal, int LineasConCostoEstimado, int LineasSinCosto,
    decimal VentaTotal, decimal VentaConCostoReal, decimal VentaConCostoEstimado, decimal VentaSinCosto,
    bool IncluyeEstimados);

public sealed record Rentabilidad(
    DateOnly Desde, DateOnly Hasta, string ZonaHoraria,
    decimal VentaConsiderada, decimal CostoConsiderado, decimal Margen, decimal? MargenPorcentaje,
    CoberturaDeCosto Cobertura, IReadOnlyList<RentabilidadPorArticulo> PorArticulo);
```

`TicketPromedio` and `MargenPorcentaje` are **nullable, never 0**: an empty denominator has no
answer, and `0%` is a lie. `Neto` sums signed totals, so an NCX subtracts by construction
(`CalculadorDeTotales.cs:5-8,32-42`) while `CantidadTx` / `TicketPromedio` count `signo > 0` only.

**Endpoints** — `MapGroup("/api/reportes").RequireAuthorization(Politicas.LecturaDeReportes)`; the
last two stack `LecturaDeRentabilidad`. Per `dto-contract-honesty`, a parameter exists only where it
is read: `granularidad` only on the two series, `limite` only on the two top-N, `idPuntoVenta` on
everything except `por-punto-venta` (where it would be a contradiction).

`GET /reportes/ventas/resumen` · `/ventas/por-punto-venta` · `/ventas/por-vendedor` ·
`/ventas/por-medio-pago` · `/articulos/top` · `/compras/por-proveedor` · `/gastos/resumen` ·
`/rentabilidad` (+`incluirEstimados`) · `/comisiones`. All take `idEmpresa`, `desde`, `hasta`.
`idEmpresa` is validated with `db.Empresas.AnyAsync` → **404** per ADR-8 (same answer for "does not
exist" and "belongs to another tenant"); `idPuntoVenta` reuses
`ServicioDeParametros.ValidarPuntoVentaDeLaEmpresaAsync`'s rule. An empresa with zero puntos de
venta is an empty report, not an error.

## Data Flow

```
GET /api/reportes/ventas/resumen?idEmpresa&desde&hasta&granularidad
  │
  ├─ Politicas.LecturaDeReportes (claim rol_id ∈ {Supervisor, Admin})
  │
  ├─ ServicioDeReportesDeVentas
  │    ├─ Empresas.AnyAsync ................ 404 ADR-8
  │    ├─ PuntosVenta.Where(IdEmpresa) ..... int[] scope (EF filters: Tenant + BajaLogica)
  │    ├─ ServicioDeParametros.Resolver("zona_horaria") → JSON → TimeZoneInfo
  │    └─ RangoDeReporte(desde, hasta, gran, zona)  [Domain, puro]
  │           └─ DesdeUtc / HastaUtcExclusivo / Buckets()
  │
  ├─ LectorDeSerieTemporal  ── Db.Database.OpenConnectionAsync()
  │      └─ InterceptorDeContextoDeTenant → set_config(app.acceso, app.tenant_id)  ← RLS vive acá
  │      └─ date_trunc(gran, timezone($zona, fecha)) GROUP BY 1
  │
  └─ Buckets() ⟕ filas SQL  →  serie sin huecos  →  ResumenDeVentas
```

## File Changes

| File | Action | Description |
|---|---|---|
| `src/Ways.Domain/Reportes/RangoDeReporte.cs`, `Granularidad.cs`, `CoberturaDeCosto.cs` | Create | Pure boundary/bucket/label/coverage logic, DB-free |
| `src/Ways.Domain/Catalogos/ParametroConocido.cs` | Modify | `ZonaHoraria`, `ComisionPorcentaje` + array at `:30` |
| `src/Ways.Application/Parametros/ServicioDeParametros.cs` | Modify | `ValidarTipo` rejects a `null` deserialization; `zona_horaria` validated as a real IANA id |
| `src/Ways.Application/Reportes/LectorDeSerieTemporal.cs` | Create | The **entire** raw-SQL surface of the stage |
| `src/Ways.Application/Reportes/ServicioDeReportesDe{Ventas,Egresos,Rentabilidad}.cs` | Create | LINQ aggregates + orchestration |
| `src/Ways.Application/Reportes/Contratos.cs` | Create | Response records above |
| `src/Ways.Application/DependencyInjection.cs` | Modify | Register the four read services |
| `src/Ways.Api/Endpoints/ReportesEndpoints.cs` | Create | Nine `MapGet` routes |
| `src/Ways.Api/Seguridad/Politicas.cs` | Modify | Two `const` + two `AddPolicy` |
| `src/Ways.Web/package.json` | Modify | `recharts` |
| `src/Ways.Web/src/componentes/graficos/{GraficoDeLineas,GraficoDeBarras,series}.tsx\|ts` | Create | Wrappers + pure mapping helpers |
| `src/Ways.Web/src/api/reportes.ts` + `tipos.ts` | Create/Modify | Client + mirrors + `PARAMETROS_CONOCIDOS` `tipo: 'texto'` |
| `src/Ways.Web/src/paginas/{Tablero,Parametros}.tsx`, `App.tsx`, `componentes/Layout.tsx` | Create/Modify | Page, route (`ROL.Supervisor`, `ROL.Admin`), nav, typed parametro editor |
| **Database / every write path** | **Untouched** | Proposal *Modelo de datos propuesto* |

## Web Composition

`Tablero.tsx` is a filter bar (`desde`, `hasta`, `granularidad`, punto de venta) over independent
panels, each owning its own fetch. Per `react-async-state` rules 2/4/9: a single `useRef`
generation token is bumped **before** any filter change mutates state, every setter after every
`await` is gated on it, and each `finally` that clears a panel's `cargando` is gated too — a stale
7-day response must never repaint a panel the user has already re-scoped to 30 days. Filter inputs
stay disabled per-panel while that panel is in flight; there is no page-level busy boolean. Rule 10:
the token pattern applies to **all** panels in the same PR, not just the first one written.

The rentabilidad panel is **not rendered at all** for a non-Admin (`useAuth` role check), not
disabled — proposal success criterion. It always renders the coverage banner above the figure,
derived from `CoberturaDeCosto`; when `VentaSinCosto > 0` the banner says so explicitly. The
comisiones card carries a literal `PROVISIONAL` badge and states the rate it used.

Vitest per `web-descriptor-tests`: colocated unit tests for every pure helper
(`series.ts` mappers, bucket-label formatters, `aParametroAlta`), plus component tests with
`vi.mock('recharts')` stubbing each chart to a `data-testid` node that serializes its `data` prop —
so panel assertions test *our* mapping, never the library. Smoke-only is not done.

## Slicing (refined — 10 PRs, stacked-to-main)

The proposal's slice 1 estimate (~380) does not survive a breakdown: domain type + tests (~210),
parametros + ABM + web tests (~90), policies (~50), read layer + lector + service + contracts +
endpoint (~250), integration tests (~180) ≈ **780**. It splits into 1 and 2. Slice 5 splits exactly
where the proposal pre-authorized ("split the chart wrappers from the page") → 6 and 7.

| # | Branch | Content | ~Lines | Test plan |
|---|---|---|---|---|
| 1 | `…slice1-parametros-y-politicas` | 2 `ParametroConocido` keys, `ValidarTipo` null hardening + IANA validation, `PARAMETROS_CONOCIDOS` `tipo:'texto'` + zone `<select>`, 2 policies | ~230 | `ParametrosTests`: quoted round-trip, unquoted → 400, `null` → 400, bad zone → 400; colocated `Parametros` component test; policy claim test |
| 2 | `…slice2-ventas-resumen` | `Domain/Reportes/*`, `LectorDeSerieTemporal`, `ServicioDeReportesDeVentas`, contracts, `/ventas/resumen`, endpoint group | ~390 | Full domain unit suite (22:30 ART, DST-free ISO week, gap fill, 366-day guard); the 4-test integration pattern below |
| 3 | `…slice3-ventas-por-dimension` | `/por-punto-venta`, `/por-vendedor`, `/por-medio-pago` | ~300 | 4-test pattern ×3 + NCX sign |
| 4 | `…slice4-articulos-y-margen` | `/articulos/top`, `/rentabilidad`, coverage, `LecturaDeRentabilidad` | ~380 | 4-test pattern ×2 + coverage matrix + 403 Supervisor |
| 5 | `…slice5-egresos` | `/compras/por-proveedor`, `/gastos/resumen` (reuses the lector) | ~300 | 4-test pattern ×2 + `fecha_recepcion`/`estado=confirmada` |
| 6 | `…slice6-graficos` | `recharts`, `componentes/graficos/`, mapping helpers | ~200 | Colocated unit tests per helper + mocked-chart render |
| 7 | `…slice7-tablero-g1` | `Tablero` + route + nav + G1 cards (7 días ventas/gastos) + ticket promedio | ~300 | Component tests: render, token gating, error path |
| 8 | `…slice8-tablero-dimensiones` | Filter bar + 4 breakdown panels | ~350 | Stale-response test per panel |
| 9 | `…slice9-tablero-rentabilidad` | Margin panel, role-gated, coverage banner | ~250 | Non-Admin ⇒ panel absent from the DOM; banner copy per coverage state |
| 10 | `…slice10-comisiones` | `/comisiones` + PROVISIONAL card — **droppable in full** | ~220 | 4-test pattern + rate `0` ⇒ all-zero report |

Total ≈ **2,920**. Chained PRs required, `chain_strategy: stacked-to-main`.

## Testing Strategy

| Layer | What to test | Approach |
|---|---|---|
| Domain | `RangoDeReporte`: a 22:30 ART sale on the 3rd buckets to the 3rd (and to the 4th under a UTC zone, proving the parameter is live); `hasta` inclusivity; ISO Monday-start + `2026-W01` year rollover; month boundaries; gap fill; invalid range and 366-day guard | Pure xUnit, no DB — `PoliticaDeRoles` pattern |
| Integration — **the 4-test pattern, per endpoint** | (1) cross-tenant: tenant B's rows absent; (2) soft delete: a `deleted_at` row absent; (3) estado: an `anulado`/`borrador` row absent; (4) hand-computed fixture equality | Extend `WaysApiFixture` with a two-tenant report seeder. Non-negotiable per endpoint — raw SQL and LINQ alike |
| Integration — timezone | Same seed read with `zona_horaria` = ART vs UTC returns **different** bucket assignment for a 22:30 sale — proves the parametro reaches the SQL, not just the C# boundary | `ReportesZonaHorariaTests` |
| Integration — NCX | An NCX reduces `NetoVendido`, leaves `CantidadTx`/`TicketPromedio` untouched, and reverses margin (negative `cantidad` × unsigned `costo_unitario`) | `ReportesSemanticaTests` |
| Integration — coverage | Four seeded lines (real cost / estimated / `NULL` / cost `0`): estimated excluded by default and included with the flag; `NULL` never counted as `0`; every count and revenue field asserted | `RentabilidadTests` |
| Integration — role matrix | Vendedor → 403 on all nine; Supervisor → 200 on seven, 403 on `/rentabilidad` and `/comisiones`; Admin → 200 on nine; Root → 403 on all nine | `ReportesAutorizacionTests`, parameterized over the route list |
| Integration — no migration | `dotnet ef migrations list` unchanged; the model snapshot untouched | Slice gate, every slice |
| Web (vitest) | Pure mappers/labels colocated; panel components with mocked `recharts`; stale-response and non-Admin-invisibility assertions | `web-descriptor-tests` + `react-async-state` |

## What Does NOT Change (asserted)

No table, column, index, constraint, enum, RLS policy or migration. No write path
(`ServicioDeVentas`, compras, gastos, caja, cuenta corriente) is in the diff. No existing endpoint,
policy or DTO changes shape — `Politicas.cs` and `ParametroConocido.cs` are **additive only**, and
`ServicioDeParametros.ValidarTipo` gets strictly *tighter*, never looser. `SuperficieDeAutorizacionTests`
needs no allowlist entry: it guards write verbs, and this stage adds only `GET`s.

## Open Questions

- [ ] **`ValidarTipo` hardening is a behaviour change to an existing endpoint.** Rejecting a `null`
      deserialization is correct, but any tenant that already stored `"null"` under a decimal key
      would now fail on re-save. Verified as impossible through the current ABM (it always sends a
      number), so the fix is safe — recorded because it is the only non-additive edit in the stage.
- [ ] **Empresa-wide range scans reach `comprobantes_venta` through a per-PV bitmap scan** (`ANY`
      over the PV set). Proposal-recorded candidate `ix_comprobantes_venta_tenant_fecha` stays
      unproposed until a real `EXPLAIN (ANALYZE)` justifies it.
- [ ] **`ways_owner` is a superuser in the test container** (inherited from stage 9), so integration
      cross-tenant tests prove the *explicit predicates*, not RLS itself. That is why decision 8
      makes the tenant predicate explicit rather than delegating isolation to RLS alone.
- [ ] **Commission rate scope.** `comision_porcentaje` resolves per punto de venta / empresa, so a
      report spanning several PVs with different rates uses the empresa-level value (decision 5's
      zone rule, applied to the rate). Provisional by construction — slice 10 is droppable.
