using Microsoft.AspNetCore.Http.Extensions;
using Ways.Application.Dominios;

namespace Ways.Api.Spa;

/// <summary>
/// Redirecciones (302) del front hacia donde vive hoy, solo para GET y HEAD y conservando el query:
/// las rutas que el back office tenía en la raíz (<see cref="RutasDeLaSpa.RutasHeredadasDelBackOffice"/>)
/// pasan a <c>/admin/...</c> para que los favoritos sigan andando, y la raíz del host de la plataforma
/// va a <c>/admin</c> (la landing es del comercio, no de la plataforma). Los pedidos directos a
/// <c>/admin.html</c>, <c>/online.html</c> e <c>/index.html</c> van a su prefijo (<c>/admin</c>, <c>/online</c>,
/// <c>/</c>): el router de React no reconoce esas rutas y la página quedaría en blanco. El destino es siempre
/// una ruta propia: no se puede usar para mandar a otro sitio.
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
            var destino = RutasDeLaSpa.DestinoDeEntradaCruda(solicitud.Path);

            if (destino is null && (esLaRaiz && dominioActual.EsPlataforma || RutasDeLaSpa.EsRutaHeredada(solicitud.Path)))
            {
                destino = new PathString(RutasDeLaSpa.PrefijoAdmin).Add(esLaRaiz ? PathString.Empty : solicitud.Path);
            }

            if (destino is { } adonde)
            {
                contexto.Response.Redirect(
                    UriHelper.BuildRelative(solicitud.PathBase, adonde, solicitud.QueryString),
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
