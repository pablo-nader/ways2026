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
}
