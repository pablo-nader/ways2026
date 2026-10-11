using Ways.Domain.Ventas;

namespace Ways.Domain.Tests.Ventas;

/// <summary>Los totales de un documento ya emitido salen de los importes que guardan sus items,
/// nunca de volver a pasarlos por <see cref="CalculadorDeTotales"/>. Pura, sin base de datos.</summary>
public class TotalesGuardadosTests
{
    [Fact]
    public void LosCentavosGuardadosSeSumanTalCualSinRedondear()
    {
        // 3 x 10.50 con 1.25 de descuento guarda un total de 30.25: la fórmula vigente daría otro.
        var resultado = TotalesGuardados.Sumar([new ItemCalculado(3m, 10.50m, 1.25m, 30.25m)]);

        Assert.Equal(31.50m, resultado.Subtotal);
        Assert.Equal(1.25m, resultado.DescuentoTotal);
        Assert.Equal(30.25m, resultado.Total);
    }

    [Fact]
    public void UnEmpateDeMedioPesoGuardadoNoSeReinterpretaConElDescuentoPorUnidad()
    {
        // 3 x 33.50 con 32 de descuento guarda neto 69. Recalcular con el descuento por unidad
        // (32 / 3, inexacto) caía del lado equivocado del medio peso y daba neto 68.
        var guardado = TotalesGuardados.Sumar([new ItemCalculado(3m, 33.50m, 32m, 69m)]);
        var recalculado = CalculadorDeTotales.Calcular([new LineaParaCalcular(3m, 33.50m, 32m / 3m)]);

        Assert.Equal(101m, guardado.Subtotal);
        Assert.Equal(32m, guardado.DescuentoTotal);
        Assert.Equal(69m, guardado.Total);
        Assert.NotEqual(guardado.Total, recalculado.Total);
    }

    [Fact]
    public void ElSubtotalDeVariasLineasEsLaSumaDeTotalMasDescuentoDeCadaUna()
    {
        var resultado = TotalesGuardados.Sumar(
        [
            new ItemCalculado(1m, 100m, 10m, 90m),
            new ItemCalculado(2m, 7.25m, 0m, 14.50m),
            new ItemCalculado(1m, 50m, 50m, 0m)
        ]);

        Assert.Equal(164.50m, resultado.Subtotal);
        Assert.Equal(60m, resultado.DescuentoTotal);
        Assert.Equal(104.50m, resultado.Total);
        Assert.Equal(3, resultado.Items.Count);
    }

    [Fact]
    public void SinItemsTodosLosTotalesSonCero()
    {
        var resultado = TotalesGuardados.Sumar([]);

        Assert.Equal(0m, resultado.Subtotal);
        Assert.Equal(0m, resultado.DescuentoTotal);
        Assert.Equal(0m, resultado.Total);
    }
}
