namespace Ways.Domain.Precios;

/// <summary>
/// Sugerencia de precio a partir de costo + margen (design decision 8, spec: precios /
/// Margin-Based Price Suggestion) — función pura, sin acceso a base de datos. Quien llama
/// resuelve el costo base ya normalizado (por unidad, con IVA incluido y neto del descuento del
/// proveedor, ver <see cref="Ways.Domain.Articulos.CalculadorDeCostoNominal"/>), los márgenes
/// desde sus referencias (<c>grupos.margen</c>/<c>proveedores.margen</c>) y el múltiplo de
/// redondeo del parámetro <c>redondeo_sugerencia_precio</c>. La sugerencia NUNCA se aplica sola:
/// el llamador solo la muestra; el alta/edición de un precio real pasa siempre por
/// <c>ServicioDePrecios</c> (Slice 3).
/// </summary>
public static class SugeridorDePrecio
{
    /// <summary>Precio sugerido: <paramref name="costoBase"/> × (1 + margen / 100), donde el margen es
    /// <paramref name="margenGrupo"/> cuando está presente y, si no, <paramref name="margenProveedor"/>
    /// (spec: "grupos.margen takes precedence ... otherwise proveedores.margen"); ambos son PORCENTAJES
    /// (columnas <c>numeric(5,2)</c>, escala 0-100).
    ///
    /// Con <paramref name="multiploDeRedondeo"/> mayor que cero el resultado se redondea al múltiplo más
    /// cercano, con los empates lejos de cero (2896 con múltiplo 100 es 2900; 2950 es 3000). Si ese múltiplo
    /// más cercano es cero —el precio no llega a la mitad del múltiplo— se conserva el redondeo a 2 decimales
    /// en vez de sugerir un precio de cero. Con un múltiplo menor o igual que cero también se redondea a 2
    /// decimales (<see cref="MidpointRounding.AwayFromZero"/>, mismo criterio de punto de venta que
    /// <c>ResolverPrecioDerivado</c>).</summary>
    /// <returns><c>null</c> cuando no hay costo base o no hay margen (ni <paramref name="margenGrupo"/> ni
    /// <paramref name="margenProveedor"/>): sin información suficiente no hay sugerencia, es ausencia de dato
    /// y no un error de negocio.</returns>
    public static decimal? Sugerir(
        decimal? costoBase,
        decimal? margenGrupo,
        decimal? margenProveedor,
        decimal multiploDeRedondeo)
    {
        var margen = margenGrupo ?? margenProveedor;

        if (costoBase is not { } costo || margen is not { } porcentaje)
        {
            return null;
        }

        var precio = costo * (1 + porcentaje / 100m);

        if (multiploDeRedondeo > 0m)
        {
            var multiplos = Math.Round(precio / multiploDeRedondeo, 0, MidpointRounding.AwayFromZero);

            if (multiplos != 0m)
            {
                return multiplos * multiploDeRedondeo;
            }
        }

        return Math.Round(precio, 2, MidpointRounding.AwayFromZero);
    }
}
