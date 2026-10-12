using Ways.Domain.Catalogos;

namespace Ways.Domain.Caja;

/// <summary>Una línea cobrada por medio con la clasificación con que entra en los subtotales del cierre.</summary>
public sealed record LineaClasificadaDeVentaPorMedio(
    int IdMedioPago, ComportamientoMedioPago Comportamiento, decimal Importe);

/// <summary>
/// Subtotales del cierre. <see cref="TotalDigitales"/> + <see cref="TotalEfectivo"/> suman la
/// <c>TotalVentas</c> de las mismas líneas, porque cada línea cae en exactamente uno.
/// </summary>
public sealed record TotalesDeVentasPorComportamiento(
    IReadOnlyList<LineaClasificadaDeVentaPorMedio> Lineas,
    decimal TotalDigitales,
    decimal TotalEfectivo);

/// <summary>
/// Reparte lo cobrado por medio de un turno cerrado en efectivo y digital (<see
/// cref="ComportamientoMedioPago.Electronico"/>). Las líneas no incluyen cuenta corriente: se
/// informa aparte (<see cref="ResultadoDeCierrePorRetiro.VentasACuentaCorriente"/>).
///
/// Es efectivo únicamente el medio ancla del turno (<c>IdMedioPagoEfectivo</c> fijado al cierre),
/// el mismo que usan el arqueo y <c>VentasEnEfectivoNetas</c>; <see cref="TotalEfectivo"/> coincide
/// con <c>VentasEnEfectivoNetas</c> cuando el ancla tiene línea (si no la tiene, el vuelto no se
/// netea en ninguna línea y <see cref="TotalEfectivo"/> es 0). El resto sigue el catálogo ACTUAL:
/// <c>Comportamiento</c> es editable después de las ventas, así que el reparto de un turno ya
/// cerrado puede cambiar si un medio se reclasifica (p. ej. un medio electrónico pasado a cuenta
/// corriente deja de ser una línea y pasa a la cuenta corriente informada aparte). Un medio que hoy
/// figura como efectivo sin ser el ancla de este turno se cuenta como digital, porque su dinero no
/// pasó por el cajón.
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
            SumaDe(lineas, ComportamientoMedioPago.Efectivo));
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

        return comportamientoActual[idMedioPago] == ComportamientoMedioPago.CuentaCorriente
            ? throw new InvalidOperationException(
                $"El medio {idMedioPago} es de cuenta corriente y no puede ser una línea cobrada del cierre.")
            : ComportamientoMedioPago.Electronico;
    }

    private static decimal SumaDe(
        IEnumerable<LineaClasificadaDeVentaPorMedio> lineas, ComportamientoMedioPago comportamiento) =>
        lineas.Where(l => l.Comportamiento == comportamiento).Sum(l => l.Importe);
}
