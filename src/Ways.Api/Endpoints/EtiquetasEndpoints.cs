using Ways.Api.Seguridad;
using Ways.Application.Etiquetas;

namespace Ways.Api.Endpoints;

public static class EtiquetasEndpoints
{
    public static IEndpointRouteBuilder MapearEtiquetas(this IEndpointRouteBuilder app)
    {
        // stage-18-etiquetas-y-consulta, Slice 2 (task 2.22; design.md:64, decisión 13): agrupa
        // SOLO bajo OperacionDePos, nada apilado encima — mismo criterio exacto que
        // "/api/ofertas/resolver" (POST read-only bajo el POS). El allowlist de
        // SuperficieDeAutorizacionTests gana exactamente una entrada para esta ruta.
        var grupo = app.MapGroup("/api/etiquetas")
            .WithTags("Etiquetas")
            .RequireAuthorization(Politicas.OperacionDePos);

        grupo.MapPost("/datos", (
            ServicioDeEtiquetas servicio, SolicitudDeEtiquetas solicitud, CancellationToken ct) =>
            servicio.ComponerAsync(solicitud, ct))
        .WithSummary("POST, no muta nada: compone la hoja de etiquetas — selección, precio y ofertas vigentes.");

        return app;
    }
}
