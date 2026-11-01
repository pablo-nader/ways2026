using Ways.Domain.Common;
using Ways.Domain.MercadoPago;

namespace Ways.Domain.Tests.MercadoPago;

public class ReglaDeCobroEnVentaTests
{
    private const int PuntoVenta = 4;
    private const long Numero = 77;
    private const int Medio = 9;
    private const decimal Importe = 150.50m;

    private static CobroParaVender Cobro(
        int idPuntoVenta = PuntoVenta, long numero = Numero, int idMedio = Medio, string estado = EstadosDeCobroMercadoPago.Aprobado,
        decimal? importePagado = Importe, int? idComprobante = null) =>
        new(idPuntoVenta, numero, idMedio, estado, importePagado, "PAY1", idComprobante);

    private static void Exigir(CobroParaVender cobro, int idPuntoVenta = PuntoVenta, long numero = Numero, int idMedio = Medio, decimal importe = Importe) =>
        ReglaDeCobroEnVenta.ExigirCobroUtilizable(cobro, idPuntoVenta, numero, idMedio, importe);

    private static void AssertError(Action accion, string codigo, int estado)
    {
        var error = Assert.Throws<ErrorDominio>(accion);
        Assert.Equal(codigo, error.Codigo);
        Assert.Equal(estado, error.EstadoHttp);
    }

    [Fact]
    public void UnCobroAprobadoDeLaMismaVentaPorElMismoImporteSinVentaSirve() => Exigir(Cobro());

    [Fact]
    public void UnCobroDeOtroPuntoDeVentaEsDeOtraVenta() =>
        AssertError(() => Exigir(Cobro(idPuntoVenta: PuntoVenta + 1)), CodigosDePagoConMercadoPago.DeOtraVenta, 409);

    [Fact]
    public void UnCobroDeOtroNumeroEsDeOtraVenta() =>
        AssertError(() => Exigir(Cobro(numero: Numero + 1)), CodigosDePagoConMercadoPago.DeOtraVenta, 409);

    [Fact]
    public void UnCobroDeOtroMedioEsDeOtraVenta() =>
        AssertError(() => Exigir(Cobro(idMedio: Medio + 1)), CodigosDePagoConMercadoPago.DeOtraVenta, 409);

    [Theory]
    [InlineData(EstadosDeCobroMercadoPago.Creando)]
    [InlineData(EstadosDeCobroMercadoPago.Pendiente)]
    [InlineData(EstadosDeCobroMercadoPago.Cancelado)]
    [InlineData(EstadosDeCobroMercadoPago.Expirado)]
    [InlineData(EstadosDeCobroMercadoPago.Devuelto)]
    public void UnCobroQueNoEstaAprobadoNoSirve(string estado) =>
        AssertError(() => Exigir(Cobro(estado: estado)), CodigosDePagoConMercadoPago.NoAprobado, 409);

    [Fact]
    public void UnCobroYaLigadoAUnaVentaNoSirve() =>
        AssertError(() => Exigir(Cobro(idComprobante: 12)), CodigosDePagoConMercadoPago.YaUsado, 409);

    [Theory]
    [InlineData(150.49)]
    [InlineData(150.51)]
    public void UnCobroPorOtroImporteNoSirve(double pagado) =>
        AssertError(() => Exigir(Cobro(importePagado: (decimal)pagado)), CodigosDePagoConMercadoPago.ImporteDistinto, 409);

    [Fact]
    public void UnCobroSinImportePagadoNoSirve() =>
        AssertError(() => Exigir(Cobro(importePagado: null)), CodigosDePagoConMercadoPago.ImporteDistinto, 409);

    [Fact]
    public void ElImporteDelPagoSeComparaContraLoPagadoYNoContraLoPedido() =>
        AssertError(() => Exigir(Cobro(), importe: Importe + 1m), CodigosDePagoConMercadoPago.ImporteDistinto, 409);

    [Fact]
    public void ElPuntoDeVentaSeJuzgaAntesQueElEstado() =>
        AssertError(
            () => Exigir(Cobro(idPuntoVenta: PuntoVenta + 1, estado: EstadosDeCobroMercadoPago.Pendiente)),
            CodigosDePagoConMercadoPago.DeOtraVenta, 409);

    [Fact]
    public void UnMedioConProveedorSinCobroSeRechaza() =>
        AssertError(
            () => ReglaDeCobroEnVenta.ExigirFormaDelPago("mercadopago_qr", null, null, traeNumeroPreasignado: true),
            CodigosDePagoConMercadoPago.Requerido, 400);

    [Fact]
    public void UnMedioSinProveedorConCobroSeRechaza() =>
        AssertError(
            () => ReglaDeCobroEnVenta.ExigirFormaDelPago(null, 5, null, traeNumeroPreasignado: true),
            CodigosDePagoConMercadoPago.EnMedioSinProveedor, 400);

    [Fact]
    public void UnCobroSinNumeroPreasignadoSeRechaza() =>
        AssertError(
            () => ReglaDeCobroEnVenta.ExigirFormaDelPago("mercadopago_qr", 5, null, traeNumeroPreasignado: false),
            CodigosDePagoConMercadoPago.SinNumeroPreasignado, 400);

    [Fact]
    public void UnCobroConReferenciaDelClienteSeRechaza() =>
        AssertError(
            () => ReglaDeCobroEnVenta.ExigirFormaDelPago("mercadopago_qr", 5, "A-1", traeNumeroPreasignado: true),
            CodigosDePagoConMercadoPago.ReferenciaNoAdmitida, 400);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void UnCobroConReferenciaVaciaSeAcepta(string? referencia) =>
        ReglaDeCobroEnVenta.ExigirFormaDelPago("mercadopago_qr", 5, referencia, traeNumeroPreasignado: true);

    [Fact]
    public void UnMedioSinProveedorSinCobroSeAcepta() =>
        ReglaDeCobroEnVenta.ExigirFormaDelPago(null, null, "cupon", traeNumeroPreasignado: false);

    [Fact]
    public void UnaDevolucionPuedeDevolverPorUnMedioConProveedorPeroNoLlevaCobro()
    {
        ReglaDeCobroEnVenta.ExigirFormaDelPago("mercadopago_qr", null, null, traeNumeroPreasignado: true, esDevolucion: true);
        AssertError(
            () => ReglaDeCobroEnVenta.ExigirFormaDelPago("mercadopago_qr", 5, null, traeNumeroPreasignado: true, esDevolucion: true),
            CodigosDePagoConMercadoPago.NoAdmitidoEnDevolucion, 400);
    }

    [Fact]
    public void UnMismoCobroEnDosPagosSeRechaza() =>
        AssertError(() => ReglaDeCobroEnVenta.ExigirSinCobrosRepetidos([5, 6, 5]), CodigosDePagoConMercadoPago.Repetido, 400);

    [Fact]
    public void CobrosDistintosOSinCobrosSeAceptan()
    {
        ReglaDeCobroEnVenta.ExigirSinCobrosRepetidos([5, 6]);
        ReglaDeCobroEnVenta.ExigirSinCobrosRepetidos([]);
    }
}
