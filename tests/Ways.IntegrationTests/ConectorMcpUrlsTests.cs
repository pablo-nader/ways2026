using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Ways.Api.ConectorMcp;

namespace Ways.IntegrationTests;

/// <summary>Pruebas puras de <see cref="UrlsDelConector"/>: no levantan la app ni Docker.</summary>
public class ConectorMcpUrlsTests
{
    private static HttpContext Request(string esquema, string host)
    {
        var http = new DefaultHttpContext();
        http.Request.Scheme = esquema;
        http.Request.Host = new HostString(host);
        return http;
    }

    [Fact]
    public void SinUrlPublicaElRecursoYElEmisorSeDerivanDeLaRequestEnMinusculas()
    {
        var urls = new UrlsDelConector(urlPublica: null, recursosRegistrados: []);
        var http = Request("http", "LocalHost:5080");

        Assert.Equal("http://localhost:5080/mcp", urls.Recurso(http));
        Assert.Equal("http://localhost:5080/", urls.Emisor(http));
    }

    [Fact]
    public void ConUrlPublicaLaRequestNoInfluyeYElEmisorTerminaEnBarra()
    {
        var urls = new UrlsDelConector(new Uri("https://aipos.site"), recursosRegistrados: []);
        var http = Request("http", "10.0.0.5:8080");

        Assert.Equal("https://aipos.site/mcp", urls.Recurso(http));
        Assert.Equal("https://aipos.site/", urls.Emisor(http));
    }

    [Theory]
    [InlineData("https://aipos.site", "aipos.site")]
    [InlineData("https://aipos.site:443", "aipos.site")]
    [InlineData("http://aipos.site:80", "aipos.site")]
    [InlineData("https://aipos.site:8443", "aipos.site:8443")]
    public void ElHostFijadoOmiteElPuertoPorDefecto(string urlPublica, string hostEsperado)
    {
        Assert.Equal(hostEsperado, UrlsDelConector.HostFijado(new Uri(urlPublica)).Value);
    }

    [Fact]
    public void SinUrlPublicaSeRegistranLosRecursosDeLasUrlsConHostConcreto()
    {
        var configuracion = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["urls"] = "http://localhost:5080; http://0.0.0.0:8080;http://[::]:8081;https://LOCALHOST:7001"
            })
            .Build();

        var recursos = UrlsDelConector.RecursosRegistrables(configuracion, urlPublica: null);

        string[] esperados = ["http://localhost:5080/mcp", "https://localhost:7001/mcp"];
        Assert.Equal(esperados, recursos);
    }

    [Fact]
    public void SinUrlPublicaNiUrlsSeRegistraElRecursoDeLaUrlPorDefectoDeKestrel()
    {
        var recursos = UrlsDelConector.RecursosRegistrables(new ConfigurationBuilder().Build(), urlPublica: null);

        Assert.Equal("http://localhost:5000/mcp", Assert.Single(recursos));
    }

    [Fact]
    public void ConUrlPublicaElUnicoRecursoRegistradoEsElPublico()
    {
        var configuracion = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["urls"] = "http://localhost:5080" })
            .Build();

        var recursos = UrlsDelConector.RecursosRegistrables(configuracion, new Uri("https://AIPOS.site/"));

        Assert.Equal("https://aipos.site/mcp", Assert.Single(recursos));
    }
}
