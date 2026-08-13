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

        // stage-12-lotes-vencimientos (Slice 3, task 3.3, design: API Surface): feed del picker
        // FEFO — hereda OperacionDePos del grupo, cualquier rol autenticado puede consultarlo.
        grupo.MapGet("/lotes", async (ServicioDeLotes servicio, int idPuntoVenta, int idArticulo, CancellationToken ct) =>
        {
            var lotes = await servicio.ListarAsync(idPuntoVenta, idArticulo, ct);
            return Results.Ok(lotes);
        })
        .WithSummary("Lotes de un artículo en un punto de venta, con saldo, estado y sugerido FEFO.");

        // stage-12-lotes-vencimientos (Slice 3, task 3.3, design: API Surface): alta manual de un
        // lote — mismo apilado GestionDeCatalogo sobre OperacionDePos que /ajustes.
        grupo.MapPost("/lotes", async (ServicioDeLotes servicio, SolicitudDeLote solicitud, CancellationToken ct) =>
        {
            var lote = await servicio.CrearAsync(solicitud, ct);
            return Results.Created($"/api/stock/lotes/{lote.IdLote}", lote);
        })
        .RequireAuthorization(Politicas.GestionDeCatalogo)
        .WithSummary("Alta manual de un lote (admin-only) — 409 lote_duplicado si el código ya existe.");

        // stage-12-lotes-vencimientos (Slice 4, task 4.4, design: API Surface): re-run manual de
        // la reconciliación — mismo apilado GestionDeCatalogo sobre OperacionDePos que /ajustes.
        // Idempotente (design decisión 13): un alcance vacío/ya reconciliado devuelve conteos en
        // cero, nunca un error.
        grupo.MapPost("/lotes/reconciliacion", async (ServicioDeLotes servicio, SolicitudDeReconciliacion solicitud, CancellationToken ct) =>
        {
            var resultado = await servicio.ReconciliarAsync(solicitud.IdArticulo, solicitud.IdPuntoVenta, ct);
            return Results.Ok(resultado);
        })
        .RequireAuthorization(Politicas.GestionDeCatalogo)
        .WithSummary("Re-run manual de la reconciliación de lotes (admin-only) — motivo = reclasificacion.");

        return app;
    }
}
