using Ways.Api.Seguridad;
using Ways.Application.Articulos;
using Ways.Application.Precios;

namespace Ways.Api.Endpoints;

public static class ArticulosEndpoints
{
    public static IEndpointRouteBuilder MapearArticulos(this IEndpointRouteBuilder app)
    {
        var grupo = app.MapGroup("/api/articulos")
            .WithTags("Articulos")
            .RequireAuthorization(Politicas.OperacionDePos);

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
        .RequireAuthorization(Politicas.GestionDeCatalogo)
        .WithSummary("Crea un artículo. El código interno se autogenera si se omite.");

        grupo.MapPut("/{id:int}", (
            ServicioDeArticulos servicio, int id, EdicionArticulo datos, CancellationToken ct) =>
            servicio.ActualizarAsync(id, datos, ct))
        .RequireAuthorization(Politicas.GestionDeCatalogo)
        .WithSummary("Actualiza un artículo, incluida su disponibilidad por empresa.");

        grupo.MapDelete("/{id:int}", async (
            ServicioDeArticulos servicio, int id, CancellationToken ct) =>
        {
            await servicio.EliminarAsync(id, ct);
            return Results.NoContent();
        })
        .RequireAuthorization(Politicas.GestionDeCatalogo)
        .WithSummary("Baja lógica del artículo.");

        grupo.MapPost("/{id:int}/codigos-barra", async (
            ServicioDeArticulos servicio, int id, AltaCodigoBarra datos, CancellationToken ct) =>
        {
            var creado = await servicio.AgregarCodigoBarraAsync(id, datos, ct);
            return Results.Created($"/api/articulos/{id}/codigos-barra/{creado.Id}", creado);
        })
        .RequireAuthorization(Politicas.GestionDeCatalogo)
        .WithSummary("Agrega un código de barras al artículo.");

        grupo.MapDelete("/{id:int}/codigos-barra/{idCodigoBarra:int}", async (
            ServicioDeArticulos servicio, int id, int idCodigoBarra, CancellationToken ct) =>
        {
            await servicio.EliminarCodigoBarraAsync(id, idCodigoBarra, ct);
            return Results.NoContent();
        })
        .RequireAuthorization(Politicas.GestionDeCatalogo)
        .WithSummary("Quita un código de barras del artículo.");

        grupo.MapGet("/{id:int}/codigos-barra", (
            ServicioDeArticulos servicio, int id, CancellationToken ct) =>
            servicio.ListarCodigosBarraAsync(id, ct))
        .WithSummary("Lista los códigos de barra activos del artículo.");

        grupo.MapGet("/{id:int}/sugerencia-precio", (
            ServicioDeArticulos servicio, int id, CancellationToken ct) =>
            servicio.SugerirPrecioAsync(id, ct))
        .WithSummary("Sugiere un precio a partir del costo y margen del artículo (nunca se aplica solo).");

        // Slice 3 (stage-3-articulos-y-precios, task 3.5): precios nidificados bajo
        // /api/articulos/{id}/precios, no un recurso top-level propio (proposal's Affected
        // Areas note) — mismo grupo/policy que el resto de ArticulosEndpoints.

        grupo.MapPost("/{id:int}/precios", async (
            ServicioDePrecios servicio, int id, AltaPrecio datos, CancellationToken ct) =>
        {
            var creado = await servicio.EstablecerPrecioAsync(id, datos, ct);
            return Results.Created($"/api/articulos/{id}/precios/{datos.IdListaPrecio}", creado);
        })
        .RequireAuthorization(Politicas.GestionDeCatalogo)
        .WithSummary("Establece el precio vigente de un artículo en una lista fija, efectivo ahora.");

        grupo.MapPost("/{id:int}/precios/programados", async (
            ServicioDePrecios servicio, int id, ProgramarPrecio datos, CancellationToken ct) =>
        {
            var creado = await servicio.ProgramarPrecioAsync(id, datos, ct);
            return Results.Created($"/api/articulos/{id}/precios/{datos.IdListaPrecio}", creado);
        })
        .RequireAuthorization(Politicas.GestionDeCatalogo)
        .WithSummary("Programa un precio a futuro; reemplaza el pendiente existente solo con confirmarReemplazo.");

        grupo.MapGet("/{id:int}/precios", (
            ServicioDePrecios servicio, int id, DateTimeOffset? fecha, CancellationToken ct) =>
            servicio.PreciosVigentesAsync(id, fecha, ct))
        .WithSummary("Precio vigente del artículo en todas las listas activas, a una fecha dada (default: ahora).");

        grupo.MapGet("/{id:int}/precios/{idListaPrecio:int}", (
            ServicioDePrecios servicio, int id, int idListaPrecio, DateTimeOffset? fecha, CancellationToken ct) =>
            servicio.PrecioVigenteAsync(id, idListaPrecio, fecha, ct))
        .WithSummary("Precio vigente del artículo en una lista puntual, a una fecha dada (default: ahora).");

        grupo.MapGet("/{id:int}/precios/{idListaPrecio:int}/historial", (
            ServicioDePrecios servicio, int id, int idListaPrecio, CancellationToken ct) =>
            servicio.HistorialDePrecioAsync(id, idListaPrecio, ct))
        .WithSummary("Historial completo de precios del artículo en una lista fija.");

        return app;
    }
}
