using Microsoft.Net.Http.Headers;
using Ways.Application.Dominios;
using Ways.Application.Vitrinas;
using Ways.Domain.Common;

namespace Ways.Api.Endpoints;

/// <summary>
/// Lo que se puede saber del sitio sin sesión. Solo expone lo que el middleware de dominio ya
/// resolvió —si es la plataforma o el nombre del comercio de un dominio verificado— y la vitrina
/// publicada de ese tenant, leída en el modo de acceso <c>publico</c> (solo <c>SELECT</c> sobre tres
/// tablas, ver <see cref="ILectorDeVitrinaPublica"/>): ningún otro dato de tenant. La vitrina de un tenant
/// que no puede operar (suspendido) no se sirve: contesta lo mismo que un comercio sin vitrina publicada.
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
            var vitrina = (TenantQueSirve(dominio) is { } idTenant ? await lector.ObtenerAsync(idTenant, ct) : null)
                ?? throw new ErrorDominio(
                    "vitrina_no_publicada", "Este comercio todavía no publicó su página.", StatusCodes.Status404NotFound);

            contexto.Response.Headers.CacheControl = "public, max-age=60";

            return Results.Ok(vitrina);
        })
        .WithSummary("La vitrina publicada del comercio del dominio: nombre, textos, tema, logo y sucursales.");

        grupo.MapGet("/vitrina/logo", (
            IDominioActual dominio, ILectorDeVitrinaPublica lector, HttpContext contexto, CancellationToken ct) =>
        {
            var idTenant = TenantQueSirve(dominio);

            return ServirLogoAsync(
                contexto,
                "public, max-age=3600",
                "Este comercio no tiene un logo publicado.",
                () => idTenant is { } id ? lector.ObtenerVersionDelLogoAsync(id, ct) : Task.FromResult<VersionDeLogo?>(null),
                () => idTenant is { } id ? lector.ObtenerLogoAsync(id, ct) : Task.FromResult<LogoPublico?>(null));
        })
        .WithSummary("El logo de la vitrina publicada, con ETag y caché de una hora.");

        return app;
    }

    /// <summary>
    /// Sirve un logo con ETag. Primero pregunta solo por su versión (tipo y marca de carga, sin los bytes):
    /// si <c>If-None-Match</c> coincide contesta 304 sin llamar a <paramref name="obtenerLogo"/>, que es lo
    /// único que lee la columna bytea. Si no coincide, trae los bytes y el ETag sale de ESA lectura, así que
    /// un logo reemplazado entre las dos consultas se sirve con su propio ETag. Los encabezados de caché se
    /// ponen recién cuando hay algo que servir: un 404 nunca queda cacheable.
    /// </summary>
    internal static async Task<IResult> ServirLogoAsync(
        HttpContext contexto,
        string cacheControl,
        string mensajeSinLogo,
        Func<Task<VersionDeLogo?>> obtenerVersion,
        Func<Task<LogoPublico?>> obtenerLogo)
    {
        var version = await obtenerVersion() ?? throw SinLogo(mensajeSinLogo);
        var etag = new EntityTagHeaderValue(EtagDelLogo.De(version.ActualizadoEn));

        if (CoincideConLaCondicional(contexto.Request, etag))
        {
            PonerEncabezadosDelLogo(contexto, cacheControl);
            contexto.Response.Headers.ETag = etag.ToString();

            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        var logo = await obtenerLogo() ?? throw SinLogo(mensajeSinLogo);
        PonerEncabezadosDelLogo(contexto, cacheControl);

        return Results.File(
            logo.Contenido,
            logo.ContentType,
            entityTag: new EntityTagHeaderValue(EtagDelLogo.De(logo.ActualizadoEn)));
    }

    /// <summary>El tenant del dominio solo si puede servir su vitrina: el host tiene un tenant (en el de la
    /// plataforma no hay vitrina que leer) y ese tenant no está suspendido.</summary>
    private static int? TenantQueSirve(IDominioActual dominio) =>
        dominio is { IdTenant: { } idTenant, TenantPuedeOperar: true } ? idTenant : null;

    private static ErrorDominio SinLogo(string mensaje) =>
        new("logo_no_encontrado", mensaje, StatusCodes.Status404NotFound);

    private static void PonerEncabezadosDelLogo(HttpContext contexto, string cacheControl)
    {
        contexto.Response.Headers.CacheControl = cacheControl;
        // El tipo lo decidió el contenido al subirlo; que el navegador no lo reinterprete.
        contexto.Response.Headers.XContentTypeOptions = "nosniff";
    }

    /// <summary><c>If-None-Match</c> con comparación débil (RFC 9110, 13.1.2): el ETag propio, o <c>*</c>.</summary>
    private static bool CoincideConLaCondicional(HttpRequest peticion, EntityTagHeaderValue etag) =>
        peticion.GetTypedHeaders().IfNoneMatch.Any(
            candidato => candidato.Equals(EntityTagHeaderValue.Any) || candidato.Compare(etag, useStrongComparison: false));
}
