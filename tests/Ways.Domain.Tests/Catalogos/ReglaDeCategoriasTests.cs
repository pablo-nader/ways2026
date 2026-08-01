using Ways.Domain.Catalogos;
using Ways.Domain.Common;

namespace Ways.Domain.Tests.Catalogos;

public class ReglaDeCategoriasTests
{
    [Theory]
    [InlineData(0, 0)] // raíz, sin hijos: profundidad 1
    [InlineData(1, 0)] // hijo de una raíz: profundidad 2
    [InlineData(2, 0)] // nieto: profundidad 3 (el límite)
    [InlineData(0, 2)] // raíz que va a colgar un subárbol de altura 2: profundidad 3
    public void ValidarProfundidadAceptaHastaElLimite(int nivelDelPadre, int alturaDelSubarbol)
    {
        ReglaDeCategorias.ValidarProfundidad(nivelDelPadre, alturaDelSubarbol);
    }

    [Fact]
    public void ValidarProfundidadRechazaElCuartoNivel()
    {
        var error = Assert.Throws<ErrorDominio>(() =>
            ReglaDeCategorias.ValidarProfundidad(nivelDelPadre: 3, alturaDelSubarbol: 0));

        Assert.Equal("categoria_profundidad_excedida", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Fact]
    public void ValidarProfundidadRechazaUnReparentQueEmpujaUnSubarbolMasAllaDelLimite()
    {
        // Mover un subárbol de altura 1 (nodo + un nivel de hijos) bajo un padre de nivel 2
        // daría profundidad 4: 2 + 1 + 1.
        var error = Assert.Throws<ErrorDominio>(() =>
            ReglaDeCategorias.ValidarProfundidad(nivelDelPadre: 2, alturaDelSubarbol: 1));

        Assert.Equal("categoria_profundidad_excedida", error.Codigo);
    }

    [Fact]
    public void ValidarSinCicloAceptaUnDestinoQueNoEsDescendiente()
    {
        ReglaDeCategorias.ValidarSinCiclo(idDestino: 5, descendientes: [10, 11, 12]);
    }

    [Fact]
    public void ValidarSinCicloRechazaMoverUnaCategoriaDentroDeSuPropioSubarbol()
    {
        var error = Assert.Throws<ErrorDominio>(() =>
            ReglaDeCategorias.ValidarSinCiclo(idDestino: 11, descendientes: [10, 11, 12]));

        Assert.Equal("categoria_ciclo", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }
}
