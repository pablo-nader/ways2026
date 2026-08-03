namespace Ways.Domain.Precios;

/// <summary>
/// Resolución de precio de una lista <c>derivada</c> a partir de su lista base (design decision
/// 5, task 3.1; spec: precios / Derived List Price Resolution At Read Time) — función pura, sin
/// acceso a base de datos: <c>ServicioDePrecios</c> ya resolvió <c>precioBase</c> (el precio
/// vigente de la lista base a la fecha consultada) antes de llamar acá.
/// </summary>
public static class ResolvedorDePrecios
{
    /// <summary>Redondeo <see cref="MidpointRounding.AwayFromZero"/> a 2 decimales — mismo
    /// criterio de punto de venta que <see cref="SugeridorDePrecio.Sugerir"/> (design: Price
    /// Resolution &amp; Rounding): evita la sorpresa de "los empates siempre redondean al
    /// centavo par" para un valor que un cajero puede llegar a leer en pantalla.
    /// <paramref name="porcentaje"/> puede ser negativo (descuento) o positivo (recargo) sobre
    /// <paramref name="precioBase"/> — ambos casos son el mismo cálculo, sin rama especial.
    /// </summary>
    public static decimal ResolverPrecioDerivado(decimal precioBase, decimal porcentaje) =>
        Math.Round(precioBase * (1 + porcentaje / 100m), 2, MidpointRounding.AwayFromZero);
}
