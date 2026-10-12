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
        // 3 * 33.5 = 100.5 exacto -> un punto medio real de redondeo al peso. AwayFromZero
        // redondea a 101 — el banker's rounding default de .NET hubiera dado 100 (par más
        // cercano), que es exactamente lo que este criterio POS evita.
        var resultado = CalculadorDeTotales.Calcular([new LineaParaCalcular(3m, 33.5m, 0m)]);

        Assert.Equal(101m, resultado.Subtotal);
    }

    [Fact]
    public void ElDescuentoDeLineaEsElBrutoMenosElNetoRedondeadoAwayFromZeroEnElMedio()
    {
        // Precio 11 con descuento unitario 1.5: el neto verdadero es 9.5, un punto medio real que
        // AwayFromZero lleva a 10. El descuento es bruto 11 - neto 10 = 1, no el 1.5 verdadero
        // redondeado por separado.
        var resultado = CalculadorDeTotales.Calcular([new LineaParaCalcular(1m, 11m, 1.5m)]);

        Assert.Equal(10m, resultado.Items[0].Total);
        Assert.Equal(1m, resultado.Items[0].Descuento);

        // Neto verdadero 8.5: AwayFromZero da 9 (banker's daría 8, el par más cercano).
        var otro = CalculadorDeTotales.Calcular([new LineaParaCalcular(1m, 10m, 1.5m)]);

        Assert.Equal(9m, otro.Items[0].Total);
        Assert.Equal(1m, otro.Items[0].Descuento);
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
        // Neto 5 x 10 % = 0.5 exacto: punto medio real. AwayFromZero da 1 / -1; el banker's
        // rounding default de .NET hubiera dado 0 en ambos.
        var recargo = CalculadorDeTotales.Calcular([new LineaParaCalcular(1m, 5m, 0m, 10m)]);
        var descuento = CalculadorDeTotales.Calcular([new LineaParaCalcular(1m, 5m, 0m, -10m)]);

        Assert.Equal(1m, recargo.Items[0].AjusteManual);
        Assert.Equal(6m, recargo.Items[0].Total);
        Assert.Equal(-1m, descuento.Items[0].AjusteManual);
        Assert.Equal(4m, descuento.Items[0].Total);
        Assert.Equal(1m, descuento.DescuentoManualTotal);
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

        // Bruto 99.99 -> 100, neto verdadero 3 x 32.33 = 96.99 -> 97, descuento 3: el ajuste anula
        // exactamente el neto.
        Assert.Equal(-97m, resultado.Items[0].AjusteManual);
        Assert.Equal(0m, resultado.Items[0].Total);
        Assert.Equal(97m, resultado.DescuentoManualTotal);
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

    // ---- importes de venta a peso entero ---------------------------------------------------------

    [Fact]
    public void UnaOfertaDeTresPorTresMilQuinientosCobraTresMilQuinientosExactos()
    {
        // Oferta "x3 por 3500" cargada como 1166.66 por unidad (lista 1500, descuento unitario
        // 333.34): 3 x 1166.66 = 3499.98 y la línea cobra el peso entero más cercano.
        var resultado = CalculadorDeTotales.Calcular([new LineaParaCalcular(3m, 1500m, 333.34m)]);

        Assert.Equal(3500m, resultado.Items[0].Total);
        Assert.Equal(4500m, resultado.Subtotal);
        Assert.Equal(1000m, resultado.Items[0].Descuento);
        Assert.Equal(1000m, resultado.DescuentoTotal);
        Assert.Equal(3500m, resultado.Total);
    }

    [Fact]
    public void ElNetoEsElNetoVerdaderoRedondeadoYElDescuentoEsLaDiferenciaConElBruto()
    {
        // 7 x 1166.66 = 8166.62 -> neto 8167. El descuento no se redondea por su cuenta (7 x
        // 333.34 = 2333.38): es bruto 7 x 1500 = 10500 menos neto 8167 = 2333.
        var resultado = CalculadorDeTotales.Calcular([new LineaParaCalcular(7m, 1500m, 333.34m)]);

        Assert.Equal(8167m, resultado.Items[0].Total);
        Assert.Equal(10500m, resultado.Subtotal);
        Assert.Equal(2333m, resultado.Items[0].Descuento);
        Assert.Equal(resultado.Subtotal - resultado.DescuentoTotal, resultado.Total);
    }

    [Fact]
    public void UnaCantidadPesadaCalculaElNetoVerdaderoYLoRedondeaAlPeso()
    {
        // 0.375 kg x 1999.99 = 749.99625 -> 750.
        var resultado = CalculadorDeTotales.Calcular([new LineaParaCalcular(0.375m, 1999.99m, 0m)]);

        Assert.Equal(750m, resultado.Items[0].Total);
        Assert.Equal(750m, resultado.Subtotal);
    }

    [Fact]
    public void ElPuntoMedioDelNetoRedondeaAlejandoseDeCeroEnPositivoYEnNegativo()
    {
        // 1 x 100.50 -> 101; la cantidad negativa de NCX -1 x 100.50 -> -101 (banker's daría 100).
        var venta = CalculadorDeTotales.Calcular([new LineaParaCalcular(1m, 100.50m, 0m)]);
        var ncx = CalculadorDeTotales.Calcular([new LineaParaCalcular(-1m, 100.50m, 0m)]);

        Assert.Equal(101m, venta.Items[0].Total);
        Assert.Equal(-101m, ncx.Items[0].Total);
    }

    [Fact]
    public void ElAjusteManualSeCalculaSobreElNetoEnteroYTambienEsEntero()
    {
        // Neto 3500 (de 1166.66 x 3), -15 % = -525 exacto; +3 % = 105. Con 999.50 x 1 -> neto
        // 1000 (999.5 redondea hacia arriba) y +0.15 % = 1.5 -> 2 (punto medio).
        var descuento = CalculadorDeTotales.Calcular([new LineaParaCalcular(3m, 1500m, 333.34m, -15m)]);
        var recargo = CalculadorDeTotales.Calcular([new LineaParaCalcular(1m, 999.50m, 0m, 0.15m)]);

        Assert.Equal(-525m, descuento.Items[0].AjusteManual);
        Assert.Equal(2975m, descuento.Items[0].Total);
        Assert.Equal(525m, descuento.DescuentoManualTotal);
        Assert.Equal(1000m, recargo.Items[0].Total - recargo.Items[0].AjusteManual);
        Assert.Equal(2m, recargo.Items[0].AjusteManual);
        Assert.Equal(1002m, recargo.Total);
    }

    [Fact]
    public void ElAjusteManualDeUnaLineaNegativaDeNcxSeRedondeaAlejandoseDeCeroEnElMedioPeso()
    {
        // Neto -1000, +0.15 % = -1.5 -> -2 (away from zero), total -1002.
        var resultado = CalculadorDeTotales.Calcular([new LineaParaCalcular(-1m, 1000m, 0m, 0.15m)]);

        Assert.Equal(-2m, resultado.Items[0].AjusteManual);
        Assert.Equal(-1002m, resultado.Total);
    }

    [Fact]
    public void TodosLosImportesDeUnaVentaMixtaSonEnterosYElTotalCoincideConLosItems()
    {
        var resultado = CalculadorDeTotales.Calcular(
        [
            new LineaParaCalcular(3m, 1500m, 333.34m, -15m),
            new LineaParaCalcular(0.375m, 1999.99m, 0m, 7.5m),
            new LineaParaCalcular(7m, 33.333m, 1.111m, null),
            new LineaParaCalcular(-4m, 59.9m, 3.3m, -33.33m),
            new LineaParaCalcular(2m, 199.99m, 5.005m)
        ]);

        static bool EsEntero(decimal valor) => decimal.Truncate(valor) == valor;

        Assert.All(resultado.Items, i =>
        {
            Assert.True(EsEntero(i.Total), $"Total {i.Total}");
            Assert.True(EsEntero(i.Descuento), $"Descuento {i.Descuento}");
            Assert.True(EsEntero(i.AjusteManual), $"Ajuste {i.AjusteManual}");
        });
        Assert.True(EsEntero(resultado.Subtotal));
        Assert.True(EsEntero(resultado.DescuentoTotal));
        Assert.True(EsEntero(resultado.DescuentoManualTotal));
        Assert.True(EsEntero(resultado.RecargoManualTotal));
        Assert.True(EsEntero(resultado.Total));
        Assert.Equal(resultado.Total, resultado.Items.Sum(i => i.Total));
    }

    // ---- combos a peso entero --------------------------------------------------------------------

    [Fact]
    public void LosCamposDeComboPorDefectoDejanElResultadoIdenticoAlDeLaLineaSinCombo()
    {
        var sinCampos = CalculadorDeTotales.Calcular(
        [
            new LineaParaCalcular(2m, 199.99m, 5.005m, -10m),
            new LineaParaCalcular(-3m, 100m, 5.005m),
            new LineaParaCalcular(1m, 10m, 5.005m)
        ]);
        var conCerosExplicitos = CalculadorDeTotales.Calcular(
        [
            new LineaParaCalcular(2m, 199.99m, 5.005m, -10m, UnidadesConsumidasPorCombo: 0m, AhorroDeCombo: 0m),
            new LineaParaCalcular(-3m, 100m, 5.005m, null, 0m, 0m),
            new LineaParaCalcular(1m, 10m, 5.005m, null, 0m, 0m)
        ]);

        // Valores de la línea sin combo, calculados a mano con la regla de peso entero:
        //  línea 1: bruto 399.98 → 400; neto 2 × 194.985 = 389.97 → 390; descuento 10; ajuste −10 % = −39; total 351.
        //  línea 2 (NC): bruto −300; neto −3 × 94.995 = −284.985 → −285 (lejos de cero); descuento −15; total −285.
        //  línea 3: bruto 10; neto 4.995 → 5; descuento 5; total 5.
        //  subtotal 400 − 300 + 10 = 110; descuento total 10 − 15 + 5 = 0; descuento manual 39; total 110 − 0 − 39 = 71.
        var esperados = new[]
        {
            new ItemCalculado(2m, 199.99m, 10m, 351m, -39m),
            new ItemCalculado(-3m, 100m, -15m, -285m),
            new ItemCalculado(1m, 10m, 5m, 5m)
        };

        foreach (var resultado in new[] { sinCampos, conCerosExplicitos })
        {
            Assert.Equal(esperados, resultado.Items);
            Assert.Equal(110m, resultado.Subtotal);
            Assert.Equal(0m, resultado.DescuentoTotal);
            Assert.Equal(71m, resultado.Total);
            Assert.Equal(39m, resultado.DescuentoManualTotal);
        }
    }

    [Fact]
    public void NetoDeLineaSinComboDaElMismoResultadoQueConLosArgumentosDeComboEnCero()
    {
        Assert.Equal(3500m, CalculadorDeTotales.NetoDeLinea(3m, 1500m, 333.34m));
        Assert.Equal(
            CalculadorDeTotales.NetoDeLinea(3m, 1500m, 333.34m),
            CalculadorDeTotales.NetoDeLinea(3m, 1500m, 333.34m, 0m, 0m));
        Assert.Equal(-285m, CalculadorDeTotales.NetoDeLinea(-3m, 100m, 5.005m));
    }

    [Fact]
    public void LaLineaConConsumoParcialDeComboDescuentaLaOfertaSoloDeLasUnidadesNoConsumidasMasLaParteDelCombo()
    {
        // 3 unidades a 1000 con oferta de 100 por unidad; el combo consumió 2 y le tocó 1500:
        // descuento = 100 × 1 + 1500 = 1600; neto = 3000 − 1600 = 1400.
        var resultado = CalculadorDeTotales.Calcular(
            [new LineaParaCalcular(3m, 1000m, 100m, UnidadesConsumidasPorCombo: 2m, AhorroDeCombo: 1500m)]);

        Assert.Equal(3000m, resultado.Subtotal);
        Assert.Equal(1600m, resultado.Items[0].Descuento);
        Assert.Equal(1600m, resultado.DescuentoTotal);
        Assert.Equal(1400m, resultado.Items[0].Total);
        Assert.Equal(1400m, resultado.Total);
    }

    [Fact]
    public void LaLineaConTodasSusUnidadesConsumidasIgnoraLaOfertaUnitaria()
    {
        var resultado = CalculadorDeTotales.Calcular(
            [new LineaParaCalcular(2m, 3000m, 500m, UnidadesConsumidasPorCombo: 2m, AhorroDeCombo: 727m)]);

        Assert.Equal(727m, resultado.Items[0].Descuento);
        Assert.Equal(5273m, resultado.Items[0].Total);
    }

    [Fact]
    public void ElNetoDeUnaLineaConComboRedondeaLasUnidadesFueraDelComboAparteYElDescuentoEsLaDiferenciaConElBruto()
    {
        // 4 × 10 = 40; la unidad no consumida cuesta 10 − 0.5 = 9.5 → 10 y las 3 del combo cuestan
        // 30 − 4 = 26 (entero por construcción): neto 36 y descuento 40 − 36.
        var resultado = CalculadorDeTotales.Calcular(
            [new LineaParaCalcular(4m, 10m, 0.5m, UnidadesConsumidasPorCombo: 3m, AhorroDeCombo: 4m)]);

        Assert.Equal(40m, resultado.Subtotal);
        Assert.Equal(4m, resultado.Items[0].Descuento);
        Assert.Equal(36m, resultado.Items[0].Total);
    }

    [Fact]
    public void LaParteDelComboYLasUnidadesFueraDelComboSeRedondeanCadaUnaPorSuCuenta()
    {
        // Con un ahorro que no es entero (el resolvedor nunca lo entrega así) la fórmula única se
        // observa: 9.50 → 10 de la unidad fuera del combo y 10 − 0.50 = 9.50 → 10 de la del combo.
        // Redondear la línea entera, 19, daría 19.
        var resultado = CalculadorDeTotales.Calcular(
            [new LineaParaCalcular(2m, 10m, 0.50m, UnidadesConsumidasPorCombo: 1m, AhorroDeCombo: 0.50m)]);

        Assert.Equal(20m, resultado.Items[0].Total);
    }

    [Fact]
    public void TresLineasDeMilConPartesEnterasDelAhorroCobranExactamenteElPrecioDelCombo()
    {
        // Combo de 2000 sobre 3 × 1000: ahorro 1000 repartido 333 + 333 + 334.
        var resultado = CalculadorDeTotales.Calcular(
        [
            new LineaParaCalcular(1m, 1000m, 0m, null, 1m, 333m),
            new LineaParaCalcular(1m, 1000m, 0m, null, 1m, 333m),
            new LineaParaCalcular(1m, 1000m, 0m, null, 1m, 334m)
        ]);

        Assert.Equal([667m, 667m, 666m], resultado.Items.Select(i => i.Total));
        Assert.Equal(2000m, resultado.Total);
        Assert.Equal(1000m, resultado.DescuentoTotal);
    }

    [Fact]
    public void ElDescuentoDeComboAlimentaElTotalYMantieneLaInvarianteConAjusteManual()
    {
        var resultado = CalculadorDeTotales.Calcular(
        [
            new LineaParaCalcular(1m, 3000m, 0m, null, 1m, 273m),
            new LineaParaCalcular(1m, 2500m, 0m, -10m, 1m, 227m),
            new LineaParaCalcular(1m, 100m, 0m)
        ]);

        // Línea 1: 3000 − 273 = 2727. Línea 2: neto 2500 − 227 = 2273, ajuste −10 % = −227.3 → −227
        // ⇒ 2046. Línea 3: 100.
        Assert.Equal(5600m, resultado.Subtotal);
        Assert.Equal(500m, resultado.DescuentoTotal);
        Assert.Equal(227m, resultado.DescuentoManualTotal);
        Assert.Equal(4873m, resultado.Total);
        Assert.Equal(resultado.Items.Sum(i => i.Total), resultado.Total);
    }

    [Fact]
    public void LaDevolucionDeComboConAhorroConElSignoDeLaCantidadDevuelveExactamenteElImporteCobrado()
    {
        // Se devuelven 1 pancho de 3000 que en el combo costó 2727.27: bruto −3000, descuento −272.73 (el signo
        // de la cantidad, como cualquier descuento de una nota de crédito) ⇒ neto −2727.27.
        var resultado = CalculadorDeTotales.Calcular(
            [new LineaParaCalcular(-1m, 3000m, 0m, null, -1m, -272.73m)]);

        Assert.Equal(-3000m, resultado.Subtotal);
        Assert.Equal(-272.73m, resultado.Items[0].Descuento);
        Assert.Equal(-272.73m, resultado.DescuentoTotal);
        Assert.Equal(-2727.27m, resultado.Items[0].Total);
        Assert.Equal(-2727.27m, resultado.Total);
    }
}
