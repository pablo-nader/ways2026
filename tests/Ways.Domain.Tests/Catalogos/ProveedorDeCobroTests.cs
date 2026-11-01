using Ways.Domain.Catalogos;
using Ways.Domain.Common;

namespace Ways.Domain.Tests.Catalogos;

public class ProveedorDeCobroTests
{
    private const string Qr = ProveedorDeCobro.MercadoPagoQr;

    [Fact]
    public void SinEnviarNadaUnMedioElectronicoConservaElGuardado()
    {
        Assert.Equal(Qr, ProveedorDeCobro.Resolver(ComportamientoMedioPago.Electronico, Qr, false, null));
        Assert.Null(ProveedorDeCobro.Resolver(ComportamientoMedioPago.Electronico, null, false, null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void UnValorEnviadoVacioQuitaElProveedor(string? enviado)
    {
        Assert.Null(ProveedorDeCobro.Resolver(ComportamientoMedioPago.Electronico, Qr, true, enviado));
    }

    [Theory]
    [InlineData("mercadopago_qr")]
    [InlineData("mercadopago_point")]
    [InlineData("  mercadopago_point  ")]
    public void UnProveedorConocidoSeGuardaSinEspacios(string enviado)
    {
        var resuelto = ProveedorDeCobro.Resolver(ComportamientoMedioPago.Electronico, null, true, enviado);

        Assert.Equal(enviado.Trim(), resuelto);
    }

    [Theory]
    [InlineData("paypal")]
    [InlineData("MERCADOPAGO_QR")]
    public void UnProveedorDesconocidoSeRechazaAunqueElMedioNoSeaElectronico(string enviado)
    {
        var error = Assert.Throws<ErrorDominio>(
            () => ProveedorDeCobro.Resolver(ComportamientoMedioPago.Efectivo, null, true, enviado));

        Assert.Equal("proveedor_cobro_invalido", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Theory]
    [InlineData(ComportamientoMedioPago.Efectivo)]
    [InlineData(ComportamientoMedioPago.CuentaCorriente)]
    public void EnviarUnProveedorAUnMedioQueNoEsElectronicoSeRechaza(ComportamientoMedioPago comportamiento)
    {
        var error = Assert.Throws<ErrorDominio>(() => ProveedorDeCobro.Resolver(comportamiento, null, true, Qr));

        Assert.Equal("proveedor_solo_electronico", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Theory]
    [InlineData(ComportamientoMedioPago.Efectivo)]
    [InlineData(ComportamientoMedioPago.CuentaCorriente)]
    public void SinEnviarNadaUnMedioQueDejaDeSerElectronicoPierdeElProveedor(ComportamientoMedioPago comportamiento)
    {
        Assert.Null(ProveedorDeCobro.Resolver(comportamiento, Qr, false, null));
    }

    [Theory]
    [InlineData(ComportamientoMedioPago.Efectivo)]
    [InlineData(ComportamientoMedioPago.CuentaCorriente)]
    public void QuitarElProveedorDeUnMedioQueNoEsElectronicoEsValido(ComportamientoMedioPago comportamiento)
    {
        Assert.Null(ProveedorDeCobro.Resolver(comportamiento, Qr, true, null));
    }
}
