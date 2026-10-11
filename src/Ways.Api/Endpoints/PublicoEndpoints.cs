using Microsoft.Net.Http.Headers;
using Ways.Application.Dominios;
using Ways.Application.Vitrinas;
using Ways.Domain.Common;

namespace Ways.Api.Endpoints;

/// <summary>
/// Lo que se puede saber del sitio sin sesión. Solo expone lo que el middleware de dominio ya
/// resolvió —si es la plataforma o el nombre del comercio de un dominio verificado— y la vitrina
/// publicada de ese tenant, leída en el modo de acceso <c>publico</c> (solo <c>SELECT</c> sobre tres
/// tablas, ver <see cref="ILectorDeVitrinaPublica"/>): ningún otro dato de tenant.
/// </summary>
public static class PublicoEndpoints
{
    public static IEndpointRouteBuilder MapearPublico(this IEndpointRouteBuilder app)
    {
        var grupo = app.MapGroup("/api/publico")
            .WithTags("Público")
            .AllowAnonymous();

        grupo.MapGet("/sitio", (IDominioActual dominio) =>
            new SitioPublico(
                dominio.EsPlataforma,
                dominio.NombreComercio,
                AdmiteUsuario: dominio.IdTenant is not null))
        .WithSummary(
            "Describe el sitio por el que se entró: plataforma o dominio de un comercio, y si el login admite usuario.");

        grupo.MapGet("/vitrina", async (
            IDominioActual dominio, ILectorDeVitrinaPublica lector, HttpContext contexto, CancellationToken ct) =>
        {
            // En el host de la plataforma no hay tenant: no hay vitrina que leer.
            var vitrina = (dominio.IdTenant is { } idTenant ? await lector.ObtenerAsync(idTenant, ct) : null)
                ?? throw new ErrorDominio(
                    "vitrina_no_publicada", "Este comercio todavía no publicó su página.", StatusCodes.Status404NotFound);

            contexto.Response.Headers.CacheControl = "public, max-age=60";

            return Results.Ok(vitrina);
        })
        .WithSummary("La vitrina publicada del comercio del dominio: nombre, textos, tema, logo y sucursales.");

        grupo.MapGet("/vitrina/logo", async (
            IDominioActual dominio, ILectorDeVitrinaPublica lector, HttpContext contexto, CancellationToken ct) =>
        {
            var logo = (dominio.IdTenant is { } idTenant ? await lector.ObtenerLogoAsync(idTenant, ct) : null)
                ?? throw new ErrorDominio(
                    "logo_no_encontrado", "Este comercio no tiene un logo publicado.", StatusCodes.Status404NotFound);

            contexto.Response.Headers.CacheControl = "public, max-age=3600";
            // El tipo lo decidió el contenido al subirlo; que el navegador no lo reinterprete.
            contexto.Response.Headers.XContentTypeOptions = "nosniff";

            // Results.File evalúa If-None-Match contra el ETag y contesta 304 sin reenviar los bytes.
            return Results.File(
                logo.Contenido,
                logo.ContentType,
                entityTag: new EntityTagHeaderValue($"\"{logo.ActualizadoEn.UtcTicks:x}\""));
        })
        .WithSummary("El logo de la vitrina publicada, con ETag y caché de una hora.");

        return app;
    }
}
