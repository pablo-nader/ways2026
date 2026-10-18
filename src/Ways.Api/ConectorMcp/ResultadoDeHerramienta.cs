using ModelContextProtocol.Protocol;

namespace Ways.Api.ConectorMcp;

/// <summary>Las herramientas del conector informan sus errores como resultado con <c>isError</c> y el
/// mensaje, para que Claude lo lea: el SDK reemplaza por uno genérico el mensaje de una excepción que
/// escapa de la herramienta, incluso el de una <c>McpException</c>.</summary>
public static class ResultadoDeHerramienta
{
    public static async Task<CallToolResult> EjecutarAsync(Func<Task<string>> lectura)
    {
        try
        {
            return Texto(await lectura(), esError: false);
        }
        catch (ErrorDeHerramienta error)
        {
            return Texto(error.Message, esError: true);
        }
    }

    private static CallToolResult Texto(string texto, bool esError) =>
        new() { Content = [new TextContentBlock { Text = texto }], IsError = esError };
}

/// <summary>Un error que Claude tiene que ver tal cual: <see cref="ResultadoDeHerramienta"/> lo
/// devuelve como resultado con <c>isError</c>.</summary>
public sealed class ErrorDeHerramienta(string mensaje) : Exception(mensaje);
