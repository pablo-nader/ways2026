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
    /// <summary>Quita los acentos, reemplaza por un espacio todo lo que no sea letra ASCII, dígito, espacio, punto, coma o guion (así <c>S/N</c> no se vuelve <c>SN</c>), junta los espacios
    /// repetidos y corta en <paramref name="largoMaximo"/> sin dejar un espacio al final. Un texto sin nada admitido queda vacío.</summary>
    public static string Sanear(string? texto, int largoMaximo) => Sanear(texto, largoMaximo, EsAdmitido);

    /// <summary>Como <see cref="Sanear(string?, int)"/> pero para el nombre de una caja: <c>name</c> solo admite letras, dígitos, espacio,
    /// guion y guion bajo, así que el punto y la coma también se vuelven espacio.</summary>
    public static string SanearNombreDeCaja(string? texto, int largoMaximo) => Sanear(texto, largoMaximo, EsAdmitidoEnNombreDeCaja);

    private static string Sanear(string? texto, int largoMaximo, Func<char, bool> esAdmitido)
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
            else if (esAdmitido(caracter))
            {
                salida.Append(caracter);
            }
            else
            {
                AgregarEspacio(salida);
            }
        }

        var limpio = salida.ToString().Trim();
        return limpio.Length <= largoMaximo ? limpio : limpio[..largoMaximo].TrimEnd();
    }

    private static bool EsAdmitido(char c) =>
        c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or ',' or '-';

    private static bool EsAdmitidoEnNombreDeCaja(char c) =>
        c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_';

    private static void AgregarEspacio(StringBuilder salida)
    {
        if (salida.Length > 0 && salida[^1] != ' ')
        {
            salida.Append(' ');
        }
    }
}
