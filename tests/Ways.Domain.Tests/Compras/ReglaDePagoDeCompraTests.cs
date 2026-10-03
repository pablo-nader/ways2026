using Ways.Domain.Common;
using Ways.Domain.Compras;

namespace Ways.Domain.Tests.Compras;

public class ReglaDePagoDeCompraTests
{
    [Fact]
    public void ElSaldoPendienteDeUnaCompraConfirmadaEsElTotalMenosLoPagado()
    {
        Assert.Equal(300m, ReglaDePagoDeCompra.SaldoPendiente(EstadoCompra.Confirmada, 1000m, 700m));
    }

    [Fact]
    public void UnaCompraSobrepagadaQuedaEnCeroYNoEnNegativo()
    {
        Assert.Equal(0m, ReglaDePagoDeCompra.SaldoPendiente(EstadoCompra.Confirmada, 1000m, 1200m));
    }

    [Theory]
    [InlineData(EstadoCompra.Borrador)]
    [InlineData(EstadoCompra.Anulada)]
    public void UnaCompraQueNoEstaConfirmadaNoTieneSaldoPendiente(EstadoCompra estado)
    {
        Assert.Equal(0m, ReglaDePagoDeCompra.SaldoPendiente(estado, 1000m, 0m));
    }

    [Theory]
    [InlineData(300, 300)]
    [InlineData(0.01, 300)]
    public void UnPagoHastaElSaldoPendienteSeAcepta(double importe, double saldo)
    {
        ReglaDePagoDeCompra.ExigirPagoValido((decimal)importe, (decimal)saldo);
    }

    [Fact]
    public void UnPagoQueSuperaElSaldoPendienteSeRechazaConConflicto()
    {
        var error = Assert.Throws<ErrorDominio>(() => ReglaDePagoDeCompra.ExigirPagoValido(300.01m, 300m));

        Assert.Equal("pago_excede_saldo_pendiente", error.Codigo);
        Assert.Equal(409, error.EstadoHttp);
    }

    [Fact]
    public void UnPagoSobreUnaCompraSaldadaSeRechazaConUnCodigoPropio()
    {
        var error = Assert.Throws<ErrorDominio>(() => ReglaDePagoDeCompra.ExigirPagoValido(1m, 0m));

        Assert.Equal("compra_sin_saldo_pendiente", error.Codigo);
        Assert.Equal(409, error.EstadoHttp);
    }

    [Theory]
    [InlineData("Factura A", "0001-00000042", "Pago Factura A 0001-00000042")]
    [InlineData("Remito", null, "Pago Remito #7")]
    [InlineData("  Remito ", "  ", "Pago Remito #7")]
    [InlineData(null, "0002-00000001", "Pago compra 0002-00000001")]
    public void ElConceptoPorDefectoNombraElTipoYElNumeroODeLoContrarioElId(
        string? tipo, string? numero, string esperado)
    {
        Assert.Equal(esperado, ReglaDePagoDeCompra.ConceptoPorDefecto(tipo, numero, 7));
    }
}
