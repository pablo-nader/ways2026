using Ways.Api.Seguridad;
using Ways.Application.Plantel;

namespace Ways.Api.Endpoints;

public static class PersonalEndpoints
{
    public static IEndpointRouteBuilder MapearPersonal(this IEndpointRouteBuilder app)
    {
        // Lectura y escritura en grupos separados: apilar las dos políticas en un mismo grupo las
        // compondría con AND y dejaría afuera al Supervisor de la lectura.
        var lectura = app.MapGroup("/api/personal")
            .WithTags("Personal")
            .RequireAuthorization(Politicas.LecturaDePersonal);

        lectura.MapGet("/", (
            ServicioDePersonal servicio,
            string? busqueda,
            int? pagina,
            int? tamanio,
            CancellationToken ct) =>
            servicio.ListarAsync(busqueda, pagina ?? 1, tamanio ?? 25, ct))
        .WithSummary("Lista el personal con búsqueda y paginado, con el nombre del usuario y del cliente vinculados.");

        lectura.MapGet("/{id:int}", (ServicioDePersonal servicio, int id, CancellationToken ct) =>
            servicio.ObtenerAsync(id, ct))
        .WithSummary("Obtiene un personal.");

        var escritura = app.MapGroup("/api/personal")
            .WithTags("Personal")
            .RequireAuthorization(Politicas.GestionDeCatalogo);

        escritura.MapPost("/", async (
            ServicioDePersonal servicio, DatosDePersonal datos, CancellationToken ct) =>
        {
            var creado = await servicio.CrearAsync(datos, ct);
            return Results.Created($"/api/personal/{creado.Id}", creado);
        })
        .WithSummary("Crea un personal. Usuario y cliente son opcionales y cada uno se vincula a lo sumo a un personal.");

        escritura.MapPut("/{id:int}", (
            ServicioDePersonal servicio, int id, DatosDePersonal datos, CancellationToken ct) =>
            servicio.ActualizarAsync(id, datos, ct))
        .WithSummary("Actualiza un personal.");

        escritura.MapDelete("/{id:int}", async (
            ServicioDePersonal servicio, int id, CancellationToken ct) =>
        {
            await servicio.EliminarAsync(id, ct);
            return Results.NoContent();
        })
        .WithSummary("Baja lógica del personal; libera su usuario y su cliente.");

        return app;
    }
}
