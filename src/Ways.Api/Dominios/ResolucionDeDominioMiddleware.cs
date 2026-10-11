using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Ways.Application.Dominios;
using Ways.Domain.Organizacion;

namespace Ways.Api.Dominios;

/// <summary>
/// Resuelve el host de la request antes de que corra la autenticación: un host de la plataforma
/// pasa tal cual, el dominio verificado de un tenant deja su tenant en <see cref="DominioActual"/>, y
/// cualquier otro host no se sirve — <c>/api/*</c> contesta 404 <c>dominio_desconocido</c> y el resto
/// una página estática, sin tocar el front ni la API.
///
/// Corre antes que el manejador de errores y que el conector MCP (que reescribe <c>Request.Host</c>
/// en sus rutas), así que arma sus respuestas por su cuenta: el mismo <c>ProblemDetails</c> con
/// <c>codigo</c> que <c>ManejadorDeErrores</c>, y un 503 si la base no puede decir qué dominio es.
///
/// <c>/api/salud</c> se exceptúa: la sondean contenedores y proxies con el host que tengan a mano
/// (una IP, un nombre interno) y no devuelve más que si la base responde.
/// </summary>
public sealed class ResolucionDeDominioMiddleware(
    RequestDelegate siguiente,
    ILogger<ResolucionDeDominioMiddleware> log)
{
    private const string RutaDeSalud = "/api/salud";

    private static readonly byte[] PaginaDeDominioNoConfigurado = Encoding.UTF8.GetBytes(
        """
        <!doctype html>
        <html lang="es">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>Dominio no configurado</title>
        </head>
        <body>
        <p>Este dominio todavía no está configurado en Ways.</p>
        </body>
        </html>
        """);

    private static readonly byte[] PaginaDeServicioNoDisponible = Encoding.UTF8.GetBytes(
        """
        <!doctype html>
        <html lang="es">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>Servicio no disponible</title>
        </head>
        <body>
        <p>No pudimos atender este dominio en este momento. Probá de nuevo en unos segundos.</p>
        </body>
        </html>
        """);

    public async Task InvokeAsync(
        HttpContext contexto,
        DominioActual dominioActual,
        IResolvedorDeDominios resolvedor,
        IOptions<OpcionesDePlataforma> opciones,
        IProblemDetailsService problemas)
    {
        var host = contexto.Request.Host.Host.ToLowerInvariant().TrimEnd('.');

        if (opciones.Value.EsHostDePlataforma(host)
            || contexto.Request.Path.Equals(RutaDeSalud, StringComparison.OrdinalIgnoreCase))
        {
            dominioActual.EstablecerPlataforma(host);
            await siguiente(contexto);
            return;
        }

        DominioResuelto? resuelto;

        try
        {
            // Un host que no es un nombre de DNS no puede ser un dominio cargado (ux_dominios_host y
            // ck_dominios_host_formato lo impiden): no se gasta una consulta en él.
            resuelto = Dominio.NormalizarHost(host) == host
                ? await resolvedor.ResolverAsync(host, contexto.RequestAborted)
                : null;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            log.LogError(error, "No se pudo resolver el dominio {Host}.", host);
            await ResponderServicioNoDisponibleAsync(contexto, problemas);
            return;
        }

        if (resuelto is null)
        {
            await ResponderDominioDesconocidoAsync(contexto, problemas);
            return;
        }

        dominioActual.EstablecerDeTenant(host, resuelto);
        await siguiente(contexto);
    }

    private static async Task ResponderDominioDesconocidoAsync(HttpContext contexto, IProblemDetailsService problemas)
    {
        if (EsLaApi(contexto))
        {
            await ResponderProblemaAsync(
                contexto,
                problemas,
                StatusCodes.Status404NotFound,
                "Este dominio todavía no está configurado en Ways.",
                "dominio_desconocido");
            return;
        }

        await ResponderPaginaAsync(contexto, StatusCodes.Status200OK, PaginaDeDominioNoConfigurado);
    }

    private static async Task ResponderServicioNoDisponibleAsync(HttpContext contexto, IProblemDetailsService problemas)
    {
        if (EsLaApi(contexto))
        {
            await ResponderProblemaAsync(
                contexto,
                problemas,
                StatusCodes.Status503ServiceUnavailable,
                "No pudimos atender este dominio en este momento. Probá de nuevo en unos segundos.",
                "servicio_no_disponible");
            return;
        }

        await ResponderPaginaAsync(contexto, StatusCodes.Status503ServiceUnavailable, PaginaDeServicioNoDisponible);
    }

    private static bool EsLaApi(HttpContext contexto) => contexto.Request.Path.StartsWithSegments("/api");

    private static async Task ResponderProblemaAsync(
        HttpContext contexto, IProblemDetailsService problemas, int estado, string titulo, string codigo)
    {
        contexto.Response.StatusCode = estado;

        await problemas.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = contexto,
            ProblemDetails = new ProblemDetails
            {
                Status = estado,
                Title = titulo,
                Extensions = { ["codigo"] = codigo }
            }
        });
    }

    private static async Task ResponderPaginaAsync(HttpContext contexto, int estado, byte[] pagina)
    {
        contexto.Response.StatusCode = estado;
        contexto.Response.ContentType = "text/html; charset=utf-8";
        // El dominio puede configurarse en cualquier momento: que nadie recuerde esta respuesta.
        contexto.Response.Headers.CacheControl = "no-store";

        await contexto.Response.Body.WriteAsync(pagina, contexto.RequestAborted);
    }
}

public static class ResolucionDeDominioExtensions
{
    public static IApplicationBuilder UsarResolucionDeDominio(this IApplicationBuilder app) =>
        app.UseMiddleware<ResolucionDeDominioMiddleware>();
}
