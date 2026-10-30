using Ways.Domain.Articulos;

namespace Ways.Domain.Tests.Articulos;

public class CalculadorDeCostoNominalTests
{
    [Fact]
    public void PorUnidadConIvaIncluidoSinDescuentoEsElCostoDeLista() =>
        Assert.Equal(121m, CalculadorDeCostoNominal.Calcular(121m, CostoListaPor.Unidad, true, null, 21m, null));

    [Fact]
    public void PorUnidadSinIvaLeSumaLaAlicuota() =>
        Assert.Equal(121m, CalculadorDeCostoNominal.Calcular(100m, CostoListaPor.Unidad, false, null, 21m, null));

    [Fact]
    public void PorBultoConIvaIncluidoDivideEntreLasUnidadesDelBulto() =>
        Assert.Equal(25m, CalculadorDeCostoNominal.Calcular(300m, CostoListaPor.Bulto, true, 12m, 21m, null));

    [Fact]
    public void PorBultoSinIvaDivideYLeSumaLaAlicuota() =>
        Assert.Equal(14.52m, CalculadorDeCostoNominal.Calcular(120m, CostoListaPor.Bulto, false, 10m, 21m, null));

    [Fact]
    public void ElDescuentoDelProveedorSeAplicaSobreElCostoConIva() =>
        Assert.Equal(90m, CalculadorDeCostoNominal.Calcular(100m, CostoListaPor.Unidad, true, null, 21m, 10m));

    [Fact]
    public void ElDescuentoAusenteEquivaleACero() =>
        Assert.Equal(
            CalculadorDeCostoNominal.Calcular(100m, CostoListaPor.Unidad, true, null, 21m, 0m),
            CalculadorDeCostoNominal.Calcular(100m, CostoListaPor.Unidad, true, null, 21m, null));

    [Fact]
    public void ElDescuentoYElIvaSeCombinanEnUnSoloCalculo() =>
        Assert.Equal(108.9m, CalculadorDeCostoNominal.Calcular(100m, CostoListaPor.Unidad, false, null, 21m, 10m));

    [Fact]
    public void RedondeaADosDecimalesConLaMitadLejosDeCero()
    {
        Assert.Equal(0.34m, CalculadorDeCostoNominal.Calcular(1.01m, CostoListaPor.Bulto, true, 3m, 21m, null));
        Assert.Equal(1.01m, CalculadorDeCostoNominal.Calcular(1.005m, CostoListaPor.Unidad, true, null, 21m, null));
    }

    [Fact]
    public void SinCostoDeListaNoHayCostoNominal()
    {
        Assert.Null(CalculadorDeCostoNominal.Calcular(null, CostoListaPor.Unidad, true, null, 21m, 10m));
        Assert.Null(CalculadorDeCostoNominal.Calcular(null, CostoListaPor.Bulto, false, null, 21m, null));
    }

    [Fact]
    public void ElCostoCeroDaCero() =>
        Assert.Equal(0m, CalculadorDeCostoNominal.Calcular(0m, CostoListaPor.Unidad, false, null, 21m, 10m));

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public void UnBultoSinUnidadesPositivasNoSePuedeCalcular(int? unidades)
    {
        decimal? valor = unidades;

        Assert.True(CalculadorDeCostoNominal.BultoSinUnidades(CostoListaPor.Bulto, valor));
        Assert.Throws<ArgumentException>(
            () => CalculadorDeCostoNominal.Calcular(100m, CostoListaPor.Bulto, true, valor, 21m, null));
    }

    [Fact]
    public void LasUnidadesPorBultoSoloImportanCuandoElCostoEsPorBulto()
    {
        Assert.False(CalculadorDeCostoNominal.BultoSinUnidades(CostoListaPor.Unidad, null));
        Assert.False(CalculadorDeCostoNominal.BultoSinUnidades(CostoListaPor.Bulto, 6m));
    }

    // ---- Impuestos internos ----------------------------------------------------------------------

    [Fact]
    public void SinIvaElImpuestoInternoSeSumaComoImporteYElIvaNoSeCalculaSobreEl() =>
        // 100 + 100 x 21 % + 5 (el IVA no grava el impuesto interno).
        Assert.Equal(126m, CalculadorDeCostoNominal.Calcular(
            100m, CostoListaPor.Unidad, false, null, 21m, null, impuestoInterno: 5m));

    [Fact]
    public void ConIvaIncluidoElCostoDeListaYaTraeTodoYElImpuestoInternoNoSeSumaOtraVez() =>
        Assert.Equal(121m, CalculadorDeCostoNominal.Calcular(
            121m, CostoListaPor.Unidad, true, null, 21m, null, impuestoInterno: 5m));

    [Fact]
    public void ElDescuentoDelProveedorSeAplicaSobreElCostoConIvaEImpuestoInterno() =>
        // (100 x 1,21 + 5) x 0,9
        Assert.Equal(113.4m, CalculadorDeCostoNominal.Calcular(
            100m, CostoListaPor.Unidad, false, null, 21m, 10m, impuestoInterno: 5m));

    [Fact]
    public void PorBultoSinIvaElImpuestoInternoSeDivideEntreLasUnidadesDelBulto() =>
        // 120 / 10 = 12; 12 x 1,21 + 50 / 10
        Assert.Equal(19.52m, CalculadorDeCostoNominal.Calcular(
            120m, CostoListaPor.Bulto, false, 10m, 21m, null, impuestoInterno: 50m));

    [Fact]
    public void SinImpuestoInternoElCalculoEsElDeSiempre() =>
        Assert.Equal(
            CalculadorDeCostoNominal.Calcular(100m, CostoListaPor.Unidad, false, null, 21m, null),
            CalculadorDeCostoNominal.Calcular(100m, CostoListaPor.Unidad, false, null, 21m, null, impuestoInterno: 0m));

    [Fact]
    public void ElImpuestoInternoSoloCuentaSiElCostoSeGuardaSinIvaAntesODespues()
    {
        Assert.False(CalculadorDeCostoNominal.CambiaAlgunaEntrada(Base(), Base() with { ImpuestoInterno = 5m }));
        Assert.True(CalculadorDeCostoNominal.CambiaAlgunaEntrada(
            Base() with { IncluyeIva = false }, Base() with { IncluyeIva = false, ImpuestoInterno = 5m }));
        Assert.False(CalculadorDeCostoNominal.CambiaAlgunaEntrada(
            Base() with { ImpuestoInterno = 5m }, Base() with { ImpuestoInterno = 8m }));
    }

    // ---- CambiaAlgunaEntrada ---------------------------------------------------------------------

    private static CalculadorDeCostoNominal.Entradas Base() =>
        new(100m, CostoListaPor.Unidad, true, 6m, 21m, 10m);

    [Fact]
    public void SinCambiosNingunaEntradaCambio() =>
        Assert.False(CalculadorDeCostoNominal.CambiaAlgunaEntrada(Base(), Base()));

    [Fact]
    public void ElCostoDeListaLaBaseElIvaIncluidoYElDescuentoSiempreCuentan()
    {
        Assert.True(CalculadorDeCostoNominal.CambiaAlgunaEntrada(Base(), Base() with { CostoLista = 101m }));
        Assert.True(CalculadorDeCostoNominal.CambiaAlgunaEntrada(Base(), Base() with { CostoLista = null }));
        Assert.True(CalculadorDeCostoNominal.CambiaAlgunaEntrada(Base(), Base() with { Por = CostoListaPor.Bulto }));
        Assert.True(CalculadorDeCostoNominal.CambiaAlgunaEntrada(Base(), Base() with { IncluyeIva = false }));
        Assert.True(CalculadorDeCostoNominal.CambiaAlgunaEntrada(Base(), Base() with { DescuentoProveedor = 5m }));
        Assert.True(CalculadorDeCostoNominal.CambiaAlgunaEntrada(Base(), Base() with { DescuentoProveedor = null }));
    }

    [Fact]
    public void LasUnidadesPorBultoSoloCuentanSiLaBaseEsBultoAntesODespues()
    {
        Assert.False(CalculadorDeCostoNominal.CambiaAlgunaEntrada(Base(), Base() with { UnidadesPorBulto = 12m }));
        Assert.True(CalculadorDeCostoNominal.CambiaAlgunaEntrada(
            Base() with { Por = CostoListaPor.Bulto }, Base() with { Por = CostoListaPor.Bulto, UnidadesPorBulto = 12m }));
    }

    [Fact]
    public void ElPorcentajeDeIvaSoloCuentaSiElCostoSeGuardaSinIvaAntesODespues()
    {
        Assert.False(CalculadorDeCostoNominal.CambiaAlgunaEntrada(Base(), Base() with { PorcentajeIva = 10.5m }));
        Assert.True(CalculadorDeCostoNominal.CambiaAlgunaEntrada(
            Base() with { IncluyeIva = false }, Base() with { IncluyeIva = false, PorcentajeIva = 10.5m }));
    }
}
