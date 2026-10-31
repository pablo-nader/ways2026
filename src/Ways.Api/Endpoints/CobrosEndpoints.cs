using Ways.Api.Seguridad;
using Ways.Application.CuentaCorriente;

namespace Ways.Api.Endpoints;

public static class CobrosEndpoints
{
    /// <summary>
    /// E7-S3: aplicar el cobro de un recibo (pago a cuenta) a ventas en cuenta corriente, y liberarlo. Mismo criterio que la
    /// reliquidación y el ajuste: <see cref="Politicas.OperacionDePos"/> en el grupo y
    /// <see cref="Politicas.SupervisionDeCuentaCorriente"/> apilado, con efecto neto Supervisor y Admin. No apilan
    /// <c>GestionDeCatalogo</c>: van declaradas en el allowlist de <c>SuperficieDeAutorizacionTests</c>.
    /// </summary>
    public static IEndpointRouteBuilder MapearCobros(this IEndpointRouteBuilder app)
    {
        var grupo = app.MapGroup("/api/cobros")
            .WithTags("Cobros")
            .RequireAuthorization(Politicas.OperacionDePos);

        grupo.MapPost("/{idRecibo:int}/aplicaciones", async (
            ServicioDeCobros servicio, int idRecibo, SolicitudDeAplicacionesDeCobro solicitud, CancellationToken ct) =>
            Results.Ok(await servicio.AplicarAsync(idRecibo, solicitud.Aplicaciones, ct)))
        .RequireAuthorization(Politicas.SupervisionDeCuentaCorriente)
        .WithSummary(
            "Aplica lo disponible de un recibo a ventas del mismo cliente. Una sola aplicación viva por par recibo-venta: " +
            "aplicar de nuevo la agranda. Responde lo cobrado, aplicado y disponible, y las aplicaciones vivas.");

        grupo.MapDelete("/{idRecibo:int}/aplicaciones/{idAplicacion:int}", async (
            ServicioDeCobros servicio, int idRecibo, int idAplicacion, CancellationToken ct) =>
            Results.Ok(await servicio.DesaplicarAsync(idRecibo, idAplicacion, ct)))
        .RequireAuthorization(Politicas.SupervisionDeCuentaCorriente)
        .WithSummary("Libera una aplicación del recibo: lo aplicado vuelve a estar disponible. Responde el estado del recibo.");

        return app;
    }
}
