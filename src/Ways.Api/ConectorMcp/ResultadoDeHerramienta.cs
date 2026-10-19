using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Protocol;
using Npgsql;
using Ways.Api.Seguridad;
using Ways.Domain.Common;

namespace Ways.Api.ConectorMcp;

/// <summary>Las herramientas del conector informan sus errores como resultado con <c>isError</c> y el
/// mensaje, para que Claude lo lea: el SDK reemplaza por uno genérico el mensaje de una excepción que
/// escapa de la herramienta, incluso el de una <c>McpException</c>.</summary>
public static class ResultadoDeHerramienta
{
    /// <summary>Corre la herramienta y devuelve su texto. Un <see cref="ErrorDeHerramienta"/> o un
    /// <see cref="ErrorDominio"/> vuelven con <c>isError</c> y su mensaje; un <see cref="DbUpdateException"/> que la
    /// API clasifica (<see cref="Clasificar"/>) vuelve con <c>isError</c> y el título que daría el endpoint. Lo
    /// demás escapa como error inesperado.</summary>
    public static async Task<CallToolResult> EjecutarAsync(Func<Task<string>> lectura, ILogger log)
    {
        try
        {
            return Texto(await lectura(), esError: false);
        }
        catch (ErrorDeHerramienta error)
        {
            return Texto(error.Message, esError: true);
        }
        catch (ErrorDominio error)
        {
            return Texto(error.Message, esError: true);
        }
        catch (DbUpdateException error) when (Clasificar(error, log) is { } clasificado)
        {
            return Texto(clasificado.Titulo, esError: true);
        }
    }

    /// <summary>La misma clasificación que <see cref="ManejadorDeErrores"/> aplica a un fallo de escritura de la
    /// API, o <c>null</c> si no la tiene.</summary>
    internal static (int EstadoHttp, string Titulo, string Codigo)? Clasificar(DbUpdateException error, ILogger log) =>
        error.InnerException is PostgresException pg ? ManejadorDeErrores.ClasificarPostgresException(pg, log) : null;

    private static CallToolResult Texto(string texto, bool esError) =>
        new() { Content = [new TextContentBlock { Text = texto }], IsError = esError };
}

/// <summary>Un error que Claude tiene que ver tal cual: <see cref="ResultadoDeHerramienta"/> lo
/// devuelve como resultado con <c>isError</c>.</summary>
public sealed class ErrorDeHerramienta(string mensaje) : Exception(mensaje);
