using Ways.Application.Precios;

namespace Ways.Application.Tests.Precios;

/// <summary>
/// El orden en que se toman los locks advisory de los pares (artículo, lista) de UN artículo que escribe en
/// varias listas —el miembro nuevo de una familia, que copia el estado de precios de cada lista—. La regla es la
/// de <c>OrdenDeLocksDePares</c>: ascendente por la CLAVE del lock y no por un id, porque la identidad de un lock
/// es su clave. Para un mismo artículo la clave de dos listas es un XOR con el id de cada lista, así que no sigue
/// el orden de <c>id_lista_precio</c>. Sin base de datos: <c>OrdenDeLocksDeParesDeUnArticulo</c> y
/// <c>ClaveDeLockDePar</c> son funciones puras.
/// </summary>
public class ServicioDePreciosOrdenDeLocksDeParesDeUnArticuloTests
{
    private const int Tenant = 7;

    private static (int, int) Clave(int idArticulo, int idLista) =>
        ServicioDePrecios.ClaveDeLockDePar(Tenant, idArticulo, idLista);

    /// <summary>Un artículo para el que la clave de la lista 1 es MAYOR que la de la lista 2: el XOR con el id de
    /// la lista invierte el orden de las dos cuando los dos bits bajos de <c>id * 397</c> son <c>10</c>.</summary>
    private static int ArticuloConLasClavesDeLasListas1Y2Cruzadas() =>
        Enumerable.Range(1, 100).First(id => (id * 397 & 3) == 2);

    [Fact]
    public void LasListasSeTomanEnOrdenAscendenteDeClaveYNoDeIdDeLista()
    {
        var idArticulo = ArticuloConLasClavesDeLasListas1Y2Cruzadas();

        // Precondición: por id de lista el orden es opuesto, es decir, el cruce existe.
        Assert.True(Clave(idArticulo, 1).Item2 > Clave(idArticulo, 2).Item2);

        var orden = ServicioDePrecios.OrdenDeLocksDeParesDeUnArticulo(Tenant, idArticulo, [1, 2]);

        Assert.Equal([2, 1], orden);
        Assert.Equal(orden.Select(l => Clave(idArticulo, l)).Order(), orden.Select(l => Clave(idArticulo, l)));
    }

    [Fact]
    public void ElOrdenNoDependeDelOrdenEnQueLlegan()
    {
        var idArticulo = ArticuloConLasClavesDeLasListas1Y2Cruzadas();
        int[] listas = [3, 8, 1, 21, 2, 90];

        var deIda = ServicioDePrecios.OrdenDeLocksDeParesDeUnArticulo(Tenant, idArticulo, listas);
        var deVuelta = ServicioDePrecios.OrdenDeLocksDeParesDeUnArticulo(Tenant, idArticulo, Enumerable.Reverse(listas));

        Assert.Equal(deIda, deVuelta);
    }

    [Fact]
    public void ElOrdenConservaExactamenteLasListasPedidas()
    {
        int[] listas = [90, 3, 21, 8, 1, 2];

        var orden = ServicioDePrecios.OrdenDeLocksDeParesDeUnArticulo(Tenant, 5, listas);

        Assert.Equal(listas.Order(), orden.Order());
    }

    [Fact]
    public void ConUnaSolaListaElOrdenEsEsaLista()
    {
        Assert.Equal([42], ServicioDePrecios.OrdenDeLocksDeParesDeUnArticulo(Tenant, 5, [42]));
    }

    [Fact]
    public void SinListasNoHayNingunLock()
    {
        Assert.Empty(ServicioDePrecios.OrdenDeLocksDeParesDeUnArticulo(Tenant, 5, []));
    }
}
