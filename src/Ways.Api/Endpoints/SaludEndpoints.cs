using Microsoft.EntityFrameworkCore;
using Ways.Infrastructure.Persistencia;

namespace Ways.Api.Endpoints;

public static class SaludEndpoints
{
    public static IEndpointRouteBuilder MapearSalud(this IEndpointRouteBuilder app)
    {
        // Sin autenticar a propósito: lo usa el HEALTHCHECK del contenedor.
        // No expone nada más que si la base responde.
        app.MapGet("/api/salud", async (WaysDbContext db, CancellationToken ct) =>
        {
            var baseViva = await db.Database.CanConnectAsync(ct);

            return baseViva
                ? Results.Ok(new { estado = "ok", baseDeDatos = "ok" })
                : Results.Json(
                    new { estado = "degradado", baseDeDatos = "sin conexión" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
        })
        .AllowAnonymous()
        .WithTags("Salud")
        .WithSummary("Estado del servicio y de la base.");

        return app;
    }
}
