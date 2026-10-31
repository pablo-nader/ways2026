namespace Ways.Domain.Pagos;

/// <summary>
/// Reglas puras del cierre en cero por redondeo (E4 de pagos y aplicaciones): una diferencia positiva de hasta la
/// tolerancia configurada (<c>tolerancia_redondeo_pago_proveedor</c>) se perdona o se absorbe con un movimiento
/// <c>redondeo</c> de la cuenta corriente del proveedor, en vez de dejar un saldo o un disponible de centavos.
/// </summary>
public static class ReglaDeRedondeoDePago
{
    /// <summary>Tope de <c>numeric(14,2)</c>, la columna donde termina la tolerancia.</summary>
    public const decimal ToleranciaMaxima = 999_999_999_999.99m;

    /// <summary>Una tolerancia válida: de 0 al tope, con a lo sumo dos decimales. Con 0 el redondeo nunca aplica.</summary>
    public static bool ToleranciaEsValida(decimal tolerancia) =>
        tolerancia >= 0m && decimal.Round(tolerancia, 2) == tolerancia && tolerancia <= ToleranciaMaxima;

    /// <summary>La diferencia (en valor absoluto) se puede redondear: es positiva y no supera la tolerancia.</summary>
    public static bool EstaEnTolerancia(decimal diferenciaAbsoluta, decimal tolerancia) =>
        diferenciaAbsoluta > 0m && diferenciaAbsoluta <= tolerancia;
}
