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

    /// <summary>Design: Price Resolution &amp; Rounding — AwayFromZero (no bankers' rounding) en
    /// un empate exacto de medio centavo.</summary>
    [Fact]
    public void UnEmpateDeRedondeoVaLejosDeCero()
    {
        // 100 * 1.125 = 112.5 -> redondeado a enteros de centavo, el tercer decimal es
        // exactamente 5: AwayFromZero redondea a 112.5 (sin tercer decimal que perder acá,
        // se fuerza el empate con un porcentaje que deja el tercer decimal en 5 exacto).
        var resuelto = ResolvedorDePrecios.ResolverPrecioDerivado(precioBase: 0.125m, porcentaje: 0m);

        Assert.Equal(0.13m, resuelto);
    }
}
