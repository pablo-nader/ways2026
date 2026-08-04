using Ways.Api.Seguridad;
using Ways.Application.Clientes;

namespace Ways.Api.Endpoints;

public static class ClientesEndpoints
{
    public static IEndpointRouteBuilder MapearClientes(this IEndpointRouteBuilder app)
    {
        var grupo = app.MapGroup("/api/clientes")
            .WithTags("Clientes")
            .RequireAuthorization(Politicas.OperacionDePos);

        grupo.MapGet("/", (
            ServicioDeClientes servicio,
            string? busqueda,
            bool? incluirEliminados,
            int? pagina,
            int? tamanio,
            CancellationToken ct) =>
            servicio.ListarAsync(busqueda, incluirEliminados ?? false, pagina ?? 1, tamanio ?? 25, ct))
        .WithSummary("Lista clientes con búsqueda y paginado.");

        grupo.MapGet("/{id:int}", (ServicioDeClientes servicio, int id, CancellationToken ct) =>
            servicio.ObtenerAsync(id, ct))
        .WithSummary("Obtiene un cliente.");

        grupo.MapPost("/", async (
            ServicioDeClientes servicio, AltaCliente datos, CancellationToken ct) =>
        {
            var creado = await servicio.CrearAsync(datos, ct);
            return Results.Created($"/api/clientes/{creado.Id}", creado);
        })
        .RequireAuthorization(Politicas.GestionDeCatalogo)
        .WithSummary("Crea un cliente. El numero lo asigna el servidor (contador atómico por tenant).");

        grupo.MapPut("/{id:int}", (
            ServicioDeClientes servicio, int id, EdicionCliente datos, CancellationToken ct) =>
            servicio.ActualizarAsync(id, datos, ct))
        .RequireAuthorization(Politicas.GestionDeCatalogo)
        .WithSummary("Actualiza un cliente. Rechazado sobre el Consumidor Final (numero = 1).");

        grupo.MapDelete("/{id:int}", async (
            ServicioDeClientes servicio, int id, CancellationToken ct) =>
        {
            await servicio.EliminarAsync(id, ct);
            return Results.NoContent();
        })
        .RequireAuthorization(Politicas.GestionDeCatalogo)
        .WithSummary("Baja lógica del cliente. Rechazada sobre el Consumidor Final (numero = 1).");

        // Referencia de solo lectura para el selector de lista de precios del formulario —
        // no un ABM de listas_precio (design decision 1, spec: listas_precio ABM Is Out of
        // Scope This Stage). Mismo criterio que /api/roles en UsuariosEndpoints. Etapa 5
        // (design decisión 6): pasa a OperacionDePos, la lectura la necesita el POS.
        app.MapGet("/api/listas-precio", (ServicioDeClientes servicio, CancellationToken ct) =>
            servicio.ListasDePrecioAsignablesAsync(ct))
        .WithTags("Clientes")
        .RequireAuthorization(Politicas.OperacionDePos)
        .WithSummary("Listas de precio asignables a un cliente. Sin ABM propio esta etapa.");

        return app;
    }
}
