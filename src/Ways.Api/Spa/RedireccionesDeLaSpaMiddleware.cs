using Microsoft.AspNetCore.Http.Extensions;
using Ways.Application.Dominios;

namespace Ways.Api.Spa;

/// <summary>
/// Redirecciones (302) del front hacia donde vive hoy, solo para GET y HEAD y conservando el query:
/// las rutas que el back office tenía en la raíz (<see cref="RutasDeLaSpa.RutasHeredadasDelBackOffice"/>)
/// pasan a <c>/admin/...</c> para que los favoritos sigan andando, y la raíz del host de la plataforma
/// va a <c>/admin</c> (la landing es del comercio, no de la plataforma). El destino siempre empieza con
/// <c>/admin</c>: no se puede usar para mandar a otro sitio.
///
/// Va después de la resolución del dominio (necesita saber si el host es de la plataforma) y antes de los
/// archivos estáticos, que de otro modo servirían <c>index.html</c> en la raíz de la plataforma.
/// </summary>
public sealed class RedireccionesDeLaSpaMiddleware(RequestDelegate siguiente)
{
    public Task InvokeAsync(HttpContext contexto, IDominioActual dominioActual)
    {
        var solicitud = contexto.Request;

        if (HttpMethods.IsGet(solicitud.Method) || HttpMethods.IsHead(solicitud.Method))
        {
            var esLaRaiz = !solicitud.Path.HasValue || solicitud.Path == "/";

            if (esLaRaiz && dominioActual.EsPlataforma || RutasDeLaSpa.EsRutaHeredada(solicitud.Path))
            {
                var destino = new PathString(RutasDeLaSpa.PrefijoAdmin).Add(esLaRaiz ? PathString.Empty : solicitud.Path);
                contexto.Response.Redirect(
                    UriHelper.BuildRelative(solicitud.PathBase, destino, solicitud.QueryString),
                    permanent: false);

                return Task.CompletedTask;
            }
        }

        return siguiente(contexto);
    }
}

public static class RedireccionesDeLaSpaExtensions
{
    public static IApplicationBuilder UsarRedireccionesDeLaSpa(this IApplicationBuilder app) =>
        app.UseMiddleware<RedireccionesDeLaSpaMiddleware>();
}
