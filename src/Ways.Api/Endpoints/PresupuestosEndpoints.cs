using Ways.Api.Seguridad;
using Ways.Application.Ventas;
using Ways.Domain.Ventas;

namespace Ways.Api.Endpoints;

/// <summary>
/// stage-17-presupuestos-y-remitos, Slice 2 (design: API Surface, decisión 17/proposal decisión
/// 10). Grupo bajo <c>Politicas.OperacionDePos</c> ÚNICAMENTE — sin apilar
/// <c>GestionDeCatalogo</c>, a diferencia de <c>OrdenesDeCompraEndpoints</c>: un Vendedor puede
/// vender, y quotear/enviar/anular un presupuesto es vender (design: "un Vendedor tiene que poder
/// vender"). <c>POST /{id:int}/para-venta</c> y la conversión llegan en la Slice 3.
/// </summary>
public static class PresupuestosEndpoints
{
    public static IEndpointRouteBuilder MapearPresupuestos(this IEndpointRouteBuilder app)
    {
        var grupo = app.MapGroup("/api/presupuestos")
            .WithTags("Presupuestos")
            .RequireAuthorization(Politicas.OperacionDePos);

        grupo.MapGet("/", (
            ServicioDePresupuestos servicio,
            int? idPuntoVenta,
            int? idCliente,
            EstadoPresupuesto? estado,
            bool? vencido,
            DateTimeOffset? desde,
            DateTimeOffset? hasta,
            int? pagina,
            int? tamanio,
            CancellationToken ct) =>
            servicio.ListarAsync(idPuntoVenta, idCliente, estado, vencido, desde, hasta, pagina ?? 1, tamanio ?? 25, ct))
        .WithSummary("Lista presupuestos con filtros y paginado. 'vencido' requiere idPuntoVenta.");

        grupo.MapGet("/{id:int}", (ServicioDePresupuestos servicio, int id, CancellationToken ct) =>
            servicio.ObtenerDetalleAsync(id, ct))
        .WithSummary("Detalle de un presupuesto: header + items + Vencido/Convertible derivados.");

        grupo.MapPost("/", async (
            ServicioDePresupuestos servicio, SolicitudDePresupuesto solicitud, CancellationToken ct) =>
        {
            var creado = await servicio.CrearBorradorAsync(solicitud, ct);
            return Results.Created($"/api/presupuestos/{creado.Id}", creado);
        })
        .WithSummary("Crea un borrador de presupuesto (header + items opcionales, precio resuelto al guardar).");

        grupo.MapPut("/{id:int}", async (
            ServicioDePresupuestos servicio, int id, SolicitudDePresupuesto solicitud, CancellationToken ct) =>
        {
            var actualizado = await servicio.EditarAsync(id, solicitud, ct);
            return Results.Ok(actualizado);
        })
        .WithSummary("Reemplaza el header y el set completo de items de un borrador (borrador únicamente).");

        grupo.MapPost("/{id:int}/enviar", async (
            ServicioDePresupuestos servicio, int id, SolicitudDeEnvio solicitud, CancellationToken ct) =>
        {
            var enviado = await servicio.EnviarAsync(id, solicitud, ct);
            return Results.Ok(enviado);
        })
        .WithSummary("Asigna numero/fecha_envio propios (serie 'PRES') y pasa el presupuesto a enviado.");

        grupo.MapPost("/{id:int}/anular", async (ServicioDePresupuestos servicio, int id, CancellationToken ct) =>
        {
            var anulado = await servicio.AnularAsync(id, ct);
            return Results.Ok(anulado);
        })
        .WithSummary("Anula un presupuesto borrador/enviado. Un presupuesto convertido no puede anularse.");

        return app;
    }
}
