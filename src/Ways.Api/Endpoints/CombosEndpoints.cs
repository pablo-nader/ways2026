using Ways.Api.Seguridad;
using Ways.Application.Combos;

namespace Ways.Api.Endpoints;

public static class CombosEndpoints
{
    public static IEndpointRouteBuilder MapearCombos(this IEndpointRouteBuilder app)
    {
        var grupo = app.MapGroup("/api/combos")
            .WithTags("Combos")
            .RequireAuthorization(Politicas.OperacionDePos);

        grupo.MapGet("/", (ServicioDeCombos servicio, CancellationToken ct) => servicio.ListarAsync(ct))
        .WithSummary("Lista los combos vivos del tenant con sus componentes.");

        grupo.MapGet("/{id:int}", (ServicioDeCombos servicio, int id, CancellationToken ct) =>
            servicio.ObtenerAsync(id, ct))
        .WithSummary("Obtiene un combo con sus componentes.");

        grupo.MapPost("/", async (ServicioDeCombos servicio, AltaCombo datos, CancellationToken ct) =>
        {
            var creado = await servicio.CrearAsync(datos, ct);
            return Results.Created($"/api/combos/{creado.Id}", creado);
        })
        .RequireAuthorization(Politicas.GestionDeCatalogo)
        .WithSummary("Crea un combo; sin código, el servidor asigna el siguiente libre.");

        grupo.MapPut("/{id:int}", (ServicioDeCombos servicio, int id, EdicionCombo datos, CancellationToken ct) =>
            servicio.ActualizarAsync(id, datos, ct))
        .RequireAuthorization(Politicas.GestionDeCatalogo)
        .WithSummary("Actualiza un combo; reemplaza por completo el conjunto de componentes.");

        grupo.MapDelete("/{id:int}", async (ServicioDeCombos servicio, int id, CancellationToken ct) =>
        {
            await servicio.EliminarAsync(id, ct);
            return Results.NoContent();
        })
        .RequireAuthorization(Politicas.GestionDeCatalogo)
        .WithSummary("Baja lógica del combo; sus componentes se borran.");

        return app;
    }
}
