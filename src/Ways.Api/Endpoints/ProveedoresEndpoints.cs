using Ways.Api.Seguridad;
using Ways.Application.Proveedores;

namespace Ways.Api.Endpoints;

public static class ProveedoresEndpoints
{
    public static IEndpointRouteBuilder MapearProveedores(this IEndpointRouteBuilder app)
    {
        var grupo = app.MapGroup("/api/proveedores")
            .WithTags("Proveedores")
            .RequireAuthorization(Politicas.GestionDeCatalogo);

        grupo.MapGet("/", (
            ServicioDeProveedores servicio,
            string? busqueda,
            bool? incluirEliminados,
            int? pagina,
            int? tamanio,
            CancellationToken ct) =>
            servicio.ListarAsync(busqueda, incluirEliminados ?? false, pagina ?? 1, tamanio ?? 25, ct))
        .WithSummary("Lista proveedores con búsqueda y paginado.");

        grupo.MapGet("/{id:int}", (ServicioDeProveedores servicio, int id, CancellationToken ct) =>
            servicio.ObtenerAsync(id, ct))
        .WithSummary("Obtiene un proveedor.");

        grupo.MapPost("/", async (
            ServicioDeProveedores servicio, AltaProveedor datos, CancellationToken ct) =>
        {
            var creado = await servicio.CrearAsync(datos, ct);
            return Results.Created($"/api/proveedores/{creado.Id}", creado);
        })
        .WithSummary("Crea un proveedor. El cuit, si se provee, es único por tenant.");

        grupo.MapPut("/{id:int}", (
            ServicioDeProveedores servicio, int id, EdicionProveedor datos, CancellationToken ct) =>
            servicio.ActualizarAsync(id, datos, ct))
        .WithSummary("Actualiza un proveedor.");

        grupo.MapDelete("/{id:int}", async (
            ServicioDeProveedores servicio, int id, CancellationToken ct) =>
        {
            await servicio.EliminarAsync(id, ct);
            return Results.NoContent();
        })
        .WithSummary("Baja lógica del proveedor.");

        return app;
    }
}
