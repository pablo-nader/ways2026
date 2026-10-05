using Ways.Application.Precios;

namespace Ways.Application.Tests.Precios;

/// <summary>
/// El orden en que se toman los locks advisory de los pares (artículo, lista) cuando una escritura toma VARIOS
/// artículos en VARIAS listas —agrupar artículos en una familia alinea cada destino en cada lista fija—. La regla es
/// la de <c>OrdenDeLocksDePares</c> y la de <c>OrdenDeLocksDeParesDeUnArticulo</c>, de las que ésta es la general:
/// ascendente por la CLAVE del lock, no por artículo y después por lista, ni por lista y después por artículo.
/// Dos escrituras que compartan dos claves en orden cruzado respecto de cualquiera de esos dos criterios se esperarían
/// en ciclo. Sin base de datos: <c>OrdenDeLocksDeParesDeVariosArticulosYListas</c> y <c>ClaveDeLockDePar</c> son
/// funciones puras.
/// </summary>
public class ServicioDePreciosOrdenDeLocksDeParesDeVariosArticulosYListasTests
{
    private const int Tenant = 7;

    private static (int, int) Clave((int IdArticulo, int IdListaPrecio) par) =>
        ServicioDePrecios.ClaveDeLockDePar(Tenant, par.IdArticulo, par.IdListaPrecio);

    private static (int IdArticulo, int IdListaPrecio)[] Producto(int[] articulos, int[] listas) =>
        [.. articulos.SelectMany(articulo => listas.Select(lista => (articulo, lista)))];

    /// <summary>Dos artículos y dos listas cuyas cuatro claves, ascendentes, no siguen ni el orden por artículo y
    /// lista ni el orden por lista y artículo: con el artículo 2 la clave de la lista 2 es MENOR que la de la lista 1.
    /// Cualquiera de los dos criterios de ordenamiento simples deja la prueba en rojo.</summary>
    [Fact]
    public void LosParesSeTomanEnOrdenAscendenteDeClaveYNoPorArticuloNiPorLista()
    {
        var pares = Producto([1, 2], [1, 2]);

        var orden = ServicioDePrecios.OrdenDeLocksDeParesDeVariosArticulosYListas(Tenant, pares);

        Assert.Equal([(1, 1), (1, 2), (2, 2), (2, 1)], orden);

        // Precondición: ninguno de los dos criterios simples da este orden, es decir, el cruce existe.
        Assert.NotEqual(pares.OrderBy(par => par.IdArticulo).ThenBy(par => par.IdListaPrecio), orden);
        Assert.NotEqual(pares.OrderBy(par => par.IdListaPrecio).ThenBy(par => par.IdArticulo), orden);
        Assert.Equal(orden.Select(Clave).Order(), orden.Select(Clave));
    }

    [Fact]
    public void ElOrdenNoDependeDelOrdenEnQueLlegan()
    {
        var pares = Producto([3, 8, 1, 21], [2, 1, 90]);

        var deIda = ServicioDePrecios.OrdenDeLocksDeParesDeVariosArticulosYListas(Tenant, pares);
        var deVuelta = ServicioDePrecios.OrdenDeLocksDeParesDeVariosArticulosYListas(Tenant, Enumerable.Reverse(pares));

        Assert.Equal(deIda, deVuelta);
    }

    [Fact]
    public void ElOrdenConservaExactamenteLosParesPedidos()
    {
        var pares = Producto([90, 3, 21], [8, 1, 2]);

        var orden = ServicioDePrecios.OrdenDeLocksDeParesDeVariosArticulosYListas(Tenant, pares);

        Assert.Equal(pares.Order(), orden.Order());
    }

    /// <summary>Con una sola lista es el orden de <c>OrdenDeLocksDePares</c>: los ids elegidos cruzan el límite de los
    /// 2^31 del producto por 397, así que la clave y el id de artículo van en orden OPUESTO.</summary>
    [Fact]
    public void ConUnaSolaListaEsElOrdenDeLosArticulosDeLaEscrituraDeUnaLista()
    {
        int[] articulos = [5_000_000, 6_000_000, 4_000_000];

        var orden = ServicioDePrecios.OrdenDeLocksDeParesDeVariosArticulosYListas(Tenant, Producto(articulos, [1]));

        Assert.Equal(ServicioDePrecios.OrdenDeLocksDePares(Tenant, 1, articulos), orden.Select(par => par.IdArticulo));
        Assert.NotEqual(articulos.Order(), orden.Select(par => par.IdArticulo));
    }

    [Fact]
    public void ConUnSoloArticuloEsElOrdenDeLasListasDeLaEscrituraDeUnArticulo()
    {
        const int articulo = 2;
        int[] listas = [1, 2, 3, 4];

        var orden = ServicioDePrecios.OrdenDeLocksDeParesDeVariosArticulosYListas(Tenant, Producto([articulo], listas));

        Assert.Equal(ServicioDePrecios.OrdenDeLocksDeParesDeUnArticulo(Tenant, articulo, listas), orden.Select(par => par.IdListaPrecio));
        Assert.NotEqual(listas, orden.Select(par => par.IdListaPrecio));
    }

    [Fact]
    public void SinParesNoHayNingunLock()
    {
        Assert.Empty(ServicioDePrecios.OrdenDeLocksDeParesDeVariosArticulosYListas(Tenant, []));
    }
}
