using Ways.Api.Seguridad;
using Ways.Application.Articulos;

namespace Ways.Api.Endpoints;

public static class ArticulosEndpoints
{
    public static IEndpointRouteBuilder MapearArticulos(this IEndpointRouteBuilder app)
    {
        var grupo = app.MapGroup("/api/articulos")
            .WithTags("Articulos")
            .RequireAuthorization(Politicas.GestionDeCatalogo);

        grupo.MapGet("/", (
            ServicioDeArticulos servicio,
            string? busqueda,
            int? idEmpresa,
            bool? incluirEliminados,
            int? pagina,
            int? tamanio,
            CancellationToken ct) =>
            servicio.ListarAsync(busqueda, idEmpresa, incluirEliminados ?? false, pagina ?? 1, tamanio ?? 25, ct))
        .WithSummary("Lista artículos con búsqueda, filtro de disponibilidad por empresa y paginado.");

        grupo.MapGet("/{id:int}", (ServicioDeArticulos servicio, int id, CancellationToken ct) =>
            servicio.ObtenerAsync(id, ct))
        .WithSummary("Obtiene un artículo.");

        grupo.MapPost("/", async (
            ServicioDeArticulos servicio, AltaArticulo datos, CancellationToken ct) =>
        {
            var creado = await servicio.CrearAsync(datos, ct);
            return Results.Created($"/api/articulos/{creado.Id}", creado);
        })
        .WithSummary("Crea un artículo. El código interno se autogenera si se omite.");

        grupo.MapPut("/{id:int}", (
            ServicioDeArticulos servicio, int id, EdicionArticulo datos, CancellationToken ct) =>
            servicio.ActualizarAsync(id, datos, ct))
        .WithSummary("Actualiza un artículo, incluida su disponibilidad por empresa.");

        grupo.MapDelete("/{id:int}", async (
            ServicioDeArticulos servicio, int id, CancellationToken ct) =>
        {
            await servicio.EliminarAsync(id, ct);
            return Results.NoContent();
        })
        .WithSummary("Baja lógica del artículo.");

        grupo.MapPost("/{id:int}/codigos-barra", async (
            ServicioDeArticulos servicio, int id, AltaCodigoBarra datos, CancellationToken ct) =>
        {
            var creado = await servicio.AgregarCodigoBarraAsync(id, datos, ct);
            return Results.Created($"/api/articulos/{id}/codigos-barra/{creado.Id}", creado);
        })
        .WithSummary("Agrega un código de barras al artículo.");

        grupo.MapDelete("/{id:int}/codigos-barra/{idCodigoBarra:int}", async (
            ServicioDeArticulos servicio, int id, int idCodigoBarra, CancellationToken ct) =>
        {
            await servicio.EliminarCodigoBarraAsync(id, idCodigoBarra, ct);
            return Results.NoContent();
        })
        .WithSummary("Quita un código de barras del artículo.");

        grupo.MapGet("/{id:int}/sugerencia-precio", (
            ServicioDeArticulos servicio, int id, CancellationToken ct) =>
            servicio.SugerirPrecioAsync(id, ct))
        .WithSummary("Sugiere un precio a partir del costo y margen del artículo (nunca se aplica solo).");

        return app;
    }
}
