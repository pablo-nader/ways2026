using Ways.Domain.Catalogos;

namespace Ways.Domain.Ofertas;

/// <summary>
/// Construye, en memoria, la cadena de ancestros de una categoría (design: Batch Boundary —
/// Categoria scope matching; task 3.3; spec: resolucion-de-ofertas / Categoria-scoped oferta
/// reaches subcategoria articulos) a partir de UNA proyección <c>id_categoria</c> →
/// <c>id_categoria_padre</c> del tenant completo — así una oferta scoped a "Bebidas" alcanza
/// artículos de "Gaseosas" (hija) sin una consulta jerárquica por artículo.
/// </summary>
public static class CadenaDeCategorias
{
    /// <summary>Devuelve <paramref name="idCategoria"/> más todos sus ancestros, acotado por
    /// <see cref="ReglaDeCategorias.ProfundidadMaxima"/> iteraciones — ni la profundidad real
    /// del árbol (que ya respeta ese límite en escritura, ADR-12) ni un ciclo corrupto por fuera
    /// de esa regla pueden hacer loopear esta función.</summary>
    public static IReadOnlySet<int> ConstruirAncestros(
        int idCategoria, IReadOnlyDictionary<int, int?> padrePorCategoria)
    {
        var ancestros = new HashSet<int> { idCategoria };
        var actual = idCategoria;

        for (var i = 0; i < ReglaDeCategorias.ProfundidadMaxima; i++)
        {
            if (!padrePorCategoria.TryGetValue(actual, out var padre) || padre is not { } idPadre)
            {
                break;
            }

            if (!ancestros.Add(idPadre))
            {
                // Ciclo corrupto (no alcanzable en operación normal — ReglaDeCategorias lo
                // rechaza en escritura, ADR-12): cortar acá en vez de loopear.
                break;
            }

            actual = idPadre;
        }

        return ancestros;
    }
}
