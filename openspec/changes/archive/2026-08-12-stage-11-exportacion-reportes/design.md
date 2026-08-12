# Design: Stage 11 — Infraestructura de exportación + reportes descargables

## Technical Approach

One port, one adapter, N pure mappers. `Ways.Application/Exportacion/` owns a neutral **typed**
table (`TablaExportable`) and the port (`IExportadorDeTabla`); `Ways.Infrastructure/Exportacion/`
owns the single file that names the Excel library. This is the seam the repo already uses for
`IHasheadorDeContrasenas` → `HasheadorPbkdf2` (`DependencyInjection.cs:56`), so the swap after a
failed licence audit is one file plus one `PackageReference`.

Every export is a sibling `MapGet` declared immediately after its source route inside the same
`MapGroup`, so parameters **and** policy are inherited structurally, not by convention
(`ReportesEndpoints.cs:11-13` for the group, `:51`/`:78` for the stacked `LecturaDeRentabilidad`).
Every mapper consumes an **already materialised response record** from an existing service — no
export opens a second query path, which is this stage's top risk.

The load-bearing refinement of the proposal is **where the caja detail lives**. Decision 5 puts the
Z-report under `OperacionDePos`, but the proposal's route list placed it at
`GET /api/reportes/cajas/{id}` — inside a group already gated by `LecturaDeReportes`, and ASP.NET
composes with AND, so that route would lock out the very cajero it was written for. Co-location only
works if the route sits next to a source with the right gate: the **detail and its export move to
`/api/caja/turnos/{id}/detalle(/export)`** (`CajaEndpoints.cs:10-12`, `OperacionDePos`), and only
the cross-turno histórico and the tesorería book stay under `/api/reportes` (decision 5's management
half). Same policies as the proposal intended, now inherited instead of re-declared.

## Architecture Decisions

| # | Decision | Alternatives considered | Rationale |
|---|---|---|---|
| 1 | **Cells are typed, not pre-formatted.** `TipoDeColumna { Texto, Entero, Decimal, Moneda, Cantidad, Fecha, FechaHora }`; a `Celda` carries a kind + a boxed value built through factories (`Celda.Moneda(decimal?)`, `Celda.Fecha(DateOnly?)`). The XLSX writer sets a real numeric/date cell value plus a **column-level** number format | Map each cell to a formatted `string` (`total.ToString("N2")`) | A text column that looks numeric is the exact failure mode of this stage: the accountant selects it, Excel sums nothing, and the file was forwarded three people ago. Formatting on the column (not the cell) also keeps 25 000 rows from carrying 25 000 styles |
| 2 | **`TablaExportable`'s constructor validates itself**: every row has `Columnas.Count` cells and `fila[i].Tipo == Columnas[i].Tipo`, else it throws | Trust the mapper; let the writer coerce | Makes "a mapper put a string in a money column" a failing **unit** test with a hand-built record, not a silent cell in a workbook nobody re-opens. `null` stays an empty cell — never `0`, never `"-"`, the same rule as `TicketPromedio` |
| 3 | **`FechaHora` is converted to store-local, zone-less `DateTime` before it is written** | Write the `DateTimeOffset` | Excel has no timezone concept; writing an instant hands the spreadsheet a second chance to shift the day cut the server already fixed (stage-10 ADR-6, applied to files) |
| 4 | **Containment is guarded by a real xUnit source-scan test**, `tests/Ways.Application.Tests/Exportacion/ContencionDelExportadorTests.cs`, walking `src/**/*.cs` from `ResolverRaizDelRepositorio()` and asserting exactly ONE file matches `ClosedXML`, plus that only `Ways.Infrastructure.csproj` carries the `PackageReference` | (a) a CI `rg` step — stage-10's recharts answer, which is WARNING-1 in its own verify report ("verified only by source inspection… no automated regression test"); (b) NetArchTest | (a) is invisible on the developer's machine and this repo has no lint job to hang it on. (b) is a new test dependency that inspects IL *types*, not files, and still needs to be told the package name — for one assertion. The repo already has the idiom: `VentasCheckoutTests.NingunLiteralDeToleranciaOVueltoHardcodeadoEnElCaminoDeCheckout` (`:663-705`) scans source files exactly this way, and it runs inside the fast DB-free suite. The scan root is `src/` **only** — test code reads workbooks back on purpose (decision 8) |
| 5 | **The row cap is an option, not a `const`**: `OpcionesDeExportacion.TopeDeFilas` (default `25_000`), bound from configuration and overridable by the integration fixture | `public const int TopeDeFilas = 25_000` | A cap that can only be exercised by seeding 25 001 rows is a cap whose guard is never actually tested — deleting the `if` would leave every test green (`mutation-proof-tests` rule 2). With an option the fixture binds it to `3`, seeds `4`, and the mutation is observable in under a second. A unit test asserts the production default is 25 000 |
| 6 | **Two cap shapes, by report shape.** *Aggregates* (the nine stage-10 reports, `/stock/existencias`, the G2 listing) are bounded by construction (≤ 366 buckets, ≤ `limite`, one row per PV/vendedor/medio/turno): the guard runs on `TablaExportable.Filas.Count` after mapping — **no query at all**. *Listings* (ventas, compras, estado de cuenta, tesorería, tickets/gastos del turno) run `COUNT(*)` first over the **same** `IQueryable` | A `COUNT(*)` before every export, aggregates included | The proposal's rule is "never build a workbook you will reject". For an aggregate there is nothing to count: the mapped table *is* the bound, and a count query would be a second query path — the thing this stage forbids |
| 7 | **Listing exports reuse the filter chain by extraction, never by copy.** `ServicioDeVentas.ListarAsync` already counts over its own query (`:269`) but clamps `tamanio` to 200 (`:240`), so an export cannot ask for one big page. The filter chain is extracted into a private `ConstruirQuery(filtros)` shared by `ListarAsync` and a new `ListarParaExportacionAsync`: `Contar → refuse → single read with .Take(tope + 1)` | (a) page the export through `ListarAsync` in 200-row chunks; (b) a bespoke `ContarAsync` per service | (a) is up to 125 round trips **and** a shifting `Skip/Take` window under concurrent writes — a duplicated or missing row in a file whose entire promise is "equals the screen". (b) duplicates the predicate chain in a second place, which is precisely the drift this stage exists to kill. The `+1` is the race backstop: if the read returns `tope + 1` rows the count raced, and we refuse — **no truncated file can escape even in that window** |
| 8 | **The equality invariant is proven by reading the workbook back**, with ClosedXML, in the test project | Golden-file byte comparison; assert only the row count | A golden file breaks on every library upgrade and proves nothing about agreement with the endpoint. The binding claim is "same parameters ⇒ same figures", so the test must call both routes and compare **values** |
| 9 | **`formato` is bound as `string` and parsed by us**, `FormatoDeExportacion.Parsear` → `ErrorDominio("formato_no_soportado", …, 400)` | Bind it as the enum and let minimal APIs reject | The framework's enum-binding failure is a bare 400 with no `codigo`, indistinguishable from a bad `desde`. The spec pins the code, so the code has to come from our layer. Missing `formato` stays a framework 400 (non-nullable required query parameter), which is correct and needs no code |
| 10 | **The caja detail is a NEW route, `GET /api/caja/turnos/{id}/detalle`**, returning `DetalleDeTurno(ResumenDeTurno, Tickets, Gastos)`; `/resumen` is untouched | Enrich `ResumenDeTurno` with the two listings | `ResumenDeTurno` feeds the cierre screen and the stage-6 spec pins "Resumen Parcial Uses The Same Derivation As Cierre". Additive route, zero contract change, and `/detalle/export` is its sibling under the same `OperacionDePos` group |
| 11 | **The tesorería book is ordered by `id`, never by `fecha`** | `OrderBy(m => m.Fecha)` | The ledger's meaning *is* insertion order (`Inicio` chains off the previous `Final`, `MovimientoTesoreria.cs:23-38`); two closes in the same second sorted by date render the running balance nonsensical. `ix_movimientos_tesoreria_punto_venta_id` is exactly this access shape |
| 12 | **`api.descargar()` shares `pedir`'s error path by extraction**, not by copy: `exigirRespuestaOk(respuesta)` is factored out of `pedir` (`cliente.ts:52-69`) and called by both | Duplicate the 401/ProblemDetails handling inside `descargar` | Two copies of the session-expiry path is one copy that will be forgotten (`react-async-state` rule 10). Extracting it also means the existing `pedir` tests keep covering the shared branch |
| 13 | **No separate print route.** The print view is the SAME component under `@media print` + Bootstrap's `d-print-none` on chrome | A dedicated `/imprimir/:id` page fetching its own data | Proposal risk "print view drifting from the screen it prints" is eliminated by construction: there is no second render tree and no second fetch |

## Interfaces / Contracts

```csharp
// Ways.Application/Exportacion/
public enum TipoDeColumna { Texto, Entero, Decimal, Moneda, Cantidad, Fecha, FechaHora }

public readonly record struct Celda(TipoDeColumna Tipo, object? Valor)
{
    public static Celda Texto(string? v);      public static Celda Entero(int? v);
    public static Celda Moneda(decimal? v);    public static Celda Cantidad(decimal? v);
    public static Celda Fecha(DateOnly? v);    public static Celda FechaHora(DateTimeOffset? v, TimeZoneInfo zona);
}

public sealed record ColumnaExportable(string Titulo, TipoDeColumna Tipo);

/// Encabezado del archivo (decisión 7 del proposal): empresa, PV o "Todos", rango, generado por/cuándo
/// con su zona, y la línea de COBERTURA cuando el reporte lleva algún costo estimado.
public sealed record ContextoDeExportacion(
    string Empresa, string? PuntoVenta, DateOnly Desde, DateOnly Hasta,
    string ZonaHoraria, string Usuario, DateTimeOffset GeneradoEl, string? Cobertura);

public sealed record TablaExportable(
    string NombreDeHoja, ContextoDeExportacion Contexto,
    IReadOnlyList<ColumnaExportable> Columnas, IReadOnlyList<IReadOnlyList<Celda>> Filas);

public interface IExportadorDeTabla
{
    string TipoDeContenido { get; }
    byte[] Generar(TablaExportable tabla);
}
```

`NombreDeArchivo.Construir(reporte, alcance, desde, hasta)` is a pure static in the same folder —
deterministic, ASCII by construction (ids, never names), unit-tested. `ResultadoDeExportacion`
(Ways.Api) writes `Content-Disposition: attachment; filename="…"; filename*=UTF-8''…` plus
`application/vnd.openxmlformats-officedocument.spreadsheetml.sheet`.

**Mappers** live next to the services whose responses they consume, one static class per capability:
`Ways.Application/Reportes/ExportacionDeReportes.cs` (nine overloads + existencias),
`Ways.Application/Caja/ExportacionDeCaja.cs` (G2 listing, detalle, tesorería),
`Ways.Application/Exportacion/ExportacionDeListados.cs` (ventas, compras, estado de cuenta). Each is
`public static TablaExportable De(X respuesta, ContextoDeExportacion ctx)` — a pure function of an
already-typed record, so every mapper is unit-testable with a hand-built response and **no database**.

## Data Flow — `ResumenDeVentas` end to end

```
GET /api/reportes/ventas/resumen/export?idEmpresa&idPuntoVenta&desde&hasta&granularidad&formato=xlsx
  │
  ├─ MapGroup("/api/reportes").RequireAuthorization(LecturaDeReportes)   ← heredado por co-location
  │
  ├─ FormatoDeExportacion.Parsear("xlsx")            → 400 formato_no_soportado
  │
  ├─ ServicioDeReportesDeVentas.ObtenerResumenAsync(…)   ← EL MISMO método que la ruta JSON
  │        └─ ResumenDeVentas { Serie[], NetoVendido, CantidadTx, TicketPromedio, ZonaHoraria, … }
  │
  ├─ ExportacionDeReportes.De(resumen, ctx)          [puro, sin DB]
  │        Columnas: Período(Texto) │ Neto(Moneda) │ TX(Entero) │ Ticket promedio(Moneda)
  │        Filas   : una por bucket + fila de totales;  TicketPromedio null ⇒ celda vacía
  │
  ├─ GuardaDeTope.Exigir(tabla, opciones.TopeDeFilas) → 400 exportacion_demasiado_grande (agregado: sin query)
  │
  ├─ IExportadorDeTabla.Generar(tabla)   → ExportadorXlsx (ÚNICO archivo con ClosedXML)
  │        filas 1-4 encabezado · fila 5 vacía · fila 6 títulos · formato numérico por COLUMNA
  │
  └─ ResultadoDeExportacion → ventas_resumen_pv3_2026-08-01_2026-08-12.xlsx
```

For a listing the third block becomes `ConstruirQuery(f).CountAsync()` → refuse → single read with
`.Take(tope + 1)` → map → generate.

## File Changes

| File | Action | Description |
|---|---|---|
| `src/Ways.Application/Exportacion/{TablaExportable,Celda,ColumnaExportable,ContextoDeExportacion,IExportadorDeTabla,FormatoDeExportacion,NombreDeArchivo,GuardaDeTope,OpcionesDeExportacion}.cs` | Create | The seam. Feature folder, not `Abstracciones/` — that folder holds ambient cross-cutting services (`IRelojDelSistema`, `IWaysDbContext`); this port is meaningless without its model |
| `src/Ways.Infrastructure/Exportacion/ExportadorXlsx.cs` | Create | **The only file in `src/` that names ClosedXML** |
| `src/Ways.Infrastructure/Ways.Infrastructure.csproj` · `DependencyInjection.cs` | Modify | One `PackageReference`; `AddSingleton<IExportadorDeTabla, ExportadorXlsx>()` next to `HasheadorPbkdf2` (`:56`) |
| `src/Ways.Api/Exportacion/ResultadoDeExportacion.cs` | Create | `Content-Disposition` (ASCII + RFC 5987) + content type |
| `src/Ways.Api/Endpoints/ReportesEndpoints.cs` | Modify | Nine `/export` siblings (`/rentabilidad`, `/comisiones` re-stack `LecturaDeRentabilidad`) + `/stock/existencias(+export)` + `/cajas(+export)` + `/tesoreria(+export)` |
| `src/Ways.Api/Endpoints/CajaEndpoints.cs` | Modify | `/{id}/detalle` + `/{id}/detalle/export` — `OperacionDePos` by co-location (decision 10) |
| `src/Ways.Api/Endpoints/{Ventas,Compras,CuentaCorriente}Endpoints.cs` | Modify | One `/export` sibling each, all under their existing `OperacionDePos` groups |
| `src/Ways.Application/Reportes/{ExportacionDeReportes,ServicioDeReportesDeStock}.cs` | Create | Mappers; existencias report |
| `src/Ways.Application/Caja/{ServicioDeHistoricoDeCajas,ServicioDeTesoreria,LectorDeLineasDelTurno,ExportacionDeCaja}.cs` | Create | G2 listing/detail lines, G3 read, mappers |
| `src/Ways.Application/{Ventas,Compras,CuentaCorriente}/Servicio*.cs` | Modify | Extract `ConstruirQuery`; add `ListarParaExportacionAsync` (decision 7) |
| `src/Ways.Web/src/api/cliente.ts` | Modify | `exigirRespuestaOk` extracted; `api.descargar()`; `nombreDeArchivo()` helper |
| `src/Ways.Web/src/componentes/BotonDeDescarga.tsx` | Create | Busy + re-entrancy guard, errors funnelled out via `onError` |
| `src/Ways.Web/src/paginas/{HistoricoDeCajas,CajaZ,Tesoreria,Existencias}.tsx` | Create | Two caja screens + tesorería + existencias |
| `src/Ways.Web/src/paginas/{Tablero,CuentaCorriente,Compras,CierreDeCaja}.tsx` | Modify | Download buttons; link from cierre to its Z |
| `src/Ways.Web/src/{App.tsx,componentes/Layout.tsx,estilos impresión}` | Modify | Four routes, nav under Caja, `@media print` + `d-print-none` |
| **Database** | **None** | Zero-schema gate. `dotnet ef migrations list` unchanged, asserted every slice |

## G2 / G3 — minimal aggregation

- **G2 listing** (`ServicioDeHistoricoDeCajas.ListarCierresAsync`): `TurnosCaja.Where(t => t.Estado == EstadoTurno.Cerrado)` filtered by PV/fecha, then **one** `GroupBy` over `ArqueosTurno` for the ids of the page — Σ `ImporteEsperado`, Σ `ImporteDeclarado`, Σ `Diferencia` from the **already persisted** rows. `CalculadorDeArqueo` is never invoked. Egresos reuse `EgresosDeTurno`'s existing definition. Soft delete and tenant come from the EF query filters (`TurnoCaja : EntidadTenant`).
- **G2 detail**: `ServicioDeResumenDeTurno.ObtenerAsync` verbatim (`:19-22`) + `LectorDeLineasDelTurno` — two plain indexed reads, `ComprobantesVenta.Where(c => c.IdTurnoCaja == id)` (anulados excluded, matching the resumen) and `Gastos.Where(g => g.IdTurnoCaja == id)`.
- **G3**: `MovimientosTesoreria` by PV, `OrderBy(m => m.Id)`, paginated. Zero derivation.

Each of the three ships the house **4-test integration pattern**: (1) cross-tenant absence,
(2) soft-deleted/open row absence, (3) estado discrimination, (4) hand-computed fixture equality.

## Testing Strategy

| Layer | What | Approach |
|---|---|---|
| Application unit (no DB) | Every mapper; `TablaExportable` type/arity validation; `NombreDeArchivo` determinism and ASCII; header block incl. the coverage line; `FormatoDeExportacion.Parsear`; `TopeDeFilas` default is 25 000 | Hand-built response records, xUnit — the `PoliticaDeRoles` pattern |
| Architecture | Exactly one `src/` file names ClosedXML; only `Ways.Infrastructure.csproj` references the package | `ContencionDelExportadorTests`, source scan (decision 4) |
| Integration — per export | **Equality**: call JSON route and `/export` with the same query string, read the workbook back with ClosedXML, compare every figure. **Policy**: 403 for the role one step below the gate. **Cap**: `TopeDeFilas` bound low ⇒ 400 `exportacion_demasiado_grande` naming the real count, and **no bytes returned**. **Headers**: `Content-Disposition` filename matches `NombreDeArchivo` | `ExportacionTests` per capability, parameterized over the route list |
| Integration — G2/G3 | The 4-test pattern ×3, plus: the listing's totals equal the detail's persisted arqueos for the same turno | `HistoricoDeCajasTests`, `TesoreriaTests` |
| Web (vitest) | `nombreDeArchivo` parsing (`filename*` wins over `filename`); `descargar` happy path (objectURL created **and** revoked), 403 ⇒ `onError` with `ErrorApi.message` and no objectURL, 401 ⇒ `alPerderLaSesion` fired; double-click ⇒ exactly one `fetch`; screens per `web-descriptor-tests` | `vi.mock` of `fetch`, `URL.createObjectURL/revokeObjectURL` stubbed |
| Exempt | Print rendering has no automated assertion beyond `d-print-none` presence — recorded exemption, verified by eye | — |

**Licence audit (slice 1, binding).**
`dotnet list src/Ways.Infrastructure/Ways.Infrastructure.csproj package --include-transitive --format json`,
then for each `(id, version)` read `<license>` / `<licenseUrl>` from
`$HOME/.nuget/packages/{id}/{version}/{id}.nuspec`. The full table goes in the PR body. Anything
outside MIT / Apache-2.0 / BSD-* / MS-PL ⇒ stop and switch to MiniExcel. Specific things to look at:
`DocumentFormat.OpenXml`, `ExcelNumberFormat`, `SixLabors.Fonts`.

**Mutation targets** (`mutation-proof-tests`: name the clause, run the mutation, record
applied → failing test → reverted → green in the PR body):

| Clause | Mutation | Test that MUST fail |
|---|---|---|
| `if (filas > opciones.TopeDeFilas) throw` | delete the `if` | over-cap test (only observable because the tope is an option — decision 5) |
| the mapper's figure projection | wrap a money cell in `Math.Round(x, 0)` | the equality test (proves it compares values, not shapes) |
| `.RequireAuthorization(Politicas.LecturaDeRentabilidad)` on `/rentabilidad/export` | delete the line | Supervisor-403 test (the group policy alone admits Supervisor, so the mutation is observable) |
| `Where(t => t.Estado == EstadoTurno.Cerrado)` in G2 | delete the predicate | listing test seeded with one open + one closed turno, asserting the **row set and the totals**, not just a count |

## Slicing (refined — 12 PRs, stacked-to-main)

Slices 1, 5 and 6 were pre-identified in the proposal as overflow risks; each splits at the boundary
the proposal named.

| # | Branch | Content | ~Lines | Test plan |
|---|---|---|---|---|
| 1a | `…slice1a-seam` | Licence audit + package, the whole `Exportacion/` folder, `ExportadorXlsx`, DI | ~300 | Mapper-free unit suite + containment test |
| 1b | `…slice1b-primer-export` | `ResultadoDeExportacion`, `GuardaDeTope`, first export (`/ventas/resumen/export`) | ~290 | Equality + 403 + cap + header tests |
| 2 | `…slice2-exports-reportes` | The remaining eight stage-10 exports | ~380 | Equality ×8; coverage block on the two margin exports |
| 3 | `…slice3-exports-listados` | `ConstruirQuery` extraction + ventas/compras/estado de cuenta exports | ~350 | Cap ×3 + equality ×3 + the `+1` race backstop |
| 4 | `…slice4-descarga-web` | `exigirRespuestaOk`, `api.descargar()`, `BotonDeDescarga`, Tablero wiring | ~330 | The six vitest cases above |
| 5a | `…slice5a-cajas-listado` | G2 listing service + route + export | ~300 | 4-test pattern + open-turno mutation |
| 5b | `…slice5b-cajas-detalle` | `/detalle` + lines + export, under `OperacionDePos` | ~280 | 4-test pattern + Vendedor-200 / cross-turno-403 matrix |
| 6a | `…slice6a-historico-web` | `/caja/historico` screen + download | ~250 | Descriptor tests |
| 6b | `…slice6b-caja-z-web` | Caja Z screen + link from cierre + download | ~260 | Descriptor tests |
| 7 | `…slice7-tesoreria` | G3 endpoint + export + `/caja/tesoreria` | ~330 | 4-test pattern + chain-order assertion |
| 8 | `…slice8-vistas-impresion` | `@media print` + `d-print-none` on estado de cuenta and Caja Z | ~200 | `d-print-none` presence |
| 9 | `…slice9-existencias` | `/reportes/stock/existencias` + export + screen — **droppable to Etapa 13** | ~300 | 4-test pattern + equality |

Total ≈ **3 570**. Chained PRs required, `chain_strategy: stacked-to-main`.

**Parallelism.** Everything blocks on 1a→1b. After 1b merges, four fronts are independent and can be
developed concurrently: **[2 → 3]**, **[4]**, **[5a → 5b → 6a/6b]**, **[7]**, **[9]**. 8 needs 6b.
One caution: 4, 6a, 6b, 7 and 9 all touch `App.tsx` and `Layout.tsx` — slice 4 adds **no** nav entry
(it only wires buttons into existing panels), and every screen slice adds only its own route/nav
line, so the conflict surface stays one line per branch.

## Open Questions

- [ ] **Branding on generated files** (proposal Q1, owner flag). A text header block only. Unblocked
      for implementation, but it is the one product-weight call of the stage.
- [ ] **The caja detail route moved** from `/api/reportes/cajas/{id}` to
      `/api/caja/turnos/{id}/detalle` so decision 5's `OperacionDePos` is inherited rather than
      fought. `sdd-spec` must describe the split at these routes.
- [ ] **`Content-Disposition` is readable only because `/api` is same-origin** (`cliente.ts:42`). If
      the API ever moves to another origin, it needs `Access-Control-Expose-Headers` or
      `api.descargar()` silently falls back to a default file name.
- [ ] **`URL.revokeObjectURL` runs in a `setTimeout(…, 0)` after the synthetic click**, not
      synchronously — revoking in the same tick cancels the download in some browsers. The vitest
      case must flush timers to assert the revoke.
- [ ] **The transitive licence graph is unverified until slice 1a runs the audit.** This design does
      not assert ClosedXML's graph is clean; it asserts the swap costs one file if it is not.
