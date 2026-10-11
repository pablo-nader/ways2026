namespace Ways.Api.Spa;

public static class FallbackDeLaSpaExtensions
{
    /// <summary>
    /// Cualquier ruta que no sea <c>/api</c> y no sea un archivo la resuelve el router de React, con la
    /// entrada que corresponde al prefijo (<see cref="RutasDeLaSpa.EntradaDe"/>). Una <c>/api/...</c>
    /// inexistente tiene que dar 404, no devolver un HTML.
    /// </summary>
    public static void MapearFallbackDeLaSpa(this IEndpointRouteBuilder app) =>
        app.MapFallback(async contexto =>
        {
            if (contexto.Request.Path.StartsWithSegments("/api"))
            {
                contexto.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var entorno = contexto.RequestServices.GetRequiredService<IWebHostEnvironment>();
            var raiz = string.IsNullOrEmpty(entorno.WebRootPath)
                ? Path.Combine(AppContext.BaseDirectory, "wwwroot")
                : entorno.WebRootPath;
            var entrada = Path.Combine(raiz, RutasDeLaSpa.EntradaDe(contexto.Request.Path));

            if (!File.Exists(entrada))
            {
                contexto.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            contexto.Response.ContentType = "text/html; charset=utf-8";
            await contexto.Response.SendFileAsync(entrada);
        }).AllowAnonymous();
}
