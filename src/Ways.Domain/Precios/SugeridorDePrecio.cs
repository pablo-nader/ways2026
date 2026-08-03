namespace Ways.Domain.Precios;

/// <summary>
/// Sugerencia de precio a partir de costo + margen (design decision 8, spec: precios /
/// Margin-Based Price Suggestion) — función pura, sin acceso a base de datos:
/// <c>ServicioDeArticulos.SugerirPrecioAsync</c> ya resuelve <c>costoNominal</c>/
/// <c>costoLista</c>/<c>descuentoProveedor</c> desde el propio artículo y
/// <c>margenGrupo</c>/<c>margenProveedor</c> desde sus referencias
/// (<c>grupos.margen</c>/<c>proveedores.margen</c>) antes de llamar acá. La sugerencia NUNCA
/// se aplica sola — el llamador solo la muestra; el alta/edición de un precio real pasa
/// siempre por <c>ServicioDePrecios</c> (Slice 3).
/// </summary>
public static class SugeridorDePrecio
{
    /// <summary>Costo base: <paramref name="costoNominal"/> cuando está presente, si no
    /// <c>costoLista * (1 - descuentoProveedor / 100)</c> (spec: "Base cost is costo_nominal
    /// when present, else costo_lista * (1 - descuento_proveedor)").
    /// <paramref name="descuentoProveedor"/> es un PORCENTAJE (columna <c>numeric(5,2)</c>,
    /// misma escala 0-100 que <c>margen</c> — <c>proveedores.descuento_proveedor = 25</c>
    /// significa 25%, no 0.25), así que se divide por 100 antes de aplicarlo, igual que
    /// <paramref name="margenGrupo"/>/<paramref name="margenProveedor"/> más abajo. Un
    /// <paramref name="descuentoProveedor"/> ausente junto a <paramref name="costoLista"/> se
    /// trata como 0 (sin descuento) — ninguna de las dos columnas es obligatoria en
    /// <c>articulos</c> (doc 10 §3), así que la ausencia del descuento no debe bloquear la
    /// sugerencia. Margen: <paramref name="margenGrupo"/> cuando está presente, si no
    /// <paramref name="margenProveedor"/> (spec: "grupos.margen takes precedence ... otherwise
    /// proveedores.margen").
    ///
    /// Redondeo <see cref="MidpointRounding.AwayFromZero"/> a 2 decimales — mismo criterio de
    /// punto de venta que <c>ResolverPrecioDerivado</c> (Slice 3, design: Price Resolution &amp;
    /// Rounding): evita la sorpresa de "los empates siempre redondean al centavo par" para un
    /// valor que un cajero puede llegar a leer en pantalla.
    /// </summary>
    /// <returns><c>null</c> cuando no hay costo base (ni <paramref name="costoNominal"/> ni
    /// <paramref name="costoLista"/> presentes) o no hay margen (ni
    /// <paramref name="margenGrupo"/> ni <paramref name="margenProveedor"/> presentes) — sin
    /// suficiente información para sugerir, no hay sugerencia que mostrar (spec no cubre estos
    /// casos con un escenario propio; ausencia de dato, no un error de negocio).</returns>
    public static decimal? Sugerir(
        decimal? costoNominal,
        decimal? costoLista,
        decimal? descuentoProveedor,
        decimal? margenGrupo,
        decimal? margenProveedor)
    {
        var costoBase = costoNominal ?? (costoLista is { } lista ? lista * (1 - (descuentoProveedor ?? 0m) / 100m) : null);
        var margen = margenGrupo ?? margenProveedor;

        if (costoBase is not { } costo || margen is not { } porcentaje)
        {
            return null;
        }

        return Math.Round(costo * (1 + porcentaje / 100m), 2, MidpointRounding.AwayFromZero);
    }
}
