using System.Text.RegularExpressions;

namespace Ways.Domain.MercadoPago;

/// <summary>
/// Reglas del identificador de una terminal Point de Mercado Pago (<c>TIPO__SERIE</c>, por ejemplo <c>PAX_A910__8701</c>). Solo se
/// admiten los modelos Newland N950 y PAX A910; el modelo lo valida Mercado Pago al cambiar el modo, no Ways. El tipo lleva un guion
/// bajo propio (<c>PAX_A910</c>), así que el doble guion separa el tipo de la serie solo si lo que sigue es la serie.
/// </summary>
public static partial class TerminalPointMercadoPago
{
    public const int LargoMaximo = 80;

    public const string CodigoDeIdInvalido = "terminal_point_invalida";

    /// <summary>Modo de operación con el que la terminal recibe las órdenes que crea Ways.</summary>
    public const string ModoPdv = "PDV";

    [GeneratedRegex("^[A-Z0-9]+(_[A-Z0-9]+)*__[A-Za-z0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex FormaDelId();

    /// <summary>El identificador sin espacios en los extremos si tiene la forma <c>TIPO__SERIE</c> y entra en
    /// <see cref="LargoMaximo"/>; si no, nulo.</summary>
    public static string? Normalizar(string? id)
    {
        var limpio = id?.Trim();
        return limpio is { Length: > 0 and <= LargoMaximo } && FormaDelId().IsMatch(limpio) ? limpio : null;
    }

    public static bool EstaEnPdv(string? modoOperativo) =>
        string.Equals(modoOperativo?.Trim(), ModoPdv, StringComparison.OrdinalIgnoreCase);
}
