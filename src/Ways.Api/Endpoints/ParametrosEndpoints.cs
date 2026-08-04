using Ways.Api.Seguridad;
using Ways.Application.Parametros;

namespace Ways.Api.Endpoints;

public static class ParametrosEndpoints
{
    public static IEndpointRouteBuilder MapearParametros(this IEndpointRouteBuilder app)
    {
        var grupo = app.MapGroup("/api/parametros")
            .WithTags("Parámetros")
            .RequireAuthorization(Politicas.OperacionDePos);

        grupo.MapGet("/{clave}", (
            ServicioDeParametros servicio, string clave, int idEmpresa, int? idPuntoVenta, CancellationToken ct) =>
            servicio.ResolverAsync(clave, idEmpresa, idPuntoVenta, ct))
        .WithSummary("Resuelve un parámetro: punto de venta > empresa > default declarado (ADR-13).");

        grupo.MapGet("/", (ServicioDeParametros servicio, int idEmpresa, CancellationToken ct) =>
            servicio.ListarAsync(idEmpresa, ct))
        .WithSummary("Lista los parámetros configurados de una empresa.");

        grupo.MapPut("/", (
            ServicioDeParametros servicio, int idEmpresa, ParametroAlta datos, CancellationToken ct) =>
            servicio.EstablecerAsync(idEmpresa, datos, ct))
        .RequireAuthorization(Politicas.GestionDeCatalogo)
        .WithSummary("Crea o edita un parámetro (upsert por clave + punto de venta).");

        return app;
    }
}
