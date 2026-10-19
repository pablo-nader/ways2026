using System.Text;

namespace Ways.Domain.Proveedores;

/// <summary>El CUIT se guarda como se tipeó ("20-12345678-9", "20 12345678 9", "20123456789"); dos
/// CUIT son el mismo si coinciden sus dígitos.</summary>
public static class NormalizacionDeCuit
{
    public const int CantidadDeDigitos = 11;

    /// <summary>Los once dígitos del CUIT, o <c>null</c> si el texto tiene otra cantidad de dígitos o
    /// algún carácter que no sea dígito, guion, punto o espacio.</summary>
    public static string? Digitos(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto))
        {
            return null;
        }

        var digitos = new StringBuilder(CantidadDeDigitos);
        foreach (var caracter in texto)
        {
            if (char.IsAsciiDigit(caracter))
            {
                digitos.Append(caracter);
            }
            else if (caracter is not ('-' or '.') && !char.IsWhiteSpace(caracter))
            {
                return null;
            }
        }

        return digitos.Length == CantidadDeDigitos ? digitos.ToString() : null;
    }
}
