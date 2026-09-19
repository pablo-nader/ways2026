using Ways.Domain.Caja;
using Ways.Domain.Catalogos;

namespace Ways.Domain.Tests.Caja;

/// <summary>
/// stage-etapa-5 (cierre por retiro), pura sin base de datos — mismo criterio que
/// <see cref="CalculadorDeArqueoTests"/>. IDs sintéticos: 1 = efectivo (ancla en todos los casos),
/// 2 = tarjeta (electrónico).
///
/// La invariante central (<c>Diferencia == −(esperado(ancla) − fondo_inicial)</c>, spec
/// arqueo-de-cierre: Cierre Por Retiro) se prueba explícitamente en
/// <see cref="LaDiferenciaEsElNegativoDeLaDiferenciaDeArqueoDelAnclaCuandoSeDeclaraElFondo"/> —
/// mutation-proof-tests: mutar el signo de <c>Refuerzos</c> o cambiar el orden de la resta en
/// <c>CalculadorDeCierrePorRetiro.Calcular</c> rompe esa prueba (evidencia registrada en el
/// comentario de esa prueba).
/// </summary>
public class CalculadorDeCierrePorRetiroTests
{
    private const int IdEfectivo = 1;
    private const int IdTarjeta = 2;

    private static ActividadDeMedio Actividad(
        int id, ComportamientoMedioPago comportamiento, decimal pagos = 0m, decimal vueltos = 0m,
        decimal gastos = 0m, bool tuvoFilas = true) =>
        new(id, comportamiento, pagos, vueltos, gastos, tuvoFilas);

    /// <summary>Ventas por medio neta el vuelto SOLO en el ancla (mismo término
    /// <c>vueltosTotales</c> que <c>CalculadorDeArqueo</c>), pero NUNCA resta gastos — a
    /// diferencia de "esperado". Con fondo/retiro/refuerzo en 0, la línea de tarjeta es
    /// simplemente sus pagos (sin neteo de gastos), distinta de lo que el arqueo reportaría.</summary>
    [Fact]
    public void VentasPorMedioNetaElVueltoSoloEnElAnclaYNuncaRestaGastos()
    {
        var insumos = new InsumosDeArqueo(
            FondoInicial: 0m, Refuerzos: 0m, Retiros: 0m,
            Actividad:
            [
                Actividad(IdEfectivo, ComportamientoMedioPago.Efectivo, pagos: 1000m, vueltos: 50m, gastos: 100m),
                Actividad(IdTarjeta, ComportamientoMedioPago.Electronico, pagos: 500m, gastos: 80m)
            ]);

        var arqueables = CalculadorDeArqueo.Calcular(insumos, IdEfectivo);
        var resultado = CalculadorDeCierrePorRetiro.Calcular(insumos, IdEfectivo, arqueables);

        var efectivo = resultado.VentasPorMedio.Single(v => v.IdMedioPago == IdEfectivo);
        var tarjeta = resultado.VentasPorMedio.Single(v => v.IdMedioPago == IdTarjeta);

        // Efectivo: 1000 - 50 (vuelto) = 950 — SIN restar los 100 de gastos.
        Assert.Equal(950m, efectivo.Importe);
        // Tarjeta: 500 tal cual — sin vuelto propio que netear, sin restar sus 80 de gastos.
        Assert.Equal(500m, tarjeta.Importe);
        Assert.Equal(1450m, resultado.TotalVentas);
    }

    /// <summary>La invariante que ata este cálculo al arqueo persistido (spec arqueo-de-cierre:
    /// Cierre Por Retiro): cuando el ancla se declara con <c>FondoInicial</c> (lo que
    /// <c>ServicioDeTurnos.CerrarPorRetiroAsync</c> hace), <c>Diferencia</c> es exactamente el
    /// negativo de la diferencia que <c>arqueos_turno</c> persistiría para el medio ancla
    /// (<c>esperado(ancla) − declarado(ancla)</c>, con <c>declarado(ancla) = fondo_inicial</c>).
    ///
    /// Mutation-proof-tests: se corrió la mutación de verdad — invertir el signo de
    /// <c>insumos.Refuerzos</c> en <c>CalculadorDeCierrePorRetiro.Calcular</c> (de <c>+
    /// insumos.Refuerzos</c> a <c>- insumos.Refuerzos</c>) hizo fallar esta prueba
    /// (esperado <c>-770</c>, obtenido <c>-570</c>) y ninguna otra del archivo; revertido después
    /// de confirmar el fallo, la suite vuelve a quedar verde.</summary>
    [Fact]
    public void LaDiferenciaEsElNegativoDeLaDiferenciaDeArqueoDelAnclaCuandoSeDeclaraElFondo()
    {
        const decimal fondoInicial = 500m;
        const decimal refuerzos = 100m;
        const decimal retiros = 260m; // incluye el retiro de cierre.

        var insumos = new InsumosDeArqueo(
            FondoInicial: fondoInicial, Refuerzos: refuerzos, Retiros: retiros,
            Actividad:
            [
                Actividad(IdEfectivo, ComportamientoMedioPago.Efectivo, pagos: 1000m, vueltos: 30m, gastos: 40m),
                Actividad(IdTarjeta, ComportamientoMedioPago.Electronico, pagos: 300m)
            ]);

        var arqueables = CalculadorDeArqueo.Calcular(insumos, IdEfectivo);
        var resultado = CalculadorDeCierrePorRetiro.Calcular(insumos, IdEfectivo, arqueables);

        // esperado(ancla) = pagos - gastos + fondo + refuerzos - retiros - vueltos
        //                 = 1000 - 40 + 500 + 100 - 260 - 30 = 1270.
        var esperadoAncla = arqueables.Single(a => a.IdMedioPago == IdEfectivo).ImporteEsperado;
        Assert.Equal(1270m, esperadoAncla);

        // declarado(ancla) = fondo_inicial (se queda en el cajón) = 500.
        // diferencia de arqueo = esperado - declarado = 1270 - 500 = 770.
        var diferenciaDeArqueoDelAncla = esperadoAncla - fondoInicial;
        Assert.Equal(770m, diferenciaDeArqueoDelAncla);

        Assert.Equal(-diferenciaDeArqueoDelAncla, resultado.Diferencia);
        Assert.Equal(-770m, resultado.Diferencia);
    }

    /// <summary>Sin retiro de cierre (<c>ImporteRetirado = 0</c>, prueba de la capa de aplicación),
    /// acá se aísla el caso puro: <c>Retiros</c> del turno viene en 0 — <c>TotalRetiros</c> tiene
    /// que reflejarlo tal cual, nunca asumir un mínimo.</summary>
    [Fact]
    public void SinNingunRetiroElTotalDeRetirosEsCero()
    {
        var insumos = new InsumosDeArqueo(
            FondoInicial: 200m, Refuerzos: 0m, Retiros: 0m,
            Actividad: [Actividad(IdEfectivo, ComportamientoMedioPago.Efectivo, pagos: 300m)]);

        var arqueables = CalculadorDeArqueo.Calcular(insumos, IdEfectivo);
        var resultado = CalculadorDeCierrePorRetiro.Calcular(insumos, IdEfectivo, arqueables);

        Assert.Equal(0m, resultado.TotalRetiros);
        // diferencia = 0 - (ventas - gastos + refuerzos) = -(300 - 0 + 0) = -300.
        Assert.Equal(-300m, resultado.Diferencia);
    }

    /// <summary>Los totales del ancla (ventas netas, gastos) se leen de <c>insumos.Actividad</c>
    /// directo, NUNCA de la lista de arqueables — tienen que valer incluso cuando el ancla no
    /// quedó arqueable (sin fila propia en el arqueo persistido, spec: Arqueo Rows Only For
    /// Medios With Activity), porque la invariante de la diferencia no puede depender de eso.</summary>
    [Fact]
    public void LosTotalesDelAnclaValenAunqueElAnclaNoQuedeArqueable()
    {
        var insumos = new InsumosDeArqueo(
            FondoInicial: 0m, Refuerzos: 0m, Retiros: 0m,
            Actividad:
            [
                // Efectivo sin ninguna actividad ni movimiento físico: no aparece en `arqueables`.
                Actividad(IdEfectivo, ComportamientoMedioPago.Efectivo, tuvoFilas: false),
                Actividad(IdTarjeta, ComportamientoMedioPago.Electronico, pagos: 500m)
            ]);

        var arqueables = CalculadorDeArqueo.Calcular(insumos, IdEfectivo);
        Assert.DoesNotContain(arqueables, a => a.IdMedioPago == IdEfectivo);

        var resultado = CalculadorDeCierrePorRetiro.Calcular(insumos, IdEfectivo, arqueables);

        Assert.Equal(0m, resultado.VentasEnEfectivoNetas);
        Assert.Equal(0m, resultado.GastosEnEfectivo);
        Assert.DoesNotContain(resultado.VentasPorMedio, v => v.IdMedioPago == IdEfectivo);
        Assert.Single(resultado.VentasPorMedio, v => v.IdMedioPago == IdTarjeta && v.Importe == 500m);
    }

    [Fact]
    public void ElResultadoDeVentasPorMedioQuedaOrdenadoPorIdMedioPago()
    {
        var insumos = new InsumosDeArqueo(
            FondoInicial: 0m, Refuerzos: 0m, Retiros: 0m,
            Actividad:
            [
                Actividad(IdTarjeta, ComportamientoMedioPago.Electronico, pagos: 10m),
                Actividad(IdEfectivo, ComportamientoMedioPago.Efectivo, pagos: 10m)
            ]);

        var arqueables = CalculadorDeArqueo.Calcular(insumos, IdEfectivo);
        var resultado = CalculadorDeCierrePorRetiro.Calcular(insumos, IdEfectivo, arqueables);

        Assert.Equal([IdEfectivo, IdTarjeta], resultado.VentasPorMedio.Select(v => v.IdMedioPago));
    }
}
