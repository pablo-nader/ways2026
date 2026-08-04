using Ways.Api.Seguridad;
using Ways.Application.Ventas;
using Ways.Domain.Ventas;

namespace Ways.Api.Endpoints;

public static class VentasEndpoints
{
    public static IEndpointRouteBuilder MapearVentas(this IEndpointRouteBuilder app)
    {
        var grupo = app.MapGroup("/api/ventas")
            .WithTags("Ventas")
            .RequireAuthorization(Politicas.OperacionDePos);

        // stage-5-pos-ventas (Slice 4, task 4.6, design: API Surface): checkout — sin
        // GestionDeCatalogo apilado (design: Authorization Surface, "/api/ventas... solo
        // GestionDeCatalogo en POST /api/stock/ajustes"). Un Vendedor tiene que poder vender.
        grupo.MapPost("/", async (ServicioDeVentas servicio, SolicitudDeVenta solicitud, CancellationToken ct) =>
        {
            var emitido = await servicio.EmitirAsync(solicitud, ct);
            return Results.Created($"/api/ventas/{emitido.Id}", emitido);
        })
        .WithSummary("Emite un comprobante de venta (checkout). Todo el dinero se recalcula server-side.");

        grupo.MapGet("/{id:int}", (ServicioDeVentas servicio, int id, CancellationToken ct) =>
            servicio.ObtenerAsync(id, ct))
        .WithSummary("Reimpresión: lee el snapshot del comprobante, nunca re-joinea el catálogo.");

        grupo.MapGet("/", (
            ServicioDeVentas servicio,
            int? idPuntoVenta,
            DateTimeOffset? desde,
            DateTimeOffset? hasta,
            int? idCliente,
            EstadoComprobante? estado,
            int? pagina,
            int? tamanio,
            CancellationToken ct) =>
            servicio.ListarAsync(idPuntoVenta, desde, hasta, idCliente, estado, pagina ?? 1, tamanio ?? 25, ct))
        .WithSummary("Lista comprobantes de venta con filtros y paginado.");

        return app;
    }
}
