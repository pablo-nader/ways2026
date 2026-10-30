using Ways.Application.Usuarios;

namespace Ways.Application.Tests.Listados;

/// <summary>Recorre un listado paginado de a dos filas: si el orden se aplicara después de paginar, la secuencia que
/// resulta de concatenar las páginas no sería la del conjunto completo.</summary>
public static class RecorridoDePaginas
{
    public const int Tamanio = 2;

    public static async Task<List<int>> RecorrerAsync<T>(
        Func<int, int, Task<PaginaDe<T>>> pedir, Func<T, int> id)
    {
        var ids = new List<int>();
        for (var pagina = 1; ; pagina++)
        {
            var p = await pedir(pagina, Tamanio);
            ids.AddRange(p.Items.Select(id));
            if (pagina * Tamanio >= p.Total)
            {
                return ids;
            }
        }
    }

    /// <summary>Los ids sembrados, en el orden que indican las posiciones (1 a N, en orden de alta).</summary>
    public static List<int> PorPosicion(IReadOnlyList<int> ids, string posiciones) =>
        [.. posiciones.Split(',').Select(p => ids[int.Parse(p) - 1])];
}
