using Ways.Domain.Organizacion;

namespace Ways.Domain.Tests.Organizacion;

public class DominioTests
{
    [Theory]
    [InlineData("tienda.com", "tienda.com")]
    [InlineData("  Tienda.COM  ", "tienda.com")]
    [InlineData("tienda.com:8080", "tienda.com")]
    [InlineData("tienda.com.", "tienda.com")]
    [InlineData("www.mi-tienda.com.ar", "www.mi-tienda.com.ar")]
    [InlineData("xn--cafe-yvc.com", "xn--cafe-yvc.com")]
    [InlineData("localhost", "localhost")]
    [InlineData("127.0.0.1", "127.0.0.1")]
    public void NormalizarHostDevuelveElHostEnMinusculaSinPuertoNiPuntoFinal(string crudo, string esperado)
    {
        Assert.Equal(esperado, Dominio.NormalizarHost(crudo));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://tienda.com")]
    [InlineData("http://tienda.com:8080")]
    [InlineData("tienda.com/ruta")]
    [InlineData("tienda.com\\ruta")]
    [InlineData("tienda .com")]
    [InlineData("tienda\t.com")]
    [InlineData("tienda\n.com")]
    [InlineData("tienda.com:")]
    [InlineData("tienda.com:abc")]
    [InlineData("tienda.com:80:90")]
    [InlineData("tienda.com:123456")]
    [InlineData(":8080")]
    [InlineData("-tienda.com")]
    [InlineData("tienda-.com")]
    [InlineData("tienda..com")]
    [InlineData(".tienda.com")]
    [InlineData("tienda_x.com")]
    [InlineData("ti@enda.com")]
    [InlineData("tienda.com?x=1")]
    [InlineData("tienda.com#ancla")]
    [InlineData("tiénda.com")]
    [InlineData("[::1]")]
    [InlineData(".")]
    public void NormalizarHostRechazaLoQueNoEsUnHostDeDns(string? crudo)
    {
        Assert.Null(Dominio.NormalizarHost(crudo));
    }

    [Fact]
    public void NormalizarHostAceptaUnaEtiquetaDe63CaracteresYRechazaUnaDe64()
    {
        var etiquetaMaxima = new string('a', 63);

        Assert.Equal($"{etiquetaMaxima}.com", Dominio.NormalizarHost($"{etiquetaMaxima}.com"));
        Assert.Null(Dominio.NormalizarHost($"{etiquetaMaxima}a.com"));
    }

    [Fact]
    public void NormalizarHostAceptaUnHostDe253CaracteresYRechazaUnoDe254()
    {
        // Tres etiquetas de 63, una de 61 y tres puntos: 253. Una letra más en la última etiqueta
        // (62) deja cada etiqueta válida, así que lo que rechaza es solo el largo total.
        var etiqueta = new string('a', 63);
        var hostDe253 = $"{etiqueta}.{etiqueta}.{etiqueta}.{new string('a', 61)}";
        Assert.Equal(253, hostDe253.Length);

        Assert.Equal(hostDe253, Dominio.NormalizarHost(hostDe253));
        Assert.Null(Dominio.NormalizarHost(hostDe253 + "a"));
    }

    [Fact]
    public void UnDominioNuevoNoEstaVerificadoNiEsPrincipal()
    {
        var dominio = new Dominio { Host = "tienda.com" };

        Assert.False(dominio.EsPrincipal);
        Assert.False(dominio.EstaVerificado);
        Assert.Null(dominio.VerificadoEn);
    }

    [Fact]
    public void ElDominioQuedaVerificadoCuandoTieneInstanteDeVerificacion()
    {
        var dominio = new Dominio { Host = "tienda.com", VerificadoEn = DateTimeOffset.UtcNow };

        Assert.True(dominio.EstaVerificado);
    }
}
