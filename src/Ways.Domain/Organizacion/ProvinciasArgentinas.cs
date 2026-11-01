using System.Globalization;
using System.Text;

namespace Ways.Domain.Organizacion;

/// <summary>
/// Las 24 jurisdicciones argentinas con su nombre canónico. La dirección de una sucursal guarda siempre ese nombre, que
/// es el que Mercado Pago espera como provincia.
/// </summary>
public static class ProvinciasArgentinas
{
    public static IReadOnlyList<string> Nombres { get; } =
    [
        "Buenos Aires",
        "Ciudad Autónoma de Buenos Aires",
        "Catamarca",
        "Chaco",
        "Chubut",
        "Córdoba",
        "Corrientes",
        "Entre Ríos",
        "Formosa",
        "Jujuy",
        "La Pampa",
        "La Rioja",
        "Mendoza",
        "Misiones",
        "Neuquén",
        "Río Negro",
        "Salta",
        "San Juan",
        "San Luis",
        "Santa Cruz",
        "Santa Fe",
        "Santiago del Estero",
        "Tierra del Fuego",
        "Tucumán"
    ];

    private static readonly Dictionary<string, string> PorClave =
        Nombres.ToDictionary(Clave, n => n, StringComparer.Ordinal);

    /// <summary>El nombre canónico de la provincia que el texto nombra, sin importar mayúsculas, acentos ni espacios
    /// repetidos; <c>null</c> si no es ninguna de la lista.</summary>
    public static string? Canonica(string texto) =>
        PorClave.TryGetValue(Clave(texto), out var nombre) ? nombre : null;

    private static string Clave(string texto)
    {
        var descompuesto = texto.Normalize(NormalizationForm.FormD);
        var sinAcentos = new StringBuilder(descompuesto.Length);
        foreach (var caracter in descompuesto)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(caracter) != UnicodeCategory.NonSpacingMark)
            {
                sinAcentos.Append(caracter);
            }
        }

        var palabras = sinAcentos.ToString().ToUpperInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        return string.Join(' ', palabras);
    }
}
