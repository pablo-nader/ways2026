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

    private static ReglaDeCambioDeMedios.SituacionDeLaVenta Situacion(
        bool anulada = false, string codigoTipo = "TX", short signo = 1, ResultadoFiscal? deLaVenta = null,
        ResultadoFiscal? deLaFactura = null, bool conciliada = false, bool conCuentaCorriente = false) =>
        new(anulada, codigoTipo, signo, deLaVenta, deLaFactura, conciliada, conCuentaCorriente);

    [Fact]
    public void UnaVentaSinNingunBloqueoTieneLosMediosEditables() =>
        Assert.Null(ReglaDeCambioDeMedios.MotivoDeBloqueo(Situacion()));

    [Theory]
    [InlineData(true, "TX", 1, null, null, false, false, "venta_anulada")]
    [InlineData(false, "NCX", -1, null, null, false, false, "comprobante_sin_cambio_de_medios")]
    [InlineData(false, "RC", 1, null, null, false, false, "comprobante_sin_cambio_de_medios")]
    [InlineData(false, "TX", 1, ResultadoFiscal.Aprobado, null, false, false, "venta_con_cae")]
    [InlineData(false, "TX", 1, null, ResultadoFiscal.AprobadoConObservaciones, false, false, "venta_con_cae")]
    [InlineData(false, "TX", 1, null, ResultadoFiscal.Pendiente, false, false, "venta_con_factura_en_tramite")]
    [InlineData(false, "TX", 1, null, ResultadoFiscal.Rechazado, false, false, null)]
    [InlineData(false, "TX", 1, null, null, true, false, "periodo_conciliado")]
    [InlineData(false, "TX", 1, null, null, false, true, "cambio_de_medios_con_cuenta_corriente")]
    public void CadaMotivoDeBloqueoTieneSuCodigo(
        bool anulada, string codigoTipo, short signo, ResultadoFiscal? deLaVenta, ResultadoFiscal? deLaFactura, bool conciliada,
        bool conCuentaCorriente, string? esperado) =>
        Assert.Equal(
            esperado,
            ReglaDeCambioDeMedios.MotivoDeBloqueo(
                Situacion(anulada, codigoTipo, signo, deLaVenta, deLaFactura, conciliada, conCuentaCorriente)));

    /// <summary>Con varios motivos a la vez gana el primero en el orden en que el servicio evalúa las guardas.</summary>
    [Fact]
    public void ConVariosMotivosGanaElPrimeroEnElOrdenDeLasGuardas()
    {
        var todo = Situacion(true, "NCX", -1, ResultadoFiscal.Aprobado, ResultadoFiscal.Pendiente, true, true);

        Assert.Equal("venta_anulada", ReglaDeCambioDeMedios.MotivoDeBloqueo(todo));
        Assert.Equal("comprobante_sin_cambio_de_medios", ReglaDeCambioDeMedios.MotivoDeBloqueo(todo with { Anulada = false }));
        Assert.Equal(
            "venta_con_cae",
            ReglaDeCambioDeMedios.MotivoDeBloqueo(todo with { Anulada = false, CodigoTipo = "TX", Signo = 1 }));
        Assert.Equal(
            "periodo_conciliado",
            ReglaDeCambioDeMedios.MotivoDeBloqueo(
                todo with { Anulada = false, CodigoTipo = "TX", Signo = 1, ResultadoDeLaVenta = null, ResultadoDeLaFactura = null }));
    }

    [Theory]
    [InlineData("2026-10-04", "2026-10-02", "2026-10-02")]
    [InlineData("2026-10-01", "2026-10-02", "2026-10-01")]
    [InlineData("2026-10-01", null, "2026-10-01")]
    public void ElLibroAsientaLaMenorEntreElCierreDelTurnoYLaFechaDeLaVenta(string venta, string? cierre, string esperada) =>
        Assert.Equal(
            DateTimeOffset.Parse(esperada),
            ReglaDeCambioDeMedios.FechaQueCuentaParaElLibro(
                DateTimeOffset.Parse(venta), cierre is null ? null : DateTimeOffset.Parse(cierre)));
}
