using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Ways.Api.ConectorMcp;

namespace Ways.IntegrationTests;

/// <summary>Pruebas puras de <see cref="PaginaDeConsentimiento"/>: el HTML y los valores que fija
/// <see cref="PaginaDeConsentimiento.AplicarEncabezados"/>, sin levantar la app ni Docker. Los encabezados de
/// las respuestas reales de /connect/authorize los prueba <see cref="ConectorMcpFlujoTests"/>.</summary>
public class ConectorMcpPaginaDeConsentimientoTests
{
    private const string CampoAntiforgery = "__RequestVerificationToken";

    private static PaginaDeConsentimiento Pagina(
        string cliente = "Claude", params KeyValuePair<string, StringValues>[] parametros) =>
        new(
            cliente,
            [ConstantesDeMcp.AlcanceMcp, "offline_access"],
            ["https://aipos.site/mcp"],
            ConstantesDeMcp.RutaDeAutorizacion,
            parametros,
            ["accion", "mail", "password"]);

    private static List<(string Nombre, string Valor)> CamposOcultos(string html) =>
        [.. Regex.Matches(html, "<input type=\"hidden\" name=\"([^\"]*)\" value=\"([^\"]*)\">")
            .Select(m => (m.Groups[1].Value, m.Groups[2].Value))];

    [Fact]
    public void CadaValorReflejadoSeCodificaComoHtml()
    {
        var html = Pagina(
                "<script>cliente()</script>",
                new("state", "\"><script>estado()</script>"),
                new("<x>", "valor"))
            .Renderizar(CampoAntiforgery, "token", error: "<b>error</b>", mail: "\"><img src=x>");

        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("<b>error</b>", html);
        Assert.DoesNotContain("<img", html);
        Assert.DoesNotContain("<x>", html);
        Assert.Contains("&lt;script&gt;cliente()&lt;/script&gt;", html);
    }

    [Fact]
    public void ElFormularioReenviaLosParametrosOriginalesMasElTokenAntiforgeryNuevo()
    {
        var html = Pagina(
                "Claude",
                new("state", "abc"),
                new("resource", new StringValues(["https://aipos.site/mcp", "https://otro.test/mcp"])),
                new("accion", "aprobar"),
                new("mail", "a@ways.test"),
                new("password", "secreto"),
                new(CampoAntiforgery, "token-viejo"))
            .Renderizar(CampoAntiforgery, "token-nuevo", error: null, mail: null);

        List<(string Nombre, string Valor)> esperados =
        [
            ("state", "abc"),
            ("resource", "https://aipos.site/mcp"),
            ("resource", "https://otro.test/mcp"),
            (CampoAntiforgery, "token-nuevo")
        ];
        Assert.Equal(esperados, CamposOcultos(html));
    }

    [Fact]
    public void LaPaginaDeclaraLoQueLaConexionPermiteYLoQueNo()
    {
        var html = Pagina().Renderizar(CampoAntiforgery, "token", error: null, mail: null);

        var permisos = Regex.Matches(html, "<li>(.*?)</li>").Select(m => WebUtility.HtmlDecode(m.Groups[1].Value)).ToList();
        Assert.Equal(
            [
                "Ver su nombre de usuario, su correo, su rol y su tenant.",
                "Consultar datos de ventas, compras, artículos y proveedores, según los permisos de su rol.",
                "Crear borradores de compra, que usted revisa y confirma en Ways.",
                "Asociar códigos de barras y de proveedor a artículos."
            ],
            permisos.Take(4));
        Assert.Contains(
            "<p>Claude no podrá confirmar compras, mover stock ni dinero, ni crear artículos o proveedores.</p>", html);
        Assert.DoesNotContain("ningún otro dato", html);
    }

    [Fact]
    public void AplicarEncabezadosFijaDenyNoStoreNoReferrerYUnaCspSinFormAction()
    {
        var http = new DefaultHttpContext();

        PaginaDeConsentimiento.AplicarEncabezados(http.Response);

        var encabezados = http.Response.Headers;
        Assert.Equal("DENY", encabezados.XFrameOptions.ToString());
        Assert.Equal("no-store", encabezados.CacheControl.ToString());
        Assert.Equal("no-referrer", encabezados["Referrer-Policy"].ToString());
        Assert.Contains("frame-ancestors 'none'", encabezados.ContentSecurityPolicy.ToString());
        Assert.DoesNotContain("form-action", encabezados.ContentSecurityPolicy.ToString());
    }
}
