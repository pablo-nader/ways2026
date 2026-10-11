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

    /// <summary>Un empate exacto de medio peso se redondea AwayFromZero (no bankers' rounding) y
    /// el precio derivado de venta es siempre un peso entero.</summary>
    [Theory]
    [InlineData(100, 12.5, 113)]
    [InlineData(100, 12.4, 112)]
    [InlineData(100, -12.5, 88)]
    [InlineData(1000, 11.6666, 1117)]
    public void ElPrecioDerivadoSeRedondeaAlPesoEnteroYUnEmpateVaLejosDeCero(
        double precioBase, double porcentaje, double esperado)
    {
        // 100 * 1.125 = 112.5 -> 113 (banker's daría 112, el par más cercano).
        var resuelto = ResolvedorDePrecios.ResolverPrecioDerivado((decimal)precioBase, (decimal)porcentaje);

        Assert.Equal((decimal)esperado, resuelto);
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
