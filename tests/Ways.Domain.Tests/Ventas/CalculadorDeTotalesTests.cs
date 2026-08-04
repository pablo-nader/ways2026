using Ways.Domain.Ventas;

namespace Ways.Domain.Tests.Ventas;

/// <summary>
/// stage-5-pos-ventas, Slice 3 (task 3.15, design: Checkout Contract — orden de redondeo
/// pineado) — pura, sin base de datos.
/// </summary>
public class CalculadorDeTotalesTests
{
    [Fact]
    public void UnaLineaSinDescuentoCalculaElTotalComoCantidadPorPrecio()
    {
        var resultado = CalculadorDeTotales.Calcular([new LineaParaCalcular(2m, 150m, 0m)]);

        Assert.Equal(300m, resultado.Subtotal);
        Assert.Equal(0m, resultado.DescuentoTotal);
        Assert.Equal(300m, resultado.Total);
        Assert.Equal(300m, resultado.Items[0].Total);
    }

    [Fact]
    public void UnaLineaConDescuentoUnitarioLoAplicaPorCantidad()
    {
        // 3 unidades a 100, descuento unitario 10 -> descuento total de línea 30.
        var resultado = CalculadorDeTotales.Calcular([new LineaParaCalcular(3m, 100m, 10m)]);

        Assert.Equal(300m, resultado.Subtotal);
        Assert.Equal(30m, resultado.DescuentoTotal);
        Assert.Equal(270m, resultado.Total);
        Assert.Equal(270m, resultado.Items[0].Total);
    }

    [Fact]
    public void VariasLineasSumanCorrectamenteSubtotalYDescuentoTotal()
    {
        var resultado = CalculadorDeTotales.Calcular(
        [
            new LineaParaCalcular(1m, 100m, 0m),
            new LineaParaCalcular(2m, 50m, 5m)
        ]);

        // Línea 1: 100, sin descuento -> total 100.
        // Línea 2: 2*50=100, descuento 2*5=10 -> total 90.
        Assert.Equal(200m, resultado.Subtotal);
        Assert.Equal(10m, resultado.DescuentoTotal);
        Assert.Equal(190m, resultado.Total);
        Assert.Equal(resultado.Total, resultado.Items.Sum(i => i.Total));
    }

    // ---- redondeo AwayFromZero --------------------------------------------------------------

    [Fact]
    public void ElBrutoDeLineaRedondeaAwayFromZeroEnElMedio()
    {
        // 3 * 33.335 = 100.005 exacto -> un punto medio real de redondeo a centavos.
        // AwayFromZero redondea a 100.01 — el banker's rounding default de .NET hubiera dado
        // 100.00 (par más cercano), que es exactamente lo que este criterio POS evita.
        var resultado = CalculadorDeTotales.Calcular([new LineaParaCalcular(3m, 33.335m, 0m)]);

        Assert.Equal(100.01m, resultado.Subtotal);
    }

    [Fact]
    public void ElDescuentoDeLineaRedondeaAwayFromZeroEnElMedio()
    {
        // Descuento unitario 0.005 * 1 unidad = 0.005 exacto -> otro punto medio real.
        var resultado = CalculadorDeTotales.Calcular([new LineaParaCalcular(1m, 10m, 0.005m)]);

        Assert.Equal(0.01m, resultado.Items[0].Descuento);
    }

    // ---- descuento clamp / total == Σ item.total --------------------------------------------

    [Fact]
    public void ElTotalGeneralSiempreCoincideConLaSumaDeLosItems()
    {
        var resultado = CalculadorDeTotales.Calcular(
        [
            new LineaParaCalcular(2m, 199.99m, 5.005m),
            new LineaParaCalcular(1m, 0.01m, 0m),
            new LineaParaCalcular(7m, 33.333m, 1.111m)
        ]);

        Assert.Equal(resultado.Total, resultado.Items.Sum(i => i.Total));
        Assert.Equal(resultado.Subtotal - resultado.DescuentoTotal, resultado.Total);
    }

    [Fact]
    public void UnaListaVaciaDeLineasDaTotalesEnCero()
    {
        var resultado = CalculadorDeTotales.Calcular([]);

        Assert.Empty(resultado.Items);
        Assert.Equal(0m, resultado.Subtotal);
        Assert.Equal(0m, resultado.DescuentoTotal);
        Assert.Equal(0m, resultado.Total);
    }

    // ---- líneas negativas de NCX (design decisión 4) ------------------------------------------

    [Fact]
    public void UnaLineaNegativaDeNcxDaUnTotalNegativo()
    {
        var resultado = CalculadorDeTotales.Calcular([new LineaParaCalcular(-2m, 150m, 0m)]);

        Assert.Equal(-300m, resultado.Subtotal);
        Assert.Equal(-300m, resultado.Total);
        Assert.Equal(-300m, resultado.Items[0].Total);
    }

    [Fact]
    public void UnaLineaNegativaDeNcxConDescuentoSigueCumpliendoElInvariante()
    {
        // Cantidad negativa: el descuento (descuentoUnitario * cantidad) también se vuelve
        // negativo, reduciendo la magnitud del total negativo -- la aritmética es uniforme,
        // sin rama especial para el signo.
        var resultado = CalculadorDeTotales.Calcular([new LineaParaCalcular(-3m, 100m, 10m)]);

        // Bruto: -3*100 = -300. Descuento: 10*-3 = -30. Total: -300 - (-30) = -270.
        Assert.Equal(-300m, resultado.Subtotal);
        Assert.Equal(-30m, resultado.DescuentoTotal);
        Assert.Equal(-270m, resultado.Total);
        Assert.Equal(resultado.Total, resultado.Items.Sum(i => i.Total));
    }

    [Fact]
    public void UnaMezclaDeLineasPositivasYNegativasCumpleElInvariante()
    {
        var resultado = CalculadorDeTotales.Calcular(
        [
            new LineaParaCalcular(2m, 100m, 0m),
            new LineaParaCalcular(-1m, 100m, 0m)
        ]);

        Assert.Equal(100m, resultado.Total);
        Assert.Equal(resultado.Total, resultado.Items.Sum(i => i.Total));
    }
}
