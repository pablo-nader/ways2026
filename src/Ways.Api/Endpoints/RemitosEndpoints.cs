using Ways.Api.Seguridad;
using Ways.Application.Ventas;
using Ways.Domain.Ventas;

namespace Ways.Api.Endpoints;

/// <summary>
/// stage-17-presupuestos-y-remitos, Slice 5 (design: API Surface, decisión 17/proposal decisión
/// 10). **DEVIATION REGISTERED** — no task 5.x names this file individually, mismo criterio
/// registrado por la Slice 2 de <c>PresupuestosEndpoints.cs</c> ("not named by any Slice task
/// individually, but load-bearing"): sin esto <c>ServicioDeRemitos</c> queda inalcanzable por
/// HTTP. Grupo bajo <c>Politicas.OperacionDePos</c> ÚNICAMENTE — sin apilar
/// <c>GestionDeCatalogo</c>, mismo criterio que <c>PresupuestosEndpoints</c>/<c>VentasEndpoints</c>:
/// un Vendedor tiene que poder despachar un remito (design decisión 17).
///
/// stage-17-presupuestos-y-remitos, Slice 6 (design: API Surface — "POST /api/remitos/facturacion").
/// **DEVIATION REGISTERED** — ninguna tarea 6.x nombra este archivo individualmente, mismo criterio
/// que el párrafo de arriba: sin esta ruta, <c>ServicioDeFacturacionDeRemitos</c> queda
/// inalcanzable por HTTP. Mismo grupo, mismo criterio de autorización (un Vendedor consolida
/// remitos igual que despacha uno).
/// </summary>
public static class RemitosEndpoints
{
    public static IEndpointRouteBuilder MapearRemitos(this IEndpointRouteBuilder app)
    {
        var grupo = app.MapGroup("/api/remitos")
            .WithTags("Remitos")
            .RequireAuthorization(Politicas.OperacionDePos);

        grupo.MapGet("/", (
            ServicioDeRemitos servicio,
            int? idPuntoVenta,
            int? idCliente,
            EstadoRemito? estado,
            DateTimeOffset? desde,
            DateTimeOffset? hasta,
            int? pagina,
            int? tamanio,
            CancellationToken ct) =>
            servicio.ListarAsync(idPuntoVenta, idCliente, estado, desde, hasta, pagina ?? 1, tamanio ?? 25, ct))
        .WithSummary("Lista remitos con filtros y paginado.");

        grupo.MapGet("/{id:int}", (ServicioDeRemitos servicio, int id, CancellationToken ct) =>
            servicio.ObtenerDetalleAsync(id, ct))
        .WithSummary("Detalle de un remito: header + items.");

        grupo.MapPost("/", async (
            ServicioDeRemitos servicio, SolicitudDeRemito solicitud, CancellationToken ct) =>
        {
            var creado = await servicio.CrearBorradorAsync(solicitud, ct);
            return Results.Created($"/api/remitos/{creado.Id}", creado);
        })
        .WithSummary("Crea un borrador de remito (header + items opcionales, precio resuelto al guardar).");

        grupo.MapPut("/{id:int}", async (
            ServicioDeRemitos servicio, int id, SolicitudDeRemito solicitud, CancellationToken ct) =>
        {
            var actualizado = await servicio.EditarAsync(id, solicitud, ct);
            return Results.Ok(actualizado);
        })
        .WithSummary("Reemplaza el header y el set completo de items de un borrador (borrador únicamente).");

        grupo.MapPost("/{id:int}/emitir", async (ServicioDeRemitos servicio, int id, CancellationToken ct) =>
        {
            var emitido = await servicio.EmitirAsync(id, ct);
            return Results.Ok(emitido);
        })
        .WithSummary("Asigna numero/fecha_salida propios (serie 'REM') y mueve stock — el cuarto write site.");

        grupo.MapPost("/{id:int}/anular", async (ServicioDeRemitos servicio, int id, CancellationToken ct) =>
        {
            var anulado = await servicio.AnularAsync(id, ct);
            return Results.Ok(anulado);
        })
        .WithSummary("Anula un remito borrador/emitido, revirtiendo stock si ya había salido. Un remito facturado no puede anularse directamente.");

        grupo.MapPost("/facturacion", async (
            ServicioDeFacturacionDeRemitos servicio, SolicitudDeFacturacionDeRemitos solicitud, CancellationToken ct) =>
        {
            var facturado = await servicio.FacturarAsync(solicitud, ct);
            return Results.Created($"/api/ventas/{facturado.Id}", facturado);
        })
        .WithSummary("Consolida N remitos emitidos del mismo cliente/punto de venta en un comprobante TXR sin items.");

        return app;
    }
}
