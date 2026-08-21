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

    /// <summary>stage-18-etiquetas-y-consulta, Slice 2 (task 2.1; design.md:207-215, decisión 8):
    /// la MISMA proyección <c>id_categoria</c> → <c>id_categoria_padre</c> que
    /// <see cref="ConstruirAncestros"/>, recorrida en el sentido inverso — así <c>idCategoria</c>
    /// en <c>GET /api/articulos</c>/<c>POST /api/etiquetas/datos</c> alcanza toda la subcategoría,
    /// no solo el nodo exacto (spec: articulos / "idCategoria on a parent returns descendant
    /// artículos too"). Invariante bajo prueba: <c>d ∈ ConstruirDescendientes(c) ⟺ c ∈
    /// ConstruirAncestros(d)</c>. Misma cota de <see cref="ReglaDeCategorias.ProfundidadMaxima"/>
    /// que <see cref="ConstruirAncestros"/> (BFS acotado en profundidad, no en cantidad de nodos
    /// por nivel — un árbol ancho de un mismo nivel no cuenta contra la cota).</summary>
    public static IReadOnlySet<int> ConstruirDescendientes(
        int idCategoria, IReadOnlyDictionary<int, int?> padrePorCategoria)
    {
        // hijosPorCategoria: sentido inverso del mapa recibido — se arma una sola vez, en
        // memoria, sin una segunda consulta (mismo criterio que ConstruirAncestros).
        var hijosPorCategoria = new Dictionary<int, List<int>>();
        foreach (var (hijo, padre) in padrePorCategoria)
        {
            if (padre is not { } idPadre)
            {
                continue;
            }

            if (!hijosPorCategoria.TryGetValue(idPadre, out var hijos))
            {
                hijos = [];
                hijosPorCategoria[idPadre] = hijos;
            }

            hijos.Add(hijo);
        }

        var descendientes = new HashSet<int> { idCategoria };
        var nivelActual = new List<int> { idCategoria };

        for (var i = 0; i < ReglaDeCategorias.ProfundidadMaxima && nivelActual.Count > 0; i++)
        {
            var siguienteNivel = new List<int>();

            foreach (var nodo in nivelActual)
            {
                if (!hijosPorCategoria.TryGetValue(nodo, out var hijos))
                {
                    continue;
                }

                foreach (var hijo in hijos)
                {
                    // Ciclo corrupto (no alcanzable en operación normal, ver ConstruirAncestros):
                    // un hijo ya visto no vuelve a expandirse ni hace loopear el BFS.
                    if (descendientes.Add(hijo))
                    {
                        siguienteNivel.Add(hijo);
                    }
                }
            }

            nivelActual = siguienteNivel;
        }

        return descendientes;
    }
}
