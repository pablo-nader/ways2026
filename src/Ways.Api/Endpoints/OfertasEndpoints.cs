using Ways.Api.Seguridad;
using Ways.Application.Ofertas;

namespace Ways.Api.Endpoints;

public static class OfertasEndpoints
{
    public static IEndpointRouteBuilder MapearOfertas(this IEndpointRouteBuilder app)
    {
        var grupo = app.MapGroup("/api/ofertas")
            .WithTags("Ofertas")
            .RequireAuthorization(Politicas.OperacionDePos);

        grupo.MapGet("/", (ServicioDeOfertas servicio, bool? incluirEliminados, CancellationToken ct) =>
            servicio.ListarAsync(incluirEliminados ?? false, ct))
        .WithSummary("Lista las ofertas del tenant.");

        grupo.MapGet("/{id:int}", (ServicioDeOfertas servicio, int id, CancellationToken ct) =>
            servicio.ObtenerAsync(id, ct))
        .WithSummary("Obtiene una oferta, incluido su targeting actual de listas.");

        grupo.MapPost("/", async (ServicioDeOfertas servicio, AltaOferta datos, CancellationToken ct) =>
        {
            var creada = await servicio.CrearAsync(datos, ct);
            return Results.Created($"/api/ofertas/{creada.Id}", creada);
        })
        .RequireAuthorization(Politicas.GestionDeCatalogo)
        .WithSummary("Crea una oferta, incluidas las listas a las que aplica.");

        grupo.MapPut("/{id:int}", (ServicioDeOfertas servicio, int id, EdicionOferta datos, CancellationToken ct) =>
            servicio.ActualizarAsync(id, datos, ct))
        .RequireAuthorization(Politicas.GestionDeCatalogo)
        .WithSummary("Actualiza una oferta; reemplaza por completo el subconjunto de listas.");

        grupo.MapDelete("/{id:int}", async (ServicioDeOfertas servicio, int id, CancellationToken ct) =>
        {
            await servicio.EliminarAsync(id, ct);
            return Results.NoContent();
        })
        .RequireAuthorization(Politicas.GestionDeCatalogo)
        .WithSummary("Baja lógica de la oferta.");

        // stage-4-ofertas, Slice 3 (design decision 7): POST, no muta nada — solo resuelve
        // precios + ofertas aplicadas para un lote de líneas, nunca escribe en ninguna tabla
        // (spec: resolucion-de-ofertas / Applied Ofertas Are Reported, Never Persisted). POST en
        // vez de GET porque el cuerpo es un lote de líneas, no algo que entre en un query string.
        // stage-5-pos-ventas (design decisión 6, spec: resolucion-de-ofertas / OperacionDePos
        // Authorization For POST /api/ofertas/resolver): queda solo en la policy del grupo — el
        // POS necesita resolver precios sin ser admin, cierra el carryover de verify de la
        // etapa 4.
        grupo.MapPost("/resolver", (ServicioDeOfertas servicio, SolicitudDeResolucion solicitud, CancellationToken ct) =>
            servicio.ResolverAsync(solicitud.Lineas, solicitud.Momento, ct))
        .WithSummary("POST, no muta nada: resuelve precio final y ofertas aplicadas para un lote de líneas.");

        return app;
    }
}
