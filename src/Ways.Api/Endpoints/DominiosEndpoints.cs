using Ways.Api.Seguridad;
using Ways.Application.Dominios;

namespace Ways.Api.Endpoints;

/// <summary>
/// Dominios propios de un tenant, solo plataforma: el dominio decide a qué tenant llega una request
/// anónima, así que cargarlo y verificarlo no puede estar al alcance de un tenant. Mismo grupo de
/// policy que el resto de <c>/api/plataforma/tenants</c>.
/// </summary>
public static class DominiosEndpoints
{
    public static IEndpointRouteBuilder MapearDominios(this IEndpointRouteBuilder app)
    {
        var grupo = app.MapGroup("/api/plataforma/tenants/{id:int}/dominios")
            .WithTags("Dominios")
            .RequireAuthorization(Politicas.SoloPlataforma);

        grupo.MapGet("/", (ServicioDeDominios servicio, int id, CancellationToken ct) =>
            servicio.ListarAsync(id, ct))
        .WithSummary("Lista los dominios vivos de un tenant: el principal primero.");

        grupo.MapPost("/", async (
            ServicioDeDominios servicio, int id, SolicitudDeDominio datos, CancellationToken ct) =>
        {
            var creado = await servicio.AgregarAsync(id, datos, ct);
            return Results.Created($"/api/plataforma/tenants/{id}/dominios/{creado.Id}", creado);
        })
        .WithSummary(
            "Carga un dominio sin verificar. Con esPrincipal, la marca de principal se mueve desde el dominio que la tenía.");

        grupo.MapPost("/{idDominio:int}/verificar", (
            ServicioDeDominios servicio, int id, int idDominio, CancellationToken ct) =>
            servicio.VerificarAsync(id, idDominio, ct))
        .WithSummary("Marca el dominio como verificado: recién entonces resuelve. Idempotente.");

        grupo.MapDelete("/{idDominio:int}", async (
            ServicioDeDominios servicio, int id, int idDominio, CancellationToken ct) =>
        {
            await servicio.EliminarAsync(id, idDominio, ct);
            return Results.NoContent();
        })
        .WithSummary("Baja lógica del dominio: deja de resolver y su host queda libre.");

        return app;
    }
}
