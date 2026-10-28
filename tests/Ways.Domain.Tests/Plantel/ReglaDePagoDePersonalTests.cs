using Ways.Domain.Common;
using Ways.Domain.Pagos;
using Ways.Domain.Plantel;

namespace Ways.Domain.Tests.Plantel;

public class ReglaDePagoDePersonalTests
{
    private static LineaDePagoDePersonal Linea(decimal importe, int idMedioPago = 1, string? referencia = null) =>
        new(idMedioPago, importe, referencia);

    private static ErrorDominio Rechazo(
        decimal? descuento, IReadOnlyList<LineaDePagoDePersonal>? lineas, OrigenFondosPago? origen, string? detalle = null) =>
        Assert.Throws<ErrorDominio>(() => ReglaDePagoDePersonal.Planificar(descuento, lineas, origen, detalle));

    [Fact]
    public void ElTotalEsElDescuentoMasLaSumaDeLasLineasDeDinero()
    {
        var plan = ReglaDePagoDePersonal.Planificar(
            500.25m, [Linea(1000m), Linea(250.50m, idMedioPago: 2)], OrigenFondosPago.Tesoreria, null);

        Assert.Equal(1750.75m, plan.Total);
        Assert.Equal(500.25m, plan.DescuentoDeCuentaCorriente);
        Assert.Equal(2, plan.Lineas.Count);
        Assert.Equal(OrigenFondosPago.Tesoreria, plan.OrigenFondos);
    }

    [Fact]
    public void UnPagoSoloDeDescuentoNoLlevaOrigenNiLineas()
    {
        var plan = ReglaDePagoDePersonal.Planificar(300m, [], null, null);

        Assert.Equal(300m, plan.Total);
        Assert.Empty(plan.Lineas);
        Assert.Null(plan.OrigenFondos);
    }

    [Fact]
    public void UnPagoSoloDeDineroNoLlevaDescuento()
    {
        var plan = ReglaDePagoDePersonal.Planificar(null, [Linea(80m)], OrigenFondosPago.CajaTurno, null);

        Assert.Equal(80m, plan.Total);
        Assert.Null(plan.DescuentoDeCuentaCorriente);
    }

    [Fact]
    public void LasLineasDelMismoMedioConviven()
    {
        var plan = ReglaDePagoDePersonal.Planificar(
            null, [Linea(10m), Linea(20m)], OrigenFondosPago.Tesoreria, null);

        Assert.Equal(30m, plan.Total);
    }

    [Fact]
    public void ElDetalleYLaReferenciaSeRecortanYLosBlancosQuedanNulos()
    {
        var plan = ReglaDePagoDePersonal.Planificar(
            null, [Linea(10m, referencia: "  op-123 "), Linea(5m, referencia: "   ")], OrigenFondosPago.Tesoreria, "  adelanto  ");

        Assert.Equal("adelanto", plan.Detalle);
        Assert.Equal("op-123", plan.Lineas[0].Referencia);
        Assert.Null(plan.Lineas[1].Referencia);
    }

    [Fact]
    public void UnDetalleEnBlancoQuedaNulo()
    {
        Assert.Null(ReglaDePagoDePersonal.Planificar(5m, null, null, "   ").Detalle);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnPagoSinDescuentoNiLineasSeRechaza(bool conListaVacia)
    {
        var error = Rechazo(null, conListaVacia ? [] : null, null);

        Assert.Equal("pago_personal_vacio", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public void ConLineasDeDineroElOrigenEsObligatorio()
    {
        var error = Rechazo(null, [Linea(10m)], null);

        Assert.Equal("pago_personal_origen_requerido", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Theory]
    [InlineData(OrigenFondosPago.Tesoreria)]
    [InlineData(OrigenFondosPago.CajaTurno)]
    public void SinLineasDeDineroUnOrigenSeRechaza(OrigenFondosPago origen)
    {
        var error = Rechazo(100m, [], origen);

        Assert.Equal("pago_personal_origen_sin_lineas", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(10.005)]
    [InlineData(1000000000000)]
    public void UnDescuentoNoPositivoConMasDeDosDecimalesOFueraDeRangoSeRechaza(double descuento)
    {
        var error = Rechazo((decimal)descuento, [], null);

        Assert.Equal("pago_personal_descuento_invalido", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(0.001)]
    [InlineData(1000000000000)]
    public void UnaLineaNoPositivaConMasDeDosDecimalesOFueraDeRangoSeRechaza(double importe)
    {
        var error = Rechazo(null, [Linea(10m), Linea((decimal)importe)], OrigenFondosPago.Tesoreria);

        Assert.Equal("pago_personal_importe_invalido", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public void ElMaximoExactoPorLineaSeAceptaYSuperarloEnLaSumaSeRechaza()
    {
        var exacto = ReglaDePagoDePersonal.Planificar(
            null, [Linea(ReglaDeMovimientoDePersonal.ImporteMaximo)], OrigenFondosPago.Tesoreria, null);
        Assert.Equal(ReglaDeMovimientoDePersonal.ImporteMaximo, exacto.Total);

        var error = Rechazo(1m, [Linea(ReglaDeMovimientoDePersonal.ImporteMaximo)], OrigenFondosPago.Tesoreria);

        Assert.Equal("importe_fuera_de_rango", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public void LaReferenciaMuyLargaSeRechazaYElLimiteExactoSeAcepta()
    {
        var exacta = ReglaDePagoDePersonal.Planificar(
            null, [Linea(1m, referencia: new string('r', ReglaDePagoDePersonal.LongitudMaximaDeReferencia))],
            OrigenFondosPago.Tesoreria, null);
        Assert.Equal(ReglaDePagoDePersonal.LongitudMaximaDeReferencia, exacta.Lineas[0].Referencia!.Length);

        var error = Rechazo(
            null, [Linea(1m, referencia: new string('r', ReglaDePagoDePersonal.LongitudMaximaDeReferencia + 1))],
            OrigenFondosPago.Tesoreria);

        Assert.Equal("pago_personal_referencia_muy_larga", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public void ElDetalleMuyLargoSeRechaza()
    {
        var error = Rechazo(1m, [], null, new string('d', ReglaDeMovimientoDePersonal.LongitudMaximaDetalle + 1));

        Assert.Equal("detalle_muy_largo", error.Codigo);
    }

    [Fact]
    public void ElMaximoDeLineasSeAceptaYUnaMasSeRechaza()
    {
        var lineas = Enumerable.Range(1, ReglaDePagoDePersonal.MaximoDeLineas).Select(_ => Linea(1m)).ToList();
        Assert.Equal(
            ReglaDePagoDePersonal.MaximoDeLineas,
            ReglaDePagoDePersonal.Planificar(null, lineas, OrigenFondosPago.Tesoreria, null).Lineas.Count);

        lineas.Add(Linea(1m));
        var error = Rechazo(null, lineas, OrigenFondosPago.Tesoreria);

        Assert.Equal("pago_personal_demasiadas_lineas", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public void UnaLineaNulaSeRechazaComoImporteInvalidoSinRomper()
    {
        var error = Rechazo(null, [Linea(1m), null!], OrigenFondosPago.Tesoreria);

        Assert.Equal("pago_personal_importe_invalido", error.Codigo);
    }

    [Theory]
    [InlineData(7, true)]
    [InlineData(-1, true)]
    [InlineData(2, false)]
    public void UnOrigenFueraDelEnumSeRechazaConSuPropioCodigoAntesQueCualquierOtraRegla(int valor, bool conLineas)
    {
        var error = Rechazo(null, conLineas ? [Linea(10m)] : [], (OrigenFondosPago)valor);

        Assert.Equal("pago_personal_origen_invalido", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    private static PlanDePagoDePersonal Plan(decimal? descuento, OrigenFondosPago? origen) =>
        ReglaDePagoDePersonal.Planificar(descuento, origen is null ? [] : [Linea(10m)], origen, null);

    [Theory]
    [InlineData(null, OrigenFondosPago.Tesoreria, 7, null)]
    [InlineData(null, OrigenFondosPago.Tesoreria, 7, 3)]
    [InlineData(50.0, OrigenFondosPago.Tesoreria, 7, 3)]
    [InlineData(null, OrigenFondosPago.CajaTurno, null, 3)]
    [InlineData(50.0, OrigenFondosPago.CajaTurno, null, 3)]
    [InlineData(50.0, null, null, 3)]
    public void LasUbicacionesLegalesSeAceptan(double? descuento, OrigenFondosPago? origen, int? idEmpresa, int? idPuntoVenta)
    {
        ReglaDePagoDePersonal.ExigirUbicacion(Plan((decimal?)descuento, origen), idEmpresa, idPuntoVenta);
    }

    [Fact]
    public void LaTesoreriaExigeLaEmpresa()
    {
        var error = Assert.Throws<ErrorDominio>(
            () => ReglaDePagoDePersonal.ExigirUbicacion(Plan(null, OrigenFondosPago.Tesoreria), null, 3));

        Assert.Equal("pago_personal_empresa_requerida", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Theory]
    [InlineData(null, OrigenFondosPago.CajaTurno)]
    [InlineData(50.0, OrigenFondosPago.CajaTurno)]
    [InlineData(50.0, null)]
    public void FueraDeLaTesoreriaLaEmpresaSeRechazaPorqueSaleDelPuntoDeVenta(double? descuento, OrigenFondosPago? origen)
    {
        var error = Assert.Throws<ErrorDominio>(
            () => ReglaDePagoDePersonal.ExigirUbicacion(Plan((decimal?)descuento, origen), 7, 3));

        Assert.Equal("pago_personal_empresa_no_aplica", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Theory]
    [InlineData(null, OrigenFondosPago.CajaTurno, null)]
    [InlineData(50.0, null, null)]
    [InlineData(50.0, OrigenFondosPago.Tesoreria, 7)]
    public void ElCajonYElDescuentoExigenElPuntoDeVenta(double? descuento, OrigenFondosPago? origen, int? idEmpresa)
    {
        var error = Assert.Throws<ErrorDominio>(
            () => ReglaDePagoDePersonal.ExigirUbicacion(Plan((decimal?)descuento, origen), idEmpresa, null));

        Assert.Equal("pago_personal_punto_venta_requerido", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }
}
