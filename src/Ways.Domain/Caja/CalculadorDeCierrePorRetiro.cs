namespace Ways.Domain.Caja;

/// <summary>
/// Una línea de <see cref="ResultadoDeCierrePorRetiro.VentasPorMedio"/> — ventas NETAS de vuelto
/// (nunca de gastos: eso es un renglón aparte, <see cref="ResultadoDeCierrePorRetiro.GastosEnEfectivo"/>).
/// </summary>
public sealed record LineaDeVentaPorMedio(int IdMedioPago, decimal Importe);

/// <summary>
/// Salida pura de <see cref="CalculadorDeCierrePorRetiro"/> — los números que
/// <c>Ways.Application.Caja.LectorDeResumenDeCierrePorRetiro</c> combina con nombres (medio,
/// punto de venta, empleados) para armar <c>ResumenDeCierrePorRetiro</c>. Nunca toca base de
/// datos ni el catálogo de medios — esos son responsabilidad del armador de Application.
/// </summary>
public sealed record ResultadoDeCierrePorRetiro(
    IReadOnlyList<LineaDeVentaPorMedio> VentasPorMedio,
    decimal TotalVentas,
    decimal VentasEnEfectivoNetas,
    decimal GastosEnEfectivo,
    decimal Refuerzos,
    decimal TotalRetiros,
    decimal Diferencia);

/// <summary>
/// Cierre por retiro (práctica del dueño: el cajero retira el efectivo contado y DEJA el fondo
/// inicial en el cajón; nada se cuenta al cierre). Pura, sin base de datos — mismo criterio que
/// <see cref="CalculadorDeArqueo"/>, y deliberadamente SEPARADA de ella: <see
/// cref="CalculadorDeArqueo"/> queda byte-identical (mismo criterio que la nota de la etapa 8
/// sobre gastos de proveedor), esta clase solo LEE su resultado (<paramref name="arqueables"/>)
/// para el listado de "ventas por medio" y usa <see cref="InsumosDeArqueo"/> directo para los
/// totales del ancla.
///
/// "Ventas por medio" difiere de "esperado" del arqueo: neta el vuelto (mismo término
/// <c>vueltosTotales</c>, absorbido solo por el ancla — design decisión 2 de
/// <see cref="CalculadorDeArqueo"/>) pero NUNCA resta gastos ni suma/resta fondo, retiros o
/// refuerzos — esos son conceptos de caja física, no de venta, y viajan aparte
/// (<see cref="ResultadoDeCierrePorRetiro.GastosEnEfectivo"/>/<see
/// cref="ResultadoDeCierrePorRetiro.Refuerzos"/>/<see cref="ResultadoDeCierrePorRetiro.TotalRetiros"/>).
///
/// La invariante que ata este cálculo al arqueo persistido: cuando el ancla se declara con el
/// fondo inicial (lo que <c>ServicioDeTurnos.CerrarPorRetiroAsync</c> hace), <see
/// cref="ResultadoDeCierrePorRetiro.Diferencia"/> es exactamente <c>−(esperado(ancla) −
/// fondo_inicial)</c>, es decir el negativo de la diferencia que <c>arqueos_turno</c> persiste
/// para el medio ancla — la prueba de esa identidad vive en
/// <c>CalculadorDeCierrePorRetiroTests</c> (mutation-proof-tests).
/// </summary>
public static class CalculadorDeCierrePorRetiro
{
    public static ResultadoDeCierrePorRetiro Calcular(
        InsumosDeArqueo insumos, int idMedioAncla, IReadOnlyList<LineaDeArqueo> arqueables)
    {
        var vueltosTotales = insumos.Actividad.Sum(a => a.Vueltos);

        var ventasPorMedio = arqueables
            .OrderBy(l => l.IdMedioPago)
            .Select(l => new LineaDeVentaPorMedio(
                l.IdMedioPago,
                PagosDe(insumos, l.IdMedioPago) - (l.IdMedioPago == idMedioAncla ? vueltosTotales : 0m)))
            .ToList();

        var totalVentas = ventasPorMedio.Sum(v => v.Importe);
        // Del ANCLA directo sobre insumos (no de ventasPorMedio): tiene que valer aunque el
        // ancla no haya quedado arqueable (sin fila en el arqueo persistido) — la invariante de
        // la diferencia no puede depender de eso.
        var ventasEnEfectivoNetas = PagosDe(insumos, idMedioAncla) - vueltosTotales;
        var gastosEnEfectivo = GastosDe(insumos, idMedioAncla);
        var diferencia = insumos.Retiros - (ventasEnEfectivoNetas - gastosEnEfectivo + insumos.Refuerzos);

        return new ResultadoDeCierrePorRetiro(
            ventasPorMedio, totalVentas, ventasEnEfectivoNetas, gastosEnEfectivo, insumos.Refuerzos,
            insumos.Retiros, diferencia);
    }

    private static decimal PagosDe(InsumosDeArqueo insumos, int idMedioPago) =>
        insumos.Actividad.FirstOrDefault(a => a.IdMedioPago == idMedioPago).Pagos;

    private static decimal GastosDe(InsumosDeArqueo insumos, int idMedioPago) =>
        insumos.Actividad.FirstOrDefault(a => a.IdMedioPago == idMedioPago).Gastos;
}
