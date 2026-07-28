using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Ways.Domain.Common;

namespace Ways.Api.Seguridad;

/// <summary>
/// Traduce los <see cref="ErrorDominio"/> a ProblemDetails con su código de negocio,
/// y cualquier otra excepción a un 500 genérico sin filtrar detalles internos.
/// </summary>
public class ManejadorDeErrores(
    IProblemDetailsService problemDetails,
    ILogger<ManejadorDeErrores> log) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext contexto, Exception excepcion, CancellationToken ct)
    {
        var (estado, titulo, codigo) = excepcion switch
        {
            ErrorDominio e => (e.EstadoHttp, e.Message, e.Codigo),
            _ => (StatusCodes.Status500InternalServerError,
                  "Ocurrió un error inesperado.",
                  "error_interno")
        };

        if (estado >= 500)
        {
            log.LogError(excepcion, "Error no controlado en {Ruta}.", contexto.Request.Path);
        }

        contexto.Response.StatusCode = estado;

        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = contexto,
            Exception = excepcion,
            ProblemDetails = new ProblemDetails
            {
                Status = estado,
                Title = titulo,
                Extensions = { ["codigo"] = codigo }
            }
        });
    }
}
