using Ways.Api.Seguridad;
using Ways.Application.Gastos;

namespace Ways.Api.Endpoints;

public static class GastosEndpoints
{
    public static IEndpointRouteBuilder MapearGastos(this IEndpointRouteBuilder app)
    {
        var grupo = app.MapGroup("/api/gastos")
            .WithTags("Gastos")
            .RequireAuthorization(Politicas.OperacionDePos);

        // stage-6-turnos-caja (Slice 3, task 3.2, design: API Surface): captura de gasto contra
        // el turno abierto — sin GestionDeCatalogo apilado (spec: Gasto Authorization, un
        // Vendedor tiene que poder registrar un gasto), mismo criterio que
        // "/api/caja/turnos/{id}/movimientos".
        grupo.MapPost("/", async (ServicioDeGastos servicio, SolicitudDeGasto solicitud, CancellationToken ct) =>
        {
            var gasto = await servicio.RegistrarAsync(solicitud, ct);
            return Results.Created($"/api/gastos/{gasto.Id}", gasto);
        })
        .WithSummary("Registra un gasto contra el turno abierto del punto de venta.");

        grupo.MapGet("/", (
            ServicioDeGastos servicio,
            int? idPuntoVenta,
            DateTimeOffset? desde,
            DateTimeOffset? hasta,
            int? pagina,
            int? tamanio,
            CancellationToken ct) =>
            servicio.ListarAsync(idPuntoVenta, desde, hasta, pagina ?? 1, tamanio ?? 25, ct))
        .WithSummary("Historial de gastos, paginado.");

        return app;
    }
}
