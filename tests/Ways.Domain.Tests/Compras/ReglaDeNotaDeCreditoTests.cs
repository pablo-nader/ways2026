using Ways.Domain.Common;
using Ways.Domain.Compras;

namespace Ways.Domain.Tests.Compras;

public class ReglaDeNotaDeCreditoTests
{
    private const int Proveedor = 7;
    private const int Empresa = 3;

    private static FacturaAsociada Factura(
        EstadoCompra estado = EstadoCompra.Confirmada, int proveedor = Proveedor, short signo = 1,
        bool registraLibroIva = true, int empresa = Empresa) =>
        new(estado, proveedor, signo, registraLibroIva, empresa);

    private static string CodigoDelRechazo(Action accion) =>
        Assert.Throws<ErrorDominio>(accion).Codigo;

    [Theory]
    [InlineData((short)-1, true)]
    [InlineData((short)1, false)]
    public void SoloUnSignoNegativoEsNotaDeCredito(short signo, bool esperado)
    {
        Assert.Equal(esperado, ReglaDeNotaDeCredito.EsNotaDeCredito(signo));
    }

    [Fact]
    public void UnaFacturaConfirmadaDelMismoProveedorYEmpresaEsAsociable()
    {
        ReglaDeNotaDeCredito.ExigirFacturaAsociable(Factura(), 10, Proveedor, Empresa);
    }

    [Fact]
    public void LaFacturaInexistenteEsReferenciaInvalida()
    {
        Assert.Equal(
            "referencia_invalida", CodigoDelRechazo(() => ReglaDeNotaDeCredito.ExigirFacturaAsociable(null, 10, Proveedor, Empresa)));
    }

    [Fact]
    public void UnRemitoNoEsAsociable()
    {
        Assert.Equal(
            "compra_asociada_no_es_factura",
            CodigoDelRechazo(() => ReglaDeNotaDeCredito.ExigirFacturaAsociable(
                Factura(registraLibroIva: false), 10, Proveedor, Empresa)));
    }

    [Fact]
    public void OtraNotaDeCreditoNoEsAsociable()
    {
        Assert.Equal(
            "compra_asociada_no_es_factura",
            CodigoDelRechazo(() => ReglaDeNotaDeCredito.ExigirFacturaAsociable(
                Factura(signo: -1), 10, Proveedor, Empresa)));
    }

    [Theory]
    [InlineData(EstadoCompra.Borrador)]
    [InlineData(EstadoCompra.Anulada)]
    public void UnaFacturaQueNoEstaConfirmadaNoEsAsociable(EstadoCompra estado)
    {
        Assert.Equal(
            "compra_asociada_no_confirmada",
            CodigoDelRechazo(() => ReglaDeNotaDeCredito.ExigirFacturaAsociable(
                Factura(estado), 10, Proveedor, Empresa)));
    }

    [Fact]
    public void LaFacturaDeOtroProveedorNoEsAsociable()
    {
        Assert.Equal(
            "compra_asociada_de_otro_proveedor",
            CodigoDelRechazo(() => ReglaDeNotaDeCredito.ExigirFacturaAsociable(
                Factura(proveedor: Proveedor + 1), 10, Proveedor, Empresa)));
    }

    [Fact]
    public void LaFacturaDeOtraEmpresaNoEsAsociable()
    {
        Assert.Equal(
            "compra_asociada_de_otra_empresa",
            CodigoDelRechazo(() => ReglaDeNotaDeCredito.ExigirFacturaAsociable(
                Factura(empresa: Empresa + 1), 10, Proveedor, Empresa)));
    }

    [Fact]
    public void UnaNotaDeCreditoNoSeLigaAUnaOrdenDeCompra()
    {
        Assert.Equal(
            "nota_de_credito_sin_orden_de_compra",
            CodigoDelRechazo(() => ReglaDeNotaDeCredito.ExigirCoherenciaConElTipo(-1, null, 5, [false])));
    }

    [Fact]
    public void UnaNotaDeCreditoConUnaLineaQueActualizaCostoEsRechazada()
    {
        Assert.Equal(
            "nota_de_credito_no_actualiza_costo",
            CodigoDelRechazo(() => ReglaDeNotaDeCredito.ExigirCoherenciaConElTipo(-1, null, null, [null, true])));
    }

    [Fact]
    public void UnaNotaDeCreditoSinActualizaCostoExplicitoEsValida()
    {
        ReglaDeNotaDeCredito.ExigirCoherenciaConElTipo(-1, 10, null, [null, false]);
    }

    [Fact]
    public void LaAsociacionSoloExisteEnUnaNotaDeCredito()
    {
        Assert.Equal(
            "asociacion_solo_en_nota_de_credito",
            CodigoDelRechazo(() => ReglaDeNotaDeCredito.ExigirCoherenciaConElTipo(1, 10, null, [true])));
    }

    [Fact]
    public void UnaFacturaSinAsociacionPuedeActualizarCosto()
    {
        ReglaDeNotaDeCredito.ExigirCoherenciaConElTipo(1, null, 5, [true]);
    }

    [Fact]
    public void UnaNotaDeCreditoNuncaTieneSaldoPendienteNiEstadoDePago()
    {
        Assert.Equal(0m, ReglaDePagoDeCompra.SaldoPendiente(EstadoCompra.Confirmada, true, 1000m, 0m));
        Assert.Null(ReglaDePagoDeCompra.EstadoDePago(EstadoCompra.Confirmada, true, 1000m, 0m));
    }

    [Fact]
    public void UnaFacturaConservaElSaldoPendienteYElEstadoDePago()
    {
        Assert.Equal(400m, ReglaDePagoDeCompra.SaldoPendiente(EstadoCompra.Confirmada, false, 1000m, 600m));
        Assert.Equal(EstadoDePagoDeCompra.Pendiente, ReglaDePagoDeCompra.EstadoDePago(EstadoCompra.Confirmada, false, 1000m, 600m));
    }
}
