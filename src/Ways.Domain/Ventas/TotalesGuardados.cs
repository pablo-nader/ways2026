namespace Ways.Domain.Ventas;

/// <summary>
/// Totales de un documento a partir de los importes que sus items ya tienen guardados
/// (presupuestos y remitos), sin pasarlos de nuevo por <see cref="CalculadorDeTotales"/>: un
/// documento emitido antes del redondeo a peso entero conserva sus centavos, y recalcularlo con la
/// fórmula vigente lo haría discrepar de su propio encabezado.
/// </summary>
public static class TotalesGuardados
{
    /// <summary>Suma lo guardado en cada item: el subtotal es <c>Total + Descuento</c> (bruto de la
    /// línea, porque estos documentos no llevan ajuste manual) y el total, la suma de
    /// <see cref="ItemCalculado.Total"/>.</summary>
    public static TotalesCalculados Sumar(IReadOnlyList<ItemCalculado> items)
    {
        var subtotal = 0m;
        var descuentoTotal = 0m;
        var total = 0m;

        foreach (var item in items)
        {
            subtotal += item.Total + item.Descuento;
            descuentoTotal += item.Descuento;
            total += item.Total;
        }

        return new TotalesCalculados(items, subtotal, descuentoTotal, total);
    }
}
