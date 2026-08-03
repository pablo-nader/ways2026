using Ways.Domain.Common;

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
    ///
    /// <para>(judgment-day, item 4) Un descuento mayor a -100% (p.ej. -150%) da un precio
    /// derivado negativo, sin sentido de negocio — se rechaza acá con un error de dominio en
    /// lugar de devolver un número negativo silencioso. Obligación hacia adelante (Slice 4,
    /// registrada en <c>state.yaml</c>): <c>ServicioDeListasPrecio</c> tiene que rechazar
    /// <c>porcentaje &lt;= -100</c> al ESCRIBIR la lista, para que este caso deje de ser
    /// alcanzable en absoluto — esta guarda queda como defensa en profundidad en LECTURA, mismo
    /// criterio que la guarda de profundidad-1 de listas derivadas en
    /// <c>ServicioDePrecios.ResolverPrecioAsync</c>.</para>
    /// </summary>
    public static decimal ResolverPrecioDerivado(decimal precioBase, decimal porcentaje)
    {
        var resultado = Math.Round(precioBase * (1 + porcentaje / 100m), 2, MidpointRounding.AwayFromZero);

        if (resultado < 0)
        {
            throw new ErrorDominio(
                "precio_derivado_invalido",
                "El precio derivado no puede ser negativo — revisá el porcentaje configurado en la lista.",
                422);
        }

        return resultado;
    }
}
