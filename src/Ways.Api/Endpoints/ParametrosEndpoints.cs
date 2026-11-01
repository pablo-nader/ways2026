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
            ServicioDeParametros servicio, string clave, int idEmpresa, int? idPuntoVenta, int? idSucursal,
            CancellationToken ct) =>
            servicio.ResolverEnAlcanceAsync(clave, idEmpresa, idPuntoVenta, idSucursal, ct))
        .WithSummary(
            "Resuelve un parámetro: de caja, punto de venta > empresa > default; de sucursal (zona horaria, lotes, días de alerta), " +
            "sucursal > empresa > default, y con idPuntoVenta usa la sucursal de esa caja (ADR-13).");

        grupo.MapGet("/", (ServicioDeParametros servicio, int idEmpresa, CancellationToken ct) =>
            servicio.ListarAsync(idEmpresa, ct))
        .WithSummary("Lista los parámetros configurados de una empresa.");

        grupo.MapPut("/", (
            ServicioDeParametros servicio, int idEmpresa, ParametroAlta datos, CancellationToken ct) =>
            servicio.EstablecerAsync(idEmpresa, datos, ct))
        .RequireAuthorization(Politicas.GestionDeCatalogo)
        .WithSummary("Crea o edita un parámetro (upsert por clave + punto de venta o sucursal, según el alcance de la clave).");

        return app;
    }
}
