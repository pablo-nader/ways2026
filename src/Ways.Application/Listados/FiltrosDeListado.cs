using static Ways.Application.Busqueda.BusquedaSinAcentos;

namespace Ways.Application.Listados;

public static class FiltrosDeListado
{
    /// <summary>Patrón <c>%texto%</c> del filtro de una columna de texto (sin mayúsculas ni acentos, vía
    /// <see cref="Busqueda.BusquedaSinAcentos.Coincide"/>). Un filtro ausente o en blanco no filtra: devuelve
    /// <c>null</c>.</summary>
    public static string? PatronONulo(string? texto) =>
        string.IsNullOrWhiteSpace(texto) ? null : PatronDeContiene(texto.Trim());
}
