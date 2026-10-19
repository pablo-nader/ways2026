using System.Globalization;

namespace Ways.Domain.Articulos;

/// <summary>
/// Criterio de "código de barras estándar" (GTIN: EAN-8, UPC-A, EAN-13, GTIN-14). Espejo de la
/// advertencia de la web (<c>validacionCodigoBarra.ts</c>); el servidor no lo exige al guardar, lo
/// consultan quienes quieren pedir confirmación antes de guardar un código que no lo cumple.
/// </summary>
public static class ReglaDeGtin
{
    private static readonly int[] LargosEstandar = [8, 12, 13, 14];

    /// <summary>Evalúa un código ya recortado. Devuelve el motivo por el que no parece un GTIN
    /// estándar, o <c>null</c> si lo parece. Los 8 dígitos se evalúan como EAN-8 (UPC-E no está
    /// modelado) y el vacío devuelve <c>null</c>: quien llama lo descarta antes de preguntar.</summary>
    public static string? MotivoDeNoEstandar(string codigo)
    {
        if (codigo.Length == 0)
        {
            return null;
        }

        if (!codigo.All(char.IsAsciiDigit))
        {
            return "El código contiene caracteres que no son dígitos, por lo que no es un código GTIN estándar (EAN/UPC).";
        }

        if (!LargosEstandar.Contains(codigo.Length))
        {
            var unidad = codigo.Length == 1 ? "dígito" : "dígitos";
            return string.Create(
                CultureInfo.InvariantCulture,
                $"El código tiene {codigo.Length} {unidad} y los códigos GTIN estándar tienen 8, 12, 13 o 14.");
        }

        if (DigitoVerificadorEsperado(codigo) != codigo[^1] - '0')
        {
            return "El dígito verificador no coincide: puede haber un error de tipeo.";
        }

        return null;
    }

    /// <summary>Módulo 10 de GS1: desde el dígito de datos más a la derecha hacia la izquierda,
    /// pesos 3, 1, 3, 1…</summary>
    private static int DigitoVerificadorEsperado(string codigo)
    {
        var suma = 0;
        var peso = 3;
        for (var i = codigo.Length - 2; i >= 0; i--)
        {
            suma += (codigo[i] - '0') * peso;
            peso = peso == 3 ? 1 : 3;
        }

        return (10 - (suma % 10)) % 10;
    }
}
