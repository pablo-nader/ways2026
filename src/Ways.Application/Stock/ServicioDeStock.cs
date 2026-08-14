using System.Data;
using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ways.Application.Abstracciones;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Common;
using Ways.Domain.Organizacion;
using Ways.Domain.Stock;

namespace Ways.Application.Stock;

/// <summary>
/// Ajuste manual de stock (design decisión 10: dedicado, no una extensión de
/// <c>ServicioDeArticulos</c> — autorización admin-only y forma de escritura son propias de esta
/// operación, mismo criterio que <c>ServicioDeVentas</c>/<c>ServicioDeEscaneo</c>). El endpoint
/// (<c>Politicas.GestionDeCatalogo</c>) es quien bloquea al Vendedor — este servicio no repite
/// ese chequeo, mismo criterio que el resto de los ABM del proyecto (autorización vive en la capa
/// de API, nunca duplicada en Application).
///
/// Etapa 12, slice 10 (design: Write site 3): <see cref="ServicioDeLotes"/> se inyecta para el
/// mismo trío de primitivas que <c>ServicioDeVentas</c> ya consume — <c>LeerSaldosAsync</c> (fase
/// de resolución de la transferencia) y <c>ResolverSinIdentificarAsync</c> (get-or-create
/// perezoso, statement crudo, cuando ningún lote del artículo tiene saldo positivo en el origen).
///
/// Etapa 12, slice 11 (design: Write site 3, proposal decisión 9): <c>AjustarAsync</c> gana la
/// dimensión de lote (<c>idLote</c> requerido/rechazado según <c>EsLoteEfectivo</c>) y
/// <c>DecomisarAsync</c> nace como motivo de primera clase — estructuralmente el mismo camino de
/// <c>AjustarAsync</c>, con la cantidad positiva del cliente negada server-side (disciplina de
/// <c>ContarAsync</c>) y el único rechazo de negatividad que este servicio conoce
/// (<c>409 stock_insuficiente_para_decomiso</c>). No restringido a lotes vencidos.
/// </summary>
public class ServicioDeStock(IWaysDbContext db, IRelojDelSistema reloj, IContextoDeUsuario contexto, ServicioDeLotes servicioDeLotes)
{
    public async Task<decimal> ObtenerCantidadAsync(int idPuntoVenta, int idArticulo, CancellationToken ct = default) =>
        await db.Stock
            .Where(s => s.IdArticulo == idArticulo && s.IdPuntoVenta == idPuntoVenta)
            .Select(s => s.Cantidad)
            .FirstOrDefaultAsync(ct);

    /// <summary>Design: API Surface — <c>POST /api/stock/ajustes</c>, <c>motivo = ajuste</c>, una
    /// única transacción (movimiento + upsert del caché, spec: Manual Ajuste Path Is
    /// Admin-Only).</summary>
    public async Task<decimal> AjustarAsync(SolicitudDeAjusteDeStock solicitud, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();
        var idEmpleado = contexto.UsuarioId;
        var momento = reloj.Ahora;

        var cantidad = ExigirCantidadValida(solicitud.Cantidad);
        var observaciones = ExigirObservaciones(solicitud.Observaciones);

        // Pre-checks de existencia/tenant ANTES de la transacción (mismo criterio que
        // ServicioDeVentas: la referencia se valida sobre una lectura simple, nunca dejando que
        // el FK real de la base la rechace con un 500 crudo dentro del INSERT crudo de abajo).
        var articulo = await ResolverArticuloAsync(solicitud.IdArticulo, ct);
        var puntoVenta = await ResolverPuntoVentaAsync(solicitud.IdPuntoVenta, ct);

        // Etapa 12, slice 11 (design: Write site 3 — "IdLote required when lot-effective"):
        // resuelto/validado ANTES de la transacción, mismo criterio que la fase de resolución de
        // TransferirAsync.
        var idLote = await ResolverIdLoteEfectivoAsync(puntoVenta.IdEmpresa, puntoVenta.Id, articulo, solicitud.IdLote, ct);

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);
        return await estrategia.ExecuteAsync(async () =>
            await EjecutarAjusteAsync(
                idTenant, idEmpleado, solicitud.IdArticulo, solicitud.IdPuntoVenta, cantidad, observaciones, idLote,
                momento, ct));
    }

    private async Task<decimal> EjecutarAjusteAsync(
        int idTenant, int idEmpleado, int idArticulo, int idPuntoVenta, decimal cantidad, string observaciones,
        int? idLote, DateTimeOffset momento, CancellationToken ct)
    {
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);

        var conexion = await ObtenerConexionAbiertaAsync(ct);
        var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction();

        await InsertarMovimientoStockAsync(
            conexion, transaccionCruda, idTenant, idArticulo, idPuntoVenta, cantidad, MotivoStock.Ajuste, idEmpleado,
            observaciones, momento, idComprobanteCompra: null, idPuntoVentaDestino: null, idLote, ct);

        var nuevaCantidad = await UpsertStockAsync(conexion, transaccionCruda, idTenant, idArticulo, idPuntoVenta, cantidad, ct);

        // Etapa 12, slice 11 (design: Write site 3 — "aggregate upsert then lot upsert, in that
        // order"): el agregado SIEMPRE upsertea primero (lock order, decisión 6/9 del proposal);
        // el lote solo cuando el artículo es lote-efectivo. Sin rechazo de negatividad — el ajuste
        // es la operación que CORRIGE un saldo negativo (spec: "no negativity refusal").
        if (idLote is { } idLoteEfectivo)
        {
            await UpsertStockLoteAsync(conexion, transaccionCruda, idTenant, idArticulo, idPuntoVenta, idLoteEfectivo, cantidad, ct);
        }

        await transaccion.CommitAsync(ct);

        return nuevaCantidad;
    }

    // ---- decomiso (design: Write site 3 — "EjecutarDecomisoAsync, structurally EjecutarAjusteAsync
    // with three deltas"; proposal decisión 9) --------------------------------------------------

    /// <summary>Design: API Surface — <c>POST /api/stock/decomiso</c>, <c>motivo = decomiso</c>,
    /// primera clase (proposal decisión 9: NO una bandera de <c>ajuste</c>). Tres diferencias con
    /// <see cref="AjustarAsync"/>: (1) <see cref="SolicitudDeDecomiso.Cantidad"/> llega SIEMPRE
    /// positiva y se niega acá, nunca en el servicio de abajo (misma disciplina que
    /// <c>ContarAsync</c> — el cliente nunca manda un delta con signo); (2) el único rechazo de
    /// negatividad de este servicio (<c>409 stock_insuficiente_para_decomiso</c>) sobre el saldo
    /// OPERATIVO — el del lote si es lote-efectivo, si no el agregado (spec: "the target balance");
    /// (3) <c>observaciones</c> obligatoria igual que un ajuste. NO restringido a lotes vencidos
    /// (decisión 9 del proposal) — la merma real entra en el mismo cajón que la vencida.</summary>
    public async Task<decimal> DecomisarAsync(SolicitudDeDecomiso solicitud, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();
        var idEmpleado = contexto.UsuarioId;
        var momento = reloj.Ahora;

        var cantidadPositiva = ExigirCantidadDeDecomisoValida(solicitud.Cantidad);
        var observaciones = ExigirObservaciones(solicitud.Observaciones);

        var articulo = await ResolverArticuloAsync(solicitud.IdArticulo, ct);
        var puntoVenta = await ResolverPuntoVentaAsync(solicitud.IdPuntoVenta, ct);

        var idLote = await ResolverIdLoteEfectivoAsync(puntoVenta.IdEmpresa, puntoVenta.Id, articulo, solicitud.IdLote, ct);

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);
        return await estrategia.ExecuteAsync(async () =>
            await EjecutarDecomisoAsync(
                idTenant, idEmpleado, solicitud.IdArticulo, solicitud.IdPuntoVenta, cantidadPositiva, observaciones,
                idLote, momento, ct));
    }

    private async Task<decimal> EjecutarDecomisoAsync(
        int idTenant, int idEmpleado, int idArticulo, int idPuntoVenta, decimal cantidadPositiva, string observaciones,
        int? idLote, DateTimeOffset momento, CancellationToken ct)
    {
        // Disciplina de ContarAsync: nunca un delta con signo provisto por el cliente — acá es
        // donde el "positivo" del contrato se convierte en la baja real.
        var delta = -cantidadPositiva;

        await using var transaccion = await db.Database.BeginTransactionAsync(ct);

        var conexion = await ObtenerConexionAbiertaAsync(ct);
        var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction();

        await InsertarMovimientoStockAsync(
            conexion, transaccionCruda, idTenant, idArticulo, idPuntoVenta, delta, MotivoStock.Decomiso, idEmpleado,
            observaciones, momento, idComprobanteCompra: null, idPuntoVentaDestino: null, idLote, ct);

        var nuevaAgregada = await UpsertStockAsync(conexion, transaccionCruda, idTenant, idArticulo, idPuntoVenta, delta, ct);

        if (idLote is { } idLoteEfectivo)
        {
            var nuevaDelLote = await UpsertStockLoteAsync(
                conexion, transaccionCruda, idTenant, idArticulo, idPuntoVenta, idLoteEfectivo, delta, ct);

            // spec lotes-y-vencimientos: "the target balance (the lot's stock_lotes.cantidad when
            // lot-effective, otherwise stock.cantidad)" — SOLO el lote decide cuando es
            // lote-efectivo, el agregado nunca se vuelve a chequear acá (a diferencia de una
            // transferencia, que chequea ambos).
            if (nuevaDelLote < 0m)
            {
                throw new ErrorDominio(
                    "stock_insuficiente_para_decomiso",
                    $"No hay stock suficiente del lote {idLoteEfectivo} del artículo {idArticulo} para decomisar.",
                    409);
            }
        }
        else if (nuevaAgregada < 0m)
        {
            throw new ErrorDominio(
                "stock_insuficiente_para_decomiso",
                $"No hay stock suficiente del artículo {idArticulo} para decomisar.",
                409);
        }

        await transaccion.CommitAsync(ct);

        return nuevaAgregada;
    }

    // ---- transferencia entre puntos de venta (design: Transactions — TRANSFERENCIA; decisión 9) ----

    /// <summary>Design decisión 9: rechaza líneas vacías/inválidas en memoria, antes de cualquier
    /// consulta. Etapa 12, slice 10 (design: Write site 3): el lote viaja — cada línea
    /// lote-efectiva resuelve su <c>idLote</c> (explícito o FEFO-defaulted) en la fase de
    /// resolución, ANTES de que la transacción abra, y esa fase es también donde corren el
    /// rechazo de duplicados <c>(IdArticulo, IdLote)</c> post-defaulting (decisión 11) y el
    /// chequeo de <c>transferencia_lote_vencido</c>. Dentro de la transacción, un único orden
    /// ascendente sobre las <c>≥2N</c> claves <c>(id_articulo, id_punto_venta, id_lote NULLS
    /// FIRST)</c> — nunca "todo el origen, después todo el destino" — es lo que evita el deadlock
    /// contra una transferencia inversa simultánea (B→A) y contra un checkout concurrente del
    /// mismo artículo/lote en cualquiera de los dos puntos de venta.</summary>
    public async Task<ResultadoTransferencia> TransferirAsync(SolicitudDeTransferencia solicitud, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();
        var idEmpleado = contexto.UsuarioId;
        var momento = reloj.Ahora;

        // spec: "Same-PV transfer is rejected... before reaching the database" — chequeo puramente
        // en memoria, antes de cualquier consulta.
        if (solicitud.IdPuntoVentaOrigen == solicitud.IdPuntoVentaDestino)
        {
            throw new ErrorDominio(
                "transferencia_origen_igual_destino",
                "El origen y el destino de una transferencia tienen que ser puntos de venta distintos.",
                400);
        }

        var observaciones = ExigirObservaciones(solicitud.Observaciones);
        var lineas = ExigirLineasDeTransferenciaValidas(solicitud.Lineas);

        // Pre-checks de existencia/tenant ANTES de la transacción (mismo criterio que
        // AjustarAsync): ResolverPuntoVentaAsync da el mismo 404 para "no existe" y "es de otro
        // tenant" (ADR-8), tanto para origen como para destino.
        var puntoVentaOrigen = await ResolverPuntoVentaAsync(solicitud.IdPuntoVentaOrigen, ct);
        await ResolverPuntoVentaAsync(solicitud.IdPuntoVentaDestino, ct);

        var articuloPorId = new Dictionary<int, Articulo>();
        foreach (var idArticulo in lineas.Select(l => l.IdArticulo).Distinct())
        {
            articuloPorId[idArticulo] = await ResolverArticuloAsync(idArticulo, ct);
        }

        // Etapa 12, slice 10 (design: Write site 3 — "lot resolution happens before the
        // transaction opens, same phase as the existing pre-checks"): FEFO-default de lotes
        // omitidos, chequeo de vencido y rechazo de duplicados post-defaulting.
        var lineasResueltas = await ResolverLineasDeTransferenciaAsync(
            idTenant, puntoVentaOrigen, articuloPorId, lineas, momento, ct);

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);
        return await estrategia.ExecuteAsync(async () =>
            await EjecutarTransferenciaAsync(
                idTenant, idEmpleado, solicitud.IdPuntoVentaOrigen, solicitud.IdPuntoVentaDestino, lineasResueltas,
                observaciones, momento, ct));
    }

    private async Task<ResultadoTransferencia> EjecutarTransferenciaAsync(
        int idTenant, int idEmpleado, int idPuntoVentaOrigen, int idPuntoVentaDestino,
        IReadOnlyList<LineaDeTransferenciaResuelta> lineas, string observaciones, DateTimeOffset momento, CancellationToken ct)
    {
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);

        var conexion = await ObtenerConexionAbiertaAsync(ct);
        var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction();

        var claves = ConstruirClavesOrdenadas(idPuntoVentaOrigen, idPuntoVentaDestino, lineas);
        // Etapa 12, slice 10, judgment-day fix (juez A, FIX 1): clave ensanchada a
        // (IdArticulo, IdLote) — dos líneas del mismo artículo con lotes distintos son ACEPTADAS
        // por spec y no pueden colapsar en una sola fila del response (dto-contract-honesty).
        var resultadosPorArticuloYLote = new Dictionary<(int IdArticulo, int? IdLote), (decimal Origen, decimal Destino)>();

        foreach (var clave in claves)
        {
            if (clave.IdLote is null)
            {
                // Elemento AGREGADO (design decisión 10): el ledger se escribe ACÁ, cargando
                // IdLoteDelMovimiento cuando la línea era lote-efectiva — el elemento LOTE (más
                // abajo) nunca escribe una segunda fila de movimientos_stock para el mismo par.
                await InsertarMovimientoStockAsync(
                    conexion, transaccionCruda, idTenant, clave.IdArticulo, clave.IdPuntoVenta, clave.Delta,
                    MotivoStock.Transferencia, idEmpleado, observaciones, momento,
                    idComprobanteCompra: null, idPuntoVentaDestino, clave.IdLoteDelMovimiento, ct);

                var nueva = await UpsertStockAsync(conexion, transaccionCruda, idTenant, clave.IdArticulo, clave.IdPuntoVenta, clave.Delta, ct);

                // La RETURNING del upsert ES el chequeo de suficiencia (design decisión 5): sin
                // segunda consulta, sin TOCTOU. Back-office tightening (spec: Insufficient Origin
                // Stock Is Refused) — asimétrico a propósito respecto de una venta, que nunca bloquea.
                if (clave.Delta < 0m && nueva < 0m)
                {
                    throw new ErrorDominio(
                        "stock_insuficiente_para_transferencia",
                        $"No hay stock suficiente del artículo {clave.IdArticulo} en el punto de venta de origen para transferir.",
                        409);
                }

                var claveResultado = (clave.IdArticulo, clave.IdLoteDelMovimiento);
                var previo = resultadosPorArticuloYLote.TryGetValue(claveResultado, out var existente) ? existente : (Origen: 0m, Destino: 0m);
                resultadosPorArticuloYLote[claveResultado] = clave.IdPuntoVenta == idPuntoVentaOrigen
                    ? (nueva, previo.Destino)
                    : (previo.Origen, nueva);
            }
            else
            {
                // Elemento LOTE: upsert de stock_lotes SOLO, sin fila de ledger propia (el
                // movimiento ya se escribió en el elemento agregado del mismo par). La RETURNING
                // es la suficiencia POR LOTE (spec: "even when the origin's aggregate
                // stock.cantidad is sufficient" — decisión 7 de la propuesta).
                var nuevaDelLote = await UpsertStockLoteAsync(
                    conexion, transaccionCruda, idTenant, clave.IdArticulo, clave.IdPuntoVenta, clave.IdLote.Value, clave.Delta, ct);

                if (clave.Delta < 0m && nuevaDelLote < 0m)
                {
                    throw new ErrorDominio(
                        "stock_insuficiente_para_transferencia",
                        $"No hay stock suficiente del lote {clave.IdLote} del artículo {clave.IdArticulo} en el punto de venta de origen para transferir.",
                        409);
                }
            }
        }

        await transaccion.CommitAsync(ct);

        var lineasResultado = resultadosPorArticuloYLote
            .OrderBy(kv => kv.Key.IdArticulo)
            .ThenBy(kv => kv.Key.IdLote.HasValue)   // NULLS FIRST, mismo criterio que ConstruirClavesOrdenadas
            .ThenBy(kv => kv.Key.IdLote ?? 0)
            .Select(kv => new LineaTransferida(kv.Key.IdArticulo, kv.Key.IdLote, kv.Value.Origen, kv.Value.Destino))
            .ToList();

        return new ResultadoTransferencia(idPuntoVentaOrigen, idPuntoVentaDestino, lineasResultado);
    }

    /// <summary>Línea de transferencia YA resuelta (design: "LineaResuelta") — <see cref="IdLote"/>
    /// es <c>null</c> para un artículo sin control de lote efectivo, o el lote explícito/FEFO-defaulted
    /// para uno lote-efectivo. Producida por <see cref="ResolverLineasDeTransferenciaAsync"/>, fuera
    /// de la transacción — <see cref="ConstruirClavesOrdenadas"/> nunca vuelve a tocar <c>lotes</c>.</summary>
    private readonly record struct LineaDeTransferenciaResuelta(int IdArticulo, decimal Cantidad, int? IdLote);

    /// <summary>Etapa 12, slice 10 (design: Write site 3 — "the lot travels"): claves ensanchadas a
    /// <c>(IdArticulo, IdPuntoVenta, IdLote, Delta, IdLoteDelMovimiento)</c>. Por línea
    /// lote-efectiva, 4 claves — agregado + su movimiento y saldo del lote, en origen y en
    /// destino; por línea sin lote, las 2 de siempre. El orden asc
    /// <c>(id_articulo, id_punto_venta, id_lote NULLS FIRST)</c> es el mismo orden total que los
    /// otros dos sitios de escritura (decisión 6, spec stock), lo que hace posible el joint proof
    /// checkout-vs-transferencia (task 10.12).</summary>
    private readonly record struct ClaveDeStock(int IdArticulo, int IdPuntoVenta, int? IdLote, decimal Delta, int? IdLoteDelMovimiento);

    private static List<ClaveDeStock> ConstruirClavesOrdenadas(
        int idPuntoVentaOrigen, int idPuntoVentaDestino, IReadOnlyList<LineaDeTransferenciaResuelta> lineas) =>
        lineas
            .SelectMany(l => l.IdLote is { } lote
                ? new[]
                  {
                      new ClaveDeStock(l.IdArticulo, idPuntoVentaOrigen, null, -l.Cantidad, lote),   // agregada + su movimiento
                      new ClaveDeStock(l.IdArticulo, idPuntoVentaOrigen, lote, -l.Cantidad, null),    // saldo del lote
                      new ClaveDeStock(l.IdArticulo, idPuntoVentaDestino, null, l.Cantidad, lote),
                      new ClaveDeStock(l.IdArticulo, idPuntoVentaDestino, lote, l.Cantidad, null)
                  }
                : new[]
                  {
                      new ClaveDeStock(l.IdArticulo, idPuntoVentaOrigen, null, -l.Cantidad, null),
                      new ClaveDeStock(l.IdArticulo, idPuntoVentaDestino, null, l.Cantidad, null)
                  })
            .OrderBy(c => c.IdArticulo)
            .ThenBy(c => c.IdPuntoVenta)
            .ThenBy(c => c.IdLote.HasValue)          // NULLS FIRST — decisión 9
            .ThenBy(c => c.IdLote ?? 0)
            .ToList();

    /// <summary>Etapa 12, slice 10 (design: Write site 3, fase de resolución — "keeps lotes and
    /// reads out of the transaction entirely"): para cada línea lote-efectiva, resuelve su lote
    /// (explícito validado contra <see cref="ServicioDeLotes.LeerSaldosAsync"/>, o FEFO-defaulted,
    /// o el sin-identificar perezoso cuando ningún lote tiene saldo positivo — mismo camino que
    /// <c>ServicioDeVentas</c>), rechaza un vencido (<c>transferencia_lote_vencido</c> — a
    /// diferencia del checkout, acá SIEMPRE bloquea, sea el lote explícito o resuelto por FEFO) y,
    /// al final, rechaza duplicados <c>(IdArticulo, IdLote)</c> evaluados DESPUÉS del defaulting
    /// (decisión 11 — reusa el código <c>articulo_repetido</c>).</summary>
    private async Task<IReadOnlyList<LineaDeTransferenciaResuelta>> ResolverLineasDeTransferenciaAsync(
        int idTenant, PuntoVenta puntoVentaOrigen, IReadOnlyDictionary<int, Articulo> articuloPorId,
        IReadOnlyList<LineaDeTransferencia> lineas, DateTimeOffset momento, CancellationToken ct)
    {
        var lineasResueltas = lineas.Select(l => new LineaDeTransferenciaResuelta(l.IdArticulo, l.Cantidad, IdLote: null)).ToList();

        var indicesConArticuloControlaLote = lineas
            .Select((l, indice) => (Linea: l, Indice: indice))
            .Where(x => articuloPorId[x.Linea.IdArticulo].ControlaLote)
            .Select(x => x.Indice)
            .ToList();

        var lotesHabilitado = indicesConArticuloControlaLote.Count > 0
            && await ResolverLotesHabilitadoAsync(puntoVentaOrigen.IdEmpresa, puntoVentaOrigen.Id, ct);

        var indicesConLoteEfectivo = lotesHabilitado ? indicesConArticuloControlaLote : [];

        // dto-contract-honesty / mismo criterio que ServicioDeVentas: un idLote en una línea SIN
        // lote efectivo no tiene destino — se rechaza en vez de tragárselo en silencio.
        var indicesConLoteEfectivoSet = indicesConLoteEfectivo.ToHashSet();
        for (var indice = 0; indice < lineas.Count; indice++)
        {
            if (!indicesConLoteEfectivoSet.Contains(indice) && lineas[indice].IdLote is not null)
            {
                throw new ErrorDominio(
                    "lote_invalido",
                    $"El artículo {lineas[indice].IdArticulo} no tiene lote efectivo; no admite idLote.",
                    400);
            }
        }

        if (indicesConLoteEfectivo.Count > 0)
        {
            var idsArticuloConLote = indicesConLoteEfectivo.Select(i => lineas[i].IdArticulo).Distinct().ToList();
            var idsLotePedidos = indicesConLoteEfectivo
                .Select(i => lineas[i].IdLote)
                .Where(idLote => idLote is not null)
                .Select(idLote => idLote!.Value)
                .Distinct()
                .ToList();

            var saldos = await servicioDeLotes.LeerSaldosAsync(puntoVentaOrigen.Id, idsArticuloConLote, idsLotePedidos, ct);
            var saldosPorArticulo = saldos.ToLookup(s => s.IdArticulo);

            // Honestidad documental: "hoy" acá es UTC naive, mismo criterio interino que
            // ServicioDeVentas/ServicioDeCompras/ServicioDeLotes en esta etapa.
            var hoy = DateOnly.FromDateTime(momento.UtcDateTime);

            foreach (var indice in indicesConLoteEfectivo)
            {
                var linea = lineas[indice];
                var saldosDelArticulo = saldosPorArticulo[linea.IdArticulo].ToList();

                SaldoDeLote loteResuelto;
                if (linea.IdLote is { } idLote)
                {
                    var posicion = saldosDelArticulo.FindIndex(s => s.IdLote == idLote);
                    if (posicion < 0)
                    {
                        throw new ErrorDominio(
                            "lote_invalido",
                            $"El lote {idLote} no existe, no pertenece al artículo {linea.IdArticulo} o fue eliminado.",
                            400);
                    }

                    loteResuelto = saldosDelArticulo[posicion];
                }
                else if (ReglaDeLotes.ElegirFefo(saldosDelArticulo, hoy) is { } elegido)
                {
                    loteResuelto = elegido;
                }
                else
                {
                    // Ningún lote con saldo positivo (design decisión 7) — get-or-create perezoso
                    // del sin-identificar, statement crudo fuera de la transacción. En una
                    // transferencia (a diferencia del checkout) esto típicamente desemboca en
                    // stock_insuficiente_para_transferencia dentro de la transacción — el sin
                    // identificar arranca en 0, y transferir cualquier cantidad positiva lo deja
                    // negativo, exactamente el mismo camino de rechazo que un lote explícito
                    // insuficiente.
                    var conexionParaLotes = await ObtenerConexionAbiertaAsync(ct);
                    var idSinIdentificar = await ServicioDeLotes.ResolverSinIdentificarAsync(
                        conexionParaLotes, transaccion: null, idTenant, linea.IdArticulo, momento, ct);

                    loteResuelto = new SaldoDeLote(
                        linea.IdArticulo, idSinIdentificar, ReglaDeLotes.CodigoSinIdentificar,
                        EsSinIdentificar: true, FechaVencimiento: null, Cantidad: 0m);
                }

                // spec transferencias-de-stock: "Expired Lot Transfer Is Refused" — a diferencia
                // del checkout (decisión 12: solo un warning), una transferencia SIEMPRE bloquea
                // sobre un lote vencido, sea explícito o resuelto por FEFO.
                if (ReglaDeLotes.EstaVencido(loteResuelto.FechaVencimiento, hoy))
                {
                    throw new ErrorDominio(
                        "transferencia_lote_vencido",
                        $"El lote {loteResuelto.Codigo} del artículo {linea.IdArticulo} está vencido; no se puede transferir.",
                        409);
                }

                lineasResueltas[indice] = lineasResueltas[indice] with { IdLote = loteResuelto.IdLote };
            }
        }

        // spec transferencias-de-stock: "Duplicate-Line Detection Widens To (IdArticulo, IdLote),
        // Evaluated After FEFO Defaulting" (decisión 11) — reusa el código articulo_repetido,
        // evaluado sobre el resultado YA resuelto, nunca contra el input crudo del cliente.
        var repetida = lineasResueltas
            .GroupBy(l => (l.IdArticulo, l.IdLote))
            .FirstOrDefault(g => g.Count() > 1);
        if (repetida is not null)
        {
            throw new ErrorDominio(
                "articulo_repetido",
                $"El artículo {repetida.Key.IdArticulo} aparece más de una vez en la transferencia para el mismo lote.",
                400);
        }

        return lineasResueltas;
    }

    /// <summary>Mismo patrón que <see cref="ServicioDeLotes"/>'s resolución de un único parámetro
    /// de clave (<c>ResolverDiasAlertaAsync</c>): candidatos filtrados por <c>Clave</c> ANTES de
    /// <c>ResolucionDeParametros.Resolver</c> (design decisión 2 — nunca un candidato multi-clave
    /// sin filtrar).</summary>
    private async Task<bool> ResolverLotesHabilitadoAsync(int idEmpresa, int idPuntoVenta, CancellationToken ct)
    {
        var candidatos = await db.Parametros
            .Where(p => p.Clave == ParametroConocido.LotesHabilitado.Clave && p.IdEmpresa == idEmpresa
                && (p.IdPuntoVenta == null || p.IdPuntoVenta == idPuntoVenta))
            .ToListAsync(ct);

        var valorJson = ResolucionDeParametros.Resolver(ParametroConocido.LotesHabilitado.Clave, candidatos, idPuntoVenta);
        return JsonSerializer.Deserialize<bool>(valorJson);
    }

    /// <summary>Etapa 12, slice 11 (design: Write site 3 — "IdLote required when lot-effective
    /// (lote_requerido), refused when not (lote_no_aplica)"). Compartido por
    /// <see cref="AjustarAsync"/> y <see cref="DecomisarAsync"/> — ambos operan sobre UN solo
    /// artículo/punto de venta, a diferencia de <see cref="ResolverLineasDeTransferenciaAsync"/>
    /// (multi-línea, con FEFO-default). Acá NO hay FEFO-default: el ajuste/decomiso de un
    /// artículo lote-efectivo siempre exige el <c>idLote</c> explícito — no hay "línea de venta"
    /// que el operador esté surtiendo, así que no hay lote físico implícito que adivinar.
    /// <see cref="ServicioDeLotes.LeerSaldosAsync"/> valida que el <c>idLote</c> explícito existe,
    /// pertenece al artículo y no está borrado (<c>400 lote_invalido</c>) — mismo criterio de
    /// nunca dejar que la FK real de la base rechace con un 500 crudo.</summary>
    private async Task<int?> ResolverIdLoteEfectivoAsync(
        int idEmpresa, int idPuntoVenta, Articulo articulo, int? idLotePedido, CancellationToken ct)
    {
        var lotesHabilitado = await ResolverLotesHabilitadoAsync(idEmpresa, idPuntoVenta, ct);
        var esLoteEfectivo = ReglaDeLotes.ControlEfectivo(articulo.ControlaLote, lotesHabilitado);

        if (!esLoteEfectivo)
        {
            if (idLotePedido is not null)
            {
                throw new ErrorDominio(
                    "lote_no_aplica",
                    $"El artículo {articulo.Id} no tiene lote efectivo; no admite idLote.",
                    400);
            }

            return null;
        }

        if (idLotePedido is null)
        {
            throw new ErrorDominio(
                "lote_requerido",
                $"El artículo {articulo.Id} es lote-efectivo; requiere idLote.",
                400);
        }

        var saldos = await servicioDeLotes.LeerSaldosAsync(idPuntoVenta, [articulo.Id], [idLotePedido.Value], ct);
        if (!saldos.Any(s => s.IdLote == idLotePedido.Value))
        {
            throw new ErrorDominio(
                "lote_invalido",
                $"El lote {idLotePedido} no existe, no pertenece al artículo {articulo.Id} o fue eliminado.",
                400);
        }

        return idLotePedido;
    }

    // ---- conteo de inventario (design: Transactions — CONTEO DE INVENTARIO; decisión 10) ----------

    /// <summary>Design decisión 10: el cliente manda el TOTAL contado, nunca un delta. El delta
    /// se deriva del lado del servidor bajo el mismo lock de fila que <c>AjustarAsync</c> usa
    /// (el upsert no-op de <see cref="BloquearYCrearSiFaltaStockAsync"/>), así que un conteo
    /// nunca puede pisar una venta que corrió entre el conteo físico y el submit.
    ///
    /// Etapa 12, slice 12 (design decisión 18 — exactly-one-of; decisión 12 — conteo por lote):
    /// <see cref="SolicitudDeConteo.Contada"/>/<see cref="SolicitudDeConteo.Lotes"/> son mutuamente
    /// excluyentes, validado ANTES de resolver referencias (<c>400 conteo_contada_y_lotes</c>, ni
    /// siquiera un SELECT si el request está mal formado). La degradación pre-aprobada del
    /// proposal (decisión 11 — <c>409 conteo_lote_no_soportado</c>) NO se implementa en este
    /// slice: el conteo por lote se entrega completo, así que esa rama queda documentada acá pero
    /// deliberadamente sin código muerto (design: "keep the 409 branch reachable only if a future
    /// regression removes per-lot support" — no aplica hoy).</summary>
    public async Task<ResultadoConteo> ContarAsync(SolicitudDeConteo solicitud, CancellationToken ct = default)
    {
        var idTenant = ExigirTenantDeLaSesion();
        var idEmpleado = contexto.UsuarioId;
        var momento = reloj.Ahora;

        ExigirExactamenteUnaFormaDeConteo(solicitud.Contada, solicitud.Lotes);
        var observaciones = ExigirObservaciones(solicitud.Observaciones);

        await ResolverArticuloAsync(solicitud.IdArticulo, ct);
        await ResolverPuntoVentaAsync(solicitud.IdPuntoVenta, ct);

        var estrategia = FabricaDeEstrategiaSinReintento.CrearEstrategiaSinReintento(db);

        if (solicitud.Lotes is { Count: > 0 } lotes)
        {
            var lotesValidados = ExigirLotesDeConteoValidos(lotes);
            return await estrategia.ExecuteAsync(async () =>
                await EjecutarConteoPorLoteAsync(
                    idTenant, idEmpleado, solicitud.IdPuntoVenta, solicitud.IdArticulo, lotesValidados, observaciones,
                    momento, ct));
        }

        var contada = ExigirContadaValida(solicitud.Contada!.Value);
        return await estrategia.ExecuteAsync(async () =>
            await EjecutarConteoAsync(idTenant, idEmpleado, solicitud.IdPuntoVenta, solicitud.IdArticulo, contada, observaciones, momento, ct));
    }

    private async Task<ResultadoConteo> EjecutarConteoAsync(
        int idTenant, int idEmpleado, int idPuntoVenta, int idArticulo, decimal contada, string observaciones,
        DateTimeOffset momento, CancellationToken ct)
    {
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);

        var conexion = await ObtenerConexionAbiertaAsync(ct);
        var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction();

        var actual = await BloquearYCrearSiFaltaStockAsync(conexion, transaccionCruda, idTenant, idArticulo, idPuntoVenta, ct);
        var delta = contada - actual;

        if (delta == 0m)
        {
            // spec: "Zero-Difference Conteo Writes No Ledger Row" — commit sin escribir nada,
            // que además evita ck_movimientos_stock_cantidad_no_cero (nunca lo alcanza).
            await transaccion.CommitAsync(ct);
            return new ResultadoConteo(idPuntoVenta, idArticulo, actual, actual, 0m, MovimientoRegistrado: false);
        }

        await InsertarMovimientoStockAsync(
            conexion, transaccionCruda, idTenant, idArticulo, idPuntoVenta, delta, MotivoStock.Inventario, idEmpleado,
            observaciones, momento, idComprobanteCompra: null, idPuntoVentaDestino: null, idLote: null, ct);

        var final = await UpsertStockAsync(conexion, transaccionCruda, idTenant, idArticulo, idPuntoVenta, delta, ct);

        if (final != contada)
        {
            // Defensa en profundidad (design decisión 5): bajo el lock tomado en el paso 1, esta
            // rama es inalcanzable en operación normal — nadie más pudo escribir esa fila entre
            // el lock y este upsert.
            throw new InvalidOperationException(
                $"El conteo de inventario produjo un resultado inconsistente: esperado {contada}, obtenido {final}.");
        }

        await transaccion.CommitAsync(ct);

        return new ResultadoConteo(idPuntoVenta, idArticulo, final, actual, delta, MovimientoRegistrado: delta != 0m);
    }

    /// <summary>Etapa 12, slice 12 (design decisión 12 — "the per-lot conteo acquires all its locks
    /// first ..., derives every delta, and only then writes"): split ADQUISICIÓN/APLICACIÓN. El
    /// agregado no puede escribirse hasta conocer TODOS los deltas por lote (su delta es la SUMA de
    /// ellos) pero el orden pineado lo exige PRIMERO — la única salida sin inventar un segundo
    /// protocolo de lock es tomar cada lock (agregado, después cada lote ascendente) como un
    /// upsert no-op ANTES de escribir ningún delta, y recién ahí aplicar. Los locks ya tomados
    /// vuelven irrelevante el orden de la fase de aplicación para la concurrencia — se mantiene
    /// ascendente por determinismo del resultado, no por lock order.</summary>
    private async Task<ResultadoConteo> EjecutarConteoPorLoteAsync(
        int idTenant, int idEmpleado, int idPuntoVenta, int idArticulo, IReadOnlyList<ConteoDeLote> lotes,
        string observaciones, DateTimeOffset momento, CancellationToken ct)
    {
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);

        var conexion = await ObtenerConexionAbiertaAsync(ct);
        var transaccionCruda = db.Database.CurrentTransaction?.GetDbTransaction();

        // ---- ADQUISICIÓN: todos los locks, en el orden pineado (agregado, después cada lote
        // ascendente por id_lote) — ningún delta escrito todavía.
        var actualAgregado = await BloquearYCrearSiFaltaStockAsync(conexion, transaccionCruda, idTenant, idArticulo, idPuntoVenta, ct);

        var lotesAscendentes = lotes.OrderBy(l => l.IdLote).ToList();
        var actualPorLote = new Dictionary<int, decimal>();
        foreach (var lote in lotesAscendentes)
        {
            actualPorLote[lote.IdLote] = await BloquearYCrearSiFaltaStockLoteAsync(
                conexion, transaccionCruda, idTenant, idArticulo, idPuntoVenta, lote.IdLote, ct);
        }

        // ---- APLICACIÓN: todos los locks ya están tomados. Un lote sin diferencia no escribe fila
        // (spec: "A lot with no difference writes no row even when a sibling lot differs") — y
        // nunca se fabrica saldo en el sin-identificar para absorber una diferencia (spec: "A
        // lot-effective conteo never writes into the sin-identificar lot"): el delta SIEMPRE se
        // escribe con el id_lote exacto que lo originó.
        var resultadosPorLote = new List<LoteContado>(lotesAscendentes.Count);
        var deltaAgregado = 0m;
        var cantidadAgregadaFinal = actualAgregado;

        foreach (var lote in lotesAscendentes)
        {
            var actualDelLote = actualPorLote[lote.IdLote];
            var deltaDelLote = lote.Contada - actualDelLote;

            if (deltaDelLote == 0m)
            {
                resultadosPorLote.Add(new LoteContado(lote.IdLote, actualDelLote, actualDelLote, 0m, MovimientoRegistrado: false));
                continue;
            }

            await InsertarMovimientoStockAsync(
                conexion, transaccionCruda, idTenant, idArticulo, idPuntoVenta, deltaDelLote, MotivoStock.Inventario,
                idEmpleado, observaciones, momento, idComprobanteCompra: null, idPuntoVentaDestino: null, lote.IdLote, ct);

            var finalDelLote = await UpsertStockLoteAsync(
                conexion, transaccionCruda, idTenant, idArticulo, idPuntoVenta, lote.IdLote, deltaDelLote, ct);

            // El agregado acumula la SUMA de los deltas por lote (design decisión 12) — un upsert
            // por lote con diferencia, nunca uno solo con el total al final: mismo criterio de
            // "re-lock de una fila que esta transacción ya tiene" que Write site 3 usa en
            // transferencias (decisión 8).
            cantidadAgregadaFinal = await UpsertStockAsync(conexion, transaccionCruda, idTenant, idArticulo, idPuntoVenta, deltaDelLote, ct);

            deltaAgregado += deltaDelLote;
            resultadosPorLote.Add(new LoteContado(lote.IdLote, finalDelLote, actualDelLote, deltaDelLote, MovimientoRegistrado: true));
        }

        await transaccion.CommitAsync(ct);

        return new ResultadoConteo(
            idPuntoVenta, idArticulo, cantidadAgregadaFinal, actualAgregado, deltaAgregado,
            MovimientoRegistrado: deltaAgregado != 0m, resultadosPorLote);
    }

    /// <summary>Upsert no-op — <c>SET cantidad = stock.cantidad</c> — que crea la fila si falta
    /// (con <c>cantidad = 0</c>) Y toma el row lock en el mismo statement, sin escribir ningún
    /// delta todavía (design decisión 5: "the conteo uses the same primitive as a no-op upsert to
    /// create-if-missing and lock in one statement, then derives the delta").
    /// <para>Etapa 12, Slice 4 (design: Reconciliation — "BloquearYCrearSiFaltaStockAsync ya
    /// existe para esto exacto"): <c>internal</c> a propósito, no un duplicado — la fila agregada
    /// del par de reconciliación (design decisión 13, paso 2) toma el MISMO lock no-op que
    /// <c>ContarAsync</c>, reusado directamente desde <c>ServicioDeLotes.ReconciliarAsync</c>.</para></summary>
    internal static async Task<decimal> BloquearYCrearSiFaltaStockAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, int idArticulo, int idPuntoVenta, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "INSERT INTO stock (id_articulo, id_punto_venta, id_tenant, cantidad) " +
            "VALUES ($1, $2, $3, 0) " +
            "ON CONFLICT (id_articulo, id_punto_venta) DO UPDATE " +
            "SET cantidad = stock.cantidad " +
            "RETURNING cantidad";

        AgregarParametro(comando, idArticulo);
        AgregarParametro(comando, idPuntoVenta);
        AgregarParametro(comando, idTenant);

        var resultado = await comando.ExecuteScalarAsync(ct)
            ?? throw new InvalidOperationException("El upsert no-op de stock no devolvió ninguna fila.");

        return Convert.ToDecimal(resultado);
    }

    /// <summary>Etapa 12, slice 12 (design decisión 12 — "the stock_lotes twin is a copy with a
    /// third key"): mismo upsert no-op que <see cref="BloquearYCrearSiFaltaStockAsync"/> un nivel
    /// arriba, sobre <c>stock_lotes</c> — crea la fila del lote si falta (<c>cantidad = 0</c>) Y
    /// toma su row lock en el mismo statement, sin escribir ningún delta todavía.</summary>
    private static async Task<decimal> BloquearYCrearSiFaltaStockLoteAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, int idArticulo, int idPuntoVenta, int idLote,
        CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "INSERT INTO stock_lotes (id_articulo, id_punto_venta, id_lote, id_tenant, cantidad) " +
            "VALUES ($1, $2, $3, $4, 0) " +
            "ON CONFLICT (id_articulo, id_punto_venta, id_lote) DO UPDATE " +
            "SET cantidad = stock_lotes.cantidad " +
            "RETURNING cantidad";

        AgregarParametro(comando, idArticulo);
        AgregarParametro(comando, idPuntoVenta);
        AgregarParametro(comando, idLote);
        AgregarParametro(comando, idTenant);

        var resultado = await comando.ExecuteScalarAsync(ct)
            ?? throw new InvalidOperationException("El upsert no-op de stock_lotes no devolvió ninguna fila.");

        return Convert.ToDecimal(resultado);
    }

    // ---- statements crudos (misma convención que ServicioDeVentas) --------------------------------

    /// <summary><see cref="idComprobanteCompra"/>/<see cref="idPuntoVentaDestino"/> quedan en
    /// <c>null</c> para todo llamador de esta clase (Ajuste/Transferencia/Inventario) salvo
    /// <see cref="idPuntoVentaDestino"/> en <see cref="EjecutarTransferenciaAsync"/> —
    /// <c>id_comprobante_compra</c> nunca se escribe fuera de
    /// <c>ServicioDeCompras.ConfirmarAsync</c>/<c>AnularAsync</c> (Slice 2, doc-comment de
    /// <see cref="Ways.Domain.Stock.MovimientoStock.IdComprobanteCompra"/>); el parámetro se suma
    /// acá solo por simetría de firma con el statement gemelo de <c>ServicioDeCompras</c> (design:
    /// File Changes — "the two raw statements gain motivo/idComprobanteCompra/idPuntoVentaDestino
    /// parameters").</summary>
    /// <summary>Etapa 12, slice 10: gana <paramref name="idLote"/> (design decisión 10 — en una
    /// transferencia, el ledger se escribe en el elemento AGREGADO de <c>ConstruirClavesOrdenadas</c>
    /// y lleva el <c>IdLoteDelMovimiento</c> de esa clave; <c>Ajustar</c>/<c>Contar</c> siguen
    /// pasando <c>null</c>, sin cambio de comportamiento).</summary>
    private static async Task InsertarMovimientoStockAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, int idArticulo, int idPuntoVenta,
        decimal cantidad, MotivoStock motivo, int idEmpleado, string? observaciones, DateTimeOffset creadoEl,
        int? idComprobanteCompra, int? idPuntoVentaDestino, int? idLote, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "INSERT INTO movimientos_stock " +
            "(id_tenant, id_articulo, id_punto_venta, cantidad, motivo, id_empleado, observaciones, " +
            "id_comprobante_compra, id_punto_venta_destino, creado_el, id_lote) " +
            "VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)";

        AgregarParametro(comando, idTenant);
        AgregarParametro(comando, idArticulo);
        AgregarParametro(comando, idPuntoVenta);
        AgregarParametro(comando, cantidad);
        AgregarParametro(comando, motivo);
        AgregarParametro(comando, idEmpleado);
        AgregarParametroNulo(comando, observaciones);
        AgregarParametroNulo(comando, idComprobanteCompra);
        AgregarParametroNulo(comando, idPuntoVentaDestino);
        AgregarParametro(comando, creadoEl);
        AgregarParametroNulo(comando, idLote);

        await comando.ExecuteNonQueryAsync(ct);
    }

    private static async Task<decimal> UpsertStockAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, int idArticulo, int idPuntoVenta,
        decimal delta, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "INSERT INTO stock (id_articulo, id_punto_venta, id_tenant, cantidad) " +
            "VALUES ($1, $2, $3, $4) " +
            "ON CONFLICT (id_articulo, id_punto_venta) DO UPDATE " +
            "SET cantidad = stock.cantidad + EXCLUDED.cantidad " +
            "RETURNING cantidad";

        AgregarParametro(comando, idArticulo);
        AgregarParametro(comando, idPuntoVenta);
        AgregarParametro(comando, idTenant);
        AgregarParametro(comando, delta);

        var resultado = await comando.ExecuteScalarAsync(ct)
            ?? throw new InvalidOperationException("El upsert de stock no devolvió ninguna fila.");

        return Convert.ToDecimal(resultado);
    }

    /// <summary>Etapa 12, slice 10 (design: Write site 3 — "UpsertStockLoteAsync: la MISMA forma
    /// que UpsertStockAsync, una clave más"): mismo shape que
    /// <c>Ways.Application.Ventas.ServicioDeVentas</c>/<c>Ways.Application.Compras.ServicioDeCompras</c>
    /// (copia deliberada, no compartida — mismo criterio de "frentes en paralelo" que el resto de
    /// la etapa). La <c>RETURNING</c> es el chequeo de suficiencia POR LOTE (spec transferencias-de-stock:
    /// "Insufficient Origin Stock Is Refused" extendido al lote) — sin segunda consulta, sin TOCTOU.</summary>
    private static async Task<decimal> UpsertStockLoteAsync(
        DbConnection conexion, DbTransaction? transaccion, int idTenant, int idArticulo, int idPuntoVenta,
        int idLote, decimal delta, CancellationToken ct)
    {
        await using var comando = conexion.CreateCommand();
        comando.Transaction = transaccion;
        comando.CommandText =
            "INSERT INTO stock_lotes (id_articulo, id_punto_venta, id_lote, id_tenant, cantidad) " +
            "VALUES ($1, $2, $3, $4, $5) " +
            "ON CONFLICT (id_articulo, id_punto_venta, id_lote) DO UPDATE " +
            "SET cantidad = stock_lotes.cantidad + EXCLUDED.cantidad " +
            "RETURNING cantidad";

        AgregarParametro(comando, idArticulo);
        AgregarParametro(comando, idPuntoVenta);
        AgregarParametro(comando, idLote);
        AgregarParametro(comando, idTenant);
        AgregarParametro(comando, delta);

        var resultado = await comando.ExecuteScalarAsync(ct)
            ?? throw new InvalidOperationException("El upsert de stock_lotes no devolvió ninguna fila.");

        return Convert.ToDecimal(resultado);
    }

    private async Task<DbConnection> ObtenerConexionAbiertaAsync(CancellationToken ct)
    {
        var conexion = db.Database.GetDbConnection();

        if (conexion.State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(ct);
        }

        return conexion;
    }

    private static void AgregarParametro(DbCommand comando, object valor)
    {
        var parametro = comando.CreateParameter();
        parametro.Value = valor;
        comando.Parameters.Add(parametro);
    }

    private static void AgregarParametroNulo(DbCommand comando, object? valor)
    {
        var parametro = comando.CreateParameter();
        parametro.Value = valor ?? DBNull.Value;
        comando.Parameters.Add(parametro);
    }

    // ---- validaciones -------------------------------------------------------------------------

    private async Task<Articulo> ResolverArticuloAsync(int idArticulo, CancellationToken ct) =>
        await db.Articulos.FirstOrDefaultAsync(a => a.Id == idArticulo, ct)
            // Mismo código que ServicioDeOfertas/ServicioDeVentas (referencia_invalida, 400): el
            // filtro de EF (+ RLS) ya deja invisible un artículo de otro tenant, así que "no
            // existe" y "es de otro tenant" caen en la misma rama.
            ?? throw new ErrorDominio("referencia_invalida", $"No existe el artículo {idArticulo}.", 400);

    private async Task<PuntoVenta> ResolverPuntoVentaAsync(int idPuntoVenta, CancellationToken ct) =>
        await db.PuntosVenta.FirstOrDefaultAsync(pv => pv.Id == idPuntoVenta, ct)
            // ADR-8: mismo 404 para "no existe" y "es de otro tenant" — mismo criterio que
            // ServicioDeVentas.ResolverPuntoVentaAsync.
            ?? throw ErrorDominio.NoEncontrado($"No existe el punto de venta {idPuntoVenta}.");

    private static decimal ExigirCantidadValida(decimal cantidad)
    {
        if (cantidad == 0)
        {
            throw new ErrorDominio("cantidad_de_ajuste_invalida", "La cantidad del ajuste no puede ser cero.", 400);
        }

        // Máximo 3 decimales (mismo código y criterio que ServicioDeVentas.ExigirLineasValidas —
        // doc 10: cantidad soporta fracción para UnidadVenta.Peso, pero sin precisión ilimitada).
        if (decimal.Round(cantidad, 3, MidpointRounding.AwayFromZero) != cantidad)
        {
            throw new ErrorDominio("cantidad_invalida", "La cantidad del ajuste admite hasta 3 decimales.", 400);
        }

        return cantidad;
    }

    /// <summary>Etapa 12, slice 11 (design: Write site 3 — "cantidad arrives positive and is
    /// negated server-side"; spec lotes-y-vencimientos: "the client MUST send a positive
    /// cantidad"). A diferencia de <see cref="ExigirCantidadValida"/> (que solo prohíbe cero,
    /// porque un ajuste carga o descarga con signo), un decomiso SIEMPRE resta — cero o negativo
    /// del cliente es un error de contrato, no una operación legítima. Reusa el mismo código de
    /// familia que <see cref="ExigirCantidadValida"/> (decomiso es estructuralmente un ajuste,
    /// design decisión 9 del proposal), sin un código nuevo en la lista de la etapa.</summary>
    private static decimal ExigirCantidadDeDecomisoValida(decimal cantidad)
    {
        if (cantidad <= 0)
        {
            throw new ErrorDominio("cantidad_de_ajuste_invalida", "La cantidad del decomiso tiene que ser mayor a cero.", 400);
        }

        if (decimal.Round(cantidad, 3, MidpointRounding.AwayFromZero) != cantidad)
        {
            throw new ErrorDominio("cantidad_invalida", "La cantidad del decomiso admite hasta 3 decimales.", 400);
        }

        return cantidad;
    }

    private static string ExigirObservaciones(string? observaciones)
    {
        var limpio = observaciones?.Trim();

        if (string.IsNullOrEmpty(limpio))
        {
            throw new ErrorDominio(
                "observaciones_requeridas", "El ajuste manual de stock requiere una observación/motivo.", 400);
        }

        return limpio;
    }

    /// <summary>Validación puramente en memoria, antes de resolver referencias o tocar la base.
    /// Etapa 12, slice 10: el rechazo de artículo repetido (<c>articulo_repetido</c>) se MUDÓ
    /// de acá a <see cref="ResolverLineasDeTransferenciaAsync"/> — la clave se ensanchó a
    /// <c>(IdArticulo, IdLote)</c> y decisión 11 exige evaluarla DESPUÉS del defaulting de FEFO,
    /// que solo corre una vez resueltos el punto de venta y los artículos.</summary>
    private static IReadOnlyList<LineaDeTransferencia> ExigirLineasDeTransferenciaValidas(
        IReadOnlyList<LineaDeTransferencia> lineas)
    {
        if (lineas is null || lineas.Count == 0)
        {
            throw new ErrorDominio(
                "transferencia_sin_lineas", "La transferencia no tiene líneas para procesar.", 400);
        }

        foreach (var linea in lineas)
        {
            if (linea.Cantidad <= 0)
            {
                throw new ErrorDominio(
                    "cantidad_de_transferencia_invalida", "La cantidad a transferir tiene que ser mayor a cero.", 400);
            }

            if (decimal.Round(linea.Cantidad, 3, MidpointRounding.AwayFromZero) != linea.Cantidad)
            {
                throw new ErrorDominio(
                    "cantidad_de_transferencia_invalida", "La cantidad a transferir admite hasta 3 decimales.", 400);
            }
        }

        return lineas;
    }

    /// <summary>Design: New Domain codes — <c>contada_invalida</c>. <see cref="SolicitudDeConteo.Contada"/>
    /// es el total físicamente contado: nunca negativo, hasta 3 decimales (mismo listón que
    /// <see cref="ExigirCantidadValida"/>, sin el chequeo de "distinto de cero" — un conteo que
    /// confirma el cero actual es un no-op legítimo, spec: Zero-Difference Conteo).</summary>
    private static decimal ExigirContadaValida(decimal contada)
    {
        if (contada < 0)
        {
            throw new ErrorDominio("contada_invalida", "La cantidad contada no puede ser negativa.", 400);
        }

        if (decimal.Round(contada, 3, MidpointRounding.AwayFromZero) != contada)
        {
            throw new ErrorDominio("contada_invalida", "La cantidad contada admite hasta 3 decimales.", 400);
        }

        return contada;
    }

    /// <summary>Etapa 12, slice 12 (design decisión 18 — exactly-one-of; dto-contract-honesty):
    /// validación puramente en memoria, ANTES de resolver referencias — un request mal formado no
    /// amerita ni un SELECT. Un <see cref="SolicitudDeConteo.Lotes"/> vacío (<c>[]</c>) cuenta como
    /// "ausente", mismo criterio que <c>null</c>: ninguna de las dos formas del conteo trajo un
    /// valor accionable.</summary>
    private static void ExigirExactamenteUnaFormaDeConteo(decimal? contada, IReadOnlyList<ConteoDeLote>? lotes)
    {
        var tieneContada = contada is not null;
        var tieneLotes = lotes is { Count: > 0 };

        if (tieneContada == tieneLotes)
        {
            throw new ErrorDominio(
                "conteo_contada_y_lotes",
                "El conteo tiene que traer exactamente uno de cantidad contada o desglose por lote, nunca ambos ni ninguno.",
                400);
        }
    }

    /// <summary>Etapa 12, slice 12 (design decisión 12): <c>conteo_lote_repetido</c> se rechaza
    /// ANTES de cualquier lock — mismo criterio "en memoria primero" que <see cref="ExigirLineasDeTransferenciaValidas"/>.
    /// Reusa <see cref="ExigirContadaValida"/> por línea: el total contado de un lote es la MISMA
    /// magnitud física que el total agregado un nivel arriba, misma disciplina de signo/precisión.</summary>
    private static IReadOnlyList<ConteoDeLote> ExigirLotesDeConteoValidos(IReadOnlyList<ConteoDeLote> lotes)
    {
        foreach (var lote in lotes)
        {
            ExigirContadaValida(lote.Contada);
        }

        var repetido = lotes.GroupBy(l => l.IdLote).FirstOrDefault(g => g.Count() > 1);
        if (repetido is not null)
        {
            throw new ErrorDominio(
                "conteo_lote_repetido",
                $"El lote {repetido.Key} aparece más de una vez en el desglose del conteo.",
                400);
        }

        return lotes;
    }

    private int ExigirTenantDeLaSesion() =>
        contexto.IdTenant
            // GestionDeCatalogo (capa de API) ya exige un actor de tenant admin — un actor de
            // plataforma nunca llega hasta acá. Defensa en profundidad, no un camino alcanzable
            // en operación normal.
            ?? throw new InvalidOperationException(
                "ServicioDeStock requiere un actor de tenant; GestionDeCatalogo no admite plataforma.");
}
