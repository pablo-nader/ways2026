using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ways.Application.Abstracciones;
using Ways.Domain.MercadoPago;

namespace Ways.Application.MercadoPago;

/// <summary>Lo que el endpoint del webhook lee de la solicitud. <c>IdDelDato</c> es el <c>data.id</c> de la URL y <c>Tipo</c> el
/// <c>type</c> (de la URL o, si falta, del cuerpo).</summary>
public sealed record AvisoDeMercadoPago(string? EncabezadoFirma, string? IdDeSolicitud, string? IdDelDato, string? Tipo);

public enum ResultadoDelAviso
{
    /// <summary>La firma era válida y el aviso se atendió (o se descartó por no ser nuestro): Mercado Pago no tiene que reintentar.</summary>
    Atendido,

    /// <summary>Falta alguna parte de la firma, no corresponde al secreto, o es de un instante fuera de la ventana: 401.</summary>
    FirmaInvalida,

    /// <summary>El entorno no tiene <see cref="OpcionesDeMercadoPago.SecretoWebhook"/>: 503, Mercado Pago reintenta.</summary>
    NoConfigurado
}

/// <summary>
/// Webhook de Mercado Pago (<c>POST /api/integraciones/mercadopago/webhook</c>, anónimo: la firma es la única credencial). Verifica la
/// firma <c>x-signature</c> con el secreto de la aplicación (<see cref="FirmaDeWebhookMercadoPago"/>) y, si es válida y el aviso es del
/// tópico <c>order</c>, busca el cobro por <c>id_orden_mercadopago</c> en todos los tenants (con el contexto de plataforma, el mismo
/// que usa la renovación de conexiones; el índice único de la columna garantiza a lo sumo una fila) y refresca ese cobro con el
/// contexto de SU tenant en un alcance propio, de modo que RLS sigue aislando todo lo demás. Una orden desconocida, de otro tópico o
/// que ya no está pendiente no cambia nada. Con firma válida, un fallo al refrescar se registra (sin datos sensibles) y se contesta
/// igual: Mercado Pago no tiene que castigar con reintentos una caída pasajera de su propia API y el sondeo del dispositivo sigue
/// siendo la fuente de verdad. El secreto nunca se registra ni forma parte de ningún mensaje.
/// </summary>
public class ServicioDeAvisosDeMercadoPago(
    IServiceScopeFactory alcances,
    [FromKeyedServices(ClavesDeContexto.Plataforma)] IWaysDbContext dbPlataforma,
    IOptions<OpcionesDeMercadoPago> opciones,
    IRelojDelSistema reloj,
    ILogger<ServicioDeAvisosDeMercadoPago> log)
{
    public const string TipoOrden = "order";

    private const int LargoMaximoDeId = 64;

    public async Task<ResultadoDelAviso> ProcesarAsync(AvisoDeMercadoPago aviso, CancellationToken ct = default)
    {
        var secreto = opciones.Value.SecretoWebhook;
        if (string.IsNullOrWhiteSpace(secreto))
        {
            return ResultadoDelAviso.NoConfigurado;
        }

        var firma = FirmaDeWebhookMercadoPago.Verificar(
            aviso.EncabezadoFirma, aviso.IdDeSolicitud, aviso.IdDelDato, secreto, reloj.Ahora);
        if (firma != ResultadoDeFirmaDeWebhook.Valida)
        {
            log.LogWarning("Se rechazó un aviso de Mercado Pago por su firma ({Motivo}).", firma);
            return ResultadoDelAviso.FirmaInvalida;
        }

        var idDeOrden = aviso.IdDelDato!;
        if (aviso.Tipo != TipoOrden || idDeOrden.Length > LargoMaximoDeId)
        {
            return ResultadoDelAviso.Atendido;
        }

        var cobro = await dbPlataforma.CobrosMercadoPago
            .AsNoTracking()
            .Where(c => c.IdOrdenMercadoPago == idDeOrden)
            .Select(c => new { c.Id, c.IdTenant })
            .FirstOrDefaultAsync(ct);
        if (cobro is null)
        {
            return ResultadoDelAviso.Atendido;
        }

        try
        {
            await using var alcance = alcances.CreateAsyncScope();
            using var suplantacion = alcance.ServiceProvider.GetRequiredService<ITenantActual>().Suplantar(cobro.IdTenant);
            await alcance.ServiceProvider.GetRequiredService<ServicioDeCobrosMercadoPago>().ActualizarPorAvisoAsync(cobro.Id, ct);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            log.LogError(
                error, "No se pudo refrescar el cobro {IdCobro} de Mercado Pago tras el aviso de su orden; el sondeo del dispositivo lo resuelve.",
                cobro.Id);
        }

        return ResultadoDelAviso.Atendido;
    }
}
