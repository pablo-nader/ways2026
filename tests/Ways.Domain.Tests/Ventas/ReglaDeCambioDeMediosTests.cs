using Ways.Domain.Catalogos;
using Ways.Domain.Common;
using Ways.Domain.Fiscal;
using Ways.Domain.Ventas;

namespace Ways.Domain.Tests.Ventas;

public class ReglaDeCambioDeMediosTests
{
    private static CobroDeVenta Cobro(
        int idMedio, decimal importe, decimal vuelto = 0m, string? referencia = null,
        ComportamientoMedioPago comportamiento = ComportamientoMedioPago.Electronico) =>
        new(idMedio, comportamiento, importe, vuelto, referencia);

    private static string Codigo(Action accion) => Assert.Throws<ErrorDominio>(accion).Codigo;

    [Theory]
    [InlineData("RC", 1)]
    [InlineData("NCX", -1)]
    [InlineData("NCB", -1)]
    public void ElReciboYLasDevolucionesNoAdmitenElCambio(string codigo, short signo) =>
        Assert.Equal(
            "comprobante_sin_cambio_de_medios",
            Codigo(() => ReglaDeCambioDeMedios.ExigirComprobanteAdmitido(codigo, signo)));

    [Fact]
    public void UnTicketAdmiteElCambio() =>
        ReglaDeCambioDeMedios.ExigirComprobanteAdmitido("TX", 1);

    [Theory]
    [InlineData(ResultadoFiscal.Aprobado, null, "venta_con_cae")]
    [InlineData(ResultadoFiscal.AprobadoConObservaciones, null, "venta_con_cae")]
    [InlineData(null, ResultadoFiscal.Aprobado, "venta_con_cae")]
    [InlineData(null, ResultadoFiscal.AprobadoConObservaciones, "venta_con_cae")]
    [InlineData(null, ResultadoFiscal.Pendiente, "venta_con_factura_en_tramite")]
    [InlineData(ResultadoFiscal.Pendiente, null, "venta_con_factura_en_tramite")]
    public void UnaFacturaAprobadaOEnTramiteRechazaElCambio(
        ResultadoFiscal? deLaVenta, ResultadoFiscal? deLaFactura, string esperado) =>
        Assert.Equal(esperado, Codigo(() => ReglaDeCambioDeMedios.ExigirSinFactura(deLaVenta, deLaFactura)));

    [Fact]
    public void SinFacturaOConLaFacturaRechazadaElCambioSigue()
    {
        ReglaDeCambioDeMedios.ExigirSinFactura(null, null);
        ReglaDeCambioDeMedios.ExigirSinFactura(null, ResultadoFiscal.Rechazado);
    }

    [Fact]
    public void LaCuentaCorrienteSeRechazaComoMedioViejoYComoMedioNuevo()
    {
        var cc = Cobro(9, 100m, comportamiento: ComportamientoMedioPago.CuentaCorriente);

        Assert.Equal("cambio_de_medios_con_cuenta_corriente", Codigo(() =>
            ReglaDeCambioDeMedios.ExigirSinCuentaCorriente([cc], [Cobro(1, 100m)])));
        Assert.Equal("cambio_de_medios_con_cuenta_corriente", Codigo(() =>
            ReglaDeCambioDeMedios.ExigirSinCuentaCorriente([Cobro(1, 100m)], [cc])));
        ReglaDeCambioDeMedios.ExigirSinCuentaCorriente([Cobro(1, 100m)], [Cobro(2, 100m)]);
    }

    [Fact]
    public void LoCobradoSeComparaNetoDelVuelto()
    {
        // 1000 entregados con 200 de vuelto cobran 800: lo mismo que 800 con tarjeta.
        ReglaDeCambioDeMedios.ExigirMismoTotalYAlgunCambio(
            [Cobro(1, 1000m, 200m, comportamiento: ComportamientoMedioPago.Efectivo)], [Cobro(2, 800m)]);

        Assert.Equal("total_de_cobros_distinto", Codigo(() =>
            ReglaDeCambioDeMedios.ExigirMismoTotalYAlgunCambio([Cobro(1, 800m)], [Cobro(2, 799m)])));
        Assert.Equal("total_de_cobros_distinto", Codigo(() =>
            ReglaDeCambioDeMedios.ExigirMismoTotalYAlgunCambio([Cobro(1, 1000m, 200m)], [Cobro(2, 1000m)])));
    }

    [Fact]
    public void ElMismoConjuntoEnOtroOrdenNoEsUnCambioPeroOtraReferenciaSi()
    {
        Assert.Equal("cambio_de_medios_sin_diferencia", Codigo(() =>
            ReglaDeCambioDeMedios.ExigirMismoTotalYAlgunCambio(
                [Cobro(1, 300m), Cobro(2, 700m, referencia: "A")], [Cobro(2, 700m, referencia: "A"), Cobro(1, 300m)])));

        ReglaDeCambioDeMedios.ExigirMismoTotalYAlgunCambio([Cobro(2, 700m, referencia: "A")], [Cobro(2, 700m, referencia: "B")]);
    }

    [Fact]
    public void LaVariacionDeBrutoUsaElImporteEntregadoYOmiteLosMediosQueNoCambian()
    {
        var variacion = ReglaDeCambioDeMedios.VariacionDeBrutoPorMedio(
            [Cobro(1, 1000m, 200m), Cobro(2, 500m)],
            [Cobro(2, 500m), Cobro(3, 800m)]);

        Assert.Equal(new Dictionary<int, decimal> { [1] = -1000m, [3] = 800m }, variacion);
    }
}
