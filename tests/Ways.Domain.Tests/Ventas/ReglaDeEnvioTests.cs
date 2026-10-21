using Ways.Domain.Common;
using Ways.Domain.Ventas;

namespace Ways.Domain.Tests.Ventas;

/// <summary>
/// <see cref="ReglaDeEnvio"/> — pura, sin base de datos. Cada cláusula de
/// <see cref="ReglaDeEnvio.ExigirClienteConDomicilio"/> tiene un caso propio, así que borrar cualquiera la rompe.
/// </summary>
public class ReglaDeEnvioTests
{
    [Fact]
    public void UnClienteIdentificadoConDomicilioEsValido()
    {
        ReglaDeEnvio.ExigirClienteConDomicilio(esConsumidorFinal: false, domicilio: "Av. Siempreviva 742");
    }

    [Fact]
    public void ElConsumidorFinalNoAdmiteEnvioAunqueTengaDomicilio()
    {
        var error = Assert.Throws<ErrorDominio>(
            () => ReglaDeEnvio.ExigirClienteConDomicilio(esConsumidorFinal: true, domicilio: "Av. Siempreviva 742"));

        Assert.Equal("envio_requiere_cliente", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t \n")]
    public void UnClienteSinDomicilioONadaMasQueEspaciosSeRechaza(string? domicilio)
    {
        var error = Assert.Throws<ErrorDominio>(
            () => ReglaDeEnvio.ExigirClienteConDomicilio(esConsumidorFinal: false, domicilio));

        Assert.Equal("envio_requiere_domicilio", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public void ElConsumidorFinalSinDomicilioSeRechazaPorElClienteNoPorElDomicilio()
    {
        var error = Assert.Throws<ErrorDominio>(
            () => ReglaDeEnvio.ExigirClienteConDomicilio(esConsumidorFinal: true, domicilio: null));

        Assert.Equal("envio_requiere_cliente", error.Codigo);
    }

    [Fact]
    public void SinImporteDelDispositivoNoHayNadaQueExigir()
    {
        ReglaDeEnvio.ExigirImporteDelDispositivo(importe: null, conEnvio: false, tieneNumeroPreasignado: false);
        ReglaDeEnvio.ExigirImporteDelDispositivo(importe: null, conEnvio: true, tieneNumeroPreasignado: false);
    }

    [Theory]
    [InlineData(true, true, 0)]
    [InlineData(true, true, 1500.5)]
    [InlineData(true, true, 999_999_999_999.99)]
    public void ElImporteDelDispositivoValidoDeUnaVentaConEnvioYNumeroPreasignadoSeAdmite(bool conEnvio, bool preasignado, double importe)
    {
        ReglaDeEnvio.ExigirImporteDelDispositivo((decimal)importe, conEnvio, preasignado);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void ElImporteDelDispositivoFueraDeUnaVentaConEnvioYNumeroPreasignadoSeRechaza(bool conEnvio, bool preasignado)
    {
        var error = Assert.Throws<ErrorDominio>(() => ReglaDeEnvio.ExigirImporteDelDispositivo(1500m, conEnvio, preasignado));

        Assert.Equal("importe_envio_no_admitido", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1500.001)]
    [InlineData(1000000000000.00)]
    public void ElImporteDelDispositivoNegativoConDemasiadosDecimalesOQueNoEntraSeRechaza(double importe)
    {
        var error = Assert.Throws<ErrorDominio>(() => ReglaDeEnvio.ExigirImporteDelDispositivo((decimal)importe, true, true));

        Assert.Equal("importe_envio_invalido", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public void LaLineaDeEnvioPasaPorElCalculadorSinDescuentoNiAjusteYSumaAlTotalDelDocumento()
    {
        var totales = CalculadorDeTotales.Calcular(
        [
            new LineaParaCalcular(2m, 1000m, 100m),
            ReglaDeEnvio.LineaParaCalcular(1500m)
        ]);

        var envio = totales.Items[1];
        Assert.Equal(1m, envio.Cantidad);
        Assert.Equal(1500m, envio.PrecioUnitario);
        Assert.Equal(0m, envio.Descuento);
        Assert.Equal(0m, envio.AjusteManual);
        Assert.Equal(1500m, envio.Total);

        Assert.Equal(1800m, totales.Items[0].Total);
        Assert.Equal(3500m, totales.Subtotal);
        Assert.Equal(200m, totales.DescuentoTotal);
        Assert.Equal(3300m, totales.Total);
    }
}
