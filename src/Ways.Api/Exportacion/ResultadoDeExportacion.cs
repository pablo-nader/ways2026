using Microsoft.Extensions.Primitives;

namespace Ways.Api.Exportacion;

/// <summary>
/// Arma el <see cref="IResult"/> HTTP de un archivo exportado (spec exportacion-de-reportes: XLSX
/// Response Contract And Deterministic Naming). El <c>Content-Disposition</c> lleva SIEMPRE los
/// dos parámetros — <c>filename</c> ASCII y <c>filename*</c> RFC 5987 UTF-8 — aunque
/// <c>NombreDeArchivo.Construir</c> ya sea ASCII por construcción: es gratis y correcto, y no ata
/// la respuesta a esa garantía para siempre.
/// </summary>
public static class ResultadoDeExportacion
{
    public static IResult Archivo(byte[] contenido, string tipoDeContenido, string nombreDeArchivo) =>
        new ArchivoExportadoResult(contenido, tipoDeContenido, nombreDeArchivo);

    private sealed class ArchivoExportadoResult(byte[] contenido, string tipoDeContenido, string nombreDeArchivo)
        : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            var disposicion =
                $"attachment; filename=\"{nombreDeArchivo}\"; filename*=UTF-8''{Uri.EscapeDataString(nombreDeArchivo)}";

            httpContext.Response.ContentType = tipoDeContenido;
            httpContext.Response.Headers.Append("Content-Disposition", new StringValues(disposicion));
            httpContext.Response.ContentLength = contenido.Length;

            return httpContext.Response.Body.WriteAsync(contenido, httpContext.RequestAborted).AsTask();
        }
    }
}
