using Ways.Application.Dominios;

namespace Ways.Api.Endpoints;

/// <summary>
/// Lo que se puede saber del sitio sin sesión. Solo expone lo que el middleware de dominio ya
/// resolvió —si es la plataforma o el nombre del comercio de un dominio verificado—: ningún dato de
/// tenant más allá de ese nombre.
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

        return app;
    }
}
