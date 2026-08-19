using Ways.Api.Seguridad;
using Ways.Application.Compras;
using Ways.Domain.Compras;

namespace Ways.Api.Endpoints;

/// <summary>
/// Órdenes de compra (design: API Surface, decisión 16) — gate copiado VERBATIM de
/// <c>ComprasEndpoints.cs:20-22, 76-109</c>: lecturas bajo <c>OperacionDePos</c>, toda escritura
/// apila <c>GestionDeCatalogo</c> (Admin-only). Ningún <c>Politicas</c> nuevo (proposal decisión
/// 7).
///
/// Slice 2: <c>POST /</c> (borrador), <c>PUT /{id}</c> (replace-set), <c>POST /{id}/enviar</c>
/// (numeración propia). Slice 4: <c>POST /{id}/cerrar</c> (cierre manual), <c>POST /{id}/anular</c>
/// (gobernada por el libro, design decisión 9) — mismo gate que las tres rutas de arriba (task
/// 4.11, matriz de autorización: las CINCO rutas de escritura apilan <c>GestionDeCatalogo</c>).
///
/// Slice 5 (design: API Surface, decisiones 12-15): <c>GET /</c> (listado paginado) y
/// <c>GET /{id}</c> (detalle con cobertura por artículo + desvío) — SIN apilar
/// <c>GestionDeCatalogo</c>: son lecturas, mismo gate que <c>ComprasEndpoints.cs:24, 67</c>
/// (Vendedor lee, no escribe).
/// </summary>
public static class OrdenesDeCompraEndpoints
{
    public static IEndpointRouteBuilder MapearOrdenesDeCompra(this IEndpointRouteBuilder app)
    {
        var grupo = app.MapGroup("/api/ordenes-compra")
            .WithTags("OrdenesDeCompra")
            .RequireAuthorization(Politicas.OperacionDePos);

        grupo.MapGet("/", (
            ServicioDeOrdenesDeCompra servicio,
            int? idProveedor,
            int? idPuntoVenta,
            EstadoOrdenCompra? estado,
            DateTimeOffset? desde,
            DateTimeOffset? hasta,
            int? pagina,
            int? tamanio,
            CancellationToken ct) =>
            servicio.ListarAsync(idProveedor, idPuntoVenta, estado, desde, hasta, pagina ?? 1, tamanio ?? 25, ct))
        .WithSummary("Lista órdenes de compra con filtros y paginado.");

        grupo.MapGet("/{id:int}", (ServicioDeOrdenesDeCompra servicio, int id, CancellationToken ct) =>
            servicio.ObtenerDetalleAsync(id, ct))
        .WithSummary("Detalle de una orden de compra: header + items + cobertura por artículo + desvío de precio.");

        grupo.MapPost("/", async (
            ServicioDeOrdenesDeCompra servicio, SolicitudDeOrdenDeCompra solicitud, CancellationToken ct) =>
        {
            var creada = await servicio.CrearBorradorAsync(solicitud, ct);
            return Results.Created($"/api/ordenes-compra/{creada.Id}", creada);
        })
        .RequireAuthorization(Politicas.GestionDeCatalogo)
        .WithSummary("Crea un borrador de orden de compra (header + items opcionales).");

        grupo.MapPut("/{id:int}", async (
            ServicioDeOrdenesDeCompra servicio, int id, SolicitudDeOrdenDeCompra solicitud, CancellationToken ct) =>
        {
            var actualizada = await servicio.ActualizarBorradorAsync(id, solicitud, ct);
            return Results.Ok(actualizada);
        })
        .RequireAuthorization(Politicas.GestionDeCatalogo)
        .WithSummary("Reemplaza el header y el set completo de items de un borrador (borrador únicamente).");

        grupo.MapPost("/{id:int}/enviar", async (ServicioDeOrdenesDeCompra servicio, int id, CancellationToken ct) =>
        {
            var enviada = await servicio.EnviarAsync(id, ct);
            return Results.Ok(enviada);
        })
        .RequireAuthorization(Politicas.GestionDeCatalogo)
        .WithSummary("Asigna numero/fecha_envio propios (serie 'OC') y pasa la orden a enviada.");

        grupo.MapPost("/{id:int}/cerrar", async (ServicioDeOrdenesDeCompra servicio, int id, CancellationToken ct) =>
        {
            var cerrada = await servicio.CerrarAsync(id, ct);
            return Results.Ok(cerrada);
        })
        .RequireAuthorization(Politicas.GestionDeCatalogo)
        .WithSummary("Cierra manualmente una orden de compra (enviada/recibida_parcial → cerrada).");

        grupo.MapPost("/{id:int}/anular", async (ServicioDeOrdenesDeCompra servicio, int id, CancellationToken ct) =>
        {
            var anulada = await servicio.AnularAsync(id, ct);
            return Results.Ok(anulada);
        })
        .RequireAuthorization(Politicas.GestionDeCatalogo)
        .WithSummary("Anula una orden de compra gobernada por el libro de recepción (design decisión 9).");

        return app;
    }
}
