using Ways.Api.Seguridad;
using Ways.Application.Plantel;

namespace Ways.Api.Endpoints;

public static class CargaSemanalDeHorasEndpoints
{
    public static IEndpointRouteBuilder MapearCargaSemanalDeHoras(this IEndpointRouteBuilder app)
    {
        // Cada ruta lleva su política: agruparlas apilaría las dos con AND.
        app.MapGet("/api/personal/horas/semana", async (
            ServicioDeCargaSemanalDeHoras servicio, DateOnly? desde, CancellationToken ct) =>
            Results.Ok(await servicio.ObtenerSemanaAsync(desde, ct)))
        .WithTags("CargaSemanalDeHoras")
        .RequireAuthorization(Politicas.LecturaDePersonal)
        .WithSummary("Grilla semanal (lunes a domingo) de horas por personal, con la precarga del horario habitual.");

        app.MapPost("/api/personal/horas/semana", async (
            ServicioDeCargaSemanalDeHoras servicio, SolicitudDeCargaSemanal solicitud, CancellationToken ct) =>
            Results.Ok(await servicio.GuardarSemanaAsync(solicitud, ct)))
        .WithTags("CargaSemanalDeHoras")
        .RequireAuthorization(Politicas.GestionDeCuentaDePersonal)
        .WithSummary("Guarda la semana: escribe en el libro solo la diferencia de cada día y lo confirma.");

        return app;
    }
}
