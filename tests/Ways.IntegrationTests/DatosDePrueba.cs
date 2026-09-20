namespace Ways.IntegrationTests;

/// <summary>
/// Generadores de valores únicos para el sembrado de las pruebas de integración.
/// </summary>
internal static class DatosDePrueba
{
    /// <summary>
    /// Número externo de comprobante de compra con el formato visible <c>PPPP-NNNNNNNN</c> y 10^8
    /// combinaciones en la parte variable. El índice único
    /// <c>ux_comprobantes_compra_numero_externo</c> alcanza a (tenant, proveedor, tipo, número),
    /// un ámbito que varias pruebas llenan con media docena de comprobantes: con poca entropía la
    /// colisión aparece de forma intermitente como un 409 <c>compra_duplicada</c> sólo cuando la
    /// suite corre completa. Nunca recortar el resultado — el prefijo del punto de venta se lleva
    /// los primeros caracteres y el recorte deja casi toda la entropía afuera.
    /// </summary>
    internal static string NumeroExternoUnico() => $"0001-{Random.Shared.Next(0, 100_000_000):D8}";
}
