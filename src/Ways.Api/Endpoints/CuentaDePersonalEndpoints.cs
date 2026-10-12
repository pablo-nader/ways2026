using Ways.Api.Seguridad;
using Ways.Application.Plantel;

namespace Ways.Api.Endpoints;

public static class CuentaDePersonalEndpoints
{
    public static IEndpointRouteBuilder MapearCuentaDePersonal(this IEndpointRouteBuilder app)
    {
        // Cada ruta lleva su política: agruparlas apilaría las dos con AND.
        app.MapGet("/api/personal/{id:int}/cuenta", async (
            ServicioDeCuentaDePersonal servicio, int id, DateOnly? desde, DateOnly? hasta, bool? historico,
            int? pagina, int? tamanio, CancellationToken ct) =>
            Results.Ok(await servicio.ObtenerCuentaAsync(
                id, desde, hasta, historico ?? false, pagina ?? 1, tamanio ?? 25, ct)))
        .WithTags("CuentaDePersonal")
        .RequireAuthorization(Politicas.LecturaDePersonal)
        .WithSummary("Estado de cuenta del personal: saldo y movimientos paginados con el saldo resultante de cada uno.");

        app.MapPost("/api/personal/{id:int}/horas", async (
            ServicioDeCuentaDePersonal servicio, int id, SolicitudDeCargaDeHoras solicitud, CancellationToken ct) =>
        {
            var movimiento = await servicio.CargarHorasAsync(id, solicitud, ct);
            return Results.Created($"/api/personal/{id}/cuenta", movimiento);
        })
        .WithTags("CuentaDePersonal")
        .RequireAuthorization(Politicas.GestionDeCuentaDePersonal)
        .WithSummary("Carga horas trabajadas en un día; el valor hora se congela en el movimiento.");

        app.MapPost("/api/personal/{id:int}/ajustes", async (
            ServicioDeCuentaDePersonal servicio, int id, SolicitudDeAjusteDePersonal solicitud, CancellationToken ct) =>
        {
            var movimiento = await servicio.RegistrarAjusteAsync(id, solicitud, ct);
            return Results.Created($"/api/personal/{id}/cuenta", movimiento);
        })
        .WithTags("CuentaDePersonal")
        .RequireAuthorization(Politicas.GestionDeCuentaDePersonal)
        .WithSummary("Ajuste manual de la cuenta del personal: importe con signo y detalle obligatorio.");

        return app;
    }
}
