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
///
/// SIN <c>Diferencia</c> a propósito (judgment-day JD-E5a-1): estos campos solo dependen de la
/// ACTIVIDAD del turno (pagos, gastos, retiros, refuerzos), nunca de lo que el cajero declaró, así
/// que valen igual sin importar qué endpoint cerró el turno. <c>Diferencia</c> SÍ depende de lo
/// declarado — que es el fondo inicial en el modo retiro, pero el conteo real del cajero en el
/// modo clásico — así que no tiene una fórmula única y NO puede vivir acá: <see
/// cref="Ways.Application.Caja.LectorDeResumenDeCierrePorRetiro"/> la lee directo de la fila YA
/// PERSISTIDA de <c>arqueos_turno</c> para el medio ancla, la única fuente que conoce lo
/// verdaderamente declarado en cualquiera de los dos modos.
/// </summary>
public sealed record ResultadoDeCierrePorRetiro(
    IReadOnlyList<LineaDeVentaPorMedio> VentasPorMedio,
    decimal TotalVentas,
    decimal VentasEnEfectivoNetas,
    decimal GastosEnEfectivo,
    decimal Refuerzos,
    decimal TotalRetiros);

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
/// NO calcula <c>Diferencia</c> (ver el doc-comment de <see cref="ResultadoDeCierrePorRetiro"/>) —
/// antes de judgment-day JD-E5a-1 esta clase la derivaba como <c>retiros − (ventasEfectivoNetas −
/// gastosEfectivo + refuerzos)</c>, una fórmula que solo es correcta cuando el ancla se declaró con
/// el fondo inicial (modo retiro). Sobre un cierre CLÁSICO (el cajero declara lo que contó, un
/// número arbitrario) esa fórmula fabricaba un número que no tenía nada que ver con lo persistido
/// en <c>arqueos_turno</c>.
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
        // ancla no haya quedado arqueable (sin fila en el arqueo persistido).
        var ventasEnEfectivoNetas = PagosDe(insumos, idMedioAncla) - vueltosTotales;
        var gastosEnEfectivo = GastosDe(insumos, idMedioAncla);

        return new ResultadoDeCierrePorRetiro(
            ventasPorMedio, totalVentas, ventasEnEfectivoNetas, gastosEnEfectivo, insumos.Refuerzos,
            insumos.Retiros);
    }

    private static decimal PagosDe(InsumosDeArqueo insumos, int idMedioPago) =>
        insumos.Actividad.FirstOrDefault(a => a.IdMedioPago == idMedioPago).Pagos;

    private static decimal GastosDe(InsumosDeArqueo insumos, int idMedioPago) =>
        insumos.Actividad.FirstOrDefault(a => a.IdMedioPago == idMedioPago).Gastos;
}
