using Ways.Domain.Common;
using Ways.Domain.Ventas;

namespace Ways.Domain.Tests.Ventas;

public class ReglaDeImporteDeVentaTests
{
    [Theory]
    [InlineData("150", 150)]
    [InlineData("150.5", 150.5)]
    [InlineData("150.50", 150.5)]
    [InlineData("0.01", 0.01)]
    [InlineData(".5", 0.5)]
    [InlineData("999999999999.99", 999999999999.99)]
    public void UnTextoDeDigitosConPuntoDecimalSeLeeComoImporte(string texto, double esperado)
    {
        Assert.True(ReglaDeImporteDeVenta.TryParsear(texto, out var importe));
        Assert.Equal((decimal)esperado, importe);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("0.00")]
    [InlineData("-5")]
    [InlineData("+5")]
    [InlineData("abc")]
    [InlineData("150,50")]
    [InlineData("1,500")]
    [InlineData("1 500")]
    [InlineData("1e3")]
    [InlineData("150.505")]
    [InlineData("1.500")]
    [InlineData("150.000")]
    [InlineData("0.100")]
    [InlineData("150.500")]
    [InlineData("1000000000000")]
    public void UnTextoQuePodriaLeerseComoOtroMontoNoEsUnImporte(string? texto)
    {
        Assert.False(ReglaDeImporteDeVenta.TryParsear(texto, out _));
    }

    [Theory]
    [InlineData(0.01)]
    [InlineData(150)]
    [InlineData(150.5)]
    [InlineData(999999999999.99)]
    public void ElImporteValidoNoLanza(double importe)
    {
        ReglaDeImporteDeVenta.Validar((decimal)importe);
        Assert.True(ReglaDeImporteDeVenta.EsValido((decimal)importe));
    }

    [Fact]
    public void SinImporteLaReglaNoRechazaNada()
    {
        ReglaDeImporteDeVenta.Validar(null);
    }

    [Fact]
    public void UnImporteConTresDecimalesSeRechazaSinRedondearlo()
    {
        AfirmarInvalido(150.005m);
    }

    [Fact]
    public void UnImporteFueraDelRangoDeLaColumnaSeRechaza()
    {
        AfirmarInvalido(1_000_000_000_000m);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void UnImporteCeroONegativoSeRechaza(int importe)
    {
        AfirmarInvalido(importe);
    }

    private static void AfirmarInvalido(decimal importe)
    {
        var error = Assert.Throws<ErrorDominio>(() => ReglaDeImporteDeVenta.Validar(importe));
        Assert.Equal("importe_invalido", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
        Assert.False(ReglaDeImporteDeVenta.EsValido(importe));
    }
}
