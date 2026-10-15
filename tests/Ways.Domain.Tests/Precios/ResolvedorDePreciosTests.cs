using Ways.Domain.Common;
using Ways.Domain.Precios;

namespace Ways.Domain.Tests.Precios;

/// <summary>
/// stage-3-articulos-y-precios, Slice 3 (task 3.6, spec: precios / Derived List Price
/// Resolution At Read Time) — función pura, sin base de datos.
/// </summary>
public class ResolvedorDePreciosTests
{
    /// <summary>Spec scenario: "Derived lista price follows the base lista automatically" —
    /// -10% sobre $100 da $90.</summary>
    [Fact]
    public void UnPorcentajeNegativoAplicaUnDescuento()
    {
        var resuelto = ResolvedorDePrecios.ResolverPrecioDerivado(precioBase: 100m, porcentaje: -10m);

        Assert.Equal(90m, resuelto);
    }

    /// <summary>Spec scenario: "Base price change propagates without a write" — $200 base a
    /// -10% da $180.</summary>
    [Fact]
    public void ElCalculoSeReaplicaCadaVezQueCambiaElPrecioBase()
    {
        var resuelto = ResolvedorDePrecios.ResolverPrecioDerivado(precioBase: 200m, porcentaje: -10m);

        Assert.Equal(180m, resuelto);
    }

    /// <summary>Un porcentaje positivo es un recargo, mismo cálculo sin rama especial.</summary>
    [Fact]
    public void UnPorcentajePositivoAplicaUnRecargo()
    {
        var resuelto = ResolvedorDePrecios.ResolverPrecioDerivado(precioBase: 100m, porcentaje: 15m);

        Assert.Equal(115m, resuelto);
    }

    /// <summary>El precio derivado conserva 2 decimales y un empate de medio centavo se redondea
    /// AwayFromZero (no bankers' rounding): el descuento de una lista no se pierde en artículos baratos.</summary>
    [Theory]
    [InlineData(1, -10, 0.90)]
    [InlineData(100, 12.5, 112.5)]
    [InlineData(100, -12.5, 87.5)]
    [InlineData(1000, 11.6666, 1116.67)]
    [InlineData(1.05, -10, 0.945)]
    [InlineData(0.25, 10, 0.28)]
    public void ElPrecioDerivadoConservaDosDecimalesYUnEmpateVaLejosDeCero(
        double precioBase, double porcentaje, double esperado)
    {
        var resuelto = ResolvedorDePrecios.ResolverPrecioDerivado((decimal)precioBase, (decimal)porcentaje);

        Assert.Equal(Math.Round((decimal)esperado, 2, MidpointRounding.AwayFromZero), resuelto);
    }

    /// <summary>(judgment-day, item 4) Un descuento mayor a -100% da un precio derivado
    /// negativo, sin sentido de negocio — se rechaza con un error de dominio limpio en lugar de
    /// devolver un número negativo silencioso.</summary>
    [Fact]
    public void UnPorcentajeMenorAMenos100DaUnPrecioNegativoYSeRechaza()
    {
        var error = Assert.Throws<ErrorDominio>(() =>
            ResolvedorDePrecios.ResolverPrecioDerivado(precioBase: 100m, porcentaje: -150m));

        Assert.Equal("precio_derivado_invalido", error.Codigo);
        Assert.Equal(422, error.EstadoHttp);
    }
}
