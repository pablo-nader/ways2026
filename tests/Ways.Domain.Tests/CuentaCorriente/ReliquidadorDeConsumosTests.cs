using Ways.Domain.CuentaCorriente;

namespace Ways.Domain.Tests.CuentaCorriente;

/// <summary>
/// stage-7-cuenta-corriente (Slice 3, task 3.2, design: Testing Strategy — Unit (Domain)):
/// exhaustiva del re-pricer puro — el centro de la etapa. Sin DB, mismo listón que
/// <c>CalculadorDeArqueoTests</c>.
/// </summary>
public class ReliquidadorDeConsumosTests
{
    private static ConsumoAReliquidar UnConsumo(int idMovimiento, params LineaAReliquidar[] lineas) =>
        new(idMovimiento, IdComprobanteVenta: idMovimiento * 10, ImporteFinanciado: lineas.Sum(l => l.TotalHistorico),
            TotalComprobante: lineas.Sum(l => l.TotalHistorico), lineas);

    // ---- plain re-price up/down ---------------------------------------------------------------

    [Fact]
    public void UnaLineaSinOfertaSeReprecificaHaciaArribaSinLogicaDeDescuento()
    {
        // spec: reliquidacion-a-precio-del-dia / Non-offer line re-prices without any discount logic.
        var linea = new LineaAReliquidar(IdArticulo: 1, Cantidad: 1m, PrecioUnitario: 100m, Descuento: 0m, TotalHistorico: 100m);
        var consumo = UnConsumo(1, linea);

        var resultado = ReliquidadorDeConsumos.Calcular([consumo], new Dictionary<int, decimal?> { [1] = 120m });

        Assert.Equal(20m, resultado.Delta);
        Assert.False(resultado.HayMas);
        var detalleLinea = Assert.Single(Assert.Single(resultado.Detalle).Lineas);
        Assert.Equal(120m, detalleLinea.TotalDelDia);
        Assert.Equal(20m, detalleLinea.Delta);
        Assert.Null(detalleLinea.Motivo);
    }

    [Fact]
    public void UnaLineaSinOfertaSeReprecificaHaciaAbajo()
    {
        var linea = new LineaAReliquidar(1, Cantidad: 2m, PrecioUnitario: 100m, Descuento: 0m, TotalHistorico: 200m);
        var consumo = UnConsumo(1, linea);

        var resultado = ReliquidadorDeConsumos.Calcular([consumo], new Dictionary<int, decimal?> { [1] = 90m });

        // 2 × 90 = 180 − 200 = -20.
        Assert.Equal(-20m, resultado.Delta);
    }

    // ---- offer reversion, ambas direcciones + el worked example ------------------------------

    [Fact]
    public void UnaLineaDeOfertaReviertelDescuentoAlPrecioActualCompletoNoAlRatio()
    {
        // spec: reliquidacion-a-precio-del-dia / Offer line re-prices to the full current price —
        // worked example verbatim: sold at list 100 with a 10 discount (line total 90), current
        // list price 120 ⇒ delta 30 (nunca 18, que sería preservar el ratio del descuento).
        var linea = new LineaAReliquidar(1, Cantidad: 1m, PrecioUnitario: 100m, Descuento: 10m, TotalHistorico: 90m);
        var consumo = UnConsumo(1, linea);

        var resultado = ReliquidadorDeConsumos.Calcular([consumo], new Dictionary<int, decimal?> { [1] = 120m });

        Assert.Equal(30m, resultado.Delta);
        Assert.NotEqual(18m, resultado.Delta);
    }

    [Fact]
    public void ElWorkedExampleDelDesignDaDelta600ConDiezUnidadesYDiezPorCientoDeDescuento()
    {
        // design: The Re-Pricing Derivation — 10 units sold at 100 with a 10% offer discount
        // (line total 900), current price 150 ⇒ delta 600 = 500 de re-pricing + 100 de descuento
        // anulado.
        var linea = new LineaAReliquidar(1, Cantidad: 10m, PrecioUnitario: 100m, Descuento: 100m, TotalHistorico: 900m);
        var consumo = UnConsumo(1, linea);

        var resultado = ReliquidadorDeConsumos.Calcular([consumo], new Dictionary<int, decimal?> { [1] = 150m });

        Assert.Equal(600m, resultado.Delta);
    }

    [Fact]
    public void UnaLineaDeOfertaTambienReviertelDescuentoCuandoElPrecioActualBajo()
    {
        // "Both directions": el precio actual puede caer respecto del precio de lista original y
        // el descuento SIGUE anulándose — el delta puede terminar negativo si el precio actual
        // cae por debajo del total ya descontado.
        var linea = new LineaAReliquidar(1, Cantidad: 1m, PrecioUnitario: 100m, Descuento: 20m, TotalHistorico: 80m);
        var consumo = UnConsumo(1, linea);

        var resultado = ReliquidadorDeConsumos.Calcular([consumo], new Dictionary<int, decimal?> { [1] = 70m });

        // 70 (precio actual completo, sin descuento) − 80 (histórico con descuento) = -10.
        Assert.Equal(-10m, resultado.Delta);
    }

    // ---- factor = 1 colapsa a la fórmula legacy ------------------------------------------------

    [Fact]
    public void ConFinanciamientoTotalElFactorEsUnoYColapsaALaFormulaLegacy()
    {
        var linea = new LineaAReliquidar(1, Cantidad: 1m, PrecioUnitario: 100m, Descuento: 0m, TotalHistorico: 100m);
        var consumo = new ConsumoAReliquidar(
            IdMovimiento: 1, IdComprobanteVenta: 10, ImporteFinanciado: 100m, TotalComprobante: 100m, [linea]);

        var resultado = ReliquidadorDeConsumos.Calcular([consumo], new Dictionary<int, decimal?> { [1] = 130m });

        Assert.Equal(30m, resultado.Delta);
    }

    // ---- financiamiento parcial: proration ------------------------------------------------------

    [Fact]
    public void ConFinanciamientoParcialSoloLaFraccionFinanciadaSeReindexa()
    {
        // Comprobante de 1000, línea con delta bruto de 100 (900 histórico, 1000 actual), pero
        // solo 200 de los 1000 fueron a cuenta corriente (factor 0.2) ⇒ delta = 100 × 0.2 = 20.
        var linea = new LineaAReliquidar(1, Cantidad: 1m, PrecioUnitario: 900m, Descuento: 0m, TotalHistorico: 900m);
        var consumo = new ConsumoAReliquidar(
            IdMovimiento: 1, IdComprobanteVenta: 10, ImporteFinanciado: 200m, TotalComprobante: 1000m, [linea]);

        var resultado = ReliquidadorDeConsumos.Calcular([consumo], new Dictionary<int, decimal?> { [1] = 1000m });

        Assert.Equal(20m, resultado.Delta);
    }

    [Fact]
    public void ElFactorNuncaSuperaUnoAunqueImporteFinanciadoSuperaElTotal()
    {
        // Defensa: min(1, importeFinanciado/total) — un importeFinanciado > total (no debería
        // pasar bajo operación normal) nunca produce un factor > 1.
        var linea = new LineaAReliquidar(1, Cantidad: 1m, PrecioUnitario: 100m, Descuento: 0m, TotalHistorico: 100m);
        var consumo = new ConsumoAReliquidar(
            IdMovimiento: 1, IdComprobanteVenta: 10, ImporteFinanciado: 500m, TotalComprobante: 100m, [linea]);

        var resultado = ReliquidadorDeConsumos.Calcular([consumo], new Dictionary<int, decimal?> { [1] = 150m });

        // factor = min(1, 5) = 1 ⇒ delta = 50 × 1 = 50, no 250.
        Assert.Equal(50m, resultado.Delta);
    }

    // ---- líneas no precificables: IdArticulo NULL / sin precio vigente -----------------------

    [Fact]
    public void UnaLineaSinArticuloSeOmiteConMotivoSinAbortar()
    {
        var lineaLibre = new LineaAReliquidar(IdArticulo: null, Cantidad: 1m, PrecioUnitario: 50m, Descuento: 0m, TotalHistorico: 50m);
        var lineaNormal = new LineaAReliquidar(IdArticulo: 1, Cantidad: 1m, PrecioUnitario: 100m, Descuento: 0m, TotalHistorico: 100m);
        var consumo = UnConsumo(1, lineaLibre, lineaNormal);

        var resultado = ReliquidadorDeConsumos.Calcular([consumo], new Dictionary<int, decimal?> { [1] = 120m });

        // Solo la línea normal aporta delta (20) — la libre no aborta ni acredita.
        Assert.Equal(20m, resultado.Delta);
        var detalle = Assert.Single(resultado.Detalle);
        var lineaLibreDetalle = detalle.Lineas.Single(l => l.IdArticulo is null);
        Assert.NotNull(lineaLibreDetalle.Motivo);
        Assert.Equal(0m, lineaLibreDetalle.Delta);
        Assert.Null(lineaLibreDetalle.TotalDelDia);
    }

    [Fact]
    public void UnaLineaSinPrecioVigenteSeOmiteConMotivoSinAbortar()
    {
        var lineaSinPrecio = new LineaAReliquidar(IdArticulo: 2, Cantidad: 1m, PrecioUnitario: 50m, Descuento: 0m, TotalHistorico: 50m);
        var lineaNormal = new LineaAReliquidar(IdArticulo: 1, Cantidad: 1m, PrecioUnitario: 100m, Descuento: 0m, TotalHistorico: 100m);
        var consumo = UnConsumo(1, lineaSinPrecio, lineaNormal);

        // El diccionario ni siquiera trae la key 2 (artículo sin precio vigente en la lista actual).
        var resultado = ReliquidadorDeConsumos.Calcular([consumo], new Dictionary<int, decimal?> { [1] = 120m });

        Assert.Equal(20m, resultado.Delta);
        var lineaSinPrecioDetalle = Assert.Single(resultado.Detalle).Lineas.Single(l => l.IdArticulo == 2);
        Assert.NotNull(lineaSinPrecioDetalle.Motivo);
        Assert.Equal(0m, lineaSinPrecioDetalle.Delta);
    }

    [Fact]
    public void UnaLineaConPrecioExplicitamenteNuloEnElDiccionarioSeOmiteConMotivo()
    {
        var linea = new LineaAReliquidar(1, Cantidad: 1m, PrecioUnitario: 100m, Descuento: 0m, TotalHistorico: 100m);
        var consumo = UnConsumo(1, linea);

        var resultado = ReliquidadorDeConsumos.Calcular([consumo], new Dictionary<int, decimal?> { [1] = null });

        Assert.Equal(0m, resultado.Delta);
        Assert.NotNull(Assert.Single(resultado.Detalle).Lineas.Single().Motivo);
    }

    // ---- all-lines-skipped ⇒ delta 0 -------------------------------------------------------------

    [Fact]
    public void UnConsumoConTodasSusLineasOmitidasAportaDeltaCero()
    {
        var lineaLibre = new LineaAReliquidar(null, Cantidad: 1m, PrecioUnitario: 50m, Descuento: 0m, TotalHistorico: 50m);
        var lineaSinPrecio = new LineaAReliquidar(2, Cantidad: 1m, PrecioUnitario: 30m, Descuento: 0m, TotalHistorico: 30m);
        var consumo = UnConsumo(1, lineaLibre, lineaSinPrecio);

        var resultado = ReliquidadorDeConsumos.Calcular([consumo], new Dictionary<int, decimal?>());

        Assert.Equal(0m, resultado.Delta);
        // Sigue "cubierto" — Calcular no decide si se marca, eso lo decide el llamador según el
        // delta TOTAL de la corrida (design: "Skipping is neutral").
        Assert.Equal([1], resultado.IdsMovimientosCubiertos);
    }

    // ---- delta total cero por deltas que se cancelan (contrato del calculador, no del servicio) --

    [Fact]
    public void DosConsumosConDeltasQueSeCancelanDanDeltaTotalCeroPeroElCalculadorSigueReportandoLosDosProcesados()
    {
        // El calculador reporta lo PROCESADO, nunca lo MARCADO — quien decide si se marca es el
        // llamador (ServicioDeReliquidacion), según si el Delta TOTAL de la corrida es distinto de
        // cero. Acá +X y −X se cancelan (Delta == 0) pero el contrato de Calcular no cambia: los
        // dos consumos siguen apareciendo en IdsMovimientosCubiertos.
        var lineaSube = new LineaAReliquidar(1, Cantidad: 1m, PrecioUnitario: 100m, Descuento: 0m, TotalHistorico: 100m);
        var consumoSube = UnConsumo(1, lineaSube); // delta +20 con precio actual 120.

        var lineaBaja = new LineaAReliquidar(2, Cantidad: 1m, PrecioUnitario: 100m, Descuento: 0m, TotalHistorico: 100m);
        var consumoBaja = UnConsumo(2, lineaBaja); // delta -20 con precio actual 80.

        var precios = new Dictionary<int, decimal?> { [1] = 120m, [2] = 80m };

        var resultado = ReliquidadorDeConsumos.Calcular([consumoSube, consumoBaja], precios);

        Assert.Equal(0m, resultado.Delta);
        Assert.Equal(2, resultado.IdsMovimientosCubiertos.Count);
        Assert.Equal([1, 2], resultado.IdsMovimientosCubiertos);
    }

    // ---- empty input -------------------------------------------------------------------------

    [Fact]
    public void SinConsumosElResultadoEsDeltaCeroYSinCubiertos()
    {
        var resultado = ReliquidadorDeConsumos.Calcular([], new Dictionary<int, decimal?>());

        Assert.Equal(0m, resultado.Delta);
        Assert.Empty(resultado.IdsMovimientosCubiertos);
        Assert.Empty(resultado.Detalle);
        Assert.False(resultado.HayMas);
    }

    // ---- redondeo AwayFromZero -----------------------------------------------------------------

    [Fact]
    public void ElRedondeoDeLineaEsAwayFromZero()
    {
        // 3 × 33.335 = 100.005 → redondea a 100.01 (AwayFromZero), no a 100.00 (banker's rounding).
        var linea = new LineaAReliquidar(1, Cantidad: 3m, PrecioUnitario: 33m, Descuento: 0m, TotalHistorico: 99m);
        var consumo = UnConsumo(1, linea);

        var resultado = ReliquidadorDeConsumos.Calcular([consumo], new Dictionary<int, decimal?> { [1] = 33.335m });

        var detalleLinea = Assert.Single(Assert.Single(resultado.Detalle).Lineas);
        Assert.Equal(100.01m, detalleLinea.TotalDelDia);
    }

    [Fact]
    public void ElRedondeoDelFactorDeConsumoEsAwayFromZero()
    {
        var linea = new LineaAReliquidar(1, Cantidad: 1m, PrecioUnitario: 100m, Descuento: 0m, TotalHistorico: 100m);
        // deltaBruto = 10; factor = 1/3 ⇒ 10 × 0.333... = 3.333... → redondea a 3.33.
        var consumo = new ConsumoAReliquidar(1, 10, ImporteFinanciado: 1m, TotalComprobante: 3m, [linea]);

        var resultado = ReliquidadorDeConsumos.Calcular([consumo], new Dictionary<int, decimal?> { [1] = 110m });

        Assert.Equal(3.33m, resultado.Delta);
    }

    // ---- el cap de 500 consumos por corrida ----------------------------------------------------

    [Fact]
    public void ConMasDe500ConsumosSoloProcesaLosPrimeros500YMarcaHayMas()
    {
        var linea = new LineaAReliquidar(1, Cantidad: 1m, PrecioUnitario: 100m, Descuento: 0m, TotalHistorico: 100m);
        var consumos = Enumerable.Range(1, 501)
            .Select(id => UnConsumo(id, linea with { }))
            .ToList();

        var resultado = ReliquidadorDeConsumos.Calcular(consumos, new Dictionary<int, decimal?> { [1] = 100m });

        Assert.True(resultado.HayMas);
        Assert.Equal(500, resultado.IdsMovimientosCubiertos.Count);
        Assert.Equal(500, resultado.Detalle.Count);
        // Los primeros 500, en el orden de entrada (el lector ya los trae ordenados por fecha ASC).
        Assert.Equal(Enumerable.Range(1, 500), resultado.IdsMovimientosCubiertos);
    }

    [Fact]
    public void ConExactamente500ConsumosNoHayMas()
    {
        var linea = new LineaAReliquidar(1, Cantidad: 1m, PrecioUnitario: 100m, Descuento: 0m, TotalHistorico: 100m);
        var consumos = Enumerable.Range(1, 500)
            .Select(id => UnConsumo(id, linea with { }))
            .ToList();

        var resultado = ReliquidadorDeConsumos.Calcular(consumos, new Dictionary<int, decimal?> { [1] = 100m });

        Assert.False(resultado.HayMas);
        Assert.Equal(500, resultado.IdsMovimientosCubiertos.Count);
    }

    // ---- dos comprobantes, tres líneas, un movimiento (agregación) -----------------------------

    [Fact]
    public void ElDeltaTotalEsLaSumaDeLosDeltasDeCadaConsumo()
    {
        var lineaA1 = new LineaAReliquidar(1, Cantidad: 1m, PrecioUnitario: 100m, Descuento: 0m, TotalHistorico: 100m);
        var lineaA2 = new LineaAReliquidar(2, Cantidad: 1m, PrecioUnitario: 50m, Descuento: 0m, TotalHistorico: 50m);
        var consumoA = UnConsumo(1, lineaA1, lineaA2);

        var lineaB = new LineaAReliquidar(1, Cantidad: 1m, PrecioUnitario: 100m, Descuento: 0m, TotalHistorico: 100m);
        var consumoB = UnConsumo(2, lineaB);

        var precios = new Dictionary<int, decimal?> { [1] = 130m, [2] = 45m };

        // consumoA: (130-100) + (45-50) = 25; consumoB: 130-100 = 30 ⇒ total 55.
        var resultado = ReliquidadorDeConsumos.Calcular([consumoA, consumoB], precios);
        Assert.Equal(55m, resultado.Delta);
        Assert.Equal(2, resultado.Detalle.Count);
    }

    [Fact]
    public void DosComprobantesTresLineasConDeltas30Y20YMenos5DanCuarentaYCinco()
    {
        // spec: reliquidacion-a-precio-del-dia / Two comprobantes, three lines, one movement.
        var lineaA = new LineaAReliquidar(1, Cantidad: 1m, PrecioUnitario: 100m, Descuento: 0m, TotalHistorico: 100m);
        var consumoA = UnConsumo(1, lineaA); // delta 30 con precio actual 130.

        var lineaB1 = new LineaAReliquidar(2, Cantidad: 1m, PrecioUnitario: 80m, Descuento: 0m, TotalHistorico: 80m);
        var lineaB2 = new LineaAReliquidar(3, Cantidad: 1m, PrecioUnitario: 60m, Descuento: 0m, TotalHistorico: 60m);
        var consumoB = UnConsumo(2, lineaB1, lineaB2); // delta 20 + (-5) = 15.

        var precios = new Dictionary<int, decimal?> { [1] = 130m, [2] = 100m, [3] = 55m };

        var resultado = ReliquidadorDeConsumos.Calcular([consumoA, consumoB], precios);

        Assert.Equal(45m, resultado.Delta);
        Assert.Equal(2, resultado.IdsMovimientosCubiertos.Count);
    }
}
