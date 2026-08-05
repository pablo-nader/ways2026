using Ways.Domain.Caja;
using Ways.Domain.Catalogos;

namespace Ways.Domain.Tests.Caja;

/// <summary>
/// stage-6-turnos-caja, Slice 4 (tasks 4.1, 4.9, design: The Derivation — binding, una sola
/// fórmula). Pura, sin base de datos: la exclusión de anulados NO se prueba acá porque ya pasó
/// antes de llegar a este nivel (es <c>LectorDeMovimientosDelTurno</c>, Application, quien filtra
/// <c>estado = emitido</c> al armar <see cref="ActividadDeMedio.Pagos"/>/<see
/// cref="ActividadDeMedio.Vueltos"/>) — la cubre
/// <c>CajaCierreEndpointsTests.LosPagosYVueltosDeUnComprobanteAnuladoQuedanExcluidosDeLaDerivacion</c>
/// (spec: Anulados Are Excluded From The Derivation).
///
/// IDs sintéticos usados en todo el archivo: 1 = efectivo (ancla en la mayoría de los casos),
/// 2 = tarjeta (electrónico), 3 = cuenta corriente.
/// </summary>
public class CalculadorDeArqueoTests
{
    private const int IdEfectivo = 1;
    private const int IdTarjeta = 2;
    private const int IdCuentaCorriente = 3;

    private static ActividadDeMedio Actividad(
        int id, ComportamientoMedioPago comportamiento, decimal pagos = 0m, decimal vueltos = 0m,
        decimal gastos = 0m, bool tuvoFilas = true) =>
        new(id, comportamiento, pagos, vueltos, gastos, tuvoFilas);

    /// <summary>spec: Efectivo expected includes fondo, pagos, vuelto, gastos, and movimientos —
    /// también la prueba de colapso (design decisión 2): con vuelto SOLO en el ancla, la fórmula
    /// da exactamente lo mismo que la literal de doc 10 (<c>fondo + pagos − vueltos − gastos −
    /// retiros + refuerzos</c>).</summary>
    [Fact]
    public void ElEfectivoIncluyeFondoPagosVueltoGastosYMovimientosYColapsaConDoc10()
    {
        var insumos = new InsumosDeArqueo(
            FondoInicial: 500m, Refuerzos: 100m, Retiros: 200m,
            Actividad: [Actividad(IdEfectivo, ComportamientoMedioPago.Efectivo, pagos: 3000m, vueltos: 120m, gastos: 400m)]);

        var resultado = CalculadorDeArqueo.Calcular(insumos, IdEfectivo);

        var linea = Assert.Single(resultado);
        Assert.Equal(IdEfectivo, linea.IdMedioPago);
        Assert.Equal(2880m, linea.ImporteEsperado);
    }

    /// <summary>spec: Electrónico expected is pagos net of its own gastos only — sin fondo,
    /// vuelto, retiro ni refuerzo (esos son conceptos solo del ancla).</summary>
    [Fact]
    public void ElElectronicoEsPagosNetoDeSusPropiosGastosSinTerminosDeCajaFisica()
    {
        var insumos = new InsumosDeArqueo(
            FondoInicial: 500m, Refuerzos: 100m, Retiros: 200m,
            Actividad:
            [
                Actividad(IdEfectivo, ComportamientoMedioPago.Efectivo, pagos: 1000m),
                Actividad(IdTarjeta, ComportamientoMedioPago.Electronico, pagos: 1500m, gastos: 200m)
            ]);

        var resultado = CalculadorDeArqueo.Calcular(insumos, IdEfectivo);

        var tarjeta = resultado.Single(l => l.IdMedioPago == IdTarjeta);
        Assert.Equal(1300m, tarjeta.ImporteEsperado);
    }

    /// <summary>design decisión 2: el vuelto de un medio electrónico también sale físicamente del
    /// cajón — lo absorbe la línea del ANCLA (vueltosTotales), nunca el propio medio electrónico
    /// que lo generó.</summary>
    [Fact]
    public void UnVueltoEnUnMedioElectronicoLoAbsorbeElAnclaNoElPropioMedio()
    {
        var insumos = new InsumosDeArqueo(
            FondoInicial: 0m, Refuerzos: 0m, Retiros: 0m,
            Actividad:
            [
                Actividad(IdEfectivo, ComportamientoMedioPago.Efectivo, pagos: 1000m, vueltos: 50m),
                Actividad(IdTarjeta, ComportamientoMedioPago.Electronico, pagos: 500m, vueltos: 30m)
            ]);

        var resultado = CalculadorDeArqueo.Calcular(insumos, IdEfectivo);

        var ancla = resultado.Single(l => l.IdMedioPago == IdEfectivo);
        var tarjeta = resultado.Single(l => l.IdMedioPago == IdTarjeta);

        // vueltosTotales = 50 + 30 = 80, restado SOLO en el ancla.
        Assert.Equal(1000m - 80m, ancla.ImporteEsperado);
        // La tarjeta no resta ni su propio vuelto ni el ajeno.
        Assert.Equal(500m, tarjeta.ImporteEsperado);
    }

    /// <summary>Una NCX aporta un pago con importe negativo (stage-5 decisión 4: aritmética con
    /// signo, sin rama especial) — el neteo ya viene resuelto en <see
    /// cref="ActividadDeMedio.Pagos"/> (Application ya sumó todas las filas).</summary>
    [Fact]
    public void UnaNcxAportaPagoNegativoYSeNeteaSinRamaEspecial()
    {
        var insumos = new InsumosDeArqueo(
            FondoInicial: 0m, Refuerzos: 0m, Retiros: 0m,
            // 1000 de un TX menos 300 de una NCX asociada = 700 netos.
            Actividad: [Actividad(IdEfectivo, ComportamientoMedioPago.Efectivo, pagos: 700m)]);

        var resultado = CalculadorDeArqueo.Calcular(insumos, IdEfectivo);

        Assert.Equal(700m, Assert.Single(resultado).ImporteEsperado);
    }

    /// <summary>Retiros/refuerzos/fondo son SOLO del ancla — un medio no-ancla con actividad
    /// nunca los recibe (ya cubierto en parte por el test de electrónico; acá se aísla el caso
    /// de un no-ancla SIN actividad propia, que además no debería aparecer en el resultado).</summary>
    [Fact]
    public void UnMedioNoAnclaSinActividadPropiaNoApareceAunConFondoYMovimientosDelTurno()
    {
        var insumos = new InsumosDeArqueo(
            FondoInicial: 500m, Refuerzos: 100m, Retiros: 50m,
            Actividad:
            [
                Actividad(IdEfectivo, ComportamientoMedioPago.Efectivo, pagos: 100m),
                Actividad(IdTarjeta, ComportamientoMedioPago.Electronico, tuvoFilas: false)
            ]);

        var resultado = CalculadorDeArqueo.Calcular(insumos, IdEfectivo);

        Assert.DoesNotContain(resultado, l => l.IdMedioPago == IdTarjeta);
    }

    /// <summary>spec: Arqueo Rows Only For Medios With Activity — un medio que netea exactamente
    /// 0 (dos pagos que se cancelan) sigue debiendo una fila, porque la inclusión es por
    /// EXISTENCIA de fila, nunca por valor.</summary>
    [Fact]
    public void UnMedioQueNeteaExactamenteCeroConActividadSigueTeniendoFila()
    {
        var insumos = new InsumosDeArqueo(
            FondoInicial: 0m, Refuerzos: 0m, Retiros: 0m,
            Actividad: [Actividad(IdEfectivo, ComportamientoMedioPago.Efectivo, pagos: 0m, tuvoFilas: true)]);

        var resultado = CalculadorDeArqueo.Calcular(insumos, IdEfectivo);

        var linea = Assert.Single(resultado);
        Assert.Equal(0m, linea.ImporteEsperado);
    }

    /// <summary>proposal decisión 6 / spec: Cuenta corriente never produces a row — excluida
    /// siempre, incluso con actividad y aunque coincidiera (por error de configuración) con el
    /// id del ancla resuelto.</summary>
    [Fact]
    public void CuentaCorrienteNuncaProduceUnaFilaAunqueTengaActividad()
    {
        var insumos = new InsumosDeArqueo(
            FondoInicial: 0m, Refuerzos: 0m, Retiros: 0m,
            Actividad:
            [
                Actividad(IdEfectivo, ComportamientoMedioPago.Efectivo, pagos: 100m),
                Actividad(IdCuentaCorriente, ComportamientoMedioPago.CuentaCorriente, pagos: 500m, tuvoFilas: true)
            ]);

        var resultado = CalculadorDeArqueo.Calcular(insumos, IdEfectivo);

        Assert.DoesNotContain(resultado, l => l.IdMedioPago == IdCuentaCorriente);
    }

    /// <summary>El ancla entra sin <c>TuvoFilas</c> propio cuando el turno tuvo movimiento físico
    /// (fondo/retiro/refuerzo/vuelto) — el dinero se movió aunque nadie pagó con ese medio en
    /// este turno puntual.</summary>
    [Fact]
    public void ElAnclaApareceSinPagosPropiosCuandoHuboFondoORetiroORefuerzoOVuelto()
    {
        var insumos = new InsumosDeArqueo(
            FondoInicial: 500m, Refuerzos: 0m, Retiros: 0m,
            Actividad: [Actividad(IdEfectivo, ComportamientoMedioPago.Efectivo, tuvoFilas: false)]);

        var resultado = CalculadorDeArqueo.Calcular(insumos, IdEfectivo);

        var linea = Assert.Single(resultado);
        Assert.Equal(500m, linea.ImporteEsperado);
    }

    /// <summary>Sin ningún movimiento físico ni pago, el ancla tampoco aparece — mismo criterio
    /// "por existencia" que cualquier otro medio (spec: A medio with no activity gets no row).</summary>
    [Fact]
    public void ElAnclaNoApareceSiNoHuboNingunMovimientoFisicoNiPagoPropio()
    {
        var insumos = new InsumosDeArqueo(
            FondoInicial: 0m, Refuerzos: 0m, Retiros: 0m,
            Actividad: [Actividad(IdEfectivo, ComportamientoMedioPago.Efectivo, tuvoFilas: false)]);

        var resultado = CalculadorDeArqueo.Calcular(insumos, IdEfectivo);

        Assert.Empty(resultado);
    }

    [Fact]
    public void ElResultadoQuedaOrdenadoEnFormaEstablePorIdMedioPago()
    {
        var insumos = new InsumosDeArqueo(
            FondoInicial: 0m, Refuerzos: 0m, Retiros: 0m,
            Actividad:
            [
                Actividad(IdTarjeta, ComportamientoMedioPago.Electronico, pagos: 10m),
                Actividad(IdEfectivo, ComportamientoMedioPago.Efectivo, pagos: 10m)
            ]);

        var resultado = CalculadorDeArqueo.Calcular(insumos, IdEfectivo);

        Assert.Equal([IdEfectivo, IdTarjeta], resultado.Select(l => l.IdMedioPago));
    }
}
