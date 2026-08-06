using Ways.Api.Seguridad;
using Ways.Application.Stock;

namespace Ways.Api.Endpoints;

public static class StockEndpoints
{
    public static IEndpointRouteBuilder MapearStock(this IEndpointRouteBuilder app)
    {
        var grupo = app.MapGroup("/api/stock")
            .WithTags("Stock")
            .RequireAuthorization(Politicas.OperacionDePos);

        grupo.MapGet("/", async (ServicioDeStock servicio, int idPuntoVenta, int idArticulo, CancellationToken ct) =>
        {
            var cantidad = await servicio.ObtenerCantidadAsync(idPuntoVenta, idArticulo, ct);
            return Results.Ok(new StockActual(idPuntoVenta, idArticulo, cantidad));
        })
        .WithSummary("Balance de stock de un artículo en un punto de venta (badge del POS).");

        // stage-5-pos-ventas (Slice 5, task 5.4, design: API Surface; spec: stock / Manual
        // Ajuste Path Is Admin-Only): único endpoint de escritura de esta etapa que apila
        // GestionDeCatalogo sobre OperacionDePos — un Vendedor no puede cargar stock a mano.
        grupo.MapPost("/ajustes", async (ServicioDeStock servicio, SolicitudDeAjusteDeStock solicitud, CancellationToken ct) =>
        {
            var cantidad = await servicio.AjustarAsync(solicitud, ct);
            return Results.Ok(new StockActual(solicitud.IdPuntoVenta, solicitud.IdArticulo, cantidad));
        })
        .RequireAuthorization(Politicas.GestionDeCatalogo)
        .WithSummary("Ajuste manual de stock (admin-only) — motivo = ajuste.");

        // stage-8-compras-transferencias-inventario (Slice 3, task 3.4, design: API Surface):
        // transferencia entre puntos de venta — mismo apilado GestionDeCatalogo sobre
        // OperacionDePos que /ajustes.
        grupo.MapPost("/transferencias", async (ServicioDeStock servicio, SolicitudDeTransferencia solicitud, CancellationToken ct) =>
        {
            var resultado = await servicio.TransferirAsync(solicitud, ct);
            return Results.Ok(resultado);
        })
        .RequireAuthorization(Politicas.GestionDeCatalogo)
        .WithSummary("Transferencia de stock entre puntos de venta (admin-only) — motivo = transferencia.");

        // stage-8-compras-transferencias-inventario (Slice 3, task 3.4, design: API Surface):
        // conteo de inventario — mismo apilado GestionDeCatalogo sobre OperacionDePos que /ajustes.
        grupo.MapPost("/conteos", async (ServicioDeStock servicio, SolicitudDeConteo solicitud, CancellationToken ct) =>
        {
            var resultado = await servicio.ContarAsync(solicitud, ct);
            return Results.Ok(resultado);
        })
        .RequireAuthorization(Politicas.GestionDeCatalogo)
        .WithSummary("Conteo de inventario (admin-only) — motivo = inventario.");

        return app;
    }
}
