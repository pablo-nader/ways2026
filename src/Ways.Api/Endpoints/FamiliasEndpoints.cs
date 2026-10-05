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

        grupo.MapPost("/previsualizacion", (
            ServicioDeAgrupacionDeFamilias servicio, SolicitudDePrevisualizacion datos, CancellationToken ct) =>
            servicio.PrevisualizarAsync(datos, ct))
        .WithSummary(
            "Previsualiza agrupar artículos con un artículo de referencia: por artículo, las columnas compartidas y "
                + "los precios que cambiarían, y los problemas que impedirían agrupar. No escribe nada.");

        grupo.MapPut("/{id:int}", (
            ServicioDeFamilias servicio, int id, EdicionFamilia datos, CancellationToken ct) =>
            servicio.ActualizarAsync(id, datos, ct))
        .WithSummary(
            "Cambia el nombre y el estado (activo) de la familia. Una familia inactiva no admite artículos nuevos.");

        grupo.MapDelete("/{id:int}/articulos/{idArticulo:int}", async (
            ServicioDeFamilias servicio, int id, int idArticulo, CancellationToken ct) =>
        {
            await servicio.SacarArticuloAsync(id, idArticulo, ct);
            return Results.NoContent();
        })
        .WithSummary(
            "Saca un artículo de su familia: queda sin familia y conserva todos sus valores. 409 familia_cambio si "
                + "el artículo no es miembro de esa familia.");

        grupo.MapDelete("/{id:int}", async (ServicioDeFamilias servicio, int id, CancellationToken ct) =>
        {
            await servicio.DisolverAsync(id, ct);
            return Results.NoContent();
        })
        .WithSummary(
            "Disuelve la familia: sus artículos vivos quedan sin familia, con todos sus valores, y la familia se "
                + "da de baja.");

        return app;
    }
}
