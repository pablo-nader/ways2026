using Ways.Domain.Ofertas;

namespace Ways.Domain.Tests.Ofertas;

/// <summary>
/// stage-4-ofertas, Slice 3 (task 3.3; design: Batch Boundary — Categoria scope matching) —
/// función pura, sin base de datos.
/// </summary>
public class CadenaDeCategoriasTests
{
    /// <summary>Bebidas (1, raíz) → Gaseosas (2) → Cola (3): la cadena de "Cola" incluye los tres
    /// niveles — spec: resolucion-de-ofertas / "Categoria-scoped oferta reaches subcategoria
    /// articulos".</summary>
    [Fact]
    public void ConstruirAncestrosDevuelveLaPropiaCategoriaMasTodosSusAncestros()
    {
        var padrePorCategoria = new Dictionary<int, int?> { [1] = null, [2] = 1, [3] = 2 };

        var ancestros = CadenaDeCategorias.ConstruirAncestros(3, padrePorCategoria);

        Assert.Equal(new HashSet<int> { 1, 2, 3 }, ancestros);
    }

    [Fact]
    public void UnaCategoriaRaizDevuelveSoloASiMisma()
    {
        var padrePorCategoria = new Dictionary<int, int?> { [1] = null };

        var ancestros = CadenaDeCategorias.ConstruirAncestros(1, padrePorCategoria);

        Assert.Equal(new HashSet<int> { 1 }, ancestros);
    }

    /// <summary>Nunca hace más de <see cref="Catalogos.ReglaDeCategorias.ProfundidadMaxima"/>
    /// saltos, incluso ante un ciclo corrupto por fuera de esa regla (no alcanzable en operación
    /// normal, pero esta función no debe loopear si llegara a existir).</summary>
    [Fact]
    public void UnCicloCorruptoNoHaceLoopearLaFuncion()
    {
        var padrePorCategoria = new Dictionary<int, int?> { [1] = 2, [2] = 1 };

        var ancestros = CadenaDeCategorias.ConstruirAncestros(1, padrePorCategoria);

        Assert.Equal(new HashSet<int> { 1, 2 }, ancestros);
    }

    [Fact]
    public void UnaCategoriaAusenteDelMapaDevuelveSoloASiMisma()
    {
        var padrePorCategoria = new Dictionary<int, int?>();

        var ancestros = CadenaDeCategorias.ConstruirAncestros(99, padrePorCategoria);

        Assert.Equal(new HashSet<int> { 99 }, ancestros);
    }

    // ---- stage-18-etiquetas-y-consulta, Slice 2 (tasks 2.2/2.3, design.md:207-215) --------------
    // Bosque de tres niveles: Bebidas (1, raíz) → Gaseosas (2) → Cola (3); Bebidas también tiene
    // otro hijo directo, Jugos (4); Limpieza (5, raíz) es un árbol HERMANO sin relación con
    // ninguno de los anteriores — discrimina "devuelve todo" de "devuelve la subcategoría real".

    private static Dictionary<int, int?> BosqueDeTresNiveles() => new()
    {
        [1] = null, // Bebidas (raíz)
        [2] = 1,    // Gaseosas (hija de Bebidas)
        [3] = 2,    // Cola (hija de Gaseosas)
        [4] = 1,    // Jugos (hija de Bebidas, hermana de Gaseosas)
        [5] = null  // Limpieza (raíz, árbol hermano sin relación)
    };

    [Fact]
    public void UnaHojaDevuelveSoloASiMisma()
    {
        var descendientes = CadenaDeCategorias.ConstruirDescendientes(3, BosqueDeTresNiveles());

        Assert.Equal(new HashSet<int> { 3 }, descendientes);
    }

    /// <summary>spec: articulos / "idCategoria on a parent returns descendant artículos too" — la
    /// raíz devuelve TODO el subárbol (los tres niveles), pero nunca el árbol hermano
    /// (Limpieza).</summary>
    [Fact]
    public void UnaRaizDevuelveTodoElSubarbolYNuncaElArbolHermano()
    {
        var descendientes = CadenaDeCategorias.ConstruirDescendientes(1, BosqueDeTresNiveles());

        Assert.Equal(new HashSet<int> { 1, 2, 4, 3 }, descendientes);
        Assert.DoesNotContain(5, descendientes);
    }

    /// <summary>Un subárbol hermano (Jugos, hijo directo de Bebidas) NUNCA aparece al pedir los
    /// descendientes de Gaseosas — mutation target 15 en su forma "descendientes", espejo de
    /// "sibling subtree never leaks" de ConstruirAncestros.</summary>
    [Fact]
    public void UnSubarbolHermanoNuncaAparece()
    {
        var descendientes = CadenaDeCategorias.ConstruirDescendientes(2, BosqueDeTresNiveles());

        Assert.Equal(new HashSet<int> { 2, 3 }, descendientes);
        Assert.DoesNotContain(4, descendientes);
    }

    /// <summary>Mismo bound que ConstruirAncestros: un ciclo corrupto (no alcanzable en operación
    /// normal — ReglaDeCategorias lo rechaza en escritura) no hace loopear el BFS.</summary>
    [Fact]
    public void UnCicloCorruptoNoHaceLoopearConstruirDescendientes()
    {
        var padrePorCategoria = new Dictionary<int, int?> { [1] = 2, [2] = 1 };

        var descendientes = CadenaDeCategorias.ConstruirDescendientes(1, padrePorCategoria);

        Assert.Equal(new HashSet<int> { 1, 2 }, descendientes);
    }

    /// <summary>design.md:212, mutation target 17: la propiedad de dualidad sobre CADA par del
    /// bosque de tres niveles — <c>d ∈ ConstruirDescendientes(c) ⟺ c ∈ ConstruirAncestros(d)</c>.
    /// Cualquier ruptura de dirección en cualquiera de las dos funciones falla acá.</summary>
    [Fact]
    public void LaDualidadEntreDescendientesYAncestrosSeCumpleParaCadaParDelBosque()
    {
        var padrePorCategoria = BosqueDeTresNiveles();
        var categorias = padrePorCategoria.Keys.ToList();

        foreach (var c in categorias)
        {
            foreach (var d in categorias)
            {
                var dEsDescendienteDeC = CadenaDeCategorias.ConstruirDescendientes(c, padrePorCategoria).Contains(d);
                var cEsAncestroDeD = CadenaDeCategorias.ConstruirAncestros(d, padrePorCategoria).Contains(c);

                Assert.True(
                    dEsDescendienteDeC == cEsAncestroDeD,
                    $"Dualidad rota para (c={c}, d={d}): descendiente={dEsDescendienteDeC}, ancestro={cEsAncestroDeD}");
            }
        }
    }
}
