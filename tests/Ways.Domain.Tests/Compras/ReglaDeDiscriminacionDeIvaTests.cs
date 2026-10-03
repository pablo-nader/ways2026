using Ways.Domain.Common;
using Ways.Domain.Compras;

namespace Ways.Domain.Tests.Compras;

public class ReglaDeDiscriminacionDeIvaTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(true)]
    public void UnaFacturaQueDiscriminaFijaElValorDelTipo(bool? solicitado)
    {
        Assert.True(ReglaDeDiscriminacionDeIva.Resolver(registraLibroIva: true, discriminaIvaDelTipo: true, solicitado));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public void UnaFacturaQueNoDiscriminaFijaElValorDelTipo(bool? solicitado)
    {
        Assert.False(ReglaDeDiscriminacionDeIva.Resolver(registraLibroIva: true, discriminaIvaDelTipo: false, solicitado));
    }

    [Theory]
    [InlineData(true, false, "Este tipo de comprobante siempre discrimina IVA.")]
    [InlineData(false, true, "Este tipo de comprobante nunca discrimina IVA.")]
    public void PedirLoContrarioAlDeUnaFacturaSeRechazaEnVezDeCorregirseEnSilencio(
        bool discriminaDelTipo, bool solicitado, string mensaje)
    {
        var error = Assert.Throws<ErrorDominio>(() =>
            ReglaDeDiscriminacionDeIva.Resolver(registraLibroIva: true, discriminaDelTipo, solicitado));

        Assert.Equal("discrimina_iva_incompatible", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
        Assert.Equal(mensaje, error.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnComprobanteNoFiscalDejaLaDecisionAQuienLoCarga(bool solicitado)
    {
        Assert.Equal(
            solicitado,
            ReglaDeDiscriminacionDeIva.Resolver(registraLibroIva: false, discriminaIvaDelTipo: false, solicitado));
    }

    [Fact]
    public void UnComprobanteNoFiscalSinPedidoTomaElValorDelTipo()
    {
        Assert.False(ReglaDeDiscriminacionDeIva.Resolver(registraLibroIva: false, discriminaIvaDelTipo: false, solicitado: null));
    }
}
