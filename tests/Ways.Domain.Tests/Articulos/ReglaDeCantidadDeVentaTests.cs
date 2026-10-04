using System.Globalization;
using Ways.Domain.Articulos;

namespace Ways.Domain.Tests.Articulos;

public class ReglaDeCantidadDeVentaTests
{
    [Theory]
    [InlineData("1")]
    [InlineData("12")]
    [InlineData("3.000")]
    [InlineData("0")]
    public void UnaUnidadAdmiteCantidadesEnteras(string cantidad) =>
        Assert.True(ReglaDeCantidadDeVenta.EsCantidadAdmitida(UnidadVenta.Unidad, decimal.Parse(cantidad, CultureInfo.InvariantCulture)));

    [Theory]
    [InlineData("0.5")]
    [InlineData("1.001")]
    [InlineData("2.999")]
    [InlineData("0.0001")]
    public void UnaUnidadRechazaCualquierFraccion(string cantidad) =>
        Assert.False(ReglaDeCantidadDeVenta.EsCantidadAdmitida(UnidadVenta.Unidad, decimal.Parse(cantidad, CultureInfo.InvariantCulture)));

    [Theory]
    [InlineData("1")]
    [InlineData("0.001")]
    [InlineData("12.3")]
    [InlineData("12.345")]
    public void UnPesoAdmiteHastaTresDecimales(string cantidad) =>
        Assert.True(ReglaDeCantidadDeVenta.EsCantidadAdmitida(UnidadVenta.Peso, decimal.Parse(cantidad, CultureInfo.InvariantCulture)));

    [Theory]
    [InlineData("0.0001")]
    [InlineData("12.3456")]
    public void UnPesoRechazaMasDeTresDecimales(string cantidad) =>
        Assert.False(ReglaDeCantidadDeVenta.EsCantidadAdmitida(UnidadVenta.Peso, decimal.Parse(cantidad, CultureInfo.InvariantCulture)));

    [Fact]
    public void UnaCantidadNegativaSeJuzgaPorSuGranularidad()
    {
        Assert.True(ReglaDeCantidadDeVenta.EsCantidadAdmitida(UnidadVenta.Unidad, -2m));
        Assert.False(ReglaDeCantidadDeVenta.EsCantidadAdmitida(UnidadVenta.Unidad, -1.5m));
    }

    [Fact]
    public void UnaUnidadDeVentaDesconocidaNoSeAcepta() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ReglaDeCantidadDeVenta.EsCantidadAdmitida((UnidadVenta)99, 1m));
}
