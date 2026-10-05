using System.Globalization;
using System.Text;
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
///
/// Corre antes de autenticar, así que acota lo que manda el cliente: el método, la ruta, los tres
/// headers que registra, los parámetros OAuth y los métodos JSON-RPC salen recortados a
/// <see cref="LargoMaximoDeValor"/> caracteres y sin caracteres de control (ver
/// <see cref="Limpiar(string)"/>), y un cuerpo solo se lee en <c>/mcp</c>, <c>/connect/authorize</c>
/// y <c>/connect/token</c>, con un Content-Length de hasta <see cref="TamanioMaximoInspeccionado"/>
/// bytes. Un cuerpo que no se puede leer se informa como no inspeccionado y la request sigue.
/// </summary>
public static class DiagnosticoDelConector
{
    /// <summary>Tope del cuerpo que se lee para la línea. Un pedido JSON-RPC de un cliente MCP o un
    /// formulario OAuth ocupan pocos KB; un cuerpo más grande, o sin Content-Length, no se lee.</summary>
    public const int TamanioMaximoInspeccionado = 64 * 1024;

    public const int LargoMaximoDeValor = 200;

    public const int MaximoDeMetodosJsonRpc = 10;

    private const string ClaveDeMotivo = "ways.mcp.motivo";
    private const string CuerpoNoInspeccionado = "(cuerpo no inspeccionado)";
    private const string FormularioNoInspeccionado = "(formulario no inspeccionado)";

    // Además de las rutas propias se registran las rutas por defecto (/authorize, /token,
    // /register) que un cliente MCP usa cuando no encuentra la metadata de autorización.
    private static readonly PathString[] RutasDelConector =
        [ConstantesDeMcp.RutaMcp, "/connect", "/.well-known", "/authorize", "/token", "/register"];

    private static readonly PathString[] RutasOAuth = ["/connect", "/authorize", "/token", "/register"];

    private static readonly PathString RutaMcp = ConstantesDeMcp.RutaMcp;

    private static readonly PathString[] EndpointsOAuth = [ConstantesDeMcp.RutaDeAutorizacion, ConstantesDeMcp.RutaDeToken];

    private static readonly string[] ParametrosConValor =
        ["client_id", "redirect_uri", "response_type", "scope", "resource", "code_challenge_method", "grant_type"];

    private static readonly string[] ParametrosSoloPresencia =
        ["state", "code_challenge", "code", "code_verifier", "refresh_token"];

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
                Limpiar(request.Method),
                Limpiar(request.Path.Value),
                http.Response.StatusCode,
                Limpiar(request.Headers["MCP-Protocol-Version"]),
                Limpiar(request.Headers["Mcp-Method"]),
                Limpiar(request.Headers.UserAgent),
                request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? "si" : "no",
                metodosJsonRpc ?? "-",
                parametrosOAuth ?? "-",
                DescribirRedireccion(http.Response) ?? "-",
                DescribirErrorOAuth(http) ?? "-",
                http.Items[ClaveDeMotivo] as string ?? "-");
        }
    }

    private static bool TieneCuerpoInspeccionable(HttpRequest request) =>
        request.ContentLength is >= 0 and <= TamanioMaximoInspeccionado;

    private static async Task<string?> LeerMetodosJsonRpcAsync(HttpRequest request)
    {
        if (!HttpMethods.IsPost(request.Method) || request.Path != RutaMcp)
        {
            return null;
        }

        if (!TieneCuerpoInspeccionable(request))
        {
            return CuerpoNoInspeccionado;
        }

        request.EnableBuffering(TamanioMaximoInspeccionado);
        try
        {
            using var documento = await JsonDocument.ParseAsync(
                request.Body, cancellationToken: request.HttpContext.RequestAborted);

            return DescribirMetodos(documento.RootElement);
        }
        catch (JsonException)
        {
            return "(json inválido)";
        }
        catch (Exception)
        {
            // Leer para el diagnóstico no hace fallar la request: el SDK vuelve a leer el cuerpo y se
            // encuentra con el mismo problema.
            return CuerpoNoInspeccionado;
        }
        finally
        {
            request.Body.Position = 0;
        }
    }

    private static string DescribirMetodos(JsonElement raiz)
    {
        JsonElement[] mensajes = raiz.ValueKind == JsonValueKind.Array ? [.. raiz.EnumerateArray()] : [raiz];

        var descripcion = string.Join(",", mensajes.Take(MaximoDeMetodosJsonRpc).Select(static mensaje =>
            mensaje.ValueKind == JsonValueKind.Object &&
            mensaje.TryGetProperty("method", out var metodo) &&
            metodo.ValueKind == JsonValueKind.String
                ? Limpiar(metodo.GetString())
                : "(respuesta)"));

        var omitidos = mensajes.Length - MaximoDeMetodosJsonRpc;
        return omitidos > 0 ? $"{descripcion},(+{omitidos} más)" : descripcion;
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
            if (await LeerFormularioAsync(request) is not { } formulario)
            {
                return FormularioNoInspeccionado;
            }

            foreach (var (clave, valor) in formulario)
            {
                parametros[clave] = valor;
            }
        }

        var conValor = ParametrosConValor
            .Where(parametros.ContainsKey)
            .Select(nombre => $"{nombre}=\"{Limpiar(parametros[nombre])}\"");
        var presencia = ParametrosSoloPresencia
            .Select(nombre => $"{nombre}={(parametros.ContainsKey(nombre) ? "true" : "false")}");

        return string.Join(" ", conValor.Concat(presencia));
    }

    /// <summary>El formulario de <c>/connect/authorize</c> o <c>/connect/token</c>, si su Content-Length
    /// está dentro del tope y el framework lo puede leer; si no, <c>null</c>. De las rutas OAuth por
    /// defecto (<c>/authorize</c>, <c>/token</c>, <c>/register</c>), que no son endpoints de este
    /// servidor, solo se registra que se pidieron.</summary>
    private static async Task<IFormCollection?> LeerFormularioAsync(HttpRequest request)
    {
        if (!EndpointsOAuth.Contains(request.Path) || !TieneCuerpoInspeccionable(request))
        {
            return null;
        }

        try
        {
            return await request.ReadFormAsync(request.HttpContext.RequestAborted);
        }
        catch (Exception)
        {
            // La request conserva el resultado de la lectura, error incluido: el endpoint que vuelva a
            // leer el formulario recibe la misma excepción que habría recibido sin este diagnóstico.
            return null;
        }
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

    private static string Limpiar(StringValues valores) => Limpiar(valores.ToString());

    /// <summary>Un valor que mandó el cliente, apto para una sola línea de log: recortado a
    /// <see cref="LargoMaximoDeValor"/> caracteres (con "…" al final si se recortó) y con cada carácter
    /// de control, de formato o separador de línea o de párrafo reemplazado por '?', para que no pueda
    /// partir la línea ni simular otra.</summary>
    private static string Limpiar(string? valor)
    {
        if (string.IsNullOrEmpty(valor))
        {
            return "-";
        }

        var recortado = valor.Length > LargoMaximoDeValor;
        var limpio = new StringBuilder(LargoMaximoDeValor + 1);
        foreach (var caracter in recortado ? valor.AsSpan(0, LargoMaximoDeValor) : valor.AsSpan())
        {
            limpio.Append(char.GetUnicodeCategory(caracter) is UnicodeCategory.Control or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
                ? '?'
                : caracter);
        }

        return recortado ? limpio.Append('…').ToString() : limpio.ToString();
    }
}
