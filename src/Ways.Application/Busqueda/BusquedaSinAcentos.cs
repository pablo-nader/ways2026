using System.Text;

namespace Ways.Application.Busqueda;

/// <summary>Búsqueda de texto que ignora mayúsculas y acentos ("jose" encuentra "José"). Todas las
/// búsquedas del servidor usan <see cref="Coincide"/>; el patrón sale de <see cref="PatronDeContiene"/>.
/// <c>unaccent</c> mapea ñ a n: "Nandu" y "Ñandú" son equivalentes a propósito.</summary>
public static class BusquedaSinAcentos
{
    /// <summary>Predicado de búsqueda sobre una columna o expresión de texto. <c>WaysDbContext</c>
    /// lo traduce a <c>lower(public.sin_acentos(columna)) LIKE lower(public.sin_acentos(patrón))
    /// ESCAPE '\'</c>; <c>sin_acentos</c> es IMMUTABLE. Ejecutarlo en memoria es un error.
    /// <paramref name="patron"/> viene de <see cref="PatronDeContiene"/>.</summary>
    public static bool Coincide(string? columna, string patron) =>
        throw new NotSupportedException("Coincide solo se puede usar dentro de una consulta EF.");

    /// <summary>Patrón <c>%término%</c> con <c>%</c>, <c>_</c> y <c>\</c> del término escapados, así
    /// el usuario siempre busca el carácter literal.</summary>
    public static string PatronDeContiene(string termino)
    {
        var sb = new StringBuilder(termino.Length + 2).Append('%');
        foreach (var c in termino)
        {
            if (c is '%' or '_' or '\\')
            {
                sb.Append('\\');
            }

            sb.Append(c);
        }

        return sb.Append('%').ToString();
    }
}
