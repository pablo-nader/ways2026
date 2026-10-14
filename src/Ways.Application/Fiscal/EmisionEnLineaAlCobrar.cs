using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Ways.Application.Abstracciones;
using Ways.Domain.Common;

namespace Ways.Application.Fiscal;

/// <summary>
/// Lo que corre después de que el checkout comiteó la venta: decide si el ticket se factura, intenta UNA vez emitir la factura
/// espejo antes de responder (para que con conexión la caja reciba CAE, número y QR en la respuesta de la propia venta) y por
/// último barre las decisiones debidas del punto de venta. La venta nunca falla ni se frena por esto, y ningún paso recibe el
/// token de la solicitud: la venta ya está comiteada.
///
/// Los tres pasos comparten un tope de <see cref="TopeTotal"/> contado desde que la venta se confirma, con un
/// <see cref="TimeProvider"/> inyectado:
///   a) la decisión (<see cref="ServicioDeDecisionDeFacturacion.DecidirTrasLaVentaAsync"/>), con su presupuesto propio;
///   b) solo si quedó <c>pendiente</c>, un intento de emisión con min(<see cref="PresupuestoDeIntento"/>, lo que queda del
///      tope). Solo corre con un ticket de acceso ya cacheado para el emisor: no se firma en la caja;
///   c) el barrido de las decisiones debidas, con min(<see cref="ServicioDeDecisionDeFacturacion.PresupuestoDeBarrido"/>, lo
///      que queda del tope). Sin tiempo restante no corre, y tampoco si la decisión terminó <c>sin_decidir</c>.
///
/// Un corte del intento por su presupuesto no es un intento fallido: no suma intentos ni posterga la decisión. Si ya había un
/// espejo creado queda pendiente y la próxima pasada por el procesador por lote lo resuelve consultando primero; no se posterga
/// porque en la caja lo que sigue es ese lote y una espera de un minuto solo lo haría saltarse la decisión. Una excepción del
/// intento, o un error de dominio que llega pasado el corte, se anota como intento fallido igual que lo hace el lote.
///
/// Lo que puede pasar del tope: la decisión (su presupuesto y la anotación de su fallo corren con reloj real y no se recortan), la
/// anotación de un fallo del intento y la lectura final del estado (<see cref="PresupuestoDeCierre"/> cada una), y una firma de
/// WSAA que arranque dentro del intento, que no se interrumpe una vez en vuelo (ver el servicio de emisión).
/// </summary>
public class EmisionEnLineaAlCobrar(
    ServicioDeDecisionDeFacturacion decision,
    ServicioDeFacturacionDeVentas facturacion,
    ServicioDeFacturacionFiscal fiscal,
    IWaysDbContext db,
    TimeProvider tiempo,
    ILogger<EmisionEnLineaAlCobrar> log)
{
    /// <summary>Tiempo total que puede ocupar lo que sigue al commit de la venta.</summary>
    public static readonly TimeSpan TopeTotal = TimeSpan.FromSeconds(8);

    /// <summary>Tiempo máximo del intento de emisión, siempre recortado a lo que queda del tope.</summary>
    public static readonly TimeSpan PresupuestoDeIntento = TimeSpan.FromSeconds(4);

    /// <summary>Tiempo para anotar un fallo del intento y para leer el estado final, aunque el tope ya esté agotado.</summary>
    public static readonly TimeSpan PresupuestoDeCierre = TimeSpan.FromSeconds(1);

    /// <summary>Código con el que se informa y se anota un intento que lanzó algo que no es un <see cref="ErrorDominio"/>.</summary>
    public const string ErrorInesperado = ProcesadorDeFacturacionesPorLote.ErrorInesperado;

    /// <summary>Nunca lanza. Devuelve el estado fiscal que la respuesta de la venta informa.</summary>
    public async Task<EstadoFiscalDeVenta> DecidirEmitirYSanearAsync(int idComprobanteVenta, int idPuntoVenta)
    {
        var inicio = tiempo.GetTimestamp();

        var decidido = await decision.DecidirTrasLaVentaAsync(idComprobanteVenta);
        var estado = decidido;
        if (decidido.IdFacturacion is { } idFacturacion)
        {
            estado = decidido.Estado == EstadosFiscalesDeVenta.Pendiente
                ? await IntentarEmitirAsync(decidido, idFacturacion, idPuntoVenta, inicio)
                : await LeerEstadoActualAsync(decidido, idFacturacion);
        }

        if (decidido.Estado != EstadosFiscalesDeVenta.SinDecidir)
        {
            var restante = Restante(inicio);
            if (restante > TimeSpan.Zero)
            {
                using var presupuesto = new CancellationTokenSource(
                    Minimo(ServicioDeDecisionDeFacturacion.PresupuestoDeBarrido, restante), tiempo);
                await decision.SanearDecisionesDebidasAsync(idPuntoVenta, idComprobanteVenta, presupuesto.Token);
            }
        }

        return estado;
    }

    private TimeSpan Restante(long inicio) => TopeTotal - tiempo.GetElapsedTime(inicio);

    private static TimeSpan Minimo(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private async Task<EstadoFiscalDeVenta> IntentarEmitirAsync(
        EstadoFiscalDeVenta decidido, int idFacturacion, int idPuntoVenta, long inicio)
    {
        var restante = Restante(inicio);
        if (restante <= TimeSpan.Zero)
        {
            return await LeerEstadoActualAsync(decidido, idFacturacion);
        }

        using var presupuesto = new CancellationTokenSource(Minimo(PresupuestoDeIntento, restante), tiempo);
        var ct = presupuesto.Token;
        try
        {
            var idEmpresa = await db.PuntosVenta.AsNoTracking()
                .Where(p => p.Id == idPuntoVenta)
                .Select(p => (int?)p.IdEmpresa)
                .FirstOrDefaultAsync(ct);

            // Sin ticket cacheado no se intenta: firmar ante WSAA no se interrumpe y puede tardar bastante más que el tope.
            if (idEmpresa is not { } empresa || !await fiscal.HayTicketVigenteAsync(empresa, ct))
            {
                return await LeerEstadoActualAsync(decidido, idFacturacion);
            }

            return await facturacion.ProcesarDecisionAsync(idFacturacion, ct);
        }
        catch (Exception ex) when (ct.IsCancellationRequested && ex is not ErrorDominio)
        {
            log.LogDebug(
                "El presupuesto del intento en línea cortó la facturación {IdFacturacion}; no se anota como fallo.", idFacturacion);
            return decidido;
        }
        catch (Exception ex)
        {
            var codigo = ex is ErrorDominio dominio ? dominio.Codigo : ErrorInesperado;
            log.LogWarning(
                ex, "No se pudo emitir en línea la facturación {IdFacturacion} ({Codigo}); queda pendiente.", idFacturacion, codigo);
            using var cierre = new CancellationTokenSource(PresupuestoDeCierre, tiempo);
            await facturacion.RegistrarFalloInesperadoAsync(idFacturacion, codigo, cierre.Token);
            return decidido with { Error = codigo };
        }
    }

    /// <summary>El estado actual de la decisión, con los datos de la factura si ya está emitida: lo que informa un reenvío de
    /// una venta ya decidida. Ante cualquier falla de la lectura devuelve lo que ya se sabía.</summary>
    private async Task<EstadoFiscalDeVenta> LeerEstadoActualAsync(EstadoFiscalDeVenta conocido, int idFacturacion)
    {
        try
        {
            using var cierre = new CancellationTokenSource(PresupuestoDeCierre, tiempo);
            return await facturacion.LeerEstadoAsync(idFacturacion, cierre.Token);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "No se pudo leer el estado de la facturación {IdFacturacion}.", idFacturacion);
            return conocido;
        }
    }
}
