using Ways.Api.Seguridad;
using Ways.Application.Vitrinas;

namespace Ways.Api.Endpoints;

/// <summary>
/// Administración de la vitrina de cada empresa (landing del comercio), con la misma policy que la edición
/// de la empresa: <see cref="Politicas.GestionDeOrganizacion"/> (admin del tenant y plataforma). La
/// lectura anónima de lo publicado vive en <see cref="PublicoEndpoints"/>.
/// </summary>
public static class VitrinasEndpoints
{
    public static IEndpointRouteBuilder MapearVitrinas(this IEndpointRouteBuilder app)
    {
        var empresas = app.MapGroup("/api/empresas")
            .WithTags("Vitrinas")
            .RequireAuthorization(Politicas.GestionDeOrganizacion);

        empresas.MapGet("/{id:int}/vitrina", (ServicioDeVitrinas servicio, int id, CancellationToken ct) =>
            servicio.ObtenerAsync(id, ct))
        .WithSummary("Obtiene la vitrina de una empresa; si no se guardó nada devuelve la forma por defecto con existe en false.");

        empresas.MapPut("/{id:int}/vitrina", (
            ServicioDeVitrinas servicio, int id, SolicitudDeVitrina datos, CancellationToken ct) =>
            servicio.GuardarAsync(id, datos, ct))
        .WithSummary("Crea o reemplaza la vitrina de una empresa. Publicar una segunda empresa del tenant da 409 vitrina_ya_publicada.");

        // El cuerpo es la imagen cruda y no JSON: el servicio lo lee acotado y valida el tipo por contenido.
        empresas.MapPut("/{id:int}/vitrina/logo", (
            ServicioDeVitrinas servicio, int id, HttpRequest peticion, CancellationToken ct) =>
            servicio.GuardarLogoAsync(id, peticion.ContentType, peticion.Body, ct))
        .WithSummary("Sube el logo (image/png, image/jpeg o image/webp, hasta 1 MB) como cuerpo de la request.");

        empresas.MapDelete("/{id:int}/vitrina/logo", async (ServicioDeVitrinas servicio, int id, CancellationToken ct) =>
        {
            await servicio.QuitarLogoAsync(id, ct);
            return Results.NoContent();
        })
        .WithSummary("Quita el logo de la vitrina de la empresa.");

        return app;
    }
}
