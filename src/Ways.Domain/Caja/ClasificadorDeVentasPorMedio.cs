using Ways.Domain.Catalogos;

namespace Ways.Domain.Caja;

/// <summary>Una venta por medio con la clasificación con que entra en los subtotales del cierre.</summary>
public sealed record LineaClasificadaDeVentaPorMedio(
    int IdMedioPago, ComportamientoMedioPago Comportamiento, decimal Importe);

/// <summary>
/// Subtotales del cierre por clasificación. Los tres suman <c>ResultadoDeCierrePorRetiro.TotalVentas</c>
/// porque cada línea cae en exactamente uno.
/// </summary>
public sealed record TotalesDeVentasPorComportamiento(
    IReadOnlyList<LineaClasificadaDeVentaPorMedio> Lineas,
    decimal TotalDigitales,
    decimal TotalEfectivo,
    decimal TotalCuentaCorriente);

/// <summary>
/// Reparte las ventas por medio de un turno cerrado en efectivo, digital (<see
/// cref="ComportamientoMedioPago.Electronico"/>) y cuenta corriente. <c>Comportamiento</c> es
/// editable después de las ventas, así que la clasificación se toma del catálogo ACTUAL, salvo el
/// efectivo: es efectivo únicamente el medio ancla del turno (<c>IdMedioPagoEfectivo</c> fijado al
/// cierre), el mismo que usan el arqueo y <c>VentasEnEfectivoNetas</c>. Un medio que hoy figura
/// como efectivo sin ser el ancla de este turno se cuenta como digital, porque su dinero no pasó
/// por el cajón.
/// </summary>
public static class ClasificadorDeVentasPorMedio
{
    public static TotalesDeVentasPorComportamiento Clasificar(
        IReadOnlyList<LineaDeVentaPorMedio> ventasPorMedio,
        IReadOnlyDictionary<int, ComportamientoMedioPago> comportamientoActual,
        int idMedioAncla)
    {
        var lineas = ventasPorMedio
            .Select(v => new LineaClasificadaDeVentaPorMedio(
                v.IdMedioPago, ClasificacionDe(v.IdMedioPago, comportamientoActual, idMedioAncla), v.Importe))
            .ToList();

        return new TotalesDeVentasPorComportamiento(
            lineas,
            SumaDe(lineas, ComportamientoMedioPago.Electronico),
            SumaDe(lineas, ComportamientoMedioPago.Efectivo),
            SumaDe(lineas, ComportamientoMedioPago.CuentaCorriente));
    }

    private static ComportamientoMedioPago ClasificacionDe(
        int idMedioPago,
        IReadOnlyDictionary<int, ComportamientoMedioPago> comportamientoActual,
        int idMedioAncla)
    {
        if (idMedioPago == idMedioAncla)
        {
            return ComportamientoMedioPago.Efectivo;
        }

        var actual = comportamientoActual[idMedioPago];
        return actual == ComportamientoMedioPago.Efectivo ? ComportamientoMedioPago.Electronico : actual;
    }

    private static decimal SumaDe(
        IEnumerable<LineaClasificadaDeVentaPorMedio> lineas, ComportamientoMedioPago comportamiento) =>
        lineas.Where(l => l.Comportamiento == comportamiento).Sum(l => l.Importe);
}
