using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Protocol;
using Npgsql;
using Ways.Api.Seguridad;
using Ways.Domain.Common;

namespace Ways.Api.ConectorMcp;

/// <summary>Las herramientas del conector informan sus errores como resultado con <c>isError</c> y el
/// mensaje, para que Claude lo lea: de una excepción que escapa de la herramienta el SDK devuelve
/// <c>An error occurred invoking '&lt;herramienta&gt;'.</c>, sin su mensaje, y de una <c>McpException</c>,
/// <c>An error occurred invoking '&lt;herramienta&gt;': &lt;mensaje&gt;</c>.</summary>
public static class ResultadoDeHerramienta
{
    /// <summary>Corre la herramienta y devuelve su texto. Un <see cref="ErrorDeHerramienta"/> o un
    /// <see cref="ErrorDominio"/> vuelven con <c>isError</c> y su mensaje; un <see cref="DbUpdateException"/> cuya
    /// <see cref="PostgresException"/> clasifica <see cref="Clasificar"/> (violaciones de restricciones y de rangos)
    /// vuelve con <c>isError</c> y el título que daría el endpoint. Lo demás escapa como error inesperado, incluidos
    /// el fallo transitorio y el conflicto de concurrencia que <see cref="ManejadorDeErrores"/> sí traduce en la
    /// API.</summary>
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

    /// <summary>La clasificación por SqlState y restricción de <see cref="ManejadorDeErrores.ClasificarPostgresException"/>
    /// sobre la <see cref="PostgresException"/> que envuelve el <see cref="DbUpdateException"/>, o <c>null</c> si no la
    /// envuelve o no la clasifica.</summary>
    internal static (int EstadoHttp, string Titulo, string Codigo)? Clasificar(DbUpdateException error, ILogger log) =>
        error.InnerException is PostgresException pg ? ManejadorDeErrores.ClasificarPostgresException(pg, log) : null;

    private static CallToolResult Texto(string texto, bool esError) =>
        new() { Content = [new TextContentBlock { Text = texto }], IsError = esError };
}

/// <summary>Un error que Claude tiene que ver tal cual: <see cref="ResultadoDeHerramienta"/> lo
/// devuelve como resultado con <c>isError</c>.</summary>
public sealed class ErrorDeHerramienta(string mensaje) : Exception(mensaje);
