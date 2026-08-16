using Ways.Api.Seguridad;
using Ways.Application.Auditoria;

namespace Ways.Api.Endpoints;

public static class AuditoriaEndpoints
{
    public static IEndpointRouteBuilder MapearAuditoria(this IEndpointRouteBuilder app)
    {
        var grupo = app.MapGroup("/api/auditoria")
            .WithTags("Auditoria")
            .RequireAuthorization(Politicas.LecturaDeAuditoria);

        grupo.MapGet("/", (
            ServicioDeConsultaDeAuditoria servicio,
            DateTimeOffset? desde, DateTimeOffset? hasta, string? accion, int? idActor,
            string? entidad, int? idEntidad, int? idPuntoVenta, int? pagina, int? tamanio,
            CancellationToken ct) =>
        {
            var filtros = new FiltrosDeAuditoria(desde, hasta, accion, idActor, entidad, idEntidad, idPuntoVenta);
            return servicio.ConsultarAsync(filtros, pagina ?? 1, tamanio ?? 25, ct);
        })
        .WithSummary(
            "Log de auditoría filtrado y paginado — Admin-only (LecturaDeAuditoria), sin apilar " +
            "sobre LecturaDeReportes: dentro de un tenant, un Admin ve filas de TODOS los puntos " +
            "de venta (idPuntoVenta es un filtro, no un alcance). idEntidad exige entidad (400 " +
            "entidad_requerida) — accion/entidad desconocidas no rechazan, devuelven 0 filas " +
            "(design decisión 15: una acción retirada deja rastro consultable).");

        return app;
    }
}
