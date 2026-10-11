using System.Net;
using Ways.Api.Dominios;

namespace Ways.IntegrationTests;

/// <summary>
/// Qué hosts son de la plataforma: los por defecto, la configuración <c>Plataforma:Hosts</c> que los
/// reemplaza, y el origen del shell de escritorio, que no depende de ninguna de las dos.
/// </summary>
public class OpcionesDePlataformaTests
{
    [Theory]
    [InlineData("aipos.site")]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("[::1]")]
    [InlineData("tauri.localhost")]
    public void SinConfiguracionValenLosHostsPorDefectoYElDelShellDeEscritorio(string host)
    {
        Assert.True(new OpcionesDePlataforma().EsHostDePlataforma(host));
    }

    [Fact]
    public void UnHostCualquieraNoEsDeLaPlataforma()
    {
        Assert.False(new OpcionesDePlataforma().EsHostDePlataforma("waysautoservicio.com"));
    }

    [Fact]
    public void LaConfiguracionReemplazaALosPorDefectoPeroNoAlOrigenDelShellDeEscritorio()
    {
        var opciones = new OpcionesDePlataforma { Hosts = ["Mi-Plataforma.Test ", "  otra.test"] };

        Assert.True(opciones.EsHostDePlataforma("mi-plataforma.test"));
        Assert.True(opciones.EsHostDePlataforma("otra.test"));
        Assert.True(opciones.EsHostDePlataforma("tauri.localhost"));
        Assert.False(opciones.EsHostDePlataforma("aipos.site"));
        Assert.False(opciones.EsHostDePlataforma("localhost"));
    }

    [Fact]
    public void UnaConfiguracionVaciaUsaLosPorDefecto()
    {
        var opciones = new OpcionesDePlataforma { Hosts = [] };

        Assert.True(opciones.EsHostDePlataforma("aipos.site"));
        Assert.True(opciones.EsHostDePlataforma("localhost"));
    }

    [Fact]
    public void LasEntradasEnBlancoSeIgnoranYSiNoQuedaNingunaValenLosPorDefecto()
    {
        var soloEnBlanco = new OpcionesDePlataforma { Hosts = ["", "   "] };
        var mezcladas = new OpcionesDePlataforma { Hosts = ["", "unica.test"] };

        Assert.False(soloEnBlanco.EsHostDePlataforma(string.Empty));
        Assert.True(soloEnBlanco.EsHostDePlataforma("aipos.site"));

        Assert.True(mezcladas.EsHostDePlataforma("unica.test"));
        Assert.False(mezcladas.EsHostDePlataforma(string.Empty));
        Assert.False(mezcladas.EsHostDePlataforma("aipos.site"));
    }
}

/// <summary>La configuración <c>Plataforma:Hosts</c> llega al middleware del host de la API.</summary>
[Collection("Ways.IntegrationTests secuencial")]
public class PlataformaHostsConfiguradosTests(WaysApiFixture fixture) : IClassFixture<WaysApiFixture>
{
    [Fact]
    public async Task ElHostConfiguradoEsDeLaPlataformaYLosPorDefectoDejanDeSerlo()
    {
        await using var host = fixture.WithWebHostBuilder(builder =>
            builder.UseSetting("Plataforma:Hosts:0", "mi-plataforma.test"));

        using var configurado = host.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://mi-plataforma.test")
        });
        using var porDefecto = host.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://aipos.site")
        });

        var enElConfigurado = await configurado.GetAsync("/api/publico/sitio");
        var enElDeAntes = await porDefecto.GetAsync("/api/publico/sitio");

        Assert.Equal(HttpStatusCode.OK, enElConfigurado.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, enElDeAntes.StatusCode);
    }
}
