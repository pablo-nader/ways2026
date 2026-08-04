using Ways.Domain.Common;

namespace Ways.Domain.Ventas;

/// <summary>Una línea antes de calcular (design: Checkout Contract). <see cref="Cantidad"/>
/// llega con signo (negativa en NCX, design decisión 4) — la aritmética es uniforme para ambos
/// signos, sin rama especial.</summary>
public readonly record struct LineaParaCalcular(decimal Cantidad, decimal PrecioUnitario, decimal DescuentoUnitario);

/// <summary>Una línea ya calculada — lista para materializar en
/// <see cref="ItemComprobanteVenta"/> (más los campos de snapshot que Slice 4 completa).</summary>
public readonly record struct ItemCalculado(decimal Cantidad, decimal PrecioUnitario, decimal Descuento, decimal Total);

/// <summary>Resultado completo de <see cref="CalculadorDeTotales.Calcular"/>.</summary>
public readonly record struct TotalesCalculados(
    IReadOnlyList<ItemCalculado> Items, decimal Subtotal, decimal DescuentoTotal, decimal Total);

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

        foreach (var linea in lineas)
        {
            var brutoDeLinea = Math.Round(linea.Cantidad * linea.PrecioUnitario, 2, MidpointRounding.AwayFromZero);
            var descuento = Math.Round(linea.DescuentoUnitario * linea.Cantidad, 2, MidpointRounding.AwayFromZero);
            var totalDeLinea = brutoDeLinea - descuento;

            items.Add(new ItemCalculado(linea.Cantidad, linea.PrecioUnitario, descuento, totalDeLinea));

            subtotal += brutoDeLinea;
            descuentoTotal += descuento;
        }

        var total = subtotal - descuentoTotal;

        // Invariante de dominio (doc 10: "verificados por dominio") — defensa en profundidad,
        // nunca debería fallar si el bucle de arriba es correcto; si falla, es un bug de esta
        // clase, no un caso de negocio válido.
        var sumaDeItems = items.Sum(i => i.Total);
        if (total != sumaDeItems)
        {
            throw new ErrorDominio(
                "totales_inconsistentes", "El total no coincide con la suma de los items.", 500);
        }

        return new TotalesCalculados(items, subtotal, descuentoTotal, total);
    }
}
