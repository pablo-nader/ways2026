using Ways.Domain.Common;
using Ways.Domain.Compras;

namespace Ways.Domain.Tests.Compras;

public class ReglaDePercepcionesTests
{
    private static PercepcionDeCompra Iibb(decimal importe = 3m, decimal baseImponible = 100m, decimal alicuota = 3m) =>
        new(TiposDePercepcion.Iibb, baseImponible, alicuota, importe);

    private static PercepcionDeCompra Iva(decimal importe = 1.5m) => new(TiposDePercepcion.Iva, 100m, 1.5m, importe);

    private static ErrorDominio Rechazo(bool registraLibroIva, bool discriminaIva, params PercepcionDeCompra[] percepciones)
    {
        var error = Assert.Throws<ErrorDominio>(
            () => ReglaDePercepciones.Validar(registraLibroIva, discriminaIva, percepciones));
        Assert.Equal(400, error.EstadoHttp);
        return error;
    }

    [Fact]
    public void SinPercepcionesNoHayNadaQueValidarEnNingunTipoDeComprobante()
    {
        ReglaDePercepciones.Validar(registraLibroIva: false, discriminaIva: false, []);
    }

    [Fact]
    public void UnaFacturaQueDiscriminaAdmiteLasDosPercepciones()
    {
        ReglaDePercepciones.Validar(registraLibroIva: true, discriminaIva: true, [Iibb(), Iva()]);
    }

    [Fact]
    public void UnaFacturaQueNoDiscriminaAdmiteIibb()
    {
        ReglaDePercepciones.Validar(registraLibroIva: true, discriminaIva: false, [Iibb()]);
    }

    [Fact]
    public void UnComprobanteQueNoRegistraLibroIvaNoAdmitePercepciones()
    {
        Assert.Equal("percepciones_sin_libro_iva", Rechazo(registraLibroIva: false, discriminaIva: true, Iibb()).Codigo);
    }

    [Fact]
    public void LaPercepcionDeIvaExigeUnComprobanteQueDiscrimina()
    {
        Assert.Equal("percepcion_iva_sin_discriminar", Rechazo(registraLibroIva: true, discriminaIva: false, Iva()).Codigo);
    }

    [Fact]
    public void UnTipoDesconocidoSeRechaza()
    {
        var error = Rechazo(true, true, new PercepcionDeCompra("ganancias", 100m, 1m, 1m));

        Assert.Equal("percepcion_tipo_invalido", error.Codigo);
    }

    [Fact]
    public void ElMismoTipoDosVecesSeRechazaEnVezDeQuedarseConUna()
    {
        Assert.Equal("percepcion_duplicada", Rechazo(true, true, Iibb(), Iibb(importe: 5m)).Codigo);
    }

    [Theory]
    [InlineData(-0.01, 100, 3)]
    [InlineData(3, -1, 3)]
    public void LosImportesNegativosSeRechazan(decimal importe, decimal baseImponible, decimal alicuota)
    {
        Assert.Equal(
            "percepcion_importes_invalidos",
            Rechazo(true, true, Iibb(importe, baseImponible, alicuota)).Codigo);
    }

    [Fact]
    public void ElImporteCeroEsValidoPorqueUnaFacturaPuedeImprimirloAsi()
    {
        ReglaDePercepciones.Validar(true, true, [Iibb(importe: 0m)]);
    }

    [Theory]
    [InlineData(-0.001)]
    [InlineData(100.001)]
    public void LaAlicuotaFueraDeCeroACienSeRechaza(decimal alicuota)
    {
        Assert.Equal("percepcion_alicuota_invalida", Rechazo(true, true, Iibb(alicuota: alicuota)).Codigo);
    }

    [Fact]
    public void LaAlicuotaDeCienEsElLimiteValido()
    {
        ReglaDePercepciones.Validar(true, true, [Iibb(alicuota: 100m)]);
    }

    [Theory]
    [InlineData(3.001, 100, 3)]
    [InlineData(3, 100.005, 3)]
    [InlineData(3, 100, 3.0005)]
    public void MasDecimalesDeLosQueGuardaLaBaseSeRechazanEnVezDeRedondearseEnSilencio(
        decimal importe, decimal baseImponible, decimal alicuota)
    {
        Assert.Equal(
            "percepcion_decimales_invalidos",
            Rechazo(true, true, Iibb(importe, baseImponible, alicuota)).Codigo);
    }

    [Fact]
    public void LaAlicuotaAdmiteTresDecimales()
    {
        ReglaDePercepciones.Validar(true, true, [Iibb(alicuota: 2.512m)]);
    }
}
