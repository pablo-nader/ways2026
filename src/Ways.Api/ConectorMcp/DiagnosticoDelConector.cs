using System.Text.Json;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;

namespace Ways.Api.ConectorMcp;

/// <summary>
/// Una línea de log por request del conector (MCP, OAuth y descubrimiento) para saber qué manda
/// realmente el cliente y por qué se rechaza algo. De OAuth registra el valor de una lista blanca de
/// parámetros y solo la presencia de los secretos; nunca tokens, códigos, contraseñas ni el valor del
/// header Authorization.
/// </summary>
public static class DiagnosticoDelConector
{
    private const string ClaveDeMotivo = "ways.mcp.motivo";

    // Además de las rutas propias se registran las rutas por defecto (/authorize, /token,
    // /register) que un cliente MCP usa cuando no encuentra la metadata de autorización.
    private static readonly PathString[] RutasDelConector =
        [ConstantesDeMcp.RutaMcp, "/connect", "/.well-known", "/authorize", "/token", "/register"];

    private static readonly PathString[] RutasOAuth = ["/connect", "/authorize", "/token", "/register"];

    private static readonly string[] ParametrosConValor =
        ["client_id", "redirect_uri", "response_type", "scope", "resource", "code_challenge_method", "grant_type"];

    private static readonly string[] ParametrosSoloPresencia =
        ["state", "code_challenge", "code", "code_verifier", "refresh_token"];

    private const long TamanioMaximoInspeccionado = 1_000_000;

    public static bool EsRutaDelConector(PathString ruta) => RutasDelConector.Any(ruta.StartsWithSegments);

    /// <summary>Por qué el conector rechazó esta request; sale en la línea de diagnóstico.</summary>
    public static void RegistrarMotivo(HttpContext http, string motivo) => http.Items[ClaveDeMotivo] = motivo;

    public static async Task RegistrarAsync(HttpContext http, RequestDelegate siguiente, ILogger logueador)
    {
        var request = http.Request;
        var metodosJsonRpc = await LeerMetodosJsonRpcAsync(request);
        var parametrosOAuth = await DescribirParametrosOAuthAsync(request);

        try
        {
            await siguiente(http);
        }
        finally
        {
            logueador.LogInformation(
                "Conector MCP: {Metodo} {Ruta} -> {Estado} | MCP-Protocol-Version={Version} | Mcp-Method={McpMethod} | " +
                "User-Agent={Agente} | Bearer={ConBearer} | JSON-RPC={MetodosJsonRpc} | OAuth={ParametrosOAuth} | " +
                "Redireccion={Redireccion} | Error={ErrorOAuth} | Motivo={Motivo}",
                request.Method,
                request.Path.Value,
                http.Response.StatusCode,
                Valor(request.Headers["MCP-Protocol-Version"]),
                Valor(request.Headers["Mcp-Method"]),
                Recortar(request.Headers.UserAgent.ToString(), 200),
                request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? "si" : "no",
                metodosJsonRpc ?? "-",
                parametrosOAuth ?? "-",
                DescribirRedireccion(http.Response) ?? "-",
                DescribirErrorOAuth(http) ?? "-",
                http.Items[ClaveDeMotivo] as string ?? "-");
        }
    }

    private static async Task<string?> LeerMetodosJsonRpcAsync(HttpRequest request)
    {
        if (!HttpMethods.IsPost(request.Method) || !request.Path.StartsWithSegments(ConstantesDeMcp.RutaMcp))
        {
            return null;
        }

        if (request.ContentLength is > TamanioMaximoInspeccionado)
        {
            return "(cuerpo no inspeccionado)";
        }

        request.EnableBuffering();
        try
        {
            using var documento = await JsonDocument.ParseAsync(
                request.Body, cancellationToken: request.HttpContext.RequestAborted);

            var raiz = documento.RootElement;
            List<JsonElement> mensajes = raiz.ValueKind == JsonValueKind.Array ? [.. raiz.EnumerateArray()] : [raiz];

            return string.Join(",", mensajes.Select(static mensaje =>
                mensaje.ValueKind == JsonValueKind.Object &&
                mensaje.TryGetProperty("method", out var metodo) &&
                metodo.ValueKind == JsonValueKind.String
                    ? metodo.GetString()
                    : "(respuesta)"));
        }
        catch (JsonException)
        {
            return "(json inválido)";
        }
        finally
        {
            request.Body.Position = 0;
        }
    }

    private static async Task<string?> DescribirParametrosOAuthAsync(HttpRequest request)
    {
        if (!RutasOAuth.Any(request.Path.StartsWithSegments))
        {
            return null;
        }

        var parametros = new Dictionary<string, StringValues>(StringComparer.Ordinal);
        foreach (var (clave, valor) in request.Query)
        {
            parametros[clave] = valor;
        }

        if (request.HasFormContentType)
        {
            foreach (var (clave, valor) in await request.ReadFormAsync(request.HttpContext.RequestAborted))
            {
                parametros[clave] = valor;
            }
        }

        var conValor = ParametrosConValor
            .Where(parametros.ContainsKey)
            .Select(nombre => $"{nombre}=\"{parametros[nombre]}\"");
        var presencia = ParametrosSoloPresencia
            .Select(nombre => $"{nombre}={(parametros.ContainsKey(nombre) ? "true" : "false")}");

        return string.Join(" ", conValor.Concat(presencia));
    }

    private static string? DescribirErrorOAuth(HttpContext http) =>
        http.GetOpenIddictServerResponse() is { Error: { Length: > 0 } error } respuesta
            ? $"{error}: {respuesta.ErrorDescription}"
            : null;

    private static string? DescribirRedireccion(HttpResponse response)
    {
        var ubicacion = response.Headers.Location.ToString();
        if (string.IsNullOrEmpty(ubicacion) || !Uri.TryCreate(ubicacion, UriKind.Absolute, out var uri))
        {
            return null;
        }

        return $"{uri.GetLeftPart(UriPartial.Path)}?[{string.Join(",", QueryHelpers.ParseQuery(uri.Query).Keys)}]";
    }

    private static string Valor(StringValues valores) => StringValues.IsNullOrEmpty(valores) ? "-" : valores.ToString();

    private static string Recortar(string texto, int largo) =>
        string.IsNullOrEmpty(texto) ? "-" : texto.Length <= largo ? texto : texto[..largo];
}
