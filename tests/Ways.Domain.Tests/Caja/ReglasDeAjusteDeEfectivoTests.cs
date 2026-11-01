using Ways.Domain.Caja;
using Ways.Domain.Common;

namespace Ways.Domain.Tests.Caja;

public class ReglasDeAjusteDeEfectivoTests
{
    private static ErrorDominio Falla(Action accion) => Assert.Throws<ErrorDominio>(accion);

    [Fact]
    public void UnImportePositivoVaAlLadoIngresoYUnoNegativoAlLadoEgreso()
    {
        Assert.Equal((25.5m, 0m), ReglasDeAjusteDeEfectivo.Dividir(25.5m));
        Assert.Equal((0m, 30m), ReglasDeAjusteDeEfectivo.Dividir(-30m));
    }

    [Fact]
    public void SinImporteSeRechazaConSuPropioCodigo()
    {
        var error = Falla(() => ReglasDeAjusteDeEfectivo.ValidarImporte(null));

        Assert.Equal("importe_requerido", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0.00")]
    [InlineData("1.005")]
    [InlineData("1000000000000.00")]
    [InlineData("-1000000000000.00")]
    public void UnImporteCeroConTresDecimalesOFueraDeRangoEsInvalido(string texto)
    {
        var error = Falla(() => ReglasDeAjusteDeEfectivo.ValidarImporte(decimal.Parse(texto, System.Globalization.CultureInfo.InvariantCulture)));

        Assert.Equal("importe_invalido", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Theory]
    [InlineData("999999999999.99")]
    [InlineData("-999999999999.99")]
    [InlineData("0.01")]
    public void LosLimitesDelRangoSeAceptan(string texto)
    {
        var importe = decimal.Parse(texto, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(importe, ReglasDeAjusteDeEfectivo.ValidarImporte(importe));
    }
}
