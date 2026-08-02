using Ways.Api.Seguridad;
using Ways.Application.Organizacion;

namespace Ways.Api.Endpoints;

public static class AprovisionamientoEndpoints
{
    public static IEndpointRouteBuilder MapearAprovisionamiento(this IEndpointRouteBuilder app)
    {
        var grupo = app.MapGroup("/api/plataforma/tenants")
            .WithTags("Aprovisionamiento")
            .RequireAuthorization(Politicas.SoloPlataforma);

        grupo.MapPost("/", async (
            ServicioDeAprovisionamiento servicio, SolicitudDeAprovisionamiento datos, CancellationToken ct) =>
        {
            var resultado = await servicio.CrearTenantAsync(datos, ct);
            return Results.Created($"/api/plataforma/tenants/{resultado.IdTenant}", resultado);
        })
        .WithSummary(
            "Aprovisiona un tenant nuevo: tenant + empresa + punto de venta + plantilla + admin (ADR-16).");

        return app;
    }
}
