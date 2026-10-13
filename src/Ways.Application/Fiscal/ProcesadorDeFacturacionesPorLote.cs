using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ways.Application.Abstracciones;
using Ways.Domain.Catalogos;
using Ways.Domain.Common;
using Ways.Domain.Ventas;

namespace Ways.Application.Fiscal;

/// <summary>Cuerpo de <c>POST /api/fiscal/facturaciones/procesar</c>. Los dos campos son opcionales para el binding, pero
/// <see cref="IdPuntoVenta"/> es obligatorio: sin él la solicitud es inválida. <see cref="Maximo"/> ausente vale
/// <see cref="ProcesadorDeFacturacionesPorLote.MaximoPorDefecto"/>.</summary>
public sealed record SolicitudDeLoteDeFacturacion(int? IdPuntoVenta, int? Maximo);

public sealed record ResultadoDeFacturacionDeLote(int IdFacturacion, int IdComprobanteVenta, EstadoFiscalDeVenta Fiscal);

/// <summary>Una serie (punto de venta y tipo fiscal) que el lote no pudo avanzar. <see cref="CodigoAfip"/> es <c>null</c> si
/// el lote no pudo saber de qué serie se trataba.</summary>
public sealed record SerieBloqueadaDeLote(short? CodigoAfip, string Motivo);

public sealed record ResultadoDeLoteDeFacturacion(
    int Emitidas,
    int Rechazadas,
    int Canceladas,
    int PendientesRestantes,
    bool CortadoPorPresupuesto,
    IReadOnlyList<SerieBloqueadaDeLote> SeriesBloqueadas,
    IReadOnlyList<ResultadoDeFacturacionDeLote> Resultados);

/// <summary>
/// Procesa las decisiones de facturación <c>pendiente</c> de UN punto de venta, de a una por
/// <see cref="ServicioDeFacturacionDeVentas.ProcesarDecisionAsync"/>: la misma ruta, los mismos locks y la misma lista blanca de
/// resultados definitivos. Este lote agrega, sobre eso, lo que una decisión suelta no tiene:
///
/// <list type="bullet">
/// <item>Cabeza de serie. Un espejo pendiente detiene su serie (punto de venta y tipo fiscal), así que las decisiones que lo
/// tienen se procesan primero, de la más vieja a la más nueva. Si la espera de esa decisión todavía no venció, ninguna candidata
/// de su serie se intenta en esta llamada: ni red ni penalidad. La serie de una candidata se deduce con lo mismo que usa el
/// servicio para elegir el tipo fiscal (condición del emisor × condición del receptor); si no se puede deducir, la candidata se
/// intenta igual y la serie se aprende del resultado. Además, desde que una decisión da <c>serie_ocupada</c> o
/// <c>serie_bloqueada</c> no se intenta otra de esa serie en la misma llamada.</item>
/// <item>La espera registrada (<c>proximo_intento_en</c>) se respeta acá: una candidata en espera no se intenta. El endpoint de
/// una decisión suelta la sigue ignorando.</item>
/// <item>Aislamiento: una decisión que lanza no frena a las demás. Se anota como un intento fallido (espera creciente) con un
/// código estable (el de su <see cref="ErrorDominio"/> o <c>error_inesperado</c>); el detalle va solo al log. Así no queda como la
/// candidata más vieja ni gasta cupo en cada llamada.</item>
/// <item>Presupuesto propio (<see cref="OpcionesFiscales.PresupuestoDeLote"/>, 20 s por defecto) con su propio token, creado desde
/// el <see cref="TimeProvider"/> inyectado, no el de la solicitud. Un corte por presupuesto detiene el lote y no cuenta como fallo
/// de la decisión que se estaba procesando (el servicio solo anota un intento cuando su token NO está cancelado), pero la deja
/// postergada <see cref="ServicioDeFacturacionDeVentas.EsperaPorCorteDeLote"/>, sin tocar sus intentos, para que la próxima llamada
/// llegue a las demás. Limitación: una decisión que ya no está pendiente (solo conserva un espejo pendiente) no se puede
/// postergar.</item>
/// <item>Una falla del propio lote (las lecturas de cabezas y candidatas, la carga de series) se registra y se propaga: el
/// endpoint responde un error y quien llama sabe que no hubo intento. Lo ya comiteado por las decisiones anteriores de la misma
/// llamada queda comiteado.</item>
/// </list>
///
/// Los cabezas de serie no cuentan para <c>maximo</c> (hay a lo sumo uno por serie): un espejo trabado no puede dejar sin cupo
/// al resto del punto de venta. Las lecturas del lote son SQL crudo con la misma sesión y el mismo filtro de tenant que el resto.
/// </summary>
public class ProcesadorDeFacturacionesPorLote(
    IWaysDbContext db,
    IRelojDelSistema reloj,
    IContextoDeUsuario contexto,
    ServicioDeFacturacionDeVentas facturacion,
    IOptions<OpcionesFiscales> opciones,
    TimeProvider tiempo,
    ILogger<ProcesadorDeFacturacionesPorLote> log)
{
    public const int MaximoPorDefecto = 10;

    public const int MaximoTope = 25;

    /// <summary>La decisión que tiene el espejo pendiente de la serie espera todavía: no se intentó ninguna de la serie.</summary>
    public const string CabezaEnEspera = "cabeza_en_espera";

    /// <summary>La decisión que tiene el espejo pendiente de la serie se intentó y el espejo sigue pendiente.</summary>
    public const string CabezaPendiente = "cabeza_pendiente";

    public const string SerieOcupada = ServicioDeFacturacionDeVentas.SerieOcupada;

    public const string SerieBloqueada = ServicioDeFacturacionDeVentas.SerieBloqueada;

    /// <summary>Código con el que se anota y se informa una decisión que lanzó algo que no es un <see cref="ErrorDominio"/>.</summary>
    public const string ErrorInesperado = "error_inesperado";

    private const int TamanioDePagina = 100;

    private const short SerieDesconocida = 0;

    /// <summary>Tiempo para leer lo que queda pendiente al terminar, aunque el presupuesto del lote ya esté vencido.</summary>
    private static readonly TimeSpan PresupuestoDeCierre = TimeSpan.FromSeconds(5);

    public async Task<ResultadoDeLoteDeFacturacion> ProcesarLoteAsync(
        SolicitudDeLoteDeFacturacion solicitud, CancellationToken ct = default)
    {
        _ = contexto.IdTenant
            ?? throw new InvalidOperationException(
                "ProcesadorDeFacturacionesPorLote requiere un actor de tenant; OperacionDePos no admite plataforma.");

        if (solicitud.IdPuntoVenta is not { } idPuntoVenta)
        {
            throw new ErrorDominio("solicitud_invalida", "Indicá el punto de venta (idPuntoVenta).", 400);
        }

        // Un punto de venta inexistente o de otro tenant es un 404 antes de validar el resto del cuerpo.
        var idEmpresa = await db.PuntosVenta.AsNoTracking()
            .Where(p => p.Id == idPuntoVenta)
            .Select(p => (int?)p.IdEmpresa)
            .FirstOrDefaultAsync(ct)
            ?? throw ErrorDominio.NoEncontrado($"No existe el punto de venta {idPuntoVenta}.");

        var maximo = solicitud.Maximo ?? MaximoPorDefecto;
        if (maximo < 1 || maximo > MaximoTope)
        {
            throw new ErrorDominio("solicitud_invalida", $"maximo tiene que estar entre 1 y {MaximoTope}.", 400);
        }

        var lote = new Lote();
        using (var presupuesto = new CancellationTokenSource(opciones.Value.PresupuestoDeLote, tiempo))
        {
            try
            {
                await EjecutarAsync(lote, idPuntoVenta, idEmpresa, maximo, presupuesto.Token);
            }
            catch (Exception) when (presupuesto.IsCancellationRequested)
            {
                lote.CortadoPorPresupuesto = true;
            }
            catch (Exception ex)
            {
                // Una falla del propio lote (no de una decisión) no se traga: sin esto "falló" y "no había nada" se ven igual.
                log.LogError(ex, "El lote de facturaciones del punto de venta {IdPuntoVenta} falló.", idPuntoVenta);
                throw;
            }
        }

        if (lote.CortadoPorPresupuesto)
        {
            log.LogDebug(
                "El presupuesto del lote cortó el procesamiento del punto de venta {IdPuntoVenta}; lo que no se resolvió no se anota como fallo.",
                idPuntoVenta);
        }

        using var cierre = new CancellationTokenSource(PresupuestoDeCierre, tiempo);
        var pendientes = await ContarPendientesAsync(idPuntoVenta, cierre.Token);

        return new ResultadoDeLoteDeFacturacion(
            lote.Resultados.Count(r => r.Fiscal.Estado == EstadosFiscalesDeVenta.Aprobado),
            lote.Resultados.Count(r => r.Fiscal.Estado == EstadosFiscalesDeVenta.Rechazado),
            lote.Resultados.Count(r => r.Fiscal.Estado == EstadosFiscalesDeVenta.Cancelado),
            pendientes,
            lote.CortadoPorPresupuesto,
            [.. lote.Bloqueadas.OrderBy(b => b.Key).Select(b => new SerieBloqueadaDeLote(
                b.Key == SerieDesconocida ? null : b.Key, b.Value))],
            lote.Resultados);
    }

    private sealed class Lote
    {
        public List<ResultadoDeFacturacionDeLote> Resultados { get; } = [];

        public Dictionary<short, string> Bloqueadas { get; } = [];

        public bool CortadoPorPresupuesto { get; set; }
    }

    private async Task EjecutarAsync(Lote lote, int idPuntoVenta, int idEmpresa, int maximo, CancellationToken ct)
    {
        var ahora = reloj.Ahora;
        var series = await CargarSeriesAsync(idEmpresa, ct);

        var cabezas = await LeerCabezasAsync(idPuntoVenta, ct);
        foreach (var cabeza in cabezas)
        {
            var serie = cabeza.CodigoAfip ?? SerieDesconocida;
            if (lote.Bloqueadas.ContainsKey(serie))
            {
                continue;
            }

            if (cabeza.Estado == "pendiente" && cabeza.ProximoIntentoEn > ahora)
            {
                lote.Bloqueadas[serie] = CabezaEnEspera;
                continue;
            }

            if (!await ProcesarAsync(lote, cabeza.IdFacturacion, cabeza.IdComprobanteVenta, serie, ct))
            {
                return;
            }
        }

        if (cabezas.Count > 0)
        {
            // Un espejo que sigue pendiente después de intentarlo mantiene bloqueada su serie: no se prueba con las candidatas.
            foreach (var cabeza in await LeerCabezasAsync(idPuntoVenta, ct))
            {
                lote.Bloqueadas.TryAdd(cabeza.CodigoAfip ?? SerieDesconocida, CabezaPendiente);
            }
        }

        // Las candidatas de una serie bloqueada se saltean sin gastar cupo, así que se lee de a páginas hasta juntar `maximo`
        // intentos o quedarse sin filas.
        var procesadas = 0;
        var cursor = 0;
        while (true)
        {
            var pagina = await LeerCandidatasAsync(idPuntoVenta, ahora, cursor, ct);
            if (pagina.Count == 0)
            {
                return;
            }

            foreach (var candidata in pagina)
            {
                cursor = candidata.IdFacturacion;
                var serie = series.De(candidata.CondicionReceptor);
                if (lote.Bloqueadas.ContainsKey(serie))
                {
                    continue;
                }

                procesadas++;
                if (!await ProcesarAsync(lote, candidata.IdFacturacion, candidata.IdComprobanteVenta, serie, ct)
                    || procesadas == maximo)
                {
                    return;
                }
            }
        }
    }

    /// <summary>Procesa UNA decisión aislada. Devuelve <c>false</c> si el presupuesto del lote cortó y hay que parar.</summary>
    private async Task<bool> ProcesarAsync(
        Lote lote, int idFacturacion, int idComprobanteVenta, short serie, CancellationToken ct)
    {
        EstadoFiscalDeVenta estado;
        try
        {
            estado = await facturacion.ProcesarDecisionAsync(idFacturacion, ct);
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            lote.CortadoPorPresupuesto = true;
            using var posterga = new CancellationTokenSource(PresupuestoDeCierre, tiempo);
            await facturacion.PostergarPorCorteDeLoteAsync(idFacturacion, posterga.Token);
            return false;
        }
        catch (Exception ex)
        {
            var codigo = ex is ErrorDominio dominio ? dominio.Codigo : ErrorInesperado;
            log.LogWarning(ex, "No se pudo procesar la facturación {IdFacturacion} dentro del lote ({Codigo}); el lote sigue con la siguiente.", idFacturacion, codigo);
            await facturacion.RegistrarFalloInesperadoAsync(idFacturacion, codigo, ct);
            estado = new EstadoFiscalDeVenta(EstadosFiscalesDeVenta.Pendiente, null, idFacturacion, codigo);
        }

        lote.Resultados.Add(new ResultadoDeFacturacionDeLote(idFacturacion, idComprobanteVenta, estado));

        if (estado.Error is { } error)
        {
            if (error.StartsWith(SerieOcupada + ":", StringComparison.Ordinal))
            {
                lote.Bloqueadas.TryAdd(serie, SerieOcupada);
            }
            else if (error.StartsWith(SerieBloqueada + ":", StringComparison.Ordinal))
            {
                lote.Bloqueadas.TryAdd(serie, SerieBloqueada);
            }
        }

        return true;
    }

    // ---- Lecturas ---------------------------------------------------------------------------------------

    internal sealed class FilaDeCabeza
    {
        public int IdFacturacion { get; set; }

        public int IdComprobanteVenta { get; set; }

        public string Estado { get; set; } = string.Empty;

        public DateTimeOffset? ProximoIntentoEn { get; set; }

        public short? CodigoAfip { get; set; }
    }

    internal sealed class FilaDeCandidata
    {
        public int IdFacturacion { get; set; }

        public int IdComprobanteVenta { get; set; }

        public string? CondicionReceptor { get; set; }
    }

    /// <summary>Las decisiones que tienen un espejo pendiente en el punto de venta (a lo sumo una por serie), las más viejas
    /// primero. La decisión puede no estar pendiente: el servicio igual resuelve el espejo.</summary>
    private async Task<List<FilaDeCabeza>> LeerCabezasAsync(int idPuntoVenta, CancellationToken ct) =>
        await db.Database.SqlQuery<FilaDeCabeza>(
            $"""
            SELECT f.id_facturacion_venta AS "IdFacturacion", f.id_comprobante_venta AS "IdComprobanteVenta",
                   f.estado::text AS "Estado", f.proximo_intento_en AS "ProximoIntentoEn", t.codigo_afip AS "CodigoAfip"
            FROM comprobantes_venta e
            JOIN tipos_comprobante t ON t.id_tipo_comprobante = e.id_tipo_comprobante
            JOIN facturaciones_de_venta f ON f.id_comprobante_venta = e.id_venta_origen
            WHERE e.id_punto_venta = {idPuntoVenta} AND e.id_venta_origen IS NOT NULL AND e.resultado_fiscal = 'pendiente'
            ORDER BY f.id_facturacion_venta
            """).ToListAsync(ct);

    /// <summary>Las decisiones pendientes del punto de venta cuya espera no existe o ya venció, las más viejas primero (por id de
    /// decisión), de a páginas a partir de <paramref name="cursor"/>. Se excluyen las que ya tienen un espejo (cualquier
    /// estado): ésas son cabezas de serie.</summary>
    private async Task<List<FilaDeCandidata>> LeerCandidatasAsync(
        int idPuntoVenta, DateTimeOffset ahora, int cursor, CancellationToken ct) =>
        await db.Database.SqlQuery<FilaDeCandidata>(
            $"""
            SELECT f.id_facturacion_venta AS "IdFacturacion", f.id_comprobante_venta AS "IdComprobanteVenta",
                   cf.codigo AS "CondicionReceptor"
            FROM facturaciones_de_venta f
            JOIN comprobantes_venta v ON v.id_comprobante_venta = f.id_comprobante_venta AND v.id_venta_origen IS NULL
            LEFT JOIN clientes c ON c.id_cliente = v.id_cliente
            LEFT JOIN condiciones_fiscales cf ON cf.id_condicion_fiscal = c.id_condicion_fiscal
            WHERE f.estado = 'pendiente'::estado_facturacion_venta AND v.id_punto_venta = {idPuntoVenta}
              AND (f.proximo_intento_en IS NULL OR f.proximo_intento_en <= {ahora})
              AND f.id_facturacion_venta > {cursor}
              AND NOT EXISTS (SELECT 1 FROM comprobantes_venta e WHERE e.id_venta_origen = f.id_comprobante_venta)
            ORDER BY f.id_facturacion_venta
            LIMIT {TamanioDePagina}
            """).ToListAsync(ct);

    /// <summary>Las decisiones pendientes del punto de venta, con o sin espera y con o sin espejo.</summary>
    private async Task<int> ContarPendientesAsync(int idPuntoVenta, CancellationToken ct) =>
        await db.Database.SqlQuery<int>(
            $"""
            SELECT count(*)::int AS "Value"
            FROM facturaciones_de_venta f
            JOIN comprobantes_venta v ON v.id_comprobante_venta = f.id_comprobante_venta AND v.id_venta_origen IS NULL
            WHERE f.estado = 'pendiente'::estado_facturacion_venta AND v.id_punto_venta = {idPuntoVenta}
            """).SingleAsync(ct);

    // ---- Serie de una candidata ---------------------------------------------------------------------------

    private sealed class SeriesDelPuntoDeVenta(CondicionFiscalCodigo? emisor, IReadOnlyDictionary<char, short> codigoPorLetra)
    {
        /// <summary>El código ARCA de la factura que la candidata emitiría, con la misma regla del servicio (cruce de
        /// condiciones y tipo <c>F{letra}</c>), o <see cref="SerieDesconocida"/> si no se puede saber.</summary>
        public short De(string? condicionReceptor)
        {
            if (emisor is not { } condicionEmisor || condicionReceptor is null)
            {
                return SerieDesconocida;
            }

            try
            {
                var letra = ResolvedorDeLetraComprobante.Resolver(
                    condicionEmisor, ServicioDeFacturacionFiscal.MapearCondicionFiscal(condicionReceptor));
                return codigoPorLetra.GetValueOrDefault(letra, SerieDesconocida);
            }
            catch (ErrorDominio)
            {
                return SerieDesconocida;
            }
        }
    }

    private async Task<SeriesDelPuntoDeVenta> CargarSeriesAsync(int idEmpresa, CancellationToken ct)
    {
        var codigoEmisor = await db.Empresas.AsNoTracking()
            .Where(e => e.Id == idEmpresa && e.IdCondicionFiscal != null)
            .Join(db.CondicionesFiscales, e => e.IdCondicionFiscal, c => c.Id, (e, c) => c.Codigo)
            .FirstOrDefaultAsync(ct);

        CondicionFiscalCodigo? emisor = null;
        if (codigoEmisor is not null)
        {
            try
            {
                emisor = ServicioDeFacturacionFiscal.MapearCondicionFiscal(codigoEmisor);
            }
            catch (ErrorDominio)
            {
            }
        }

        var tipos = await db.TiposComprobante.AsNoTracking()
            .Where(t => t.Clase == ClaseComprobante.Venta && t.EsFiscal && t.Activo && t.Letra != null && t.CodigoAfip != null)
            .Select(t => new { t.Codigo, Letra = t.Letra!.Value, CodigoAfip = t.CodigoAfip!.Value })
            .ToListAsync(ct);

        // La factura de la letra (FA, FB, FC): las notas de débito comparten letra, así que la letra sola no alcanza.
        return new SeriesDelPuntoDeVenta(
            emisor, tipos.Where(t => t.Codigo == $"F{t.Letra}").ToDictionary(t => t.Letra, t => t.CodigoAfip));
    }
}
