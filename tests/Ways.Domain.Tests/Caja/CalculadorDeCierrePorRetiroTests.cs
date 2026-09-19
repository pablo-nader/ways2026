using Ways.Domain.Caja;
using Ways.Domain.Catalogos;

namespace Ways.Domain.Tests.Caja;

/// <summary>
/// stage-etapa-5 (cierre por retiro), pura sin base de datos — mismo criterio que
/// <see cref="CalculadorDeArqueoTests"/>. IDs sintéticos: 1 = efectivo (ancla en todos los casos),
/// 2 = tarjeta (electrónico).
///
/// judgment-day JD-E5a-1: <c>CalculadorDeCierrePorRetiro</c> ya NO calcula <c>Diferencia</c> (ver
/// su doc-comment) — antes de esta corrección, este archivo tenía la prueba de la "invariante"
/// acá, pero esa fórmula solo era correcta para el modo retiro; sobre un cierre clásico
/// fabricaba un número falso. La prueba de <c>Diferencia</c> ahora vive en
/// <c>Ways.IntegrationTests.CajaCierrePorRetiroEndpointsTests</c>, con base de datos real, porque
/// la única fuente de ese campo es la fila YA PERSISTIDA de <c>arqueos_turno</c>
/// (<c>LectorDeResumenDeCierrePorRetiro.LeerAsync</c>), no puede probarse pura.
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
