using Ways.Api.Seguridad;
using Ways.Application.Pos;

namespace Ways.Api.Endpoints;

public static class PosEndpoints
{
    public static IEndpointRouteBuilder MapearPos(this IEndpointRouteBuilder app)
    {
        var grupo = app.MapGroup("/api/pos")
            .WithTags("Pos")
            .RequireAuthorization(Politicas.OperacionDePos);

        // stage-pos-venta-offline-backend (Parte A): RequiereDispositivo apilado sobre
        // OperacionDePos (AND) — mismo criterio exacto que POST /api/ventas/reservas-numeracion.
        // Sigue haciendo falta un rol de venta; la claim de dispositivo es una condición
        // ADICIONAL, nunca un reemplazo.
        grupo.MapGet("/instantanea", async (ServicioDeInstantaneaDePos servicio, CancellationToken ct) =>
            Results.Ok(await servicio.ObtenerAsync(ct)))
        .RequireAuthorization(Politicas.RequiereDispositivo)
        .WithSummary(
            "Instantánea completa (sin paginar) de lo que el POS de escritorio necesita para " +
            "vender sin red: catálogo con precio resuelto y congelado, medios de pago, " +
            "tolerancia de pago. Solo el propio punto de venta del dispositivo que la pide.");

        // Rendición de la cola local: RequiereDispositivo apilado sobre OperacionDePos (AND) —
        // mismo criterio exacto que /instantanea de arriba. El punto de venta y el dispositivo
        // salen de la claim, nunca del cuerpo.
        grupo.MapPost("/rendicion-de-cola", async (
            ServicioDeRendicionDeCola servicio, SolicitudDeRendicionDeCola solicitud, CancellationToken ct) =>
        {
            await servicio.RegistrarAsync(solicitud, ct);
            return Results.NoContent();
        })
        .RequireAuthorization(Politicas.RequiereDispositivo)
        .WithSummary(
            "El dispositivo declara el estado de su cola local (hasta qué número repartió y " +
            "cuántas ventas no llegaron) para que el cierre de turno pueda verificarlo.");

        return app;
    }
}
