using Ways.Api.Seguridad;
using Ways.Application.Compras;
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

        // stage-8-compras-transferencias-inventario (Slice 4, task 4.3, design: API Surface — el
        // trap de composición AND): mapeada TOP-LEVEL sobre `app`, nunca sobre `grupo` —
        // apilarla ahí compondría con GestionDeCatalogo (AND) y dejaría la lectura Admin-only,
        // contra spec: saldo-de-proveedor / Authorization And Scoping (un Vendedor tiene que
        // poder leerla). Ninguna policy nueva: OperacionDePos sola, misma puerta que el listado
        // de compras.
        app.MapGet("/api/proveedores/{id:int}/saldo", (
            ServicioDeSaldoDeProveedor servicio, int id, CancellationToken ct) =>
            servicio.ObtenerAsync(id, ct))
        .WithTags("Proveedores")
        .RequireAuthorization(Politicas.OperacionDePos)
        .WithSummary("Saldo derivado del proveedor: compras confirmadas menos gastos ligados.");

        return app;
    }
}
