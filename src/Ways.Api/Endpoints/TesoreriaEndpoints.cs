using Ways.Api.Seguridad;
using Ways.Application.Caja;

namespace Ways.Api.Endpoints;

public static class TesoreriaEndpoints
{
    public static IEndpointRouteBuilder MapearTesoreria(this IEndpointRouteBuilder app)
    {
        // Solo Admin, igual que el ajuste de una cuenta digital: el fondo de efectivo de tesorería es un dato del dueño.
        var grupo = app.MapGroup("/api/tesoreria")
            .WithTags("Tesorería")
            .RequireAuthorization(Politicas.GestionDeCatalogo);

        grupo.MapPost("/efectivo/ajustes", async (
            ServicioDeAjusteDeEfectivoDeTesoreria servicio, SolicitudDeAjusteDeEfectivo solicitud, CancellationToken ct) =>
        {
            var resultado = await servicio.AjustarAsync(solicitud, ct);
            return Results.Created($"/api/reportes/libro-tesoreria?idEmpresa={solicitud.IdEmpresa}", resultado);
        })
        .WithSummary(
            "Ajuste manual del efectivo de tesorería de una empresa: concepto obligatorio e importe con signo distinto de cero.");

        return app;
    }
}
