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
/// anotación de un fallo del intento y la lectura final del estado (<see cref="PresupuestoDeCierre"/> cada una), la anotación de
/// cada fallo del barrido (1 s de reloj real por venta, sin recortar al tope) y una firma de WSAA que arranque dentro del intento,
/// que no se interrumpe una vez en vuelo (ver el servicio de emisión).
///
/// La respuesta de la venta informa del error anotado solo su código (<see cref="CodigoDeError"/>): el texto anotado puede traer
/// mensajes de excepciones y de ARCA que no son para el cajero.
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

        return estado with { Error = CodigoDeError(estado.Error) };
    }

    /// <summary>Después de anular una venta con la factura aprobada: intenta UNA vez emitir su nota de crédito antes de responder,
    /// con el mismo criterio que el intento de la factura. Solo corre si la venta tiene la decisión de una nota de crédito y la empresa
    /// tiene un ticket de acceso ya cacheado (no firma ante WSAA); procesar una decisión que ya no está pendiente no llama a ARCA; el presupuesto es <see cref="PresupuestoDeIntento"/>. Nunca
    /// lanza ni recibe el token de la solicitud: la anulación ya está comiteada. Lo que no se emita ahora lo emite el procesador
    /// por lote. El estado resultante se lee aparte (<see cref="ServicioDeFacturacionDeVentas.LeerEstadoDeVentaAsync"/>).</summary>
    public async Task IntentarNotaDeCreditoAsync(int idVenta)
    {
        using var presupuesto = new CancellationTokenSource(PresupuestoDeIntento, tiempo);
        var ct = presupuesto.Token;
        int? idFacturacion = null;
        try
        {
            var nota = await db.FacturacionesDeVenta.AsNoTracking()
                .Where(f => f.IdComprobanteVenta == idVenta && f.IdFacturacionOrigen != null)
                .Select(f => new { f.Id, f.IdEmpresa })
                .FirstOrDefaultAsync(ct);
            if (nota is null)
            {
                return;
            }

            idFacturacion = nota.Id;

            // Sin ticket cacheado no se intenta: firmar ante WSAA no se interrumpe y puede tardar bastante más que el tope.
            if (!await fiscal.HayTicketVigenteAsync(nota.IdEmpresa, ct))
            {
                return;
            }

            _ = await facturacion.ProcesarDecisionAsync(nota.Id, ct);
        }
        catch (Exception ex) when (ct.IsCancellationRequested && ex is not ErrorDominio)
        {
            log.LogDebug(
                "El presupuesto del intento en línea cortó la nota de crédito de la venta {IdVenta}; no se anota como fallo.", idVenta);
        }
        catch (Exception ex)
        {
            var codigo = ex is ErrorDominio dominio ? dominio.Codigo : ErrorInesperado;
            log.LogWarning(
                ex, "No se pudo emitir en línea la nota de crédito de la venta {IdVenta} ({Codigo}); queda pendiente.", idVenta, codigo);
            if (idFacturacion is { } id)
            {
                using var cierre = new CancellationTokenSource(PresupuestoDeCierre, tiempo);
                await facturacion.RegistrarFalloInesperadoAsync(id, codigo, cierre.Token);
            }
        }
    }

    /// <summary>Lo único del error anotado que la respuesta de la venta informa: el código inicial (minúsculas, dígitos y
    /// guion bajo, hasta el primer <c>:</c> o todo el texto si es solo eso). Un rechazo de ARCA anotado como
    /// <c>10015: mensaje; 10016: mensaje</c> queda con su primer código (<c>10015</c>). Todo lo demás, por ejemplo
    /// <c>HttpRequestException: texto</c>, se informa como <see cref="ErrorInesperado"/>; sin error, <c>null</c>.</summary>
    public static string? CodigoDeError(string? anotado)
    {
        if (string.IsNullOrWhiteSpace(anotado))
        {
            return null;
        }

        var separador = anotado.IndexOf(':', StringComparison.Ordinal);
        var codigo = (separador < 0 ? anotado : anotado[..separador]).Trim();
        return codigo.Length > 0 && codigo.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_')
            ? codigo
            : ErrorInesperado;
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
