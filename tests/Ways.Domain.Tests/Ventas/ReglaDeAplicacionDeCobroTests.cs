using Ways.Domain.Auditoria;
using Ways.Domain.Common;
using Ways.Domain.Ventas;

namespace Ways.Domain.Tests.Ventas;

/// <summary>E7-S2: aritmética y reglas de las aplicaciones de cobros a ventas, sin base. Cada regla rechaza con el mismo
/// código que traduce <c>ManejadorDeErrores</c> para su constraint trigger.</summary>
public class ReglaDeAplicacionDeCobroTests
{
    private static ComprobanteParaAplicar Recibo(
        EstadoComprobante estado = EstadoComprobante.Emitido, string codigo = "RC", int idCliente = 7) =>
        new(1, codigo, 1, estado, idCliente);

    private static ComprobanteParaAplicar Venta(
        int id = 2, string codigo = "TX", short signo = 1, EstadoComprobante estado = EstadoComprobante.Emitido,
        int idCliente = 7) =>
        new(id, codigo, signo, estado, idCliente);

    private static string Codigo(Action accion) => Assert.Throws<ErrorDominio>(accion).Codigo;

    [Fact]
    public void ElNetoCobradoDescuentaElVueltoDeCadaCobro()
    {
        Assert.Equal(1450m, ReglaDeAplicacionDeCobro.NetoCobrado([(1000m, 50m), (500m, 0m)]));
    }

    [Fact]
    public void LaParteEnCuentaCorrienteSumaSoloLosCobrosConMedioDeCuentaCorriente()
    {
        Assert.Equal(700m, ReglaDeAplicacionDeCobro.ParteEnCuentaCorriente([(300m, false), (400m, true), (300m, true)]));
    }

    [Fact]
    public void ElDisponibleEsLoCobradoMenosLoAplicadoYCeroSiElReciboEstaAnulado()
    {
        Assert.Equal(300m, ReglaDeAplicacionDeCobro.Disponible(EstadoComprobante.Emitido, 1000m, 700m));
        Assert.Equal(0m, ReglaDeAplicacionDeCobro.Disponible(EstadoComprobante.Anulado, 1000m, 0m));
    }

    [Fact]
    public void ElSaldoPendienteEsLaParteEnCuentaCorrienteMenosLoCobradoYCeroSiLaVentaEstaAnulada()
    {
        Assert.Equal(250m, ReglaDeAplicacionDeCobro.SaldoPendiente(EstadoComprobante.Emitido, 1000m, 750m));
        Assert.Equal(0m, ReglaDeAplicacionDeCobro.SaldoPendiente(EstadoComprobante.Anulado, 1000m, 0m));
    }

    [Fact]
    public void UnPedidoVacioONuloSeRechaza()
    {
        Assert.Equal("aplicacion_sin_ventas", Codigo(() => ReglaDeAplicacionDeCobro.ExigirPedidas(null)));
        Assert.Equal("aplicacion_sin_ventas", Codigo(() => ReglaDeAplicacionDeCobro.ExigirPedidas([])));
        Assert.Equal(
            "aplicacion_sin_ventas",
            Codigo(() => ReglaDeAplicacionDeCobro.ExigirPedidas([new AplicacionDeCobroPedida(2, 10m), null])));
    }

    [Fact]
    public void MasDeVeinteVentasSeRechazanYVeintePasan()
    {
        var veinte = Enumerable.Range(1, 20).Select(i => (AplicacionDeCobroPedida?)new AplicacionDeCobroPedida(i, 1m)).ToList();
        Assert.Equal(20, ReglaDeAplicacionDeCobro.ExigirPedidas(veinte).Count);

        veinte.Add(new AplicacionDeCobroPedida(21, 1m));
        Assert.Equal("aplicacion_demasiadas_ventas", Codigo(() => ReglaDeAplicacionDeCobro.ExigirPedidas(veinte)));
    }

    [Fact]
    public void UnaVentaRepetidaSeRechaza()
    {
        Assert.Equal(
            "aplicacion_venta_repetida",
            Codigo(() => ReglaDeAplicacionDeCobro.ExigirPedidas(
                [new AplicacionDeCobroPedida(2, 10m), new AplicacionDeCobroPedida(2, 5m)])));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("10.005")]
    public void UnImporteNoPositivoOConMasDeDosDecimalesSeRechaza(string importe)
    {
        Assert.Equal(
            "aplicacion_importe_invalido",
            Codigo(() => ReglaDeAplicacionDeCobro.ExigirImportesValidos(
                [new AplicacionDeCobroPedida(2, decimal.Parse(importe, System.Globalization.CultureInfo.InvariantCulture))])));
    }

    [Fact]
    public void UnImporteConDosDecimalesPasa()
    {
        ReglaDeAplicacionDeCobro.ExigirImportesValidos([new AplicacionDeCobroPedida(2, 10.25m)]);
    }

    [Fact]
    public void ElCobroTieneQueSerUnReciboEmitido()
    {
        ReglaDeAplicacionDeCobro.ExigirRecibo(Recibo());
        Assert.Equal("aplicacion_cobro_comprobante_invalido", Codigo(() => ReglaDeAplicacionDeCobro.ExigirRecibo(Recibo(codigo: "TX"))));
        Assert.Equal(
            "aplicacion_cobro_comprobante_invalido",
            Codigo(() => ReglaDeAplicacionDeCobro.ExigirRecibo(Recibo(EstadoComprobante.Anulado))));
    }

    [Fact]
    public void LaVentaNoPuedeSerUnReciboNiUnaNotaDeCreditoNiEstarAnulada()
    {
        ReglaDeAplicacionDeCobro.ExigirVenta(Recibo(), Venta());
        Assert.Equal("aplicacion_cobro_comprobante_invalido", Codigo(() => ReglaDeAplicacionDeCobro.ExigirVenta(Recibo(), Venta(codigo: "RC"))));
        Assert.Equal(
            "aplicacion_cobro_comprobante_invalido",
            Codigo(() => ReglaDeAplicacionDeCobro.ExigirVenta(Recibo(), Venta(codigo: "NCX", signo: -1))));
        Assert.Equal(
            "aplicacion_cobro_comprobante_invalido",
            Codigo(() => ReglaDeAplicacionDeCobro.ExigirVenta(Recibo(), Venta(estado: EstadoComprobante.Anulado))));
    }

    [Fact]
    public void LaVentaTieneQueSerDelClienteDelRecibo()
    {
        Assert.Equal(
            "aplicacion_cobro_otro_cliente",
            Codigo(() => ReglaDeAplicacionDeCobro.ExigirVenta(Recibo(idCliente: 7), Venta(idCliente: 8))));
    }

    /// <summary>LA CLÁUSULA: la suma de lo pedido contra el disponible del recibo; exactamente el disponible pasa.</summary>
    [Fact]
    public void LoPedidoEnTotalNoPuedeSuperarLoDisponibleDelRecibo()
    {
        ReglaDeAplicacionDeCobro.ExigirTopes(
            100m, [(new AplicacionDeCobroPedida(2, 60m), 500m), (new AplicacionDeCobroPedida(3, 40m), 500m)]);

        Assert.Equal(
            "cobro_excede_disponible",
            Codigo(() => ReglaDeAplicacionDeCobro.ExigirTopes(
                100m, [(new AplicacionDeCobroPedida(2, 60m), 500m), (new AplicacionDeCobroPedida(3, 40.01m), 500m)])));
    }

    /// <summary>LA CLÁUSULA: lo pedido a cada venta contra su saldo pendiente; exactamente el saldo pasa.</summary>
    [Fact]
    public void LoPedidoAUnaVentaNoPuedeSuperarSuSaldoPendiente()
    {
        ReglaDeAplicacionDeCobro.ExigirTopes(1000m, [(new AplicacionDeCobroPedida(2, 250m), 250m)]);

        Assert.Equal(
            "aplicacion_excede_saldo_de_venta",
            Codigo(() => ReglaDeAplicacionDeCobro.ExigirTopes(
                1000m, [(new AplicacionDeCobroPedida(2, 100m), 500m), (new AplicacionDeCobroPedida(3, 250.01m), 250m)])));
    }

    [Fact]
    public void ElPayloadDeAplicacionEsConstruibleYDescribeElAntesYElDespues()
    {
        var (anterior, nuevo) = PayloadDeAuditoria.AplicacionDeCobro(1000m, [(5, 300m)], [(9, 200m), (5, 300m)]);

        var registro = new RegistroDeAuditoria(1, 2, AccionAuditada.CobroAplicacion, 1, anterior, nuevo);

        Assert.Equal(300m, registro.ValorAnterior!["aplicado"]);
        Assert.Equal(700m, registro.ValorAnterior!["disponible"]);
        Assert.Equal(500m, registro.ValorNuevo["aplicado"]);
        Assert.Equal(500m, registro.ValorNuevo["disponible"]);
        var aplicaciones = Assert.IsType<List<Dictionary<string, object?>>>(registro.ValorNuevo["aplicaciones"]);
        Assert.Equal([5, 9], aplicaciones.Select(a => (int)a["id_comprobante_venta"]!));
    }

    [Fact]
    public void ElPayloadDeDesaplicacionEsConstruibleYNombraElComprobanteAnulado()
    {
        var (anterior, nuevo) = PayloadDeAuditoria.DesaplicacionDeCobro(11, 5, 300m, idComprobanteAnulado: 5);

        var registro = new RegistroDeAuditoria(1, 2, AccionAuditada.CobroDesaplicacion, 1, anterior, nuevo);

        Assert.Equal(true, registro.ValorAnterior!["viva"]);
        Assert.Equal(false, registro.ValorNuevo["viva"]);
        Assert.Equal(300m, registro.ValorNuevo["importe"]);
        Assert.Equal(5, registro.ValorNuevo["id_comprobante_anulado"]);
    }
}
