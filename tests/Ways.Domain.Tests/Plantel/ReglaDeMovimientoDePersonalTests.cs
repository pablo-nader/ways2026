using Ways.Domain.Common;
using Ways.Domain.Plantel;

namespace Ways.Domain.Tests.Plantel;

public class ReglaDeMovimientoDePersonalTests
{
    [Theory]
    [InlineData(8, 1500, 12000)]
    [InlineData(7.5, 1000, 7500)]
    [InlineData(0.25, 1200.50, 300.13)]
    [InlineData(1.5, 0.01, 0.02)]
    [InlineData(8, 0, 0)]
    public void ElImporteEsHorasPorValorHoraRedondeadoADosDecimalesAlejandoseDeCero(
        double horas, double valorHora, double esperado)
    {
        var importe = ReglaDeMovimientoDePersonal.CalcularImporteDeHoras((decimal)horas, (decimal)valorHora);

        Assert.Equal((decimal)esperado, importe);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(24.01)]
    [InlineData(25)]
    [InlineData(1.001)]
    public void LasHorasFueraDeRangoOConMasDeDosDecimalesSeRechazan(double horas)
    {
        var error = Assert.Throws<ErrorDominio>(
            () => ReglaDeMovimientoDePersonal.CalcularImporteDeHoras((decimal)horas, 1000m));

        Assert.Equal("horas_invalidas", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public void LasVeinticuatroHorasEnPuntoSeAceptan()
    {
        Assert.Equal(24000m, ReglaDeMovimientoDePersonal.CalcularImporteDeHoras(24m, 1000m));
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(10.005)]
    public void UnValorHoraNegativoOConMasDeDosDecimalesSeRechaza(double valorHora)
    {
        var error = Assert.Throws<ErrorDominio>(
            () => ReglaDeMovimientoDePersonal.CalcularImporteDeHoras(8m, (decimal)valorHora));

        Assert.Equal("valor_hora_invalido", error.Codigo);
    }

    [Fact]
    public void UnImporteQueNoEntraEnLaColumnaSeRechazaConSuCodigo()
    {
        var error = Assert.Throws<ErrorDominio>(
            () => ReglaDeMovimientoDePersonal.CalcularImporteDeHoras(2m, 999_999_999_999.99m));

        Assert.Equal("importe_fuera_de_rango", error.Codigo);
    }

    [Fact]
    public void UnAjusteConImporteCeroOSinDetalleSeRechazaConLosCodigosDeLaReglaDeCuenta()
    {
        Assert.Equal(
            "ajuste_importe_invalido",
            Assert.Throws<ErrorDominio>(() => ReglaDeMovimientoDePersonal.ValidarAjuste(0m, "corrección")).Codigo);
        Assert.Equal(
            "ajuste_detalle_requerido",
            Assert.Throws<ErrorDominio>(() => ReglaDeMovimientoDePersonal.ValidarAjuste(10m, "  abc ")).Codigo);
    }

    [Theory]
    [InlineData(10.005)]
    [InlineData(-10.005)]
    public void UnAjusteConMasDeDosDecimalesSeRechaza(double importe)
    {
        var error = Assert.Throws<ErrorDominio>(
            () => ReglaDeMovimientoDePersonal.ValidarAjuste((decimal)importe, "corrección"));

        Assert.Equal("ajuste_importe_invalido", error.Codigo);
    }

    [Fact]
    public void UnAjusteFueraDeRangoSeRechazaEnAmbosSignos()
    {
        Assert.Equal(
            "importe_fuera_de_rango",
            Assert.Throws<ErrorDominio>(
                () => ReglaDeMovimientoDePersonal.ValidarAjuste(1_000_000_000_000m, "corrección")).Codigo);
        Assert.Equal(
            "importe_fuera_de_rango",
            Assert.Throws<ErrorDominio>(
                () => ReglaDeMovimientoDePersonal.ValidarAjuste(-1_000_000_000_000m, "corrección")).Codigo);
    }

    [Fact]
    public void ElAjusteValidoDevuelveElDetalleSinEspaciosSobrantes()
    {
        Assert.Equal("corrección de carga", ReglaDeMovimientoDePersonal.ValidarAjuste(-500m, "  corrección de carga "));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    [InlineData("  turno noche ", "turno noche")]
    public void ElDetalleOpcionalSeRecortaYUnoVacioQuedaNulo(string? detalle, string? esperado)
    {
        Assert.Equal(esperado, ReglaDeMovimientoDePersonal.NormalizarDetalleOpcional(detalle));
    }

    [Fact]
    public void UnDetalleDeMasDeQuinientosCaracteresSeRechaza()
    {
        var error = Assert.Throws<ErrorDominio>(
            () => ReglaDeMovimientoDePersonal.NormalizarDetalleOpcional(new string('a', 501)));

        Assert.Equal("detalle_muy_largo", error.Codigo);
    }
}
