# Design: Stage 13 — Stock inteligente (mínimos, alertas y reposición)

## Technical Approach

**One pure Domain rule, one raw upsert that never names `cantidad`, one read model seen three
times, and one rotation primitive with two callers.**

The proposal's governing constraint — *the checkout hot path pays nothing* — is met here not by
care but by **omission**: no file of any write path (`ServicioDeVentas`, `ServicioDeCompras`, the
transfer/ajuste/conteo/decomiso paths of `ServicioDeStock`) is opened by this stage at all. The
existing query-count guards (`VentasCheckoutTests`, asserting `16` without a lot-effective articulo
and `17` with one) stay byte-for-byte untouched, which is a stronger guarantee than a passing test:
there is no edit that could have moved them (decision 17).

Four structural facts decide the rest of the shape.

1. **The alert and the purchase suggestion are the same list.** `minimo IS NOT NULL AND cantidad
   <= minimo` is evaluated **once**, in one query builder, and consumed by the JSON report, its
   `/export` sibling and the Tablero tile — the stage-12 three-layer template, reused verbatim
   (`ObtenerVencimientosAsync` / `ObtenerResumenDeVencimientosAsync` /
   `ObtenerVencimientosParaExportacionAsync`). A second aggregation query is the one thing that
   could make the tile lie, so there is none.

2. **The comparison never leaves C#, and the arithmetic never touches a database.**
   `ReglaDeReposicion` is a pure static class in `Ways.Domain/Stock/`, `PoliticaDeRoles`-shaped:
   the `<=` boundary, the `sugerido` formula, the rotation averages, the honest nulls and the
   window's timezone resolution are all pure functions over hand-buildable inputs. That is what
   makes every arithmetic rule of the stage a three-line xUnit fact with no fixture — which
   matters because the proposal's risk #2 is *"a plausible and wrong figure"*, and a figure whose
   rule needs a Postgres container to be exercised gets tested once and drifts afterwards.

3. **Writing a threshold is a single statement.** `INSERT INTO stock (…, minimo, reposicion)
   VALUES (…, 0, $4, $5) ON CONFLICT (id_articulo, id_punto_venta) DO UPDATE SET minimo =
   EXCLUDED.minimo, reposicion = EXCLUDED.reposicion RETURNING cantidad, minimo, reposicion`.
   Create-if-missing, write, and read-back in one round trip, with **`cantidad` deliberately absent
   from the `SET` list** — the invariant `cantidad = SUM(movimientos)` is preserved by the *shape*
   of the statement, not by a rule someone must remember. No transaction, no ledger row, no
   `movimientos_stock` INSERT anywhere in the call path (decision 10).

4. **Rotation is one bounded aggregation, and the fork of `explore.md` §3 dissolves on
   inspection**, exactly as proposal decision 7 predicted — verified against the code: the raw SQL
   of `LectorDeSerieTemporal` exists for two reasons that rotation does not have (a
   `date_trunc('{0}', …)` literal inlined from a validated switch, and `timezone($1, fecha)`
   bucketing *inside* the query). Rotation is one `WHERE` over one instant range, one `GROUP BY
   (id_articulo)`, one `SUM` — plain LINQ over `db.MovimientosStock`, structurally identical to
   `ObtenerExistenciasAsync`, inheriting EF's global filters and the connection-level RLS GUCs for
   free. **No second raw-SQL file is opened, and `LectorDeSerieTemporal` is not touched.**

The fifth idea is the one that keeps this stage honest under review: **absence and zero are
different answers**, and the design makes the difference structural rather than documentary. An
articulo with no consumption history is *absent from the rotation result set* — not present with a
`0`. `sugerido` is `null`, never `0`, when `reposicion` is unset. `diasDeCobertura` is `null`, not
`∞` or `0`, when the articulo does not move. A row whose articulo has no proveedor habitual is
grouped under *Sin proveedor*, never filtered out. Every one of those is a named spec scenario and
a named mutation target below.

## Architecture Decisions

| # | Decision | Alternatives considered | Rationale |
|---|---|---|---|
| 1 | **`ReglaDeReposicion` is one pure static class in `Ways.Domain/Stock/`** holding `Clasificar`, `Sugerido`, `ConsumoDiario`, `MinimoSugerido`, `DiasDeCobertura` and `VentanaDeRotacion`. It takes primitives and `DateOnly`/`TimeZoneInfo`, never `IWaysDbContext` | Put the arithmetic inline in `ServicioDeReportesDeStock`; split it between the report and the service | The proposal's risk #2 is "a plausible and wrong figure". Inline arithmetic in a report service can only be exercised through an integration fixture, so each boundary case costs a seeded database and gets written once. As pure functions, the `<=` boundary at exactly `cantidad == minimo`, the `null`-vs-`0` rules and the window's zone conversion are unit facts — the `ReglaDeLotes`/`PoliticaDeRoles` pattern this repo already proved twice |
| 2 | **Two report shapes over one rule: `existencias` classifies *every* stocked row (`SinMinimo`/`Bajo`/`Ok`); `reposicion` returns *only* the `Bajo` rows** | One report with a `soloBajoMinimo` flag; a report of everything the web filters client-side | The proposal fixes both: existencias gains "`minimo`, `reposicion` and a derived `estado`" over its existing row set, and the reposición read model lists "rows where `minimo IS NOT NULL AND cantidad <= minimo`". They are different questions ("what is stocked here" vs "what do I buy"), have different consumers, different exports and different policies-adjacent screens. A shared flag would put both meanings behind one endpoint whose payload changes shape — the divergence surface decision 8 refuses for the tile, for the same reason |
| 3 | **The reposición query LEFT-JOINs `proveedores`; a row with no `id_proveedor_habitual` (or whose proveedor was soft-deleted) survives with `proveedor = null`** | Inner join; a second query for the names | An inner join silently deletes exactly the rows the operator most needs to see — the same lie-by-omission stage 12 refused when it added `SinFecha` rather than filtering NULL-expiry lots. The soft-delete case falls out of the same join: the EF global filter on `Proveedor` (`EntidadTenant`) hides the row, the LEFT JOIN yields `null`, and the articulo lands under *Sin proveedor* instead of vanishing. The join is **not** filtered by empresa: `articulos.id_proveedor_habitual` is an existing FK and is authoritative; adding an empresa predicate would blank the name of a real proveedor (`Proveedor.IdEmpresa` is nullable = shared, `explore.md` §4) |
| 4 | **Ordering is `id_proveedor_habitual` (NULLS LAST) then `id_articulo`** — ids, never names | `ORDER BY razon_social` | Postgres orders NULLs last in ASC by default (the criterion `ConstruirQueryDeVencimientos` already documents for `fecha_vencimiento`), so *Sin proveedor* lands at the end of the JSON **and** the export with no `NULLS LAST` clause and no client-side sort. Ordering by id is locale-free and deterministic — the same "ids, no nombres" criterion `NombreDeArchivo` uses. The web groups by folding an already-ordered list, never by sorting |
| 5 | **Rotation is one private primitive, `LeerConsumoAsync`, with two callers** (the reposición report and `GET /reportes/stock/rotacion`) | One aggregation per consumer | Two consumers computing "consumption" with two copies of the motivo filter is the exact defect the proposal's decision 7 spends three rules preventing. One method, one filter, one negation, one window |
| 6 | **What counts as consumption is `motivo = Venta` OR (`motivo = Anulacion` AND `id_comprobante_compra IS NULL`), and the figure is `-SUM(cantidad)`** | `motivo IN (venta, anulacion)`; `SUM(ABS(cantidad))`; count rows | Verified against the ledger: since stage 8 a *compra* anulación also writes `motivo = anulacion`, and `MovimientoStock.IdComprobanteCompra` is populated exactly on `compra` rows and the `anulacion` rows reversing them — so the naive filter **nets purchase reversals into sales**. The negation matters just as much: sale rows carry a negative `cantidad`, a sale anulación carries a positive one, and an **NCX travels the same path as a sale with a positive `cantidad`** (`ServicioDeVentas` negates the line's negative quantity). `-SUM` therefore nets returns out of demand automatically; `SUM(ABS(...))` would count a return as *extra* consumption. `Ajuste`, `Inventario`, `Decomiso`, `Transferencia` and `Reclasificacion` are excluded |
| 7 | **The rotation window is a pure Domain function, `VentanaDeRotacion(hoy, dias, zona) → (DesdeUtc, HastaUtcExclusivo)`**, and the query compares `CreadoEl` against those instants | `timezone($1, creado_el)` in SQL (the stage-10 shape); `DateOnly` arithmetic in UTC | `movimientos_stock.creado_el` is a `timestamptz` — an instant — while the window's *edges* are local days. Only the edges are zone-sensitive, so exactly one expression carries the zone, which is what makes the mutation surgical (replace it with `reloj.Ahora.UtcDateTime` and the boundary test flips a movement in or out). This is the stage-11 slice-9 bug class that needed the `08e7707` hardening commit, and it is a **binding verify criterion**. The helper also pins two edge cases a naive `ConvertTimeToUtc` throws on or silently mis-answers: an **invalid** local midnight (zones that shift at 24:00) advances to the shift instant, and an **ambiguous** midnight takes the standard offset — stated once, tested once, instead of discovered in production |
| 8 | **The tile calls the full report method, rotation included** | A "light" variant that skips the rotation aggregation for the tile | A flag that suppresses a computation makes `consumoDiarioPromedio = null` mean **two** things ("no history" and "not computed here"), which is precisely the ambiguity `dto-contract-honesty` exists to prevent, and it reintroduces the divergence surface stage 12 closed by reusing the report method. The tile's cost is one extra grouped read on a Tablero that already fires several panel queries, and a PV with **no** minimums configured costs exactly **one** query — the rotation read is skipped when the bajo-mínimo set is empty (decision 12), which is the common day-one case |
| 9 | **The tile carries three counts: `bajoMinimo`, `sinStock` (`cantidad <= 0`), `sinSugerencia` (`sugerido is null`)** — all folded from the report's own rows | One number (`bajoMinimo`); `sinProveedor` as the third | Three counts fit the `PanelDeVencimientos` shape and each answers a different question: how much is below the line, how much is **already gone** (the urgent subset — the shelf is empty *now*), and how much of the list is **not actionable yet** because nobody set a `reposicion`. `sinProveedor` was rejected as the third: it is already visible in the report as a whole group, whereas `sinSugerencia` is otherwise invisible (a column of nulls) and is the metric that nags the owner into finishing the configuration |
| 10 | **The write is ONE `INSERT … ON CONFLICT … DO UPDATE` whose `SET` list names only `minimo` and `reposicion`** — no transaction, no `BloquearYCrearSiFaltaStockAsync`, no ledger row | Reuse `BloquearYCrearSiFaltaStockAsync` then `UPDATE`; EF `SELECT`-then-`Add`/`Update`; fabricate a zero `ajuste` movement | The lock-then-update pair needs a transaction and two statements to produce the same row; the EF form has a TOCTOU window between the `SELECT` and the `INSERT` that the unique PK turns into a raw `23505`. The chosen statement creates the row with `cantidad = 0` when absent, and — because `cantidad` is **absent from the `SET` list** — provably cannot perturb an existing balance. The invariant holds trivially at `0 = 0` on create and is untouched on update. The zero-movement alternative is not merely useless: `ck_movimientos_stock_cantidad_no_cero` rejects it |
| 11 | **`PUT /api/stock/minimos` replaces BOTH fields on every call; `null` clears. The response is the persisted row read back from `RETURNING`** | PATCH semantics ("absent means keep") | System.Text.Json cannot distinguish "absent" from "explicit null" without a wrapper type, so PATCH semantics would silently make `{"minimo": null}` a no-op — a field accepted and dropped, `dto-contract-honesty` rule 1. Full replace also gives *unmanage* a natural expression (send both null). The response echoing `cantidad`/`minimo`/`reposicion`/`estado` from the same statement is what lets the grid render the authoritative row **without a post-write refetch**, killing an entire class of `react-async-state` defects (decision 16) |
| 12 | **The rotation read is skipped entirely when the bajo-mínimo set is empty, and is bounded to those articulos when it is not** | Always aggregate over the PV's whole catalog | The bajo set is small by construction (managed **and** below the line). Bounding the `GROUP BY` to it keeps the report's second query proportional to what is actually reported, and skipping it at zero rows makes "a PV with no minimums configured costs one query and returns zero rows" a testable claim rather than a hope. `GET /reportes/stock/rotacion` — whose whole purpose is the *unbounded* view for the editor — passes `null` and aggregates over the PV, the one place where that cost is the point |
| 13 | **The reposición export is the AGGREGATE cap shape** (guard on `TablaExportable.Filas.Count` after mapping, no `COUNT(*)`), and the export endpoint calls **the same method as the JSON** — there is no `ObtenerReposicionParaExportacionAsync` twin | The LISTING shape (`Contar → rechazar → Take(tope+1)`), like vencimientos | Stage-11 decision 6 splits the shapes by whether the row set is bounded *by construction*. Vencimientos is bounded by the lot count, which **grows monotonically with time**; reposición is bounded by the catalog and is a strict subset of existencias, which already ships the aggregate shape. One method for both surfaces makes "the export's figures equal the endpoint's" structural rather than asserted — and the export still **refuses rather than truncates** at cap |
| 14 | **`minimoSugerido` lives on its own endpoint (`GET /api/reportes/stock/rotacion`), not as fields on `FilaExistencia`** | Widen `Existencias`/`FilaExistencia` with rotation columns; a `?conSugerencia=` flag | Three reasons, in order of weight: (a) the proposal's **pre-approved degradation** is "ship the tile, drop `minimoSugerido`" — with a separate endpoint that is a clean non-delivery, while with widened fields it is removing already-shipped contract fields; (b) existencias would otherwise pay the rotation aggregation on every load for a column most rows cannot fill; (c) **absence is the honest encoding of "no history"** — an articulo with no qualifying movement is simply *not a row* of the rotation response, so the screen shows no suggestion without any field having to mean "unknown". The flag variant is rejected for the reason in decision 8 |
| 15 | **Existencias edits inline, one row at a time; opening another row while a write is outstanding is BLOCKED, not token-reconciled** | A modal per row; free multi-row editing with per-row tokens | The task is "walk the list and set numbers" — a modal turns N edits into 3N clicks and puts the value being typed on a different surface from the value being compared. Free multi-row editing is the exact shape `react-async-state` rule 9 was written from: supersede-during-write mutated across four consecutive review rounds in this repo before blocking the window killed the class. One `filaEnEdicion`, one outstanding write, tokens retained for READ staleness only |
| 16 | **No post-write refetch of the report.** The row is patched from the write's authoritative response through a functional updater built from `prev` | Refetch the whole existencias report after each save | A refetch per row is one extra round trip per keystroke-set **and** the surface where "a committed write reported as a failure" (`react-async-state` rule 6) is born. The `RETURNING` already carries the persisted truth including the recomputed `estado`; there is nothing left to re-read. What the refetch would have caught — a concurrent sale changing `cantidad` — is not a correctness problem here, because the write never touches `cantidad` |
| 17 | **No file of any write path is opened by this stage** — the checkout budget is protected structurally, not by discipline | Add `minimo` to the checkout's `RETURNING` (free in round trips) and warn | Proposal decision 8 settles the product question. The design consequence is worth stating as a decision because it is what makes the success criterion cheap: the guards in `VentasCheckoutTests` (`16` / `17`) are **not edited**, so "no more round-trips than before" is proven by an unchanged constant over unchanged code rather than by a new test asserting a hoped-for number |

## Interfaces / Contracts

### Domain — pure, no database (`PoliticaDeRoles` pattern)

```csharp
// Ways.Domain/Stock/ReglaDeReposicion.cs

/// Wire values are the C# member names (JsonStringEnumConverter, no naming policy — the
/// EstadoDeVencimiento precedent): "SinMinimo" | "Bajo" | "Ok".
public enum EstadoDeReposicion { SinMinimo, Bajo, Ok }

public static class ReglaDeReposicion
{
    /// Decisión 1 del proposal: minimo NULL ⇒ no gestionado (nunca alerta); el borde es
    /// cantidad <= minimo, NUNCA <.
    public static EstadoDeReposicion Clasificar(decimal cantidad, decimal? minimo)
        => minimo is null ? EstadoDeReposicion.SinMinimo
         : cantidad <= minimo.Value ? EstadoDeReposicion.Bajo
         : EstadoDeReposicion.Ok;

    /// Decisión 3/4 del proposal: null (JAMÁS 0) cuando no hay nivel objetivo; sin término
    /// "en tránsito" — ese sustraendo lo agrega la etapa 16.
    public static decimal? Sugerido(decimal cantidad, decimal? reposicion)
        => reposicion is null ? null : Math.Max(0m, reposicion.Value - cantidad);

    /// netoConsumido null ⇒ NINGÚN movimiento de consumo en la ventana: no hay historia que
    /// promediar y la respuesta honesta es "no sé", no "cero" (proposal, riesgo 3).
    /// diasVentana >= 1 lo garantiza el llamador (ExigirVentanaValida).
    public static decimal? ConsumoDiario(decimal? netoConsumido, int diasVentana);

    /// consumoDiario x diasCoberturaObjetivo, 3 decimales (la precisión de numeric(12,3)).
    public static decimal? MinimoSugerido(decimal? consumoDiario, int diasCoberturaObjetivo);

    /// null cuando el consumo diario es null (sin historia) O cero (no rota): "infinito" no es
    /// un número de días y 0 tampoco.
    public static decimal? DiasDeCobertura(decimal cantidad, decimal? consumoDiario);

    /// Decisión 7 del proposal: la ventana [hoy - (dias-1) .. hoy] resuelta en la zona del PV y
    /// devuelta como instantes. Medianoche local INVÁLIDA (zonas que saltan a las 24:00) avanza
    /// al instante del salto; medianoche AMBIGUA toma el offset estándar.
    public static (DateTimeOffset DesdeUtc, DateTimeOffset HastaUtcExclusivo) VentanaDeRotacion(
        DateOnly hoy, int dias, TimeZoneInfo zona);

    /// 400 dias_rotacion_invalido / 400 dias_cobertura_invalido — un parámetro <= 0 dividiría
    /// por cero o produciría una sugerencia negativa (refinamiento sobre el proposal).
    public static int ExigirVentanaValida(int dias, string codigo);
}
```

```csharp
// Ways.Domain/Catalogos/ParametroConocido.cs — dos entradas, SIN migración (patrón stage-10/12)
public static readonly ParametroConocido DiasRotacion          = new("dias_rotacion", typeof(int), "30");
public static readonly ParametroConocido DiasCoberturaObjetivo = new("dias_cobertura_objetivo", typeof(int), "7");
// … y ambas agregadas al diccionario PorClave (sin eso, Buscar() las rechaza como desconocidas).
```

### Application — `Ways.Application.Stock.Contratos`

```csharp
/// PUT /api/stock/minimos — REEMPLAZO COMPLETO de ambos umbrales (decisión 11): un null limpia.
/// Ningún campo sin destino: los cuatro se leen en EscribirMinimosAsync.
public sealed record SolicitudDeMinimos(int IdPuntoVenta, int IdArticulo, decimal? Minimo, decimal? Reposicion);

/// Respuesta del PUT — la fila PERSISTIDA leída del RETURNING del mismo statement, más el
/// estado derivado, para que la grilla renderice la verdad del servidor sin refetch (decisión 16).
public sealed record MinimosDeStock(
    int IdPuntoVenta, int IdArticulo, decimal Cantidad, decimal? Minimo, decimal? Reposicion,
    EstadoDeReposicion Estado);
```

### Application — `Ways.Application.Reportes.Contratos`

```csharp
// --- existencias (slice 2): el record EXISTENTE gana tres campos; Existencias no cambia.
public sealed record FilaExistencia(
    int IdArticulo, string Nombre, decimal Cantidad,
    decimal? Minimo, decimal? Reposicion, EstadoDeReposicion Estado);

// --- reposición (slice 4). Los cuatro campos de rotación NO existen en la slice 4: los agrega
//     la slice 5, cuando hay algo que los compute (dto-contract-honesty rule 3, decisión de
//     slicing más abajo).
public sealed record FilaDeReposicion(
    int IdArticulo, string Articulo, decimal Cantidad, decimal Minimo, decimal? Reposicion,
    decimal? Sugerido, int? IdProveedor, string? Proveedor,
    // + slice 5:
    decimal? ConsumoDiarioPromedio, decimal? DiasDeCobertura);
//   Minimo es decimal NO nullable: la fila existe porque minimo IS NOT NULL.
//   Sugerido      → null (nunca 0) cuando Reposicion es null (proposal decisión 3/4).
//   IdProveedor/Proveedor → ambos null ⇒ el grupo "Sin proveedor" (decisión 3).
//   DiasDeCobertura → null cuando el artículo no rota (decisión 1: ni ∞ ni 0).

public sealed record Reposicion(
    int IdPuntoVenta, DateOnly Hoy, int DiasDeRotacion, string ZonaHoraria,
    IReadOnlyList<FilaDeReposicion> Filas);

/// Tile de Tablero — los tres conteos salen de Reposicion.Filas, nunca de una segunda query.
public sealed record ResumenDeReposicion(
    int IdPuntoVenta, int BajoMinimo, int SinStock, int SinSugerencia);

// --- rotación (slice 5): UNA FILA POR ARTÍCULO CON CONSUMO EN LA VENTANA. Un artículo sin
//     historia NO ES UNA FILA — la ausencia es la respuesta (decisión 14).
public sealed record FilaDeRotacion(
    int IdArticulo, string Articulo, decimal ConsumoEnVentana, decimal ConsumoDiarioPromedio,
    decimal MinimoSugerido);

public sealed record Rotacion(
    int IdPuntoVenta, DateOnly Hoy, int DiasDeRotacion, int DiasCoberturaObjetivo,
    string ZonaHoraria, IReadOnlyList<FilaDeRotacion> Filas);
```

### Application — service surfaces

```csharp
// ServicioDeStock (slice 1) — el ÚNICO método nuevo, y no abre ninguna transacción.
public async Task<MinimosDeStock> EscribirMinimosAsync(SolicitudDeMinimos solicitud, CancellationToken ct);

// ServicioDeReportesDeStock
public Task<Existencias>          ObtenerExistenciasAsync(int idPuntoVenta, CancellationToken ct);            // slice 2: gana 3 columnas
public Task<Reposicion>           ObtenerReposicionAsync(int idPuntoVenta, int? dias, CancellationToken ct);  // slice 4 (+ rotación en la 5)
public Task<ResumenDeReposicion>  ObtenerResumenDeReposicionAsync(int idPuntoVenta, CancellationToken ct);    // slice 7 — reusa la de arriba
public Task<Rotacion>             ObtenerRotacionAsync(int idPuntoVenta, int? dias, CancellationToken ct);    // slice 5

// privados, slice 5 — UNA definición de consumo, dos llamadores (decisión 5)
private Task<IReadOnlyDictionary<int, decimal>> LeerConsumoAsync(
    int idPuntoVenta, IReadOnlyList<int>? idsArticulo, DateTimeOffset desdeUtc, DateTimeOffset hastaUtcExclusivo,
    CancellationToken ct);
private Task<int> ResolverDiasRotacionAsync(int idEmpresa, int idPuntoVenta, CancellationToken ct);
private Task<int> ResolverDiasCoberturaAsync(int idEmpresa, int idPuntoVenta, CancellationToken ct);
```

`ResolverContextoAsync` (existing, stage 12) is reused verbatim for `(idEmpresa, zonaId, hoy)` —
this stage adds no second way to resolve "hoy".

### API surface

| Route | Policy | Notes |
|---|---|---|
| `PUT /api/stock/minimos` | `GestionDeCatalogo` **stacked** over the group's `OperacionDePos` | Admin-only, exactly like `/ajustes`, `/transferencias`, `/conteos`, `/decomiso`. **A Supervisor gets 403 here and 200 on the report** |
| `GET /api/reportes/stock/existencias` | `LecturaDeReportes` (group) | Unchanged route; response gains `minimo`/`reposicion`/`estado` |
| `GET /api/reportes/stock/existencias/export` | inherited by co-location | Three new columns; unchanged aggregate cap shape |
| `GET /api/reportes/stock/reposicion?idPuntoVenta[&dias]` | inherited | The read model |
| `GET /api/reportes/stock/reposicion/export?…&formato=xlsx` | inherited | Sibling declared immediately after its source route; **same method as the JSON** (decision 13) |
| `GET /api/reportes/stock/reposicion/resumen?idPuntoVenta` | inherited | Tablero tile counts |
| `GET /api/reportes/stock/rotacion?idPuntoVenta[&dias]` | inherited | Feed of `minimoSugerido` for the editor (decision 14) — **the droppable half of slice 7** |

`dias` is optional on both rotation-bearing routes and defaults to the resolved `dias_rotacion`,
mirroring `vencimientos?dias=` exactly (stage 12). It is a *refinement over the proposal*, recorded
in Open Questions.

### New domain error codes

`minimo_negativo` (400 — covers `minimo < 0` **and** `reposicion < 0`, one family code with a
message naming the offending field, the `cantidad_de_ajuste_invalida`/decomiso precedent) ·
`reposicion_menor_que_minimo` (400) · `minimo_invalido` (400 — more than 3 decimals; without it
Postgres would silently **round** a value the operator typed) · `dias_rotacion_invalido` (400) ·
`dias_cobertura_invalido` (400).

Reused unchanged: `referencia_invalida` (400, unknown articulo — pre-checked so the real FK never
surfaces as a raw 500) and the `404` of `ResolverPuntoVentaAsync` (ADR-8: same 404 for "does not
exist" and "belongs to another tenant").

### Web — `src/api/tipos.ts` mirrors

```ts
export type EstadoDeReposicion = 'SinMinimo' | 'Bajo' | 'Ok'   // nombres de miembro de C#, no snake_case

export type FilaExistencia = { idArticulo: number; nombre: string; cantidad: number
                               minimo: number | null; reposicion: number | null; estado: EstadoDeReposicion }
export type SolicitudDeMinimos = { idPuntoVenta: number; idArticulo: number
                                   minimo: number | null; reposicion: number | null }
export type MinimosDeStock = { idPuntoVenta: number; idArticulo: number; cantidad: number
                               minimo: number | null; reposicion: number | null; estado: EstadoDeReposicion }
export type FilaDeReposicion = { idArticulo: number; articulo: string; cantidad: number; minimo: number
                                 reposicion: number | null; sugerido: number | null
                                 idProveedor: number | null; proveedor: string | null
                                 consumoDiarioPromedio: number | null; diasDeCobertura: number | null }
export type Reposicion = { idPuntoVenta: number; hoy: string; diasDeRotacion: number
                           zonaHoraria: string; filas: FilaDeReposicion[] }
export type ResumenDeReposicion = { idPuntoVenta: number; bajoMinimo: number; sinStock: number; sinSugerencia: number }
export type FilaDeRotacion = { idArticulo: number; articulo: string; consumoEnVentana: number
                               consumoDiarioPromedio: number; minimoSugerido: number }
export type Rotacion = { idPuntoVenta: number; hoy: string; diasDeRotacion: number
                         diasCoberturaObjetivo: number; zonaHoraria: string; filas: FilaDeRotacion[] }
```

## Read model — the reposición query, end to end

```csharp
/// Cláusulas bajo prueba (mutation-proof-tests), en orden de daño si se pierden:
///   s.Minimo != null          → sin ella, TODO artículo sin mínimo alerta (el día uno catastrófico
///                               que la decisión 1 del proposal existe para evitar)
///   s.Cantidad <= s.Minimo    → con < , el artículo EXACTAMENTE en el punto de pedido desaparece
///   s.IdPuntoVenta == idPv    → mezclar dos PVs del mismo tenant rompe el reporte (misma familia
///                               de bug que ObtenerExistenciasAsync documenta)
///   DefaultIfEmpty()          → sin el LEFT JOIN, las filas "Sin proveedor" desaparecen en silencio
private IQueryable<FilaCrudaDeReposicion> ConstruirQueryDeReposicion(int idPuntoVenta) =>
    from s in db.Stock
    where s.IdPuntoVenta == idPuntoVenta && s.Minimo != null && s.Cantidad <= s.Minimo
    join a in db.Articulos on s.IdArticulo equals a.Id
    join p in db.Proveedores on a.IdProveedorHabitual equals p.Id into candidatos
    from p in candidatos.DefaultIfEmpty()
    // Postgres ordena NULL último en ASC por default (mismo criterio que ConstruirQueryDeVencimientos):
    // "Sin proveedor" cae al final sin NULLS LAST explícito. El orderby va ANTES del select hacia el
    // record — EF no traduce un OrderBy sobre la propiedad de un objeto recién construido.
    orderby a.IdProveedorHabitual, a.Id
    select new FilaCrudaDeReposicion(
        a.Id, a.Nombre, s.Cantidad, s.Minimo!.Value, s.Reposicion,
        a.IdProveedorHabitual, p == null ? null : p.RazonSocial);
```

```
ObtenerReposicionAsync(idPuntoVenta, dias?)
  ├─ ResolverContextoAsync                     → (idEmpresa, zonaId, hoy)      [1 query + parametros]
  ├─ diasDeRotacion := dias ?? dias_rotacion   → ExigirVentanaValida            [400 si <= 0]
  ├─ filas := ConstruirQueryDeReposicion(pv).ToListAsync()                      [1 query]
  ├─ SI filas.Count == 0  →  devolver Reposicion(…, []) SIN tocar movimientos_stock  (decisión 12)
  ├─ (desdeUtc, hastaUtc) := ReglaDeReposicion.VentanaDeRotacion(hoy, diasDeRotacion, zona)   [puro]
  ├─ consumo := LeerConsumoAsync(pv, filas.ids, desdeUtc, hastaUtc)             [1 query]
  └─ proyección pura por fila:
         Sugerido              := ReglaDeReposicion.Sugerido(cantidad, reposicion)
         ConsumoDiarioPromedio := ReglaDeReposicion.ConsumoDiario(consumo.TryGet(id) ? -neto : null, dias)
         DiasDeCobertura       := ReglaDeReposicion.DiasDeCobertura(cantidad, consumoDiarioPromedio)
```

`LeerConsumoAsync` — the one definition of consumption (decision 5/6):

```csharp
// Cláusula bajo prueba: (m.Motivo == Anulacion && m.IdComprobanteCompra == null).
// Sin el segundo término, la anulación de una COMPRA se netea dentro de las ventas.
var query = db.MovimientosStock
    .Where(m => m.IdPuntoVenta == idPuntoVenta
             && m.CreadoEl >= desdeUtc && m.CreadoEl < hastaUtcExclusivo
             && (m.Motivo == MotivoStock.Venta
                 || (m.Motivo == MotivoStock.Anulacion && m.IdComprobanteCompra == null)));

if (idsArticulo is not null) query = query.Where(m => idsArticulo.Contains(m.IdArticulo));

return await query
    .GroupBy(m => m.IdArticulo)
    .Select(g => new { IdArticulo = g.Key, Neto = g.Sum(m => m.Cantidad) })
    .ToDictionaryAsync(x => x.IdArticulo, x => x.Neto, ct);
// El llamador NIEGA: las filas de venta llevan cantidad negativa, la anulación de venta positiva
// y una NCX viaja como venta con cantidad POSITIVA — -SUM netea devoluciones sin un caso especial.
```

## Write path — `PUT /api/stock/minimos`

```csharp
public async Task<MinimosDeStock> EscribirMinimosAsync(SolicitudDeMinimos solicitud, CancellationToken ct)
{
    var idTenant = ExigirTenantDeLaSesion();

    // 1. Validación EN MEMORIA primero (disciplina de la casa: un request mal formado no amerita
    //    ni un SELECT).
    ExigirUmbralValido(solicitud.Minimo, "mínimo");
    ExigirUmbralValido(solicitud.Reposicion, "reposición");
    if (solicitud.Minimo is { } m && solicitud.Reposicion is { } r && r < m)
        throw new ErrorDominio("reposicion_menor_que_minimo", …, 400);

    // 2. Pre-checks de existencia/tenant, reusando los helpers del archivo — nunca dejar que la FK
    //    real rechace con un 500 crudo dentro del statement de abajo.
    await ResolverArticuloAsync(solicitud.IdArticulo, ct);      // 400 referencia_invalida
    await ResolverPuntoVentaAsync(solicitud.IdPuntoVenta, ct);  // 404 (ADR-8)

    // 3. UN statement. Sin transacción, sin movimiento, sin tocar cantidad.
    var conexion = await ObtenerConexionAbiertaAsync(ct);
    var fila = await UpsertParametrosDeReposicionAsync(conexion, idTenant, solicitud, ct);

    return new MinimosDeStock(
        solicitud.IdPuntoVenta, solicitud.IdArticulo, fila.Cantidad, fila.Minimo, fila.Reposicion,
        ReglaDeReposicion.Clasificar(fila.Cantidad, fila.Minimo));
}
```

```sql
-- cantidad ESTÁ en el VALUES (crear la fila en 0) y NO ESTÁ en el SET (jamás pisar un saldo).
-- Esa asimetría es la decisión 10 entera; es el mutation target nombrado del slice 1.
INSERT INTO stock (id_articulo, id_punto_venta, id_tenant, cantidad, minimo, reposicion)
VALUES ($1, $2, $3, 0, $4, $5)
ON CONFLICT (id_articulo, id_punto_venta) DO UPDATE
SET minimo = EXCLUDED.minimo, reposicion = EXCLUDED.reposicion
RETURNING cantidad, minimo, reposicion
```

**Both fields null is legal** and is the *unmanage* operation. On a missing row it creates an inert
`cantidad = 0` row with no thresholds — the same residue `ContarAsync`'s no-op upsert already
leaves, and a row the existencias report shows as `SinMinimo` with `0`. Refusing it would break the
only way to unmanage an articulo, and branching on row existence would cost the atomicity the
single statement buys.

**No `stock_lotes` involvement, ever.** A reorder point is per `(articulo, punto de venta)`; a lot
is an expiry identity, not a replenishment unit (proposal, out of scope).

## Web composition

### `Existencias.tsx` — the per-punto-de-venta stock screen (slices 3 and 7)

Columns: `Artículo · Nombre · Cantidad · Mínimo · Reposición · Estado · (Sugerido)`. State contract,
pinned so `sdd-tasks` does not have to re-derive it from `react-async-state`:

```ts
const generacionRef = useRef(0)                 // staleness de LECTURAS (rule 2), ya existe
const [filaEnEdicion, setFilaEnEdicion] = useState<number | null>(null)   // idArticulo, UNA sola
const [guardando, setGuardando] = useState<number | null>(null)           // idArticulo en vuelo
```

- `guardando !== null` ⇒ **every** row's "Editar", the PV selector, the download button and the
  add-row are `disabled` (rule 9: block supersede-during-write; tokens stay for reads only). The
  handler's first line is `if (guardando !== null) return` — a same-tick double click beats the
  `disabled` re-render.
- The save applies `MinimosDeStock` into the list with a functional updater from `prev` (rule 1),
  gated on its own captured token (rule 2), and resets `guardando` in a **token-gated** `finally`
  (rule 4). No refetch (decision 16), so rule 6's "committed write reported as a failure" class
  cannot occur — recorded as the reason, not as luck.
- `'' → null` coercion for both inputs lives in a pure helper, `aSolicitudDeMinimos(idPv, idArticulo,
  minimoTexto, reposicionTexto)`, with colocated unit tests (`web-descriptor-tests`): `''`, `'0'`,
  `'2.5'`, `'-1'`, `'1,5'`, and the both-empty (unmanage) case.
- Client-side pre-validation mirrors `reposicion_menor_que_minimo` and disables the save while the
  aviso is visible — `react-async-state` rule 7: the copy must not claim a block the UI does not
  enforce. The server code remains authoritative and is rendered when it arrives.
- **Add-row**: an articulo lookup over `clienteDeArticulos` (the pattern `Transferencias.tsx`
  already uses), which appends a row with `cantidad = 0` locally and saves through the same PUT.
  The report's meaning is unchanged — the row exists after the write because the write created it.
- Slice 7 adds the `Sugerido` column, fed by `clienteDeReportes.rotacion(idPuntoVenta)` fetched
  alongside the report and indexed by `idArticulo`; an articulo absent from that map renders `—`,
  never `0`.

### `Reposicion.tsx` — grouped by proveedor (slice 6)

- Fetches `clienteDeReportes.reposicion(idPuntoVenta, null)`; PV selector + `BotonDeDescarga`
  pointing at `rutasDeExportacion.reposicion(idPuntoVenta)`, the `Existencias.tsx` shape verbatim.
- Grouping is a **fold over the already-ordered list** in a pure helper,
  `agruparPorProveedor(filas) → { idProveedor, proveedor, filas }[]`, with colocated unit tests
  covering: two proveedores in server order, a *Sin proveedor* bucket landing **last**, a single
  row, and the empty list. No client-side sort (decision 4).
- Header per group shows the proveedor and its row count; `sugerido` renders `—` when null, never
  `0` — asserted by a component test, because that is the cell the operator would otherwise read as
  "buy nothing".
- Route + nav entry alongside `/reportes/stock/vencimientos`.

### `Tablero.tsx` — `PanelDeReposicion` (slice 7)

`PanelDeVencimientos` cloned: `usePanelDeReporte`, requires a concrete `idPuntoVenta` (neutral copy
otherwise, never a fabricated figure), `PanelDeError` + retry, `Link` to the report, and **one
`data-testid` per metric** — `reposicion-tile-bajo-minimo`, `reposicion-tile-sin-stock`,
`reposicion-tile-sin-sugerencia` — which is the stage-12 slice-15 lesson: a tile without per-figure
testids can only be asserted as a blob, and a blob assertion cannot catch two swapped counts.

## File Changes

| File | Action | Description |
|---|---|---|
| `src/Ways.Domain/Stock/ReglaDeReposicion.cs` | Create | The one pure rule + `EstadoDeReposicion` (decision 1) |
| `src/Ways.Domain/Catalogos/ParametroConocido.cs` | Modify | 2 keys **+ both added to `PorClave`** |
| `src/Ways.Application/Stock/ServicioDeStock.cs` | Modify | `EscribirMinimosAsync`, `UpsertParametrosDeReposicionAsync`, `ExigirUmbralValido` |
| `src/Ways.Application/Stock/Contratos.cs` | Modify | `SolicitudDeMinimos`, `MinimosDeStock` |
| `src/Ways.Application/Reportes/ServicioDeReportesDeStock.cs` | Modify | `estado` on existencias; `ConstruirQueryDeReposicion`; `ObtenerReposicionAsync`/`…ResumenDeReposicionAsync`/`ObtenerRotacionAsync`; `LeerConsumoAsync`; 2 parametro resolvers |
| `src/Ways.Application/Reportes/Contratos.cs` | Modify | `FilaExistencia` +3 fields; `FilaDeReposicion`/`Reposicion`/`ResumenDeReposicion`/`FilaDeRotacion`/`Rotacion` |
| `src/Ways.Application/Reportes/ExportacionDeReportes.cs` | Modify | 3 columns on `ColumnasExistencias`; one `De(Reposicion, ctx)` mapper + its columns |
| `src/Ways.Api/Endpoints/StockEndpoints.cs` | Modify | `PUT /minimos` (Admin, one route line) |
| `src/Ways.Api/Endpoints/ReportesEndpoints.cs` | Modify | 4 routes (`/reposicion`, `/reposicion/export`, `/reposicion/resumen`, `/rotacion`) |
| `src/Ways.Web/src/api/{tipos,reportes,stock}.ts` | Modify | Mirrors, `clienteDeReportes.{reposicion,reposicionResumen,rotacion}`, `rutasDeExportacion.reposicion`, `clienteDeStock.escribirMinimos` |
| `src/Ways.Web/src/paginas/Existencias.tsx` | Modify | Editor grid + add-row + `Sugerido` column |
| `src/Ways.Web/src/paginas/Reposicion.tsx` | Create | Grouped list + download |
| `src/Ways.Web/src/paginas/Tablero.tsx` | Modify | `PanelDeReposicion` |
| `src/Ways.Web/src/App.tsx` · `componentes/Layout.tsx` | Modify | One route + one nav line |
| `docs/11-programa-post-paridad.md` | Modify | Backlog row 367 re-registered to `stage-13b-conteo-por-planilla` (proposal decision 5) |
| **Database** | **NONE** | Zero migrations. See the gate section of `proposal.md` |

## Testing Strategy

| Layer | What | Approach |
|---|---|---|
| Domain unit (no DB) | `Clasificar` at `cantidad = minimo - 1 / = minimo / = minimo + 1`, at `minimo = 0` with `cantidad = 0` and `= -1` (negative balances are legal), and `minimo = null`; `Sugerido` null-vs-0 and with negative `cantidad`; `ConsumoDiario` for `null` / `0` / positive / **negative net** (returns exceed sales ⇒ clamped to 0, still not null); `MinimoSugerido` rounding at 3 decimals; `DiasDeCobertura` null on null **and** on zero consumption; `VentanaDeRotacion` for a UTC zone, a `-03:00` zone, `dias = 1` (today only), and a zone whose local midnight is invalid | Hand-built primitives, xUnit — the `PoliticaDeRoles` pattern. No fixture, no container |
| Application unit | `ExigirVentanaValida` rejects `0` and `-1` with the two distinct codes | Pure |
| Integration — write path | (1) minimum on an articulo **with no `stock` row** ⇒ row created, `cantidad = 0`, and **`SELECT COUNT(*) FROM movimientos_stock` for that pair is exactly 0, asserted before and after**; (2) minimum on a row with `cantidad = 5` ⇒ `cantidad` still `5` and still zero new movements; (3) round-trip: PUT then GET existencias returns the persisted pair; (4) PUT with both null clears a previously-set pair; (5) each of the five refusal codes with its HTTP status | The `ServicioDeStock` integration shape. Assertion (2) is what the `SET`-list mutation must break |
| Integration — the read model, with **discriminating seeds** | One PV seeded with: an articulo `cantidad = minimo` (**appears**), one `cantidad = minimo + 0.001` (absent), one `minimo = null` with `cantidad = 0` (absent), one `minimo = 0` with `cantidad = 0` (appears), one below minimum with `id_proveedor_habitual = null` (**appears, `proveedor` null, ordered LAST**), one whose proveedor is soft-deleted (appears under *Sin proveedor*), one below minimum **at another PV** (absent), one below minimum **of another tenant** (absent), one with `reposicion` unset (`sugerido` **null**, not 0). Every field of every row asserted, with **different values per row and column** | `mutation-proof-tests` rules 4 and 6. Row order asserted as a sequence, not a set |
| Integration — the netting trap, by name | `LaRotacionNoNeteaLaAnulacionDeUnaCompraDentroDeLasVentas`: seed compra → confirm → **anular la compra** (writes `motivo = anulacion` **with** `id_comprobante_compra`) → sale → **anular la venta** (`motivo = anulacion`, `id_comprobante_compra` null) → assert `consumoEnVentana` equals exactly the sale minus its own anulación, with the compra's reversal magnitude chosen **different** from every other figure so it cannot be produced by any other combination | The named mutation is deleting `&& m.IdComprobanteCompra == null` (rule 1/4) |
| Integration — the motivo exclusions | A mixed sequence containing `ajuste`, `inventario`, `decomiso`, `transferencia` **and** `reclasificacion`, each with a distinct magnitude, asserting `consumoEnVentana` is unchanged from the sales-only baseline | One test, five distinct magnitudes — a partial exclusion is detectable |
| Integration — the clock, fixed at midday UTC | Clock pinned at `2026-08-14T12:00:00Z`, PV zone `America/Argentina/Buenos_Aires` (local `09:00`), `dias = 1`. A movement at `2026-08-14T02:00:00Z` (local `2026-08-13 23:00`) is **outside** the window; one at `2026-08-14T04:00:00Z` (local `01:00`) is **inside**. Both magnitudes differ | The named mutation replaces `VentanaDeRotacion`'s zone conversion with `hoy` computed from `reloj.Ahora.UtcDateTime` — the boundary movement flips sides. Midday UTC is deliberate: it keeps `hoy` stable in both UTC and `-03:00`, so the *window edges* alone carry the assertion (rule 3: widen the data until the clause decides) |
| Integration — export equality | `/reposicion` and `/reposicion/export` with the identical query string; read the workbook back and compare **every cell of every row**, plus the `Sin proveedor` row's empty proveedor cell and a null `sugerido` cell rendering **empty, not `0`**; and a cap refusal (`TopeDeFilas` lowered) that **refuses rather than truncates** | `mutation-proof-tests` rule 6, the stage-11 `ExportacionTests` shape |
| Integration — tile ≡ report | `/reposicion/resumen` counts equal the counts folded from `/reposicion`'s rows, over a seed with **all three metrics distinct** (`bajoMinimo = 4`, `sinStock = 2`, `sinSugerencia = 1`) so no two can be swapped without detection | Identical figures cannot detect a swapped fold |
| Integration — the empty case, by query count | A PV with hundreds of stocked articulos and **zero** minimums returns zero rows **and issues no query against `movimientos_stock`** | `ContadorDeComandos`, asserting an exact constant (never a `<=`) — proves decision 12's skip |
| Integration — authorization | Vendedor ⇒ `403` on all five new/modified routes; **Supervisor ⇒ `200` on `/existencias` (reading `minimo`/`reposicion`/`estado`) and `403` on `PUT /minimos`** — the testable form of proposal decision 6's rule (a) | One test per role per route; the `403` on the PUT is the mutation target for the `RequireAuthorization` line |
| Integration — RLS | Cross-tenant `SELECT`/`UPDATE` of `stock.minimo` and a cross-tenant `INSERT` through the upsert, over the **`ways_app`** connection (NOSUPERUSER NOBYPASSRLS) at statement level, asserting **row counts** for the silent 0-row cases and `42501` where an error is actually raised | `mutation-proof-tests` rule 5. `stock` already carries its policy — this proves the new *statement* respects it, not the schema |
| Web (vitest) | `aSolicitudDeMinimos` coercion branches; `agruparPorProveedor` (order, *Sin proveedor* last, empty); the editor: save applies the authoritative response **without** a refetch, a stale read landing after a save is discarded (**stale promise resolved inside `act`**, asserted synchronously after the flush — rule 7), double-click on save ⇒ exactly one `fetch`, and open-row-B **blocked** while row A is saving; `Reposicion.tsx` renders `—` for a null `sugerido`; the tile asserted through its three testids | `web-descriptor-tests` (colocated `*.test.ts(x)`), `react-async-state` |
| Exempt | The tile's visual layout has no automated assertion beyond the three counts equalling the report's — recorded exemption, inherited from stage 12 | — |

**Mutation targets** (`mutation-proof-tests`: name the clause, run the mutation, record
applied → failing test → reverted → green in the PR body):

| Slice | Clause | Mutation | Test that MUST fail |
|---|---|---|---|
| 1 | the `SET` list of `UpsertParametrosDeReposicionAsync` | add `cantidad = EXCLUDED.cantidad` | write a minimum over a row with `cantidad = 5` ⇒ balance unchanged |
| 1 | `ReglaDeReposicion.Clasificar`'s `<=` | change to `<` | Domain fact at exactly `cantidad == minimo` |
| 1 | `reposicion.Value - cantidad` guarded by `reposicion is null` | return `0m` instead of `null` | `Sugerido` null-vs-zero fact |
| 1 | `.RequireAuthorization(Politicas.GestionDeCatalogo)` on `PUT /minimos` | delete the line | the **Supervisor-403** test (the group's `OperacionDePos` alone admits Supervisor *and* Vendedor, so the mutation is observable) |
| 2 | `ReglaDeReposicion.Clasificar` call in the existencias projection | hard-code `EstadoDeReposicion.Ok` | the `SinMinimo`/`Bajo`/`Ok` row assertions of the existencias test |
| 4 | `s.Minimo != null` | delete it | the seeded articulo with `minimo = null` and `cantidad = 0` must **not** appear |
| 4 | `candidatos.DefaultIfEmpty()` | make it an inner join | the *Sin proveedor* row disappears |
| 4 | `orderby a.IdProveedorHabitual, a.Id` | delete the first key | the row-sequence assertion (*Sin proveedor* no longer last) |
| 5 | `&& m.IdComprobanteCompra == null` | delete it | `LaRotacionNoNeteaLaAnulacionDeUnaCompraDentroDeLasVentas` |
| 5 | `VentanaDeRotacion`'s zone conversion | compute `hoy`/edges from `reloj.Ahora.UtcDateTime` | the midday-UTC boundary test |
| 5 | `ConsumoDiario`'s `netoConsumido is null ⇒ null` | return `0m` | the zero-history articulo shows **no** suggestion, not a suggestion of zero |
| 5 | the `filas.Count == 0` early return | delete it | the empty-PV query-count test |
| 7 | the fold of `sinStock` (`f.Cantidad <= 0`) | change to `< 0` | the tile test seeded with an articulo at exactly `0` |

## Slicing (7 PRs, stacked-to-main — the proposal's plan, re-scoped not renumbered)

| # | Branch | Content | ~Lines | Test plan |
|---|---|---|---|---|
| 1 | `feat/stage13-slice1-minimos-api` | 2 `ParametroConocido` keys (+`PorClave`), the whole of `ReglaDeReposicion`, `PUT /api/stock/minimos` with its single statement and five refusals, doc-11 backlog re-registration | ~320 | Domain suite; zero-movement assertion ×2; the five codes; Vendedor/Supervisor 403; RLS over `ways_app` |
| 2 | `…slice2-existencias-minimos` | `minimo`/`reposicion`/`estado` on `/existencias` + 3 export columns | ~230 | Three-state row assertions; export equality on the widened table |
| 3 | `…slice3-web-minimos` | `Existencias.tsx` editor grid, add-row, `aSolicitudDeMinimos`, descriptor + component tests | ~380 | Stale-read flush inside `act`; double-click; supersede blocked; coercion branches |
| 4 | `…slice4-reposicion` | `ConstruirQueryDeReposicion`, `ObtenerReposicionAsync` (**no rotation fields yet**), endpoint + `/export` sibling + mapper | ~350 | Discriminating seeds; *Sin proveedor* present and last; export equality cell by cell; cap refusal |
| 5 | `…slice5-rotacion` | `VentanaDeRotacion` wiring, `LeerConsumoAsync`, the two rotation fields on `FilaDeReposicion`, `GET /rotacion` | ~340 | The netting trap; the five excluded motivos; the midday-UTC window; the empty-PV query count |
| 6 | `…slice6-web-reposicion` | `Reposicion.tsx`, `agruparPorProveedor`, download, route + nav | ~330 | Grouping unit tests; `—` for null `sugerido`; descriptor tests |
| 7 | `…slice7-tile-y-sugerencia` | `/reposicion/resumen` + `PanelDeReposicion` + the `Sugerido` column on the editor — **the designated droppable slice** | ~300 | Tile ≡ report with three distinct counts; testid-per-metric assertions |

Total ≈ **2 250**. `delivery_strategy: auto-chain`, `chain_strategy: stacked-to-main`, one
judgment-day round per slice.

**Pre-approved degradation** (proposal, decision-11 pattern of stage 12): if slice 7 overflows,
**ship `/reposicion/resumen` + the tile and drop `GET /rotacion`'s consumer** — i.e. the `Sugerido`
column on the editor. Decision 14 is what makes that a clean cut: the suggestion lives behind its
own endpoint and its own column, so dropping it removes code that was never shipped rather than
retracting fields from a published DTO. Documented reduction, never silent.

**Parallelism.** `1` blocks everything. After it merges: `[2 → 3]` (existencias + its screen) and
`[4 → 5 → 6]` (reposición) are genuinely disjoint — `2/4/5` share
`ServicioDeReportesDeStock.cs`/`Contratos.cs` and must serialize *within* their front, but `3` and
`6` touch only `src/Ways.Web`. `7` needs `4` (for `/resumen`), `5` (for `/rotacion`) and `3` (for
the column's host). The conflict surface between fronts is `ReportesEndpoints.cs` (one route line
per slice) and `tipos.ts` (append-only blocks).

**Budget.** No slice carries a migration, so — unlike stage 12 — **no `size:exception` is
anticipated**. Slices 3 and 4 sit closest to 400; the pre-identified cuts are slice 3 at the
add-row boundary (grid editing ships, the picker follows) and slice 4 at the report/export
boundary. As in every prior stage, overflow is expected from **test depth**, not scope.

## Binding verify criteria

1. `dotnet ef migrations has-pending-model-changes` reports **no pending changes**, and
   `src/Ways.Infrastructure/Persistencia/Migraciones/` contains **zero files** added by this stage
   (`git diff --stat` against the stage's base). Any DDL reopens the gate — `db_gate:
   SIN-CAMBIOS-DE-SCHEMA-RATIFICADO` in `state.yaml` is the contract.
2. `VentasCheckoutTests`' query-count constants (`16` / `17`) are **unmodified**, and no file under
   `src/Ways.Application/{Ventas,Compras}/` appears in the stage's diff.
3. Every mutation target in the table above has recorded evidence (applied → named failing test →
   reverted → green) in its slice's PR body.
4. Domain, Application, Integration and vitest suites green; colocated descriptor/helper tests exist
   for every new or changed pure web helper (`web-descriptor-tests`).

## Open Questions

- [ ] **`minimo_invalido` (>3 decimals) is a code the proposal does not name.** Without it, Postgres
      silently **rounds** the value the operator typed into `numeric(12,3)` — a small lie on a
      configuration screen. Recommendation: keep it. `sdd-spec` decides whether it is a named
      scenario or folded into `minimo_negativo`'s family.
- [ ] **`?dias=` on `/reposicion` and `/rotacion`** mirrors `vencimientos?dias=` and makes the
      window testable without touching `parametros`. It is a refinement over the proposal's route
      list; `sdd-spec` should either enumerate it or drop it (the parametro default alone is
      sufficient for the feature).
- [ ] **The tile's third metric is `sinSugerencia`, not `sinProveedor`** (decision 9). Both are
      configuration-gap counters; the choice is a product judgment made under delegated authority
      and is one word to change if the owner disagrees.
- [ ] **A rotation figure is per `(articulo, punto de venta)`, computed from `movimientos_stock` of
      that PV only.** A transfer out of the PV is deliberately **not** consumption (decision 6), so
      a warehouse PV that only ships to shops will show zero rotation and therefore no suggested
      minimum. That is correct for "what did customers buy here" and arguably wrong for "how fast
      does this leave here". Recorded, not resolved: the consolidated multi-PV view is already the
      proposal's named first follow-up, and this is the same question wearing a different hat.
- [ ] **`consumoDiarioPromedio` divides by the *nominal* window, not by days elapsed since the
      articulo first moved.** An articulo received five days ago with a 30-day window shows one
      sixth of its real daily rate. The honest alternative (divide by days since first movement)
      needs a `MIN(creado_el)` per articulo — one more aggregate column, additive later. Recorded
      because it is exactly the "plausible and wrong" family, and it is contained by the fact that
      rotation is **advisory and never gates an alert** (proposal decision 7).
- [ ] **The existencias report's inner join to `articulos` hides the stock of a soft-deleted
      articulo**, and the reposición report inherits it. Pre-existing, documented and tested since
      stage 11 (`ExistenciasTests.UnArticuloEliminadoNuncaApareceEnLasExistencias`); noted here so
      the reposición report's own coverage records the same trade-off deliberately rather than
      inheriting it by accident.
