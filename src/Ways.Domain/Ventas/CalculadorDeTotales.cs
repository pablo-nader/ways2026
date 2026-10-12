using Ways.Domain.Common;
using Ways.Domain.Dinero;

namespace Ways.Domain.Ventas;

/// <summary>Una línea antes de calcular (design: Checkout Contract). <see cref="Cantidad"/>
/// llega con signo (negativa en NCX, design decisión 4) — la aritmética es uniforme para ambos
/// signos, sin rama especial. <see cref="AjusteManualPorcentaje"/> es el porcentaje que el operador
/// aplicó a mano sobre el neto de la línea (negativo = descuento, positivo = recargo); <c>null</c>
/// es "sin ajuste" y deja el cálculo idéntico al previo a esa columna.
/// <see cref="UnidadesConsumidasPorCombo"/> son las unidades de la línea que un combo ya consumió
/// (mismo signo que <see cref="Cantidad"/>) y <see cref="AhorroDeCombo"/> es el importe de LÍNEA que
/// el combo les repartió: positivo en una venta. En una devolución de combo (cantidad negativa) lleva el
/// signo de la cantidad, igual que cualquier descuento de línea de una nota de crédito, y expresa la
/// diferencia entre el bruto de la línea y lo que se devuelve. El descuento de la línea es
/// <c>bruto − neto</c>, con el neto <c>round((Cantidad − UnidadesConsumidasPorCombo) × (Precio −
/// DescuentoUnitario)) + round(UnidadesConsumidasPorCombo × Precio − AhorroDeCombo)</c> al peso
/// entero: la oferta unitaria solo alcanza a las unidades que el combo no consumió. Con ambos en cero
/// el cálculo es el de siempre.</summary>
public readonly record struct LineaParaCalcular(
    decimal Cantidad, decimal PrecioUnitario, decimal DescuentoUnitario, decimal? AjusteManualPorcentaje = null,
    decimal UnidadesConsumidasPorCombo = 0m, decimal AhorroDeCombo = 0m);

/// <summary>Una línea ya calculada — lista para materializar en
/// <see cref="ItemComprobanteVenta"/> (más los campos de snapshot que Slice 4 completa).
/// <see cref="AjusteManual"/> lleva el signo del porcentaje que lo originó (con cantidad negativa
/// de NCX el monto sale con el signo opuesto, igual que <see cref="Descuento"/>) y ya está sumado
/// en <see cref="Total"/>.</summary>
public readonly record struct ItemCalculado(
    decimal Cantidad, decimal PrecioUnitario, decimal Descuento, decimal Total, decimal AjusteManual = 0m);

/// <summary>Resultado completo de <see cref="CalculadorDeTotales.Calcular"/>.
/// <see cref="DescuentoManualTotal"/> y <see cref="RecargoManualTotal"/> se clasifican por el signo
/// del PORCENTAJE de cada línea, nunca por el del monto, y viajan separados a propósito: un recargo
/// en una línea no puede esconder un descuento en otra.</summary>
public readonly record struct TotalesCalculados(
    IReadOnlyList<ItemCalculado> Items, decimal Subtotal, decimal DescuentoTotal, decimal Total,
    decimal DescuentoManualTotal = 0m, decimal RecargoManualTotal = 0m);

/// <summary>
/// Calcula los totales de un checkout (design: Checkout Contract — orden de redondeo pineado,
/// pura, DB-free). Todo importe calculado es un peso entero
/// (<see cref="RedondeoDePeso"/>: punto medio away-from-zero, nunca el banker's rounding default
/// de .NET); con componentes enteros el total del documento coincide con la suma de los items.
/// </summary>
public static class CalculadorDeTotales
{
    public static TotalesCalculados Calcular(IReadOnlyList<LineaParaCalcular> lineas)
    {
        var items = new List<ItemCalculado>(lineas.Count);
        var subtotal = 0m;
        var descuentoTotal = 0m;
        var descuentoManualTotal = 0m;
        var recargoManualTotal = 0m;

        foreach (var linea in lineas)
        {
            var brutoDeLinea = BrutoDeLinea(linea.Cantidad, linea.PrecioUnitario);
            var netoDeLinea = NetoDeLinea(
                linea.Cantidad, linea.PrecioUnitario, linea.DescuentoUnitario,
                linea.UnidadesConsumidasPorCombo, linea.AhorroDeCombo);
            var descuento = brutoDeLinea - netoDeLinea;
            var ajusteManual = AjusteManualSobre(netoDeLinea, linea.AjusteManualPorcentaje);
            var totalDeLinea = netoDeLinea + ajusteManual;

            items.Add(new ItemCalculado(linea.Cantidad, linea.PrecioUnitario, descuento, totalDeLinea, ajusteManual));

            subtotal += brutoDeLinea;
            descuentoTotal += descuento;

            if (linea.AjusteManualPorcentaje < 0m)
            {
                descuentoManualTotal -= ajusteManual;
            }
            else if (linea.AjusteManualPorcentaje > 0m)
            {
                recargoManualTotal += ajusteManual;
            }
        }

        var total = subtotal - descuentoTotal - descuentoManualTotal + recargoManualTotal;

        // Invariante de dominio (doc 10: "verificados por dominio") — defensa en profundidad,
        // nunca debería fallar si el bucle de arriba es correcto; si falla, es un bug de esta
        // clase, no un caso de negocio válido.
        var sumaDeItems = items.Sum(i => i.Total);
        if (total != sumaDeItems)
        {
            throw new ErrorDominio(
                "totales_inconsistentes", "El total no coincide con la suma de los items.", 500);
        }

        return new TotalesCalculados(items, subtotal, descuentoTotal, total, descuentoManualTotal, recargoManualTotal);
    }

    /// <summary>Bruto de la línea: cantidad por precio de lista, al peso entero (los importes de
    /// venta calculados nunca llevan centavos, ver <see cref="RedondeoDePeso"/>).</summary>
    public static decimal BrutoDeLinea(decimal cantidadConSigno, decimal precioUnitario) =>
        RedondeoDePeso.APesoEntero(cantidadConSigno * precioUnitario);

    /// <summary>Neto de la línea antes del ajuste manual: el neto VERDADERO (cantidad por precio
    /// final unitario) redondeado al peso. La oferta unitaria solo alcanza a las unidades que el
    /// combo no consumió, y esas unidades se redondean por su cuenta; la parte del combo (importe de
    /// lista de las unidades consumidas menos su ahorro) es un importe entero por construcción, que
    /// reparte <c>ResolvedorDeCombos</c>, y se suma sin que el redondeo de la otra parte lo
    /// altere. El descuento de línea es <c>bruto − neto</c>,
    /// nunca una cifra redondeada por separado: dos redondeos independientes dejarían centavos o
    /// cobrarían distinto de cantidad × precio final (1166.66 × 3 = 3499.98 cobra 3500). La reusan
    /// el diagnóstico de discrepancia offline y la reliquidación para no tener una segunda fórmula;
    /// sin combo el resultado es el de siempre.</summary>
    public static decimal NetoDeLinea(
        decimal cantidadConSigno, decimal precioUnitario, decimal descuentoUnitario,
        decimal unidadesConsumidasPorCombo = 0m, decimal ahorroDeCombo = 0m) =>
        RedondeoDePeso.APesoEntero((cantidadConSigno - unidadesConsumidasPorCombo) * (precioUnitario - descuentoUnitario))
        + RedondeoDePeso.APesoEntero((unidadesConsumidasPorCombo * precioUnitario) - ahorroDeCombo);

    /// <summary>Monto del ajuste manual sobre el neto de la línea (bruto − descuento de oferta), con
    /// el signo de <paramref name="porcentaje"/> aplicado a ese neto. Única fórmula del ajuste: la
    /// reusa también la reliquidación de cuenta corriente para reaplicar el porcentaje de cada
    /// item sobre el neto nuevo.</summary>
    public static decimal AjusteManualSobre(decimal netoDeLinea, decimal? porcentaje) =>
        porcentaje is { } p ? RedondeoDePeso.APesoEntero(netoDeLinea * p / 100m) : 0m;
}
