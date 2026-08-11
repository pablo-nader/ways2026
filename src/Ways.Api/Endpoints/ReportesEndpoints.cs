using Ways.Api.Seguridad;
using Ways.Application.Reportes;
using Ways.Domain.Reportes;

namespace Ways.Api.Endpoints;

public static class ReportesEndpoints
{
    public static IEndpointRouteBuilder MapearReportes(this IEndpointRouteBuilder app)
    {
        var grupo = app.MapGroup("/api/reportes")
            .WithTags("Reportes")
            .RequireAuthorization(Politicas.LecturaDeReportes);

        grupo.MapGet("/ventas/resumen", (
            ServicioDeReportesDeVentas servicio, int idEmpresa, int? idPuntoVenta, DateOnly desde, DateOnly hasta,
            Granularidad granularidad, CancellationToken ct) =>
            servicio.ObtenerResumenAsync(idEmpresa, idPuntoVenta, desde, hasta, granularidad, ct))
        .WithSummary(
            "Ventas netas bucketeadas por la zona horaria del punto de venta: ticket promedio " +
            "excluye NCX de numerador y denominador.");

        return app;
    }
}
