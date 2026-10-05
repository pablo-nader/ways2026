using Ways.Api.Seguridad;
using Ways.Application.Familias;

namespace Ways.Api.Endpoints;

/// <summary>Gestión de familias de artículos (doc 10 §3). Todo el grupo es solo de admin
/// (<see cref="Politicas.GestionDeCatalogo"/>), la misma puerta que el alta y la edición de artículos: las
/// lecturas también, porque traen los costos de la referencia de la familia.</summary>
public static class FamiliasEndpoints
{
    public static IEndpointRouteBuilder MapearFamilias(this IEndpointRouteBuilder app)
    {
        var grupo = app.MapGroup("/api/familias")
            .WithTags("Familias")
            .RequireAuthorization(Politicas.GestionDeCatalogo);

        grupo.MapGet("/", (ServicioDeFamilias servicio, CancellationToken ct) =>
            servicio.ListarAsync(ct))
        .WithSummary("Lista las familias vivas con la cantidad de artículos vivos de cada una, por nombre.");

        grupo.MapGet("/{id:int}", (ServicioDeFamilias servicio, int id, CancellationToken ct) =>
            servicio.ObtenerAsync(id, ct))
        .WithSummary(
            "Obtiene una familia con sus artículos vivos, los valores compartidos y el estado de precios de su "
                + "artículo de referencia (el de menor id), que es lo que prellena el alta dentro de la familia.");

        grupo.MapPut("/{id:int}", (
            ServicioDeFamilias servicio, int id, EdicionFamilia datos, CancellationToken ct) =>
            servicio.ActualizarAsync(id, datos, ct))
        .WithSummary(
            "Cambia el nombre y el estado (activo) de la familia. Una familia inactiva no admite artículos nuevos.");

        return app;
    }
}
