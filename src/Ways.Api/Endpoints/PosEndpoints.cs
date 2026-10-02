using Microsoft.Net.Http.Headers;
using Ways.Api.Seguridad;
using Ways.Application.Pos;
using Ways.Domain.Common;

namespace Ways.Api.Endpoints;

public static class PosEndpoints
{
    public static IEndpointRouteBuilder MapearPos(this IEndpointRouteBuilder app)
    {
        var grupo = app.MapGroup("/api/pos")
            .WithTags("Pos")
            .RequireAuthorization(Politicas.OperacionDePos);

        // stage-pos-venta-offline-backend (Parte A): RequiereDispositivo apilado sobre
        // OperacionDePos (AND) — mismo criterio exacto que POST /api/ventas/reservas-numeracion.
        // Sigue haciendo falta un rol de venta; la claim de dispositivo es una condición
        // ADICIONAL, nunca un reemplazo.
        // Sin `version`: el formato original (precio plano de la lista del Consumidor Final), que
        // siguen pidiendo los POS de escritorio ya instalados. `version=2`: todas las listas y los
        // clientes, con ETag sobre el contenido y 304 si el dispositivo ya lo tiene.
        grupo.MapGet("/instantanea", async (
            HttpContext http, ServicioDeInstantaneaDePos servicio, int? version, CancellationToken ct) =>
        {
            if (version is not null && version != 2)
            {
                throw new ErrorDominio("version_no_soportada", $"La versión {version} de la instantánea no existe.", 400);
            }

            if (version is null)
            {
                return Results.Ok(ArmadorDeInstantanea.ProyectarLegada(
                    await servicio.ObtenerAsync(soloListaDelConsumidorFinal: true, ct)));
            }

            var instantanea = await servicio.ObtenerAsync(ct: ct);

            var etiqueta = EtiquetaDeInstantanea.Calcular(instantanea);
            http.Response.Headers.ETag = etiqueta;
            http.Response.Headers.CacheControl = "private, no-cache";

            var yaLaTiene = http.Request.GetTypedHeaders().IfNoneMatch
                .Any(e => e.Equals(EntityTagHeaderValue.Any) || e.Tag.Equals(etiqueta));

            return yaLaTiene ? Results.StatusCode(StatusCodes.Status304NotModified) : Results.Ok(instantanea);
        })
        .RequireAuthorization(Politicas.RequiereDispositivo)
        .WithSummary(
            "Instantánea completa (sin paginar) de lo que el POS de escritorio necesita para " +
            "vender sin red: catálogo con precio resuelto y congelado, medios de pago, " +
            "tolerancia de pago. Con version=2 trae el precio en cada lista y los clientes, y " +
            "responde 304 si el If-None-Match coincide con el contenido. Solo el propio punto de " +
            "venta del dispositivo que la pide.");

        // Rendición de la cola local: RequiereDispositivo apilado sobre OperacionDePos (AND) —
        // mismo criterio exacto que /instantanea de arriba. El punto de venta y el dispositivo
        // salen de la claim, nunca del cuerpo.
        grupo.MapPost("/rendicion-de-cola", async (
            ServicioDeRendicionDeCola servicio, SolicitudDeRendicionDeCola solicitud, CancellationToken ct) =>
        {
            await servicio.RegistrarAsync(solicitud, ct);
            return Results.NoContent();
        })
        .RequireAuthorization(Politicas.RequiereDispositivo)
        .WithSummary(
            "El dispositivo declara el estado de su cola local (hasta qué número repartió y " +
            "cuántas ventas no llegaron) para que el cierre de turno pueda verificarlo.");

        return app;
    }
}
