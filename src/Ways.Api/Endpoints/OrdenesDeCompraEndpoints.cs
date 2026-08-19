using Ways.Api.Seguridad;
using Ways.Application.Compras;

namespace Ways.Api.Endpoints;

/// <summary>
/// Órdenes de compra (design: API Surface, decisión 16) — gate copiado VERBATIM de
/// <c>ComprasEndpoints.cs:20-22, 76-109</c>: lecturas bajo <c>OperacionDePos</c>, toda escritura
/// apila <c>GestionDeCatalogo</c> (Admin-only). Ningún <c>Politicas</c> nuevo (proposal decisión
/// 7).
///
/// Slice 2: <c>POST /</c> (borrador), <c>PUT /{id}</c> (replace-set), <c>POST /{id}/enviar</c>
/// (numeración propia). <c>GET /</c>/<c>GET /{id}</c> llegan en slice 5 (necesitan el detalle con
/// cobertura); <c>POST /{id}/cerrar</c>/<c>POST /{id}/anular</c> en slice 4.
/// </summary>
public static class OrdenesDeCompraEndpoints
{
    public static IEndpointRouteBuilder MapearOrdenesDeCompra(this IEndpointRouteBuilder app)
    {
        var grupo = app.MapGroup("/api/ordenes-compra")
            .WithTags("OrdenesDeCompra")
            .RequireAuthorization(Politicas.OperacionDePos);

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

        return app;
    }
}
