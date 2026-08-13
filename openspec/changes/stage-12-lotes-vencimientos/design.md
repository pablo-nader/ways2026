# Design: Stage 12 — Lotes y vencimientos (FEFO)

## Technical Approach

**One pure Domain rule, three unchanged writers that each grow one statement, and a second cache
that rides the lock order of the first.**

The proposal's governing constraint — *"with the module off the checkout hot path must not gain a
single round-trip"* — decides the shape of everything below. It is met by three structural facts,
not by care:

1. `articulos.controla_lote` arrives inside `articuloPorId`, a dictionary `EmitirAsync` already
   loads for pricing (`ServicioDeVentas.cs:97-99`). **There is no probing query, so there is
   nothing to skip.**
2. `lotes_habilitado` arrives inside a `parametros` read that becomes **one** query instead of two
   (`ServicioDeVentas.cs:112-113` → a single `WHERE clave IN (…)`). The budget guard already in
   the repo (`VentasCheckoutTests.ContadorDeComandos`, asserting `Assert.Equal(17, …)`) turns that
   claim into an integer: **17 → 16.**
3. The FEFO plan is **one** additional read, taken in the decide phase, issued only when the cart
   actually contains a lot-effective articulo.

The second load-bearing idea is that **the pinned order of decisions 6 is an order of *lock
acquisition*, not of value writing**. `stock` and `stock_lotes` are both written by
`INSERT … ON CONFLICT … DO UPDATE … RETURNING`, whose row lock is the concurrency primitive
(constraint 4). A *no-op* form of that statement (`SET cantidad = tabla.cantidad`, the shape
`ServicioDeStock.BloquearYCrearSiFaltaStockAsync` already uses at `:247-267`) acquires the lock
without changing anything. So an operation that cannot know its deltas until it has read the rows
— the per-lot conteo, the reconciliation — **acquires every lock it needs in the pinned order
first, then applies deltas in whatever order it likes**, because the locks are already held. This
is what makes decision 11 (conteo per lot) implementable without inventing a second lock protocol.

The third is that `lotes` is a **catalog** table, not a balance table, so it sits *above* stock in
the lock hierarchy: **every writer takes its `lotes` locks before its first `stock`/`stock_lotes`
lock, and never after.** In `ServicioDeVentas` that is free — the decide-then-commit split
(constraint 5) already forces lot resolution to happen before the transaction opens. In
`ServicioDeCompras` it is a resolution pass placed immediately after the header `UPDATE … RETURNING`
and before the stock loop. `ServicioDeStock`'s writers resolve lots in their pre-transaction
validation phase, next to `ResolverArticuloAsync`/`ResolverPuntoVentaAsync`.

Nothing about the deliberate triplication is undone. The three private
`InsertarMovimientoStockAsync`/`UpsertStockAsync` pairs stay three; each gains an `int? idLote`
parameter and a sibling `UpsertStockLoteAsync`. What is *not* triplicated is the **rule**: FEFO
ordering, effective control, código derivation and expiry classification live once in
`Ways.Domain/Stock/ReglaDeLotes.cs`, pure, DB-free, `PoliticaDeRoles`-shaped — and, critically,
**the web never re-implements FEFO**: the picker endpoint returns the server's own choice as a
`sugerido` flag.

## Architecture Decisions

| # | Decision | Alternatives considered | Rationale |
|---|---|---|---|
| 1 | **`ReglaDeLotes` is one pure static class in `Ways.Domain/Stock/`** holding `ControlEfectivo`, `OrdenarFefo`, `ElegirFefo`, `DerivarCodigo`, `Clasificar`, `EstaVencido` and the reserved `CodigoSinIdentificar` literal. It takes `SaldoDeLote` records, never `IWaysDbContext` | Put FEFO inside `ServicioDeLotes`; put it in each of the three writers | Risk #2 of the proposal is "three writers implementing the lot rule differently". A rule that needs a database to be exercised gets tested once, in the slice that wrote it, and diverges everywhere else. As a pure function over hand-built records it is exercised by every writer's unit suite for free, and the FEFO ordering test (`es_sin_identificar DESC` first) is a three-line xUnit fact with no fixture |
| 2 | **The batched parametro read groups candidates *by clave* before delegating to `ResolucionDeParametros.Resolver`** | Call `Resolver` once with the whole multi-key candidate list | `ResolucionDeParametros.Resolver` (`:12-24`) filters candidates by `IdPuntoVenta` **but not by `Clave`** — it was written for a single-key candidate set. Handing it a three-key set makes it return *another key's* PV-scoped row. This is a silent, cross-parameter corruption that no existing test would catch, so it gets a named mutation target (below): delete `Where(c => c.Clave == clave)` and the two-key test must go red |
| 3 | **`lotes` is locked before `stock`/`stock_lotes`, never after** — a global two-tier order on top of decision 6's tuple order | Fold `lotes` into the same tuple order | `lotes` has no `id_punto_venta`, so it cannot participate in a `(id_articulo, id_punto_venta, id_lote)` order at all. Two tiers with a strict precedence is the only cycle-free arrangement, and it is free: `ServicioDeVentas` already *must* resolve lots before the transaction (constraint 5), and `ServicioDeCompras` resolves them under the header lock it already holds |
| 4 | **Get-or-create is `INSERT … ON CONFLICT (id_tenant, id_articulo, codigo) WHERE deleted_at IS NULL DO UPDATE SET updated_at = lotes.updated_at RETURNING id_lote, fecha_vencimiento`** — a no-op `DO UPDATE`, never `DO NOTHING` | `DO NOTHING` + a second `SELECT`; a `try/catch 23505` + retry-read loop | `DO NOTHING` returns no row on conflict, forcing a second round trip **and** a TOCTOU window. `DO UPDATE` returns the row in one statement and takes its lock, so the immutability check (`fecha_vencimiento` differs ⇒ `409 lote_vencimiento_incompatible`) is evaluated **under the lock**, not against a stale read. There is therefore no retry-read loop in this design, and saying so is more honest than writing one that can never fire |
| 5 | **The sin-identificar lot's `codigo` is the reserved literal `SIN-IDENTIFICAR`** (`ReglaDeLotes.CodigoSinIdentificar`); a client-supplied `codigoLote` equal to it is refused `400 codigo_de_lote_reservado` | Leave `codigo` free (e.g. `""`, a GUID) and rely on `ux_lotes_sin_identificar` | With a deterministic código, **one** `ON CONFLICT` target (`ux_lotes_articulo_codigo`) serializes the lazy creation of the sin-identificar lot too — two concurrent checkouts of the same never-received articulo cannot both insert. Without it, the race lands on a *different* index than the conflict target and surfaces as a raw `23505`. This turns `ux_lotes_sin_identificar` from a racy path into a pure schema backstop, which is the `pk_stock` precedent (`ManejadorDeErrores.cs:170-172`) |
| 6 | **The FEFO read is one query whose row set is bounded to "lots physically present, plus the ones the client named"**: `lotes ⟕ stock_lotes` for the cart's lot-effective articulos, `WHERE deleted_at IS NULL AND (sl.cantidad <> 0 OR l.es_sin_identificar OR l.id_lote IN (@lotesPedidos))` | (a) `WHERE sl.cantidad > 0` only; (b) every lot of the articulo | (a) cannot validate a client-supplied `idLote` with a zero balance — the server would 404 a lot the operator is physically holding. (b) is unbounded in time: a yogurt received weekly for three years is 150 rows per cart line on the hottest path in the system. The chosen predicate is bounded by *shelf reality* (lots with a non-zero balance at that PV) and stays a single round trip. `ix_stock_lotes_punto_venta` is the access path |
| 7 | **`ElegirFefo` returns `null` when no lot of the articulo has a positive balance; the caller then get-or-creates the sin-identificar lot** rather than refusing | `409 sin_lote_disponible` | The counter never blocks (decision 4/12). Selling an articulo whose lots are all at zero drives a lot negative exactly as `stock.cantidad` already goes negative today — legacy parity. The sin-identificar lot is the honest destination: we genuinely do not know which lot left the shelf. This is also the only path that creates a lot inside a checkout, and it is a raw `ExecuteScalarAsync` (invisible to the round-trip counter, like `UpsertStockAsync` — the same honesty note its doc-comment already carries at `ServicioDeVentas.cs:866-875`) |
| 8 | **Lines are ordered `OrderBy(IdArticulo).ThenBy(IdLote)`, and the aggregate `stock` upsert is re-issued per line**, not hoisted per articulo | Group lines by articulo, upsert `stock` once with the summed delta, then the lots | Two lines of the *same* articulo with *different* lots is the split-line case decision 4 explicitly creates. Grouping decouples ledger rows from cache upserts and adds a second shape to reason about; re-issuing is a **re-lock of a row this transaction already holds**, which costs a statement and zero contention. Simplicity wins where correctness is equal, on the code path whose incorrect version is a production deadlock |
| 9 | **`NULLS FIRST` is materialised in C# as `.ThenBy(c => c.IdLote.HasValue).ThenBy(c => c.IdLote ?? 0)`** — never `?? int.MinValue`, never `?? 0` alone | `.ThenBy(c => c.IdLote ?? 0)` | It works today only because identity columns start at 1; it is a correctness claim resting on a sequence's configuration. `HasValue` (`false < true`) states the intent, survives any seed change, and is what the deadlock mutation test deletes |
| 10 | **In transfers the key list carries the aggregate element and the lot element separately; the `movimientos_stock` row is written at the aggregate element and carries `id_lote`** | Write the movement at the lot element; write two movements | The ledger row is a plain `INSERT` with no conflict target — it takes no lock and therefore has no place in the lock order. Attaching it to the aggregate element keeps the existing `2N`-key loop shape (`ServicioDeStock.cs:130-154`) recognisable, keeps exactly one ledger row per `(line, PV)` as today, and leaves the lot element as a pure `stock_lotes` upsert whose `RETURNING` **is** the per-lot sufficiency check — no second query, no TOCTOU, the identical argument the existing aggregate check rests on (`:139-148`) |
| 11 | **A transfer's duplicate-line refusal keys on `(IdArticulo, IdLote)` *after* FEFO defaulting**, keeping the `articulo_repetido` code | Keep the key at `IdArticulo`; add a new `lote_repetido` code | Moving two lots of the same articulo in one transfer is a real depot operation that the current key would refuse for no reason. Checking *after* defaulting also catches the subtle case the client cannot see: two lines of the same articulo both omitting `idLote` resolve to the **same** FEFO lot and would otherwise double-decrement one row. One code, one refusal, both cases |
| 12 | **The per-lot conteo acquires all its locks first (aggregate no-op upsert, then each lot's no-op upsert ascending), derives every delta, and only then writes** | Lock-and-write each lot in turn, accumulating the aggregate at the end | The aggregate's delta is the *sum* of the per-lot deltas, so it is unknowable until every lot is read — but the pinned order demands the aggregate row **first**. Splitting acquisition from application dissolves the contradiction: `BloquearYCrearSiFaltaStockAsync` (`:247-267`) already exists for exactly this, and its `stock_lotes` twin is a copy with a third key. The order is preserved where it matters (acquisition); the writes happen under locks already held |
| 13 | **Reconciliation's unit of transaction is one `(articulo, punto de venta)` pair; a batch is not atomic** | One transaction over the whole activation batch | A tenant-wide flag flip could otherwise hold locks over every PV's stock rows of that articulo while a cashier is mid-sale. Non-atomicity is safe **because the residue is recomputed from current state, never from a snapshot**: a pair left unreconciled by a crash is simply reconciled by the next run, and — the useful corollary — a sale that lands on an unreconciled pair drives the sin-identificar lot negative, which the next reconciliation *self-heals* into the exact right residue. Idempotence is not a nice property here, it is the recovery mechanism |
| 14 | **The reconciliation pair writes two `movimientos_stock` rows and upserts `stock_lotes` only — `stock` is never touched at all** | Upsert `stock` with a zero delta for symmetry | A zero delta on the hottest table for the sake of symmetry is a lock taken for nothing. The invariant argument is *arithmetic*: the two ledger rows sum to zero, so `stock.cantidad = SUM(movimientos)` holds without any cache write. Writing nothing is also what makes "cost of reverting: nothing to unwind" literally true |
| 15 | **The vencimientos report classifies in C# over `DateOnly`; the only zone-sensitive value is `hoy`** | `timezone($1, …)`/`date_trunc` in SQL, the stage-10 report shape | `fecha_vencimiento` is a `date`, not an instant — there is nothing to bucket and no `date_trunc` to get wrong. Pushing classification into SQL would import the whole stage-10 timezone machinery for a comparison of two `date`s. Isolating the zone into one expression (`DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(reloj.Ahora, zona).DateTime)`) makes the mutation test surgical: replace it with `UtcDateTime` and one seeded lot changes bucket |
| 16 | **`EstadoDeVencimiento` has a fourth member, `SinFecha`, and sin-identificar rows are *included* in the report** | Exclude lots with a NULL expiry | The report's rows must sum to `stock.cantidad` for a reconciled articulo, or the operator reading it is being lied to by omission. The sin-identificar residue is *precisely* the number that should nag someone into identifying it; parking it outside the report is the same mistake decision 4 refused when it put sin-identificar **first** in FEFO order. **Refinement over the proposal's three classes — flagged for `sdd-spec`** |
| 17 | **The vencimientos export is a LISTING, not an aggregate**: `COUNT(*)` → refuse → single read with `.Take(tope + 1)` | Treat it as an aggregate and guard on `TablaExportable.Filas.Count`, like `/stock/existencias` | Stage-11 decision 6 splits the two shapes by whether the row set is bounded *by construction*. Existencias is bounded by the articulo catalog; **vencimientos is bounded by the lot count, which grows monotonically with time** — a three-year-old store is a five-figure row set. It gets the listing treatment, including the `+1` race backstop |
| 18 | **`SolicitudDeConteo.Contada` widens to `decimal?`; exactly one of `Contada` / `Lotes` may be present** (`400 conteo_contada_y_lotes` otherwise) | Keep `Contada` required and ignore it for lot-effective articulos | `dto-contract-honesty` rule 1: a field the handler never reads is silent data loss wearing an API's clothes. An operator who counts 40 units of a lot-controlled articulo and sends `contada: 40` alongside a lot breakdown must be told the aggregate figure was discarded, not have it swallowed. The widening is source-compatible for every existing caller |
| 19 | **FEFO is never computed in TypeScript.** `GET /api/stock/lotes` returns each lot with its balance, its `estado` and a `sugerido` boolean produced by `ReglaDeLotes.ElegirFefo` | Let the picker sort client-side by expiry | The POS pre-selection and the server's own default would be two implementations of one rule, drifting the first time the tiebreak or the sin-identificar-first ordering changes. `sugerido` is server-authored; the picker renders it. This is also what keeps the happy path at zero keystrokes: omit `idLote` and the server picks the same lot the UI highlighted |
| 20 | **`stock_lotes` gets the hand-rolled tenant filter** `WaysDbContext.AplicarFiltroDeTenantEnStockLote`, mirroring `AplicarFiltroDeTenantEnStock`; `lotes` inherits `EntidadTenant` and needs none | Give `stock_lotes` audit columns so it can inherit `EntidadTenant` | The gate froze `stock_lotes` as a PK-only cache with no audit columns, matching `stock`. That decision carries its consequence: the EF global filter must be written by hand, exactly as `Stock`/`MovimientoStock` already do (`WaysDbContext.cs:149-150`) |
| 21 | **The migration is one file and is not split across PRs**, even at the cost of slice 1 exceeding 400 lines | Split the tables into slice 1a and the columns into slice 1b | The gate contract names **one** migration (`LotesYVencimientosEtapa12`). Two PRs editing one migration file means the second PR mutates an artifact the first already merged, which is exactly the situation the gate exists to prevent. Slice 1 is therefore the stage's one anticipated `size:exception`, declared in advance with this reason |

## Interfaces / Contracts

### Domain — pure, no database (`PoliticaDeRoles` pattern)

```csharp
// Ways.Domain/Stock/ReglaDeLotes.cs
public readonly record struct SaldoDeLote(
    int IdArticulo, int IdLote, string Codigo, bool EsSinIdentificar,
    DateOnly? FechaVencimiento, decimal Cantidad);

public enum EstadoDeVencimiento { Vencido, PorVencer, Vigente, SinFecha }

public static class ReglaDeLotes
{
    public const string CodigoSinIdentificar = "SIN-IDENTIFICAR";

    /// Decisión 2 del proposal: control efectivo = flag del artículo AND parámetro de la empresa.
    public static bool ControlEfectivo(bool controlaLote, bool lotesHabilitado)
        => controlaLote && lotesHabilitado;

    /// Decisión 4: sin-identificar PRIMERO, después vencimiento ascendente, id_lote como desempate.
    public static IReadOnlyList<SaldoDeLote> OrdenarFefo(IEnumerable<SaldoDeLote> saldos)
        => saldos.OrderByDescending(s => s.EsSinIdentificar)
                 .ThenBy(s => s.FechaVencimiento ?? DateOnly.MinValue)
                 .ThenBy(s => s.IdLote)
                 .ToList();

    /// null ⇒ ningún lote con saldo positivo: el llamador resuelve el lote sin identificar.
    public static SaldoDeLote? ElegirFefo(IEnumerable<SaldoDeLote> saldosDelArticulo);

    public static string DerivarCodigo(DateOnly fechaVencimiento);        // "2026-11-30"
    public static bool EstaVencido(DateOnly? fecha, DateOnly hoy);
    public static EstadoDeVencimiento Clasificar(DateOnly? fecha, DateOnly hoy, int diasDeAlerta);
}
```

```csharp
// Ways.Domain/Stock/
public class Lote : EntidadTenant
{
    public int Id; public int IdArticulo; public required string Codigo;
    public DateOnly? FechaVencimiento; public bool EsSinIdentificar;
}

public class StockLote   // PK-only, sin auditoría — el precedente Stock
{
    public int IdArticulo; public int IdPuntoVenta; public int IdLote;
    public int IdTenant;   public decimal Cantidad;
}

public enum MotivoStock { Venta, Compra, Anulacion, Ajuste, Transferencia, Inventario, Decomiso, Reclasificacion }
public class MovimientoStock { /* … */ public int? IdLote; }
public class Articulo        { /* … */ public bool ControlaLote; }
public class ItemComprobanteVenta  { /* … */ public int? IdLote; }
public class ItemComprobanteCompra { /* … */ public string? CodigoLote; public DateOnly? FechaVencimiento; public int? IdLote; }
```

```csharp
// Ways.Domain/Catalogos/ParametroConocido.cs  — dos entradas, sin migración (patrón stage-10)
public static readonly ParametroConocido LotesHabilitado       = new("lotes_habilitado", typeof(bool), "false");
public static readonly ParametroConocido DiasAlertaVencimiento = new("dias_alerta_vencimiento", typeof(int), "30");
```

### Application — `ServicioDeLotes` (new)

```csharp
public class ServicioDeLotes(IWaysDbContext db, IRelojDelSistema reloj, IContextoDeUsuario contexto)
{
    /// Get-or-create sobre ux_lotes_articulo_codigo (decisión 4). `codigo` null ⇒ derivado del
    /// vencimiento. Corre sobre la conexión/transacción del llamador cuando la hay (compras), o
    /// sobre la conexión suelta cuando no (venta, fase de decisión).
    Task<int> ResolverOCrearAsync(DbConnection cx, DbTransaction? tx, int idTenant,
        int idArticulo, string? codigo, DateOnly fechaVencimiento, DateTimeOffset momento, CancellationToken ct);

    Task<int> ResolverSinIdentificarAsync(DbConnection cx, DbTransaction? tx, int idTenant,
        int idArticulo, DateTimeOffset momento, CancellationToken ct);

    /// Decisión 6: UNA query, acotada a los lotes con saldo distinto de cero + los pedidos
    /// explícitamente + el sin-identificar.
    Task<IReadOnlyList<SaldoDeLote>> LeerSaldosAsync(int idPuntoVenta,
        IReadOnlyList<int> idsArticulo, IReadOnlyList<int> idsLotePedidos, CancellationToken ct);

    /// Decisión 3 del proposal: par neto cero por (artículo, PV). Idempotente.
    Task<ResultadoDeReconciliacion> ReconciliarAsync(int? idArticulo, int? idPuntoVenta, CancellationToken ct);

    Task<IReadOnlyList<LoteListado>> ListarAsync(int idPuntoVenta, int idArticulo, CancellationToken ct);
    Task<LoteListado> CrearAsync(SolicitudDeLote solicitud, CancellationToken ct);   // admin, EF ⇒ 409 lote_duplicado
}
```

### Application — request/response contracts (`dto-contract-honesty`: every field named below has a destination)

```csharp
// Ventas
public sealed record LineaDeVenta(int IdArticulo, decimal Cantidad, string? CodigoBarra, int? IdLote = null);
public sealed record ItemEmitido(/* … */, int? IdLote, string? CodigoLote, bool LoteVencido);
//   IdLote      → items_comprobante_venta.id_lote (snapshot)
//   CodigoLote  → proyectado del join con lotes en Proyectar()  (el POS lo muestra en el ticket)
//   LoteVencido → ReglaDeLotes.EstaVencido(venc, hoyEnZonaDelPv)  (decisión 12: warning, nunca bloqueo)

// Compras
public sealed record LineaDeCompraSolicitada(/* … */, string? CodigoLote = null, DateOnly? FechaVencimiento = null);
public sealed record ItemDeCompra(/* … */, string? CodigoLote, DateOnly? FechaVencimiento, int? IdLote);

// Stock
public sealed record LineaDeTransferencia(int IdArticulo, decimal Cantidad, int? IdLote = null);
public sealed record LineaTransferida(int IdArticulo, int? IdLote, decimal CantidadOrigen, decimal CantidadDestino);
public sealed record SolicitudDeAjusteDeStock(int IdPuntoVenta, int IdArticulo, decimal Cantidad, string? Observaciones, int? IdLote = null);
public sealed record SolicitudDeDecomiso(int IdPuntoVenta, int IdArticulo, int? IdLote, decimal Cantidad, string Observaciones);
public sealed record SolicitudDeConteo(int IdPuntoVenta, int IdArticulo, decimal? Contada, string Observaciones,
                                       IReadOnlyList<ConteoDeLote>? Lotes = null);          // decisión 18
public sealed record ConteoDeLote(int IdLote, decimal Contada);
public sealed record ResultadoConteo(/* … */, IReadOnlyList<LoteContado> Lotes);
public sealed record LoteContado(int IdLote, decimal Cantidad, decimal CantidadAnterior, decimal Delta, bool MovimientoRegistrado);

// Lotes
public sealed record SolicitudDeLote(int IdArticulo, string? Codigo, DateOnly FechaVencimiento);
public sealed record LoteListado(int IdLote, int IdArticulo, string Codigo, DateOnly? FechaVencimiento,
                                 bool EsSinIdentificar, decimal Cantidad, EstadoDeVencimiento Estado, bool Sugerido);
public sealed record SolicitudDeReconciliacion(int? IdArticulo, int? IdPuntoVenta);
public sealed record ResultadoDeReconciliacion(int ParesReconciliados, int ParesSinResiduo);

// Reportes
public sealed record FilaDeVencimiento(int IdArticulo, string Articulo, int IdLote, string CodigoLote,
                                       DateOnly? FechaVencimiento, decimal Cantidad, EstadoDeVencimiento Estado);
public sealed record Vencimientos(int IdPuntoVenta, DateOnly Hoy, int DiasDeAlerta, string ZonaHoraria,
                                  IReadOnlyList<FilaDeVencimiento> Filas);
public sealed record ResumenDeVencimientos(int IdPuntoVenta, int Vencidos, int PorVencer, int SinFecha);
```

### API surface

| Route | Policy | Notes |
|---|---|---|
| `GET /api/stock/lotes?idPuntoVenta&idArticulo` | `OperacionDePos` (group) | Picker feed; carries `sugerido` (decision 19) |
| `POST /api/stock/lotes` | `GestionDeCatalogo` stacked | Admin alta; `409 lote_duplicado` via `ux_lotes_articulo_codigo` |
| `POST /api/stock/decomiso` | `GestionDeCatalogo` stacked | Positive `cantidad`, server negates (the `ContarAsync` discipline) |
| `POST /api/stock/lotes/reconciliacion` | `GestionDeCatalogo` stacked | Re-runnable; `SolicitudDeReconciliacion` |
| `GET /api/reportes/stock/vencimientos` | `LecturaDeReportes` (group) | `idPuntoVenta`, `dias` (default = parametro) |
| `GET /api/reportes/stock/vencimientos/export` | inherited by co-location | Stage-11 sibling standard |
| `GET /api/reportes/stock/vencimientos/resumen` | inherited | Tablero tile counts |

### New domain error codes

`lote_requerido` (400) · `lote_no_aplica` (400) · `lote_invalido` (400, unknown / other articulo / soft-deleted) ·
`lote_duplicado` (409) · `lote_vencimiento_incompatible` (409) · `codigo_de_lote_reservado` (400) ·
`lote_vencido_en_recepcion` (409) · `transferencia_lote_vencido` (409) ·
`stock_insuficiente_para_decomiso` (409) · `conteo_contada_y_lotes` (400) · `conteo_lote_repetido` (400).
Reused unchanged: `stock_insuficiente_para_transferencia` (409, now evaluable per lot),
`compra_anulacion_stock_negativo` (409, now evaluable per lot), `articulo_repetido` (400, key widened).

## Write site 1 — `ServicioDeVentas` (checkout, anulación, NCX)

**Decide phase** (`EmitirAsync`, before any transaction). Two edits and one insertion:

```csharp
// (edit) :112-113 — DOS queries pasan a UNA.  17 → 16 round trips.
var (toleranciaPago, vueltoMaximo, lotesHabilitado) =
    await ResolverParametrosDeVentaAsync(puntoVenta.IdEmpresa, puntoVenta.Id, ct);

// (insert) inmediatamente después de MaterializarItems — articuloPorId YA está cargado (:97-99),
// así que decidir si hay lote es GRATIS: cero queries de sondeo.
var lineasConLote = items
    .Where(i => i.EsProducto && ReglaDeLotes.ControlEfectivo(articuloPorId[i.IdArticulo].ControlaLote, lotesHabilitado))
    .ToList();

if (lineasConLote.Count > 0)                                   // ← la ÚNICA query nueva del camino caliente
{
    var saldos = await servicioDeLotes.LeerSaldosAsync(
        puntoVenta.Id,
        lineasConLote.Select(i => i.IdArticulo).Distinct().ToList(),
        lineas.Where(l => l.IdLote is not null).Select(l => l.IdLote!.Value).Distinct().ToList(), ct);

    // Resolución por línea, pura salvo el get-or-create del sin-identificar (decisión 7).
    // Un idLote provisto se valida contra `saldos` (existe, es del artículo, no borrado) ⇒ lote_invalido.
}
```

`ResolverParametrosDeVentaAsync` — the batched read, with the per-clave grouping decision 2 pins:

```csharp
private async Task<(decimal Tolerancia, decimal Vuelto, bool LotesHabilitado)> ResolverParametrosDeVentaAsync(
    int idEmpresa, int idPuntoVenta, CancellationToken ct)
{
    string[] claves = [ParametroConocido.ToleranciaPago.Clave,
                       ParametroConocido.VueltoMaximo.Clave,
                       ParametroConocido.LotesHabilitado.Clave];

    var candidatos = await db.Parametros
        .Where(p => claves.Contains(p.Clave) && p.IdEmpresa == idEmpresa
                 && (p.IdPuntoVenta == null || p.IdPuntoVenta == idPuntoVenta))
        .ToListAsync(ct);

    // Cláusula bajo prueba (mutation-proof-tests): ResolucionDeParametros.Resolver NO filtra por
    // clave — fue escrito para un set de candidatos de UNA sola clave. Sin este Where, la fila de
    // punto de venta de otra clave gana la precedencia y devuelve el valor equivocado.
    string Leer(ParametroConocido c) =>
        ResolucionDeParametros.Resolver(c.Clave, candidatos.Where(p => p.Clave == c.Clave).ToList(), idPuntoVenta);
    …
}
```

**Transaction, step 5** (`EjecutarTransaccionAsync:700-709`) — the order gains its third component and one statement:

```csharp
foreach (var item in plan.Items.Where(i => i.EsProducto)
                               .OrderBy(i => i.IdArticulo)
                               .ThenBy(i => i.IdLote))            // decisión 8/9
{
    var delta = -item.Cantidad;

    await InsertarMovimientoStockAsync(
        conexion, transaccionCruda, plan.IdTenant, item.IdArticulo, plan.IdPuntoVenta, delta,
        MotivoStock.Venta, comprobante.Id, plan.IdEmpleado, plan.Momento, item.IdLote, ct);   // ← +1 parámetro

    await UpsertStockAsync(…, delta, ct);                          // fila agregada = elemento id_lote NULL, PRIMERO

    if (item.IdLote is { } idLote)
        await UpsertStockLoteAsync(conexion, transaccionCruda, plan.IdTenant,
                                   item.IdArticulo, plan.IdPuntoVenta, idLote, delta, ct);    // ← statement nuevo
}
```

```sql
-- UpsertStockLoteAsync: la MISMA forma que UpsertStockAsync, una clave más.
INSERT INTO stock_lotes (id_articulo, id_punto_venta, id_lote, id_tenant, cantidad)
VALUES ($1, $2, $3, $4, $5)
ON CONFLICT (id_articulo, id_punto_venta, id_lote) DO UPDATE
SET cantidad = stock_lotes.cantidad + EXCLUDED.cantidad
RETURNING cantidad
```

**Item snapshot**: `ItemComprobanteVenta.IdLote = i.IdLote` in the `AddRange` at `:651-673`. Frozen,
never re-derived — legal under "Snapshot Immutability of Items" because it is *added to* the snapshot
and no edit endpoint exists.

**Anulación** (`EjecutarAnulacionAsync:430-444`) needs **no new lookup**: it already iterates the
original `movimientos_stock` rows, which now carry `id_lote`. Order becomes
`.OrderBy(m => m.IdArticulo).ThenBy(m => m.IdLote)`; the inverse movement copies `original.IdLote`
and the `stock_lotes` upsert mirrors it. Exactness is structural, not derived.

**NCX**: same code path (a comprobante of `tipo NCX` with negative quantities). The only difference
is the rule: for a lot-effective articulo an NCX line **must** carry `idLote` (`400 lote_requerido`)
— FEFO defaulting is refused for a return, because "oldest first" is meaningless for merchandise
coming *back*. The POS suggests from `id_comprobante_asociado`'s item snapshot when present;
otherwise the operator reads the printed date; otherwise the sin-identificar lot is the escape hatch.
`id_comprobante_asociado` stays optional (decision 8 of the proposal).

## Write site 2 — `ServicioDeCompras` (recepción, anulación)

**Draft** (`CrearAsync`/`ActualizarBorradorAsync`): `codigo_lote` and `fecha_vencimiento` are carried
straight through `MaterializarItems` onto `items_comprobante_compra`. **Nothing is resolved at draft
time** — the replace-set deletes and re-inserts every line on each `PUT` (`:217-233`), so resolving
early would litter `lotes` with rows for drafts that never confirm.
`ck_items_comprobante_compra_lote_input` is the schema backstop for "código without expiry";
`lote_vencido_en_recepcion` (decision 12) is checked at **confirm**, not at draft, so an operator can
save a draft and fix the date before confirming.

**Confirm** (`EjecutarConfirmarAsync`) gains one block, placed between step 2 (item read under the
header lock) and step 3 (the stock loop) — i.e. **`lotes` before `stock`**, decision 3:

```csharp
// 2.b Resolución de lotes — bajo el lock del header, ANTES del primer lock de stock.
//     Orden ascendente (id_articulo, codigo_lote) para que dos confirmaciones concurrentes que
//     comparten códigos de lote tomen esas filas en el mismo orden.
foreach (var item in items.Where(EsLoteEfectivo).OrderBy(i => i.IdArticulo).ThenBy(i => i.CodigoLote))
{
    if (item.FechaVencimiento is null) throw new ErrorDominio("lote_requerido", …, 400);
    if (ReglaDeLotes.EstaVencido(item.FechaVencimiento, hoyEnZonaDelPv))
        throw new ErrorDominio("lote_vencido_en_recepcion", …, 409);

    item.IdLote = await servicioDeLotes.ResolverOCrearAsync(
        conexion, transaccionCruda, idTenant, item.IdArticulo, item.CodigoLote, item.FechaVencimiento.Value, momento, ct);
}

// 3. (edit) el loop existente pasa a ordenar por (IdArticulo, IdLote) y a escribir las dos caches.
foreach (var item in items.OrderBy(i => i.IdArticulo).ThenBy(i => i.IdLote)) { … }
```

`EsLoteEfectivo` needs `controla_lote` per articulo and `lotes_habilitado` for the header's empresa:
one extra read of the articulos of the compra (already performed for `costo_nominal` purposes at
`ResolverContextoAsync`) plus one parametro resolution — both **outside** the checkout budget, which
this service does not share.

**Anulación** (`:439-462`): reverse per original movement, now
`.OrderBy(m => m.IdArticulo).ThenBy(m => m.IdLote)`, copying `original.IdLote`. The refusal becomes
**two** checks, both mandatory: the existing aggregate `nueva < 0` **and** the new
`nuevaDelLote < 0`, both raising `compra_anulacion_stock_negativo`. Keeping both matters — an
aggregate that stays positive while the received lot has already been sold is exactly the case
"goods already sold can't be pulled back" was written for, and only the per-lot check sees it.

## Write site 3 — `ServicioDeStock` (transferencia, ajuste, conteo, decomiso)

**Transferencia.** The key record and its order:

```csharp
private readonly record struct ClaveDeStock(
    int IdArticulo, int IdPuntoVenta, int? IdLote, decimal Delta, int? IdLoteDelMovimiento);

private static List<ClaveDeStock> ConstruirClavesOrdenadas(int origen, int destino, IReadOnlyList<LineaResuelta> lineas) =>
    lineas
        .SelectMany(l => l.IdLote is { } lote
            ? new[]
              {
                  new ClaveDeStock(l.IdArticulo, origen,  null, -l.Cantidad, lote),   // agregada + su movimiento
                  new ClaveDeStock(l.IdArticulo, origen,  lote, -l.Cantidad, null),   // saldo del lote
                  new ClaveDeStock(l.IdArticulo, destino, null,  l.Cantidad, lote),
                  new ClaveDeStock(l.IdArticulo, destino, lote,  l.Cantidad, null)
              }
            : new[]
              {
                  new ClaveDeStock(l.IdArticulo, origen,  null, -l.Cantidad, null),
                  new ClaveDeStock(l.IdArticulo, destino, null,  l.Cantidad, null)
              })
        .OrderBy(c => c.IdArticulo)
        .ThenBy(c => c.IdPuntoVenta)
        .ThenBy(c => c.IdLote.HasValue)          // NULLS FIRST — decisión 9
        .ThenBy(c => c.IdLote ?? 0)
        .ToList();
```

The loop keeps its shape (`:130-154`): at an aggregate element it inserts the ledger row (carrying
`IdLoteDelMovimiento`) and upserts `stock`; at a lot element it upserts `stock_lotes` only. **Both**
`RETURNING` values are checked for negativity — the aggregate refusal is unchanged, the lot refusal
is new, both `409 stock_insuficiente_para_transferencia` (decision 7 of the proposal: stricter than
today, deliberately). `transferencia_lote_vencido` is checked in the pre-transaction phase, next to
`ResolverArticuloAsync`.

Lot resolution for a transfer happens **before** the transaction opens, in the same phase as the
existing pre-checks: read `stock_lotes` of the origin PV for the requested articulos (one query),
FEFO-default the omitted lots with the same `ReglaDeLotes.ElegirFefo`, then apply decision 11's
`(IdArticulo, IdLote)` uniqueness refusal. This keeps `lotes` and reads out of the transaction
entirely, so the transaction remains a pure sequence of ordered locks.

**Ajuste.** `IdLote` required when lot-effective (`lote_requerido`), refused when not
(`lote_no_aplica`). Aggregate upsert then lot upsert, in that order. No negativity refusal — an
ajuste is the operation that *fixes* a negative balance.

**Decomiso.** New `EjecutarDecomisoAsync`, structurally `EjecutarAjusteAsync` with three deltas:
`motivo = MotivoStock.Decomiso`; `cantidad` arrives positive and is negated server-side; the
`RETURNING` of the lot upsert (or of the aggregate upsert for a non-lot articulo) is checked
`< 0 ⇒ 409 stock_insuficiente_para_decomiso`. Observaciones stays mandatory (`ExigirObservaciones`,
reused verbatim).

**Conteo per lot** (decision 12):

```csharp
// ADQUISICIÓN de locks, en el orden pineado — todavía sin escribir ningún delta.
var actualAgregado = await BloquearYCrearSiFaltaStockAsync(cx, tx, idTenant, idArticulo, idPuntoVenta, ct);

var actualPorLote = new Dictionary<int, decimal>();
foreach (var l in solicitud.Lotes.OrderBy(l => l.IdLote))                       // ascendente
    actualPorLote[l.IdLote] = await BloquearYCrearSiFaltaStockLoteAsync(cx, tx, idTenant, idArticulo, idPuntoVenta, l.IdLote, ct);

// APLICACIÓN — todos los locks ya están tomados, el orden acá es irrelevante.
var deltas = solicitud.Lotes.ToDictionary(l => l.IdLote, l => l.Contada - actualPorLote[l.IdLote]);
foreach (var (idLote, delta) in deltas.Where(d => d.Value != 0m).OrderBy(d => d.Key))
{
    await InsertarMovimientoStockAsync(…, delta, MotivoStock.Inventario, …, idLote, ct);   // un movimiento POR LOTE
    await UpsertStockLoteAsync(…, idLote, delta, ct);
    await UpsertStockAsync(…, delta, ct);            // el agregado acumula la suma de los deltas
}
```

A lot whose count matches writes nothing (the existing "Zero-Difference Conteo Writes No Ledger Row"
rule, one level down), which is also what keeps it clear of `ck_movimientos_stock_cantidad_no_cero`.
`conteo_lote_repetido` refuses a duplicated `idLote` in the request before any lock is taken.

**Pre-approved degradation** (proposal decision 11): if slice 12 overflows, `ContarAsync` refuses a
lot-effective articulo with a clean `409` and the per-lot count moves to stage 13. A shipped refusal
beats a shipped silent divergence.

## Reconciliation — the net-zero `reclasificacion` pair

```
ReconciliarAsync(idArticulo?, idPuntoVenta?)
  └─ para cada par (artículo, PV) del alcance, ascendente, UNA TRANSACCIÓN POR PAR (decisión 13):
       1. idSinIdentificar := ResolverSinIdentificarAsync(...)         ← lotes ANTES de stock
       2. agregado  := BloquearYCrearSiFaltaStockAsync(...)            ← fila agregada, PRIMERO
       3. sumaLotes := SELECT COALESCE(SUM(cantidad),0) FROM stock_lotes
                       WHERE id_articulo=$1 AND id_punto_venta=$2 ORDER BY id_lote FOR UPDATE
       4. residuo := agregado - sumaLotes
       5. residuo = 0  ⇒  COMMIT sin escribir NADA  (idempotencia)
       6. residuo ≠ 0  ⇒  INSERT movimientos_stock (id_lote NULL,             cantidad = -residuo, motivo=reclasificacion)
                          INSERT movimientos_stock (id_lote = sinIdentificar, cantidad = +residuo, motivo=reclasificacion)
                          UpsertStockLote(sinIdentificar, +residuo)
                          -- `stock` NO se toca: el par suma cero (decisión 14)
```

**Scope resolution.** `lotes` is tenant-wide but the module is empresa-scoped, so the reconcilable
set is *`stock` rows of the articulo whose punto de venta belongs to an empresa where
`lotes_habilitado` resolves true*. Triggers:

| Trigger | Scope |
|---|---|
| `articulos.controla_lote` flipped `false → true` (ABM) | that articulo × every PV of every lot-enabled empresa of the tenant |
| `lotes_habilitado` flipped `false → true` (parametros) | every articulo with `controla_lote = true` (`ix_articulos_controla_lote`) × that empresa's PVs |
| `POST /api/stock/lotes/reconciliacion` | whatever the request narrows to |

The `false → true` transition is detected by comparing the pre-read value with the incoming one; a
flip to `false` reconciles nothing (lot rows simply become inert history).

## Migration — `LotesYVencimientosEtapa12` (PostgreSQL 17)

Statement order inside `Up()`, and why it is binding:

1. **`AlterDatabase`** — the `HasPostgresEnum` diff, which Npgsql renders as
   `ALTER TYPE motivo_stock ADD VALUE IF NOT EXISTS 'decomiso'` /`'reclasificacion'`. EF emits this
   operation **first**, which is exactly what is needed.
2. `CreateTable lotes` (+ `ux_lotes_id_articulo_tenant` alternate key, both CHECKs, both partial
   unique indexes, three indexes, two FKs) and `CreateTable stock_lotes` (+ three FKs, three indexes).
3. `AddColumn ×6` with their composite FKs, supporting indexes, and
   `ck_items_comprobante_compra_lote_input`.
4. `migrationBuilder.HabilitarRlsDeTenant("lotes")` and `("stock_lotes")` — the helper already used
   by five migrations (`ENABLE` + `FORCE ROW LEVEL SECURITY` + the `app_es_plataforma() OR
   id_tenant = app_tenant_actual()` policy).

**No `Sql()` statement in this migration may name `'decomiso'` or `'reclasificacion'`** — PG allows
`ADD VALUE` inside a transaction from v12 but forbids *using* the value in that same transaction,
and EF runs each migration in one transaction. **This needs no special test**: the existing
migration-apply test *is* the assertion, because such a statement would fail with a hard Postgres
error the moment `Up()` runs on a fresh database.

**No backfill, no `SET LOCAL app.acceso = 'plataforma'` block.** Unlike
`CostoCongeladoEnVentaEtapa9`, this migration rewrites zero existing rows: `controla_lote` defaults
to `false`, every other column is nullable, and the only data movement in the whole stage is
decision 3's runtime reconciliation, which is application code.

## Data Flow — a checkout of a lot-controlled articulo, end to end

```
POST /api/ventas   { lineas: [ { idArticulo: 7, cantidad: 2 } ] }        ← SIN idLote: cero keystrokes
  │
  ├─ FASE DE DECISIÓN (fuera de transacción, constraint 5)
  │   ├─ ResolverTipoComprobante · ResolverPuntoVenta · ResolverTurnoAbierto · ResolverCliente
  │   ├─ ServicioDeOfertas.ResolverAsync            (7 queries, sin cambios)
  │   ├─ articulos + alicuotas                      (2 queries) → controla_lote llega ACÁ, gratis
  │   ├─ ResolverParametrosDeVentaAsync             (1 query — antes 2)          ── 17 → 16
  │   ├─ medios de pago                             (1 query, sin cambios)
  │   ├─ ¿hay línea con ControlEfectivo? → sí
  │   │    └─ ServicioDeLotes.LeerSaldosAsync       (1 query)                    ── 16 → 17
  │   │         lotes ⟕ stock_lotes  WHERE cantidad<>0 OR es_sin_identificar OR id_lote IN (…)
  │   ├─ ReglaDeLotes.ElegirFefo(saldos del art. 7)  [puro]
  │   │         es_sin_identificar DESC, fecha_vencimiento ASC, id_lote ASC  →  lote 41 (vence 2026-08-20)
  │   │         (si ningún lote tuviera saldo: ResolverSinIdentificarAsync, raw ADO, invisible al contador)
  │   ├─ ValidadorDePagos.Validar
  │   └─ PlanDeVenta  { Items: [ { IdArticulo 7, IdLote 41, … } ] }   ← INMUTABLE
  │
  ├─ TRANSACCIÓN DE NUMERACIÓN (propia, se comitea sola)
  │
  └─ TRANSACCIÓN DE ESCRITURA
      0. turno FOR SHARE
      2. comprobante                     3+4. items (id_lote = 41, SNAPSHOT) + pagos
      5. stock, orden (id_articulo, id_punto_venta, id_lote NULLS FIRST):
             INSERT movimientos_stock (…, id_lote = 41, motivo = venta, cantidad = -2)
             UPSERT stock       (7, pv)          → fila agregada, PRIMERO
             UPSERT stock_lotes (7, pv, 41)      → saldo del lote
      6. cuenta corriente
      COMMIT
  │
  └─ ComprobanteEmitido { items: [ { idLote: 41, codigoLote: "2026-08-20", loteVencido: false } ] }
```

**Round-trip budget** (`ReaderExecuting`-visible, the metric the existing guard counts):

| Scenario | Count | Δ vs. today |
|---|---|---|
| Today (stage 11) | 17 | — |
| Module **off** | **16** | **−1** |
| Module on, no lot-effective articulo in the cart | **16** | **−1** |
| Module on, ≥1 lot-effective articulo | **17** | **0** |
| Module on, lot-effective, no lot with balance (lazy sin-identificar) | **17** | **0** (the get-or-create is raw `ExecuteScalarAsync`, invisible to the counter — a real round trip, recorded honestly, same family as `UpsertStockAsync`) |

## File Changes

| File | Action | Description |
|---|---|---|
| `src/Ways.Domain/Stock/{Lote,StockLote,ReglaDeLotes}.cs` | Create | Entities + the one pure rule (decision 1) |
| `src/Ways.Domain/Stock/{MotivoStock,MovimientoStock}.cs` | Modify | +2 enum members; `int? IdLote` |
| `src/Ways.Domain/Articulos/Articulo.cs` · `Ventas/ItemComprobanteVenta.cs` · `Compras/ItemComprobanteCompra.cs` | Modify | `ControlaLote`; `IdLote`; `CodigoLote`/`FechaVencimiento`/`IdLote` |
| `src/Ways.Domain/Catalogos/ParametroConocido.cs` | Modify | 2 keys + both added to `PorClave` |
| `src/Ways.Infrastructure/Persistencia/Configuraciones/{Lote,StockLote}Configuration.cs` | Create | Named PK/AK/FK/CHECK/index, EF PascalCase defaults overridden |
| `…/Configuraciones/{Articulo,MovimientoStock,ItemComprobanteVenta,ItemComprobanteCompra}Configuration.cs` | Modify | New columns, composite FKs against `ux_lotes_id_articulo_tenant` |
| `…/WaysDbContext.cs` | Modify | `DbSet<Lote>`, `DbSet<StockLote>`, `AplicarFiltroDeTenantEnStockLote` (decision 20) |
| `…/Migraciones/…_LotesYVencimientosEtapa12.cs` | Create | **One** migration, statement order above |
| `src/Ways.Api/Seguridad/ManejadorDeErrores.cs` | Modify | `23505` → `lote_duplicado` (`ux_lotes_articulo_codigo`) and the `ux_lotes_sin_identificar` schema backstop; `23503` needs **no change** (the `fk_` prefix arm already covers the four new FKs) |
| `src/Ways.Application/Stock/ServicioDeLotes.cs` | Create | Get-or-create, sin-identificar, saldos, reconciliation, listing/alta |
| `src/Ways.Application/Stock/ServicioDeStock.cs` | Modify | Lot-aware ajuste/transfer/conteo, `DecomisarAsync`, `UpsertStockLoteAsync`, `BloquearYCrearSiFaltaStockLoteAsync`, `ClaveDeStock` |
| `src/Ways.Application/Ventas/ServicioDeVentas.cs` | Modify | `ResolverParametrosDeVentaAsync` (2→1), FEFO planning, `+idLote` on the two raw statements, `UpsertStockLoteAsync`, snapshot, exact anulación |
| `src/Ways.Application/Compras/ServicioDeCompras.cs` | Modify | Draft passthrough, resolution pass at confirm, per-lot writes, per-lot anulación refusal |
| `src/Ways.Application/Articulos/ServicioDeArticulos.cs` · `Parametros/ServicioDeParametros.cs` | Modify | `false → true` detection ⇒ reconciliation trigger |
| `src/Ways.Application/Reportes/ServicioDeReportesDeStock.cs` | Modify | `ObtenerVencimientosAsync`, `ObtenerResumenDeVencimientosAsync`, `ResolverZonaAsync` |
| `src/Ways.Application/Reportes/ExportacionDeReportes.cs` | Modify | One `De(Vencimientos, ctx)` mapper |
| `src/Ways.Api/Endpoints/{Stock,Reportes}Endpoints.cs` | Modify | The seven routes above |
| `src/Ways.Web/src/paginas/{Pos,CompraEditor,Transferencias,ConteoDeInventario,Articulos,Parametros,Tablero}.tsx` | Modify | Picker, lot inputs, lot columns, flag, toggles, tile |
| `src/Ways.Web/src/paginas/Vencimientos.tsx` · `App.tsx` · `componentes/Layout.tsx` | Create/Modify | New screen + route + nav |
| `src/Ways.Web/src/api/tipos.ts` · `catalogos.ts` | Modify | Mirrored contracts; `controlaLote` descriptor field; the two parametro keys |
| **Database** | **See the gate section of `proposal.md`** | 2 tables, 6 columns, 2 enum values, 0 rewritten rows |

## Testing Strategy

| Layer | What | Approach |
|---|---|---|
| Domain unit (no DB) | `ControlEfectivo` truth table; `OrdenarFefo` with a sin-identificar + two dated lots + a tie on expiry; `ElegirFefo` returning `null` when every balance is ≤ 0; `DerivarCodigo`; `Clasificar` at all four boundaries (`hoy-1`, `hoy`, `hoy+dias`, `hoy+dias+1`, `null`) | Hand-built `SaldoDeLote` records, xUnit — the `PoliticaDeRoles` pattern |
| Application unit | The batched parametro resolution over hand-built `Parametro` lists (two keys, one with a PV row and one without) | No DB; `ResolucionDeParametros` is already pure |
| Integration — invariants | (1) `stock.cantidad = SUM(movimientos)` after a mixed sequence of **all eight** motivos including a `reclasificacion` pair; (2) `stock_lotes.cantidad = SUM(movimientos with that lot)` after compra → venta → transferencia → NCX → anulación → conteo → decomiso; (3) `SUM(stock_lotes) = stock.cantidad` for a reconciled lot-effective pair | One long-form test per invariant, asserting **every** row of `movimientos_stock` (rule 6), not just the totals |
| Integration — off switch | Query count `= 16` with `lotes_habilitado = false`; `= 16` with the module on and no lot-effective articulo; `= 17` with one; and **no `stock_lotes` row and no `id_lote`** written in the first two cases | `ContadorDeComandos` (`VentasCheckoutTests:864-882`), the existing constant updated deliberately, never behind a `<=` |
| Integration — concurrency, **one per write site** | (a) checkout vs. reverse transfer of the same articulo+lots; (b) purchase confirm vs. checkout; (c) transfer A→B vs. transfer B→A. Each asserts *both* transactions complete and no `40P01` surfaces | The `VentasAtomicidadYConcurrenciaTests` fixture shape; per-site, never one shared assertion (proposal risk #1) |
| Integration — races / backstops | Two concurrent `POST /api/stock/lotes` with the same código ⇒ exactly one 201 + one 409 `lote_duplicado`; two concurrent checkouts of a never-received lot-effective articulo ⇒ exactly one sin-identificar lot (decision 5); raw-SQL `23505` proof on `ux_lotes_sin_identificar` (documented race-test exemption, the `pk_stock` precedent) | `db-error-backstops` execution steps: mappings written **before** the endpoints, SQLSTATE asserted, never just the exception type |
| Integration — RLS | `lotes` and `stock_lotes` cross-tenant `SELECT`/`INSERT`/`UPDATE` over the **`ways_app`** connection (NOSUPERUSER NOBYPASSRLS) at statement level, asserting **row counts** for the silent 0-row cases and `42501` where an error is actually raised | `mutation-proof-tests` rule 5. Residual weakness recorded below |
| Integration — refusals | Per-lot transfer insufficiency **with a sufficient aggregate** (two lots, only one holding stock); `transferencia_lote_vencido`; `lote_vencido_en_recepcion`; `stock_insuficiente_para_decomiso`; `lote_vencimiento_incompatible` on a second reception of the same código with a different date; `conteo_contada_y_lotes` | One test per code, asserting `codigo` and HTTP status |
| Integration — reconciliation | Idempotence: run twice, assert the **`movimientos_stock` row count is identical** after the second run (the discriminating value — a count that grows proves the zero-residue guard was deleted); self-heal: sell into an unreconciled pair, then reconcile, assert `SUM(stock_lotes) = stock.cantidad` | |
| Integration — vencimientos | "Hoy" in a non-UTC zone: clock pinned at `2026-08-13T02:00Z`, PV zone `America/Argentina/Buenos_Aires` (local `2026-08-12 23:00`), one lot expiring `2026-08-12` ⇒ **`PorVencer`**, not `Vencido`. Export equality: call JSON and `/export` with the same query string, read the workbook back, compare **every cell of every row**, with different values per row and per column so a swap is detectable | `mutation-proof-tests` rules 4 and 6; the stage-11 `ExportacionTests` shape |
| Web (vitest) | `controlaLote` `aAlta`/`aValores` coercion and `'' → null`; the two parametro keys' type coercion; the lot picker's `sugerido` pre-selection; a stale picker response landing after the operator changed line — **the stale promise resolved inside `act`**, asserted synchronously after the flush (rule 7); double-click on the picker ⇒ exactly one `fetch`; Vencimientos screen + download button; incomplete-line counter on `CompraEditor` **and** on `Transferencias` | `web-descriptor-tests` (colocated `*.test.ts(x)`), `react-async-state` |
| Exempt | The Tablero tile's visual rendering has no automated assertion beyond the counts being those of the report — recorded exemption | — |

**Mutation targets** (`mutation-proof-tests`: name the clause, run the mutation, record
applied → failing test → reverted → green in the PR body):

| Clause | Mutation | Test that MUST fail |
|---|---|---|
| `ReglaDeLotes.ControlEfectivo`'s `&&` | change to `\|\|` | the off-switch test: a `controla_lote = true` articulo in an empresa with `lotes_habilitado = false` writes **no** `stock_lotes` row and still counts 16 queries |
| `candidatos.Where(p => p.Clave == c.Clave)` in the batched read | delete it | two-key test where `tolerancia_pago` has a PV-scoped row and `vuelto_maximo` only an empresa row — both values asserted |
| `.OrderByDescending(s => s.EsSinIdentificar)` in `OrdenarFefo` | delete it | Domain ordering test with a sin-identificar lot and a lot expiring yesterday — asserts the **id sequence**, not a set |
| `.ThenBy(c => c.IdLote.HasValue).ThenBy(c => c.IdLote ?? 0)` in `ConstruirClavesOrdenadas` | delete both | the transfer-vs-reverse-transfer deadlock test |
| `if (clave.IdLote is not null && delta < 0 && nueva < 0) throw` | delete the `if` | per-lot insufficiency test seeded with a **sufficient aggregate** across two lots (the aggregate check alone cannot explain the outcome — rule 3) |
| `residuo == 0 ⇒ escribir nada` in `ReconciliarAsync` | delete the guard | idempotence test asserting the `movimientos_stock` **row count** is unchanged on the second run |
| `TimeZoneInfo.ConvertTime(reloj.Ahora, zona)` in the vencimientos report | replace with `reloj.Ahora.UtcDateTime` | the non-UTC classification test above (the lot flips `PorVencer` → `Vencido`) |
| `ItemComprobanteVenta.IdLote = i.IdLote` in the snapshot | replace with `null` | exact-anulación test asserting the reversal's `id_lote` **and** the resulting per-lot balance |
| `.RequireAuthorization(Politicas.GestionDeCatalogo)` on `/stock/decomiso` | delete the line | Vendedor-403 test (the group's `OperacionDePos` alone admits Vendedor, so the mutation is observable) |

## Slicing (refined — 15 PRs, stacked-to-main)

The proposal's 11 slices split at exactly the points it pre-identified, plus one it did not: the
sale is cut at the **planning/writing** boundary (7|8) because the FEFO planner is pure and
testable without a transaction, while the write path needs the concurrency fixture — two very
different test shapes in one PR is how a 400-line budget becomes 700.

| # | Branch | Content | ~Lines | Test plan |
|---|---|---|---|---|
| 1 | `feat/stage12-slice1-esquema` | The whole migration (2 tables, 6 columns, 2 enum values, RLS), EF configs, tenant filter, `ManejadorDeErrores` mappings | **~430** ⚠ | Migration apply; RLS ×2 over `ways_app`; raw-SQL `23505` on both `lotes` indexes; `23503` regression on the four FKs |
| 2 | `…slice2-activacion` | 2 `ParametroConocido` keys, the whole of `ReglaDeLotes`, the batched parametro read (2→1) | ~260 | Domain suite (5 facts); the parametro mutation; query count `17 → 16` |
| 3 | `…slice3-servicio-de-lotes` | `ServicioDeLotes` get-or-create + sin-identificar + `LeerSaldosAsync` + `GET/POST /api/stock/lotes` | ~300 | Race test (two concurrent altas); `lote_vencimiento_incompatible`; `codigo_de_lote_reservado` |
| 4 | `…slice4-reconciliacion` | The `reclasificacion` pair, both activation hooks, the admin endpoint | ~330 | Idempotence (row count); self-heal; net-zero proof on the aggregate |
| 5 | `…slice5-recepcion` | Compra: draft passthrough, resolution pass at confirm, per-lot movement + balance, `lote_vencido_en_recepcion` | ~330 | Invariant 2 after a compra; expired-reception refusal; confirm-vs-checkout concurrency |
| 6 | `…slice6-compra-anulacion` | Per-lot anulación refusal (both checks) | ~180 | Sufficient-aggregate/insufficient-lot refusal |
| 7 | `…slice7-venta-plan-fefo` | FEFO planning in the decide phase, `LineaDeVenta.IdLote`, plan carries the lot, `lote_invalido` | ~280 | Query count `16 → 17`; omitted-`idLote` picks FEFO; supplied-`idLote` honoured |
| 8 | `…slice8-venta-escritura` | Per-lot writes in the pinned order, item snapshot, exact anulación | ~330 | Invariant 2 after venta + anulación; checkout-vs-reverse-transfer deadlock test |
| 9 | `…slice9-ncx` | NCX lot rules + the `loteVencido` warning on the response | ~200 | `lote_requerido` on NCX; expired lot sells with the flag set |
| 10 | `…slice10-transferencias` | Lot travels, `ClaveDeStock` order, per-lot sufficiency, expired refusal | ~360 | A→B vs. B→A deadlock; per-lot insufficiency with sufficient aggregate; `transferencia_lote_vencido` |
| 11 | `…slice11-ajuste-decomiso` | Lot-aware ajuste + `POST /api/stock/decomiso` | ~280 | `stock_insuficiente_para_decomiso`; Vendedor-403; sign discipline (client positive) |
| 12 | `…slice12-conteo` | Per-lot conteo (degradation pre-approved) | ~250 | Lock-acquisition order; zero-difference lot writes nothing; `conteo_contada_y_lotes` |
| 13 | `…slice13-vencimientos` | Report + `/export` sibling + `/resumen` + Tablero tile | ~320 | Non-UTC "hoy"; export equality cell by cell; cap + `+1` race backstop |
| 14 | `…slice14-web-operacion` | POS lot picker (FEFO pre-selected) + reception lot inputs | ~400 | Descriptor + component tests; stale-response flush; double-click |
| 15 | `…slice15-web-backoffice` | Vencimientos screen, `controlaLote` on the articulo editor, the two parametro toggles, lot columns in transfers/conteo | ~400 | Descriptor tests; the incomplete-line counter replicated across both grids |

Total ≈ **4 650**. `chain_strategy: stacked-to-main`, one judgment-day round per slice.

**Parallelism.** Everything blocks on `1 → 2 → 3`. After 3 merges, four fronts are independent
because they live in **different files**: `[4]` (`ServicioDeLotes`), `[5 → 6]` (`ServicioDeCompras`),
`[7 → 8 → 9]` (`ServicioDeVentas`), `[10 → 11 → 12]` (`ServicioDeStock`). `[13]` needs only slice 1
and can run from the start of that wave. `14` needs `5 + 8`; `15` needs `12 + 13`. The conflict
surface between fronts is `Contratos.cs` per capability (disjoint files) and `StockEndpoints.cs`
(one route line per slice). Only 14 and 15 touch `App.tsx`/`Layout.tsx`, one line each.

**Budget.** Slice 1 is the single `size:exception` this stage anticipates, for the reason in
decision 21 (the gate contract names one migration and a migration is not splittable across merged
PRs). Every other slice sits under 400; as in every prior stage, overflow is expected from **test
depth**, not scope, and slices 10 and 14/15 are the ones to watch.

## Open Questions

- [ ] **`EstadoDeVencimiento.SinFecha` is a fourth class** the proposal did not name (decision 16).
      `sdd-spec` must decide whether the report's contract enumerates four states or three plus a
      documented null. Recommendation: four — a report whose rows do not sum to the balance is a
      report that lies by omission.
- [ ] **`SolicitudDeConteo.Contada` widens to `decimal?`** (decision 18). Source-compatible, but it
      is a published contract change and belongs in the `conteo-de-inventario` delta explicitly.
- [ ] **`SIN-IDENTIFICAR` is a reserved código** (decision 5). It is a value, not schema, so the gate
      stays closed — but it becomes a user-visible string in the picker and on tickets, and it is the
      one place where an implementation detail leaks into the operator's vocabulary.
- [ ] **`ways_owner` is a testcontainer superuser**, so the migration fixture cannot prove RLS.
      Mitigated by running every RLS assertion over the `ways_app` connection at statement level
      (stage-9 precedent), which *does* observe a missing policy. What stays unprovable is the
      migration path itself: if `HabilitarRlsDeTenant` were omitted for one of the two tables, the
      `ways_app` test catches it — if it were emitted without `FORCE`, the `ways_app` test still
      catches it (the app role is not the table owner). Recorded as adequately covered **for these
      two tables**; the repo-wide weakness is unchanged and still open.
- [ ] **The FEFO read's row bound is behavioural, not structural** (decision 6). It is bounded by
      "lots with a non-zero balance at this PV", which is small in practice but not capped by the
      schema. If a tenant ever accumulates hundreds of live lots per articulo at one PV, the escape
      hatch is a `LIMIT` on the FEFO candidates ordered by expiry — additive, no contract change.
- [ ] **Reconciliation triggers live inside two ABM services** (`ServicioDeArticulos`,
      `ServicioDeParametros`), which is the first time either performs a stock-domain write. The
      alternative — make activation admin-endpoint-only and never automatic — is one deletion away
      and would trade convenience for a cleaner containment boundary. Flagged for the owner, not
      blocking.
