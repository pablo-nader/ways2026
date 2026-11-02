using Ways.Domain.MercadoPago;

namespace Ways.Domain.Tests.MercadoPago;

/// <summary>
/// El cotejo de las filas del reporte con los cobros de Ways (E6): el orden de la búsqueda (EXTERNAL_REFERENCE antes que SOURCE_ID), cada
/// resultado, qué filas aplican al cobro, y los candidatos de una aprobación manual (importe, ventana de un día, caja).
/// </summary>
public class ConciliadorMercadoPagoTests
{
    private static readonly DateTimeOffset Fecha = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    private static FilaDeReporteMercadoPago Fila(
        string tipo = TiposDeTransaccionMercadoPago.Settlement, string sourceId = "9876543", string? referencia = null, decimal importe = 100m) =>
        new(2, sourceId, tipo, importe, Fecha, referencia, -5m, 95m, Fecha.AddDays(14), null);

    private static CobroParaConciliar Cobro(
        int id = 1, string referencia = "W1P1N1I1", long? referenciaPago = 9876543, string? idPago = "PAY1",
        string estado = EstadosDeCobroMercadoPago.Aprobado, int? venta = 50, decimal importe = 100m, bool yaConciliado = false) =>
        new(id, referencia, referenciaPago, idPago, estado, venta, importe, yaConciliado);

    // Clausula: la referencia externa se busca ANTES que el SOURCE_ID: si una fila nombra un cobro por referencia y otro por id de pago,
    // gana el de la referencia.
    [Fact]
    public void LaReferenciaExternaSeBuscaAntesQueElSourceId()
    {
        var porReferencia = Cobro(id: 1, referencia: "W1P1N1I1", referenciaPago: 111, idPago: "A");
        var porPago = Cobro(id: 2, referencia: "W1P1N2I1", referenciaPago: 9876543, idPago: "B");
        var conciliador = new ConciliadorMercadoPago([porReferencia, porPago]);

        var clasificacion = conciliador.Clasificar(Fila(referencia: "W1P1N1I1", sourceId: "9876543"));

        Assert.Equal(1, clasificacion.IdCobroMercadoPago);
    }

    // Clausula: sin referencia (o con una que no existe) el SOURCE_ID se busca contra la referencia numérica del pago y contra el id del pago.
    [Theory]
    [InlineData(null, "9876543")]
    [InlineData("W9P9N9I9", "9876543")]
    [InlineData("", "PAY1")]
    public void SinReferenciaElSourceIdBuscaPorReferenciaDelPagoOPorIdDelPago(string? referencia, string sourceId)
    {
        var conciliador = new ConciliadorMercadoPago([Cobro()]);

        var clasificacion = conciliador.Clasificar(Fila(referencia: referencia, sourceId: sourceId));

        Assert.Equal(ResultadosDeConciliacionMercadoPago.Conciliado, clasificacion.Resultado);
        Assert.Equal(1, clasificacion.IdCobroMercadoPago);
    }

    // Clausula: un cobro aprobado, ligado a una venta y por el mismo importe es conciliado y aplica su comisión al cobro.
    [Fact]
    public void UnCobroAprobadoLigadoYDelMismoImporteEsConciliadoYAplica()
    {
        var clasificacion = new ConciliadorMercadoPago([Cobro()]).Clasificar(Fila(referencia: "W1P1N1I1"));

        Assert.Equal(new ClasificacionDeFilaMercadoPago(ResultadosDeConciliacionMercadoPago.Conciliado, 1, true), clasificacion);
    }

    // Clausula: un cobro que ya estaba conciliado sigue siendo conciliado pero no se vuelve a escribir (la primera fila gana).
    [Fact]
    public void UnCobroYaConciliadoSigueConciliadoPeroNoAplica()
    {
        var clasificacion = new ConciliadorMercadoPago([Cobro(yaConciliado: true)]).Clasificar(Fila(referencia: "W1P1N1I1"));

        Assert.Equal(ResultadosDeConciliacionMercadoPago.Conciliado, clasificacion.Resultado);
        Assert.False(clasificacion.AplicaAlCobro);
    }

    // Clausula: el importe se compara contra el esperado del cobro; con otro importe es importe_distinto y no aplica.
    [Fact]
    public void ConOtroImporteEsImporteDistintoYNoAplica()
    {
        var clasificacion = new ConciliadorMercadoPago([Cobro(importe: 100m)]).Clasificar(Fila(referencia: "W1P1N1I1", importe: 100.01m));

        Assert.Equal(new ClasificacionDeFilaMercadoPago(ResultadosDeConciliacionMercadoPago.ImporteDistinto, 1, false), clasificacion);
    }

    // Clausula: un cobro apartado, fallido con pago, sin liga o sin aprobar es sin_venta (el dinero entró y ninguna venta lo usó), aunque el importe coincida.
    [Theory]
    [InlineData(EstadosDeCobroMercadoPago.SinVenta, null)]
    [InlineData(EstadosDeCobroMercadoPago.Fallido, null)]
    [InlineData(EstadosDeCobroMercadoPago.Aprobado, null)]
    [InlineData(EstadosDeCobroMercadoPago.Pendiente, null)]
    [InlineData(EstadosDeCobroMercadoPago.Cancelado, null)]
    [InlineData(EstadosDeCobroMercadoPago.Pendiente, 50)]
    [InlineData(EstadosDeCobroMercadoPago.Expirado, 50)]
    public void UnCobroQueNoPagoNingunaVentaEsSinVenta(string estado, int? venta)
    {
        var clasificacion = new ConciliadorMercadoPago([Cobro(estado: estado, venta: venta)]).Clasificar(Fila(referencia: "W1P1N1I1"));

        Assert.Equal(new ClasificacionDeFilaMercadoPago(ResultadosDeConciliacionMercadoPago.SinVenta, 1, false), clasificacion);
    }

    // Clausula: sin ningún cobro es sin_cobro_en_ways.
    [Fact]
    public void SinCobroEsSinCobroEnWays()
    {
        var clasificacion = new ConciliadorMercadoPago([Cobro()]).Clasificar(Fila(referencia: "OTRA", sourceId: "1"));

        Assert.Equal(new ClasificacionDeFilaMercadoPago(ResultadosDeConciliacionMercadoPago.SinCobroEnWays, null, false), clasificacion);
    }

    // Clausula: devoluciones y contracargos se ligan al cobro como un pago pero nunca lo modifican; sin cobro quedan sin ligar.
    [Theory]
    [InlineData(TiposDeTransaccionMercadoPago.Refund, ResultadosDeConciliacionMercadoPago.Devolucion)]
    [InlineData(TiposDeTransaccionMercadoPago.Chargeback, ResultadosDeConciliacionMercadoPago.Contracargo)]
    [InlineData(TiposDeTransaccionMercadoPago.Dispute, ResultadosDeConciliacionMercadoPago.Contracargo)]
    public void DevolucionesYContracargosSeLiganSinAplicar(string tipo, string resultado)
    {
        var conciliador = new ConciliadorMercadoPago([Cobro()]);

        Assert.Equal(
            new ClasificacionDeFilaMercadoPago(resultado, 1, false), conciliador.Clasificar(Fila(tipo, referencia: "W1P1N1I1", importe: -100m)));
        Assert.Equal(
            new ClasificacionDeFilaMercadoPago(resultado, null, false), conciliador.Clasificar(Fila(tipo, referencia: "OTRA", sourceId: "1")));
    }

    // Clausula: retiros y payouts no se cotejan, aunque su SOURCE_ID coincida con un cobro.
    [Theory]
    [InlineData(TiposDeTransaccionMercadoPago.Withdrawal)]
    [InlineData(TiposDeTransaccionMercadoPago.WithdrawalCancel)]
    [InlineData(TiposDeTransaccionMercadoPago.Payout)]
    public void LosRetirosNoSeCotejan(string tipo)
    {
        var clasificacion = new ConciliadorMercadoPago([Cobro()]).Clasificar(Fila(tipo, referencia: "W1P1N1I1"));

        Assert.Equal(new ClasificacionDeFilaMercadoPago(ResultadosDeConciliacionMercadoPago.Retiro, null, false), clasificacion);
    }

    // Clausula: un SOURCE_ID no numérico solo se busca contra el id del pago; uno numérico no se confunde con un id de texto distinto.
    [Fact]
    public void UnSourceIdNoNumericoSoloBuscaPorIdDelPago()
    {
        var conciliador = new ConciliadorMercadoPago([Cobro(referenciaPago: null, idPago: "ORD-77")]);

        Assert.Equal(1, conciliador.Clasificar(Fila(sourceId: "ORD-77")).IdCobroMercadoPago);
        Assert.Null(conciliador.Clasificar(Fila(sourceId: "77")).IdCobroMercadoPago);
    }

    private static PagoSinVerificarParaConciliar Pago(
        int id, DateTimeOffset fecha, decimal importe = 100m, string? caja = null) => new(id, id * 10, fecha, importe, caja);

    // Clausula: los candidatos son los pagos del mismo importe; con otro importe no.
    [Fact]
    public void LosCandidatosTienenElMismoImporte()
    {
        var candidatos = CandidatosDeAprobacionManualMercadoPago.Buscar(
            100m, Fecha, null, [Pago(1, Fecha), Pago(2, Fecha, importe: 100.01m), Pago(3, Fecha, importe: 99.99m)]);

        Assert.Equal(1, Assert.Single(candidatos).IdPago);
    }

    // Clausula: la ventana es de un día (24 horas) hacia cada lado, con los extremos incluidos; un segundo de más queda afuera.
    [Fact]
    public void LaVentanaEsDeUnDiaConLosExtremosIncluidos()
    {
        var candidatos = CandidatosDeAprobacionManualMercadoPago.Buscar(
            100m,
            Fecha,
            null,
            [
                Pago(1, Fecha.AddDays(1)),
                Pago(2, Fecha.AddDays(-1)),
                Pago(3, Fecha.AddDays(1).AddSeconds(1)),
                Pago(4, Fecha.AddDays(-1).AddSeconds(-1))
            ]);

        Assert.Equal(new[] { 1, 2 }, candidatos.Select(c => c.IdPago).OrderBy(i => i));
    }

    // Clausula: con EXTERNAL_POS_ID en la fila solo cuentan los pagos de esa caja (un punto de venta sin caja no); sin él, cualquiera.
    [Fact]
    public void ConExternalPosIdSoloCuentanLosPagosDeEsaCaja()
    {
        PagoSinVerificarParaConciliar[] pagos = [Pago(1, Fecha, caja: "POS-A"), Pago(2, Fecha, caja: "POS-B"), Pago(3, Fecha, caja: null)];

        Assert.Equal(new[] { 1 }, CandidatosDeAprobacionManualMercadoPago.Buscar(100m, Fecha, "POS-A", pagos).Select(c => c.IdPago));
        Assert.Empty(CandidatosDeAprobacionManualMercadoPago.Buscar(100m, Fecha, "POS-Z", pagos));
        Assert.Equal(3, CandidatosDeAprobacionManualMercadoPago.Buscar(100m, Fecha, null, pagos).Count);
        Assert.Equal(3, CandidatosDeAprobacionManualMercadoPago.Buscar(100m, Fecha, string.Empty, pagos).Count);
    }

    // Clausula: el más cercano en el tiempo va primero y el empate se resuelve por id.
    [Fact]
    public void ElMasCercanoEnElTiempoVaPrimero()
    {
        var candidatos = CandidatosDeAprobacionManualMercadoPago.Buscar(
            100m, Fecha, null, [Pago(5, Fecha.AddHours(5)), Pago(7, Fecha.AddHours(-1)), Pago(6, Fecha.AddHours(1))]);

        Assert.Equal(new[] { 6, 7, 5 }, candidatos.Select(c => c.IdPago));
    }
}
