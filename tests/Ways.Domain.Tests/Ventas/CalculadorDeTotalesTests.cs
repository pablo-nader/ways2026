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

    // ---- ajuste manual por línea (negativo = descuento, positivo = recargo) -----------------------

    [Fact]
    public void UnDescuentoManualRestaElPorcentajeDelNetoDeLaLinea()
    {
        var resultado = CalculadorDeTotales.Calcular([new LineaParaCalcular(1m, 100m, 0m, -10m)]);

        Assert.Equal(-10m, resultado.Items[0].AjusteManual);
        Assert.Equal(90m, resultado.Items[0].Total);
        Assert.Equal(100m, resultado.Subtotal);
        Assert.Equal(0m, resultado.DescuentoTotal);
        Assert.Equal(10m, resultado.DescuentoManualTotal);
        Assert.Equal(0m, resultado.RecargoManualTotal);
        Assert.Equal(90m, resultado.Total);
    }

    [Fact]
    public void UnRecargoManualSumaElPorcentajeDelNetoDeLaLinea()
    {
        var resultado = CalculadorDeTotales.Calcular([new LineaParaCalcular(1m, 100m, 0m, 15m)]);

        Assert.Equal(15m, resultado.Items[0].AjusteManual);
        Assert.Equal(115m, resultado.Items[0].Total);
        Assert.Equal(0m, resultado.DescuentoManualTotal);
        Assert.Equal(15m, resultado.RecargoManualTotal);
        Assert.Equal(115m, resultado.Total);
    }

    [Fact]
    public void ElAjusteManualSeAplicaSobreElNetoPosteriorALaOfertaNoSobreElBruto()
    {
        // 2 x 100 = 200 bruto, oferta de 10 por unidad = 20 -> neto 180. -10 % sobre 180 = -18
        // (sobre el bruto hubiera sido -20).
        var resultado = CalculadorDeTotales.Calcular([new LineaParaCalcular(2m, 100m, 10m, -10m)]);

        Assert.Equal(20m, resultado.Items[0].Descuento);
        Assert.Equal(-18m, resultado.Items[0].AjusteManual);
        Assert.Equal(162m, resultado.Items[0].Total);
        Assert.Equal(200m, resultado.Subtotal);
        Assert.Equal(20m, resultado.DescuentoTotal);
        Assert.Equal(18m, resultado.DescuentoManualTotal);
        Assert.Equal(162m, resultado.Total);
    }

    [Fact]
    public void ElAjusteManualRedondeaAwayFromZeroEnElMedioParaRecargoYDescuento()
    {
        // Neto 0.05 x 10 % = 0.005 exacto: punto medio real. AwayFromZero da 0.01 / -0.01; el
        // banker's rounding default de .NET hubiera dado 0.00 en ambos.
        var recargo = CalculadorDeTotales.Calcular([new LineaParaCalcular(1m, 0.05m, 0m, 10m)]);
        var descuento = CalculadorDeTotales.Calcular([new LineaParaCalcular(1m, 0.05m, 0m, -10m)]);

        Assert.Equal(0.01m, recargo.Items[0].AjusteManual);
        Assert.Equal(0.06m, recargo.Items[0].Total);
        Assert.Equal(-0.01m, descuento.Items[0].AjusteManual);
        Assert.Equal(0.04m, descuento.Items[0].Total);
        Assert.Equal(0.01m, descuento.DescuentoManualTotal);
    }

    [Fact]
    public void UnDescuentoManualSobreUnaLineaDeNcxSeClasificaComoDescuentoPorElSignoDelPorcentaje()
    {
        // NCX: cantidad -3. Bruto -300, descuento de oferta -30, neto -270. -10 % sobre -270 da
        // un ajuste de +27 (acerca el total a cero, como cualquier descuento). Clasificar por el
        // signo del MONTO lo contaría como recargo; el criterio es el signo del porcentaje, igual
        // que descuento_total, que en NCX también es negativo.
        var resultado = CalculadorDeTotales.Calcular([new LineaParaCalcular(-3m, 100m, 10m, -10m)]);

        Assert.Equal(27m, resultado.Items[0].AjusteManual);
        Assert.Equal(-243m, resultado.Items[0].Total);
        Assert.Equal(-30m, resultado.DescuentoTotal);
        Assert.Equal(-27m, resultado.DescuentoManualTotal);
        Assert.Equal(0m, resultado.RecargoManualTotal);
        Assert.Equal(-243m, resultado.Total);
        Assert.Equal(resultado.Total, resultado.Items.Sum(i => i.Total));
    }

    [Fact]
    public void UnRecargoManualSobreUnaLineaDeNcxSeClasificaComoRecargoPorElSignoDelPorcentaje()
    {
        var resultado = CalculadorDeTotales.Calcular([new LineaParaCalcular(-3m, 100m, 10m, 10m)]);

        Assert.Equal(-27m, resultado.Items[0].AjusteManual);
        Assert.Equal(-297m, resultado.Items[0].Total);
        Assert.Equal(0m, resultado.DescuentoManualTotal);
        Assert.Equal(-27m, resultado.RecargoManualTotal);
        Assert.Equal(-297m, resultado.Total);
    }

    [Fact]
    public void UnDescuentoManualDelCienPorCientoDejaLaLineaEnCero()
    {
        var resultado = CalculadorDeTotales.Calcular([new LineaParaCalcular(3m, 33.33m, 1m, -100m)]);

        // Bruto 99.99, descuento 3.00, neto 96.99: el ajuste anula exactamente el neto.
        Assert.Equal(-96.99m, resultado.Items[0].AjusteManual);
        Assert.Equal(0m, resultado.Items[0].Total);
        Assert.Equal(96.99m, resultado.DescuentoManualTotal);
        Assert.Equal(0m, resultado.Total);
    }

    [Fact]
    public void UnRecargoEnUnaLineaNoEscondeUnDescuentoEnOtraEnElEncabezado()
    {
        // Línea A: -10 % sobre 100 = -10. Línea B: +5 % sobre 200 = +10. El neto de ajustes es
        // cero, pero el encabezado conserva los dos lados por separado.
        var resultado = CalculadorDeTotales.Calcular(
        [
            new LineaParaCalcular(1m, 100m, 0m, -10m),
            new LineaParaCalcular(1m, 200m, 0m, 5m)
        ]);

        Assert.Equal(10m, resultado.DescuentoManualTotal);
        Assert.Equal(10m, resultado.RecargoManualTotal);
        Assert.Equal(300m, resultado.Total);
        Assert.Equal(90m, resultado.Items[0].Total);
        Assert.Equal(210m, resultado.Items[1].Total);
    }

    [Fact]
    public void ElTotalConAjustesManualesSiempreCoincideConLaSumaDeLosItemsYConLaFormulaDelEncabezado()
    {
        var resultado = CalculadorDeTotales.Calcular(
        [
            new LineaParaCalcular(2m, 199.99m, 5.005m, -12.5m),
            new LineaParaCalcular(1m, 0.01m, 0m, 7.25m),
            new LineaParaCalcular(7m, 33.333m, 1.111m, null),
            new LineaParaCalcular(-4m, 59.9m, 3.3m, -33.33m),
            new LineaParaCalcular(-1m, 12.34m, 0m, 99.99m)
        ]);

        Assert.Equal(resultado.Total, resultado.Items.Sum(i => i.Total));
        Assert.Equal(
            resultado.Subtotal - resultado.DescuentoTotal - resultado.DescuentoManualTotal + resultado.RecargoManualTotal,
            resultado.Total);
        Assert.Equal(
            resultado.Items.Sum(i => i.AjusteManual),
            resultado.RecargoManualTotal - resultado.DescuentoManualTotal);
    }

    [Fact]
    public void SinPorcentajeElResultadoEsIdenticoAlCalculoSinAjusteManual()
    {
        var sinCuartoArgumento = CalculadorDeTotales.Calcular(
        [
            new LineaParaCalcular(2m, 199.99m, 5.005m),
            new LineaParaCalcular(-3m, 100m, 10m)
        ]);
        var conPorcentajeNulo = CalculadorDeTotales.Calcular(
        [
            new LineaParaCalcular(2m, 199.99m, 5.005m, null),
            new LineaParaCalcular(-3m, 100m, 10m, null)
        ]);

        Assert.Equal(sinCuartoArgumento.Items, conPorcentajeNulo.Items);
        Assert.Equal(sinCuartoArgumento.Subtotal, conPorcentajeNulo.Subtotal);
        Assert.Equal(sinCuartoArgumento.DescuentoTotal, conPorcentajeNulo.DescuentoTotal);
        Assert.Equal(sinCuartoArgumento.Total, conPorcentajeNulo.Total);
        Assert.All(conPorcentajeNulo.Items, i => Assert.Equal(0m, i.AjusteManual));
        Assert.Equal(0m, conPorcentajeNulo.DescuentoManualTotal);
        Assert.Equal(0m, conPorcentajeNulo.RecargoManualTotal);
        Assert.Equal(sinCuartoArgumento.Subtotal - sinCuartoArgumento.DescuentoTotal, sinCuartoArgumento.Total);
    }
}
