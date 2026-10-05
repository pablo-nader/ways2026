using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Primitives;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Ways.Api.ConectorMcp;

/// <summary>
/// Pantalla de consentimiento renderizada en el servidor: se abre en el navegador del usuario,
/// donde puede no existir la cookie de Ways ni la SPA. El formulario vuelve a enviar todos los
/// parámetros originales para que OpenIddict revalide la solicitud.
/// </summary>
public sealed class PaginaDeConsentimiento(
    string nombreDelCliente,
    IReadOnlyList<string> alcances,
    IReadOnlyList<string> recursos,
    string accionDelFormulario,
    IEnumerable<KeyValuePair<string, StringValues>> parametrosOriginales,
    IReadOnlyCollection<string> camposPropios)
{
    // Sin form-action a propósito: Chrome aplica esa directiva también a la redirección que
    // sigue al POST del formulario, y bloquearía el 302 hacia la redirect_uri de Claude.
    public const string PoliticaDeContenido =
        "default-src 'none'; style-src 'unsafe-inline'; frame-ancestors 'none'; base-uri 'none'";

    public IResult ComoResultado(
        HttpContext http, IAntiforgery antiforgery, string? error, string? mail, int estado = StatusCodes.Status200OK)
    {
        var tokens = antiforgery.GetAndStoreTokens(http);
        AplicarEncabezados(http.Response);

        return Results.Content(
            Renderizar(tokens.FormFieldName, tokens.RequestToken, error, mail),
            "text/html; charset=utf-8",
            statusCode: estado);
    }

    public static void AplicarEncabezados(HttpResponse respuesta)
    {
        var encabezados = respuesta.Headers;
        encabezados.CacheControl = "no-store";
        encabezados.XFrameOptions = "DENY";
        encabezados.ContentSecurityPolicy = PoliticaDeContenido;
        encabezados["Referrer-Policy"] = "no-referrer";
    }

    public string Renderizar(string campoAntiforgery, string? tokenAntiforgery, string? error, string? mail)
    {
        var codificador = HtmlEncoder.Default;
        var cliente = codificador.Encode(nombreDelCliente);

        var permisos = new StringBuilder();
        foreach (var alcance in alcances)
        {
            permisos.Append("<li>").Append(codificador.Encode(DescribirAlcance(alcance))).Append("</li>");
        }

        var recursosHtml = string.Join(", ", recursos.Select(r => $"<code>{codificador.Encode(r)}</code>"));

        var ocultos = new StringBuilder();
        foreach (var (nombre, valores) in parametrosOriginales)
        {
            if (camposPropios.Contains(nombre, StringComparer.OrdinalIgnoreCase) ||
                string.Equals(nombre, campoAntiforgery, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Los valores de query y formulario nunca son null; el token de antiforgery tampoco.
            foreach (var valor in valores)
            {
                AgregarOculto(ocultos, codificador, nombre, valor!);
            }
        }

        AgregarOculto(ocultos, codificador, campoAntiforgery, tokenAntiforgery!);

        var bloqueDeError = error is null
            ? string.Empty
            : $"<p class=\"error\" role=\"alert\">No se pudo iniciar sesión: {codificador.Encode(error)}</p>";

        return $$"""
            <!doctype html>
            <html lang="es">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Autorizar acceso a Ways</title>
            <style>
            body { font-family: system-ui, -apple-system, "Segoe UI", Roboto, sans-serif; background: #f4f5f7; color: #1f2328; margin: 0; padding: 16px; }
            main { max-width: 440px; margin: 32px auto; background: #fff; border: 1px solid #d0d7de; border-radius: 8px; padding: 24px; }
            h1 { font-size: 1.25rem; margin: 0 0 12px; }
            label { display: block; margin: 12px 0 4px; font-weight: 600; }
            input[type=email], input[type=password] { width: 100%; box-sizing: border-box; padding: 8px; border: 1px solid #d0d7de; border-radius: 6px; font-size: 1rem; }
            .acciones { display: flex; gap: 8px; margin-top: 20px; }
            button { flex: 1; padding: 10px; border-radius: 6px; border: 1px solid #d0d7de; background: #f6f8fa; font-size: 1rem; cursor: pointer; }
            button.aprobar { background: #1f6feb; border-color: #1f6feb; color: #fff; }
            .error { background: #ffebe9; border: 1px solid #ff8182; border-radius: 6px; padding: 8px; }
            .nota { color: #57606a; font-size: .875rem; }
            code { word-break: break-all; }
            </style>
            </head>
            <body>
            <main>
            <h1>{{cliente}} solicita acceso a Ways</h1>
            <p>Si aprueba, {{cliente}} podrá:</p>
            <ul>{{permisos}}</ul>
            <p>Esta versión experimental no da acceso a ningún otro dato de Ways.</p>
            <p class="nota">Recurso: {{recursosHtml}}</p>
            {{bloqueDeError}}
            <form method="post" action="{{codificador.Encode(accionDelFormulario)}}">
            {{ocultos}}
            <label for="mail">Correo electrónico</label>
            <input id="mail" name="mail" type="email" autocomplete="username" value="{{codificador.Encode(mail ?? string.Empty)}}" required>
            <label for="password">Contraseña</label>
            <input id="password" name="password" type="password" autocomplete="current-password" required>
            <div class="acciones">
            <button class="aprobar" type="submit" name="accion" value="aprobar">Aprobar</button>
            <button type="submit" name="accion" value="denegar" formnovalidate>Denegar</button>
            </div>
            </form>
            <p class="nota">Las credenciales se verifican en Ways; {{cliente}} no recibe su contraseña.</p>
            </main>
            </body>
            </html>
            """;
    }

    private static void AgregarOculto(StringBuilder ocultos, HtmlEncoder codificador, string nombre, string valor) =>
        ocultos.Append("<input type=\"hidden\" name=\"").Append(codificador.Encode(nombre))
            .Append("\" value=\"").Append(codificador.Encode(valor)).Append("\">");

    private static string DescribirAlcance(string alcance) => alcance switch
    {
        ConstantesDeMcp.AlcanceMcp => "Ver su nombre de usuario, su correo, su rol y su tenant.",
        Scopes.OfflineAccess => "Seguir conectado sin volver a pedirle la contraseña. La conexión se pierde cuando el servidor se reinicia.",
        Scopes.OpenId => "Confirmar su identidad con su identificador interno de usuario.",
        _ => alcance
    };
}
