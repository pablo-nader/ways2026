using System.Globalization;
using System.Text;

namespace Ways.Domain.MercadoPago;

/// <summary>
/// Limpia el texto que viaja a Mercado Pago como nombre y ubicación de un local: la API rechaza dígitos y símbolos en algunos
/// campos (<c>INVALID_NAME</c>, <c>INVALID_STATE_NAME</c>) y no se probó en vivo qué acepta cada uno, así que se manda solo
/// ASCII: letras, dígitos, espacio y <c>. , -</c>.
/// </summary>
public static class TextoParaMercadoPago
{
    /// <summary>Quita los acentos, descarta todo lo que no sea letra ASCII, dígito, espacio, punto, coma o guion, junta los espacios
    /// repetidos y corta en <paramref name="largoMaximo"/> sin dejar un espacio al final. Un texto sin nada admitido queda vacío.</summary>
    public static string Sanear(string? texto, int largoMaximo)
    {
        if (string.IsNullOrWhiteSpace(texto))
        {
            return string.Empty;
        }

        var salida = new StringBuilder(texto.Length);
        foreach (var caracter in texto.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(caracter) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsWhiteSpace(caracter))
            {
                AgregarEspacio(salida);
            }
            else if (EsAdmitido(caracter))
            {
                salida.Append(caracter);
            }
        }

        var limpio = salida.ToString().Trim();
        return limpio.Length <= largoMaximo ? limpio : limpio[..largoMaximo].TrimEnd();
    }

    private static bool EsAdmitido(char c) =>
        c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or ',' or '-';

    private static void AgregarEspacio(StringBuilder salida)
    {
        if (salida.Length > 0 && salida[^1] != ' ')
        {
            salida.Append(' ');
        }
    }
}
