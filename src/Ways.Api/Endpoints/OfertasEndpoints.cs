using Ways.Api.Seguridad;
using Ways.Application.Ofertas;

namespace Ways.Api.Endpoints;

public static class OfertasEndpoints
{
    public static IEndpointRouteBuilder MapearOfertas(this IEndpointRouteBuilder app)
    {
        var grupo = app.MapGroup("/api/ofertas")
            .WithTags("Ofertas")
            .RequireAuthorization(Politicas.GestionDeCatalogo);

        grupo.MapGet("/", (ServicioDeOfertas servicio, bool? incluirEliminados, CancellationToken ct) =>
            servicio.ListarAsync(incluirEliminados ?? false, ct))
        .WithSummary("Lista las ofertas del tenant.");

        grupo.MapGet("/{id:int}", (ServicioDeOfertas servicio, int id, CancellationToken ct) =>
            servicio.ObtenerAsync(id, ct))
        .WithSummary("Obtiene una oferta, incluido su targeting actual de listas.");

        grupo.MapPost("/", async (ServicioDeOfertas servicio, AltaOferta datos, CancellationToken ct) =>
        {
            var creada = await servicio.CrearAsync(datos, ct);
            return Results.Created($"/api/ofertas/{creada.Id}", creada);
        })
        .WithSummary("Crea una oferta, incluidas las listas a las que aplica.");

        grupo.MapPut("/{id:int}", (ServicioDeOfertas servicio, int id, EdicionOferta datos, CancellationToken ct) =>
            servicio.ActualizarAsync(id, datos, ct))
        .WithSummary("Actualiza una oferta; reemplaza por completo el subconjunto de listas.");

        grupo.MapDelete("/{id:int}", async (ServicioDeOfertas servicio, int id, CancellationToken ct) =>
        {
            await servicio.EliminarAsync(id, ct);
            return Results.NoContent();
        })
        .WithSummary("Baja lógica de la oferta.");

        return app;
    }
}
