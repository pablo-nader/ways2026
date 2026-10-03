using Ways.Domain.Common;

namespace Ways.Domain.Ventas;

/// <summary>Una línea antes de calcular (design: Checkout Contract). <see cref="Cantidad"/>
/// llega con signo (negativa en NCX, design decisión 4) — la aritmética es uniforme para ambos
/// signos, sin rama especial. <see cref="AjusteManualPorcentaje"/> es el porcentaje que el operador
/// aplicó a mano sobre el neto de la línea (negativo = descuento, positivo = recargo); <c>null</c>
/// es "sin ajuste" y deja el cálculo idéntico al previo a esa columna.</summary>
public readonly record struct LineaParaCalcular(
    decimal Cantidad, decimal PrecioUnitario, decimal DescuentoUnitario, decimal? AjusteManualPorcentaje = null);

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
/// pura, DB-free). Mismo criterio POS que <see cref="Ofertas.ResolvedorDeOfertas"/>:
/// <see cref="MidpointRounding.AwayFromZero"/> en cada redondeo, nunca el banker's rounding
/// default de .NET.
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
            var brutoDeLinea = Math.Round(linea.Cantidad * linea.PrecioUnitario, 2, MidpointRounding.AwayFromZero);
            var descuento = Math.Round(linea.DescuentoUnitario * linea.Cantidad, 2, MidpointRounding.AwayFromZero);
            var netoDeLinea = brutoDeLinea - descuento;
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

    /// <summary>Monto del ajuste manual sobre el neto de la línea (bruto − descuento de oferta), con
    /// el signo de <paramref name="porcentaje"/> aplicado a ese neto. Única fórmula del ajuste: la
    /// reusa también la reliquidación de cuenta corriente para reaplicar el porcentaje de cada
    /// item sobre el neto nuevo.</summary>
    public static decimal AjusteManualSobre(decimal netoDeLinea, decimal? porcentaje) =>
        porcentaje is { } p ? Math.Round(netoDeLinea * p / 100m, 2, MidpointRounding.AwayFromZero) : 0m;
}
