using Ways.Api.ConectorMcp;

namespace Ways.IntegrationTests;

/// <summary>Pruebas puras de <see cref="ValidacionDelConector"/>: qué configuración deja el
/// conector deshabilitado. No levantan la app ni Docker.</summary>
public class ConectorMcpValidacionTests
{
    private static OpcionesDeMcp Opciones(string? urlPublica) => new() { Habilitado = true, UrlPublica = urlPublica };

    [Fact]
    public void FueraDeDevelopmentSinUrlPublicaLaConfiguracionNoSirve()
    {
        var resultado = ValidacionDelConector.Validar(Opciones(urlPublica: null), esDesarrollo: false);

        Assert.Contains("Mcp:UrlPublica", resultado.Error);
        Assert.Null(resultado.UrlPublica);
    }

    [Fact]
    public void EnDevelopmentSinUrlPublicaLaConfiguracionSirveYLasUrlsSeDerivanDeLaRequest()
    {
        var resultado = ValidacionDelConector.Validar(Opciones(urlPublica: null), esDesarrollo: true);

        Assert.Null(resultado.Error);
        Assert.Null(resultado.UrlPublica);
    }

    [Theory]
    [InlineData("https://aipos.site/mcp")]
    [InlineData("https://aipos.site/?a=1")]
    [InlineData("https://aipos.site/#a")]
    [InlineData("https://usuario@aipos.site")]
    [InlineData("ftp://aipos.site")]
    [InlineData("aipos.site")]
    public void UnaUrlPublicaQueNoEsUnaRaizHttpNoSirveNiEnDevelopment(string urlPublica)
    {
        Assert.NotNull(ValidacionDelConector.Validar(Opciones(urlPublica), esDesarrollo: true).Error);
        Assert.NotNull(ValidacionDelConector.Validar(Opciones(urlPublica), esDesarrollo: false).Error);
    }

    [Fact]
    public void FueraDeDevelopmentLaUrlPublicaTieneQueUsarHttps()
    {
        Assert.Contains("https", ValidacionDelConector.Validar(Opciones("http://aipos.site"), esDesarrollo: false).Error);
        Assert.Null(ValidacionDelConector.Validar(Opciones("http://aipos.site"), esDesarrollo: true).Error);
    }

    [Theory]
    [InlineData("https://aipos.site")]
    [InlineData("https://AIPOS.site/")]
    [InlineData(" https://aipos.site ")]
    public void UnaUrlPublicaValidaSeDevuelveNormalizada(string urlPublica)
    {
        var resultado = ValidacionDelConector.Validar(Opciones(urlPublica), esDesarrollo: false);

        Assert.Null(resultado.Error);
        Assert.Equal("https://aipos.site/", resultado.UrlPublica!.AbsoluteUri);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void LaVidaDelAccessTokenTieneQueSerPositiva(int minutos)
    {
        var opciones = Opciones("https://aipos.site");
        opciones.MinutosDeAccessToken = minutos;

        Assert.Contains("Mcp:MinutosDeAccessToken", ValidacionDelConector.Validar(opciones, esDesarrollo: false).Error);
    }

    [Fact]
    public void ElIdDeClienteNoPuedeEstarVacio()
    {
        var opciones = Opciones("https://aipos.site");
        opciones.IdDeCliente = "  ";

        Assert.Contains("Mcp:IdDeCliente", ValidacionDelConector.Validar(opciones, esDesarrollo: false).Error);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(0, true)]
    [InlineData(-1, false)]
    public void LaToleranciaDeReusoDelRefreshPuedeFaltarOSerCeroPeroNoNegativa(int? segundos, bool sirve)
    {
        var opciones = Opciones("https://aipos.site");
        opciones.SegundosDeToleranciaDeReusoDeRefresh = segundos;

        Assert.Equal(sirve, ValidacionDelConector.Validar(opciones, esDesarrollo: false).Error is null);
    }
}
