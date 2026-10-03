using Ways.Application.Precios;

namespace Ways.Application.Tests.Precios;

/// <summary>
/// El orden en que <c>ServicioDePrecios.AbrirNuevoPrecioAsync</c> toma los locks advisory de los pares
/// (artículo, lista) de una familia. La identidad de un lock es su CLAVE, y la clave de dos pares de
/// listas distintas puede coincidir: si cada escritura tomara los pares por <c>id_articulo</c>, dos
/// escrituras de la misma familia sobre dos listas podrían tomar las mismas dos claves en orden
/// opuesto y esperarse en ciclo. Sin base de datos: <c>OrdenDeLocksDePares</c> y
/// <c>ClaveDeLockDePar</c> son funciones puras.
/// </summary>
public class ServicioDePreciosOrdenDeLocksDeParesTests
{
    private const int Tenant = 7;

    /// <summary>Cada fila son dos escrituras: la lista y los artículos (ascendentes por id) de cada una,
    /// elegidos para que las dos claves de una sean las dos claves de la otra EN ORDEN CRUZADO respecto
    /// del id. Las dos primeras son la misma familia en dos listas; la tercera, dos familias distintas.</summary>
    public static TheoryData<int, int[], int, int[]> EscriturasConClavesCruzadas() => new()
    {
        { 1, [1, 2], 662, [1, 2] },
        { 7, [435, 442], 4066, [435, 442] },
        { 1, [10565, 10597], 6091170, [4746, 4778] }
    };

    private static (int, int) Clave(int idArticulo, int idLista) =>
        ServicioDePrecios.ClaveDeLockDePar(Tenant, idArticulo, idLista);

    private static List<(int, int)> ClavesEnElOrdenDeTomaDe(int idLista, int[] idsArticulo) =>
        ServicioDePrecios.OrdenDeLocksDePares(Tenant, idLista, idsArticulo)
            .Select(idArticulo => Clave(idArticulo, idLista))
            .ToList();

    /// <summary>El choque que esta prueba cierra: recorridas por <c>id_articulo</c>, las claves de una
    /// escritura son las de la otra al revés, así que cada una esperaría la que la otra ya tiene. Con
    /// el orden por clave las dos toman las mismas claves en la MISMA secuencia, y esa secuencia es
    /// ascendente.</summary>
    [Theory]
    [MemberData(nameof(EscriturasConClavesCruzadas))]
    public void DosEscriturasConClavesCruzadasTomanLosLocksEnElMismoOrden(
        int listaA, int[] idsA, int listaB, int[] idsB)
    {
        var clavesPorIdA = idsA.Select(id => Clave(id, listaA)).ToList();
        var clavesPorIdB = idsB.Select(id => Clave(id, listaB)).ToList();

        // Precondición: por id de artículo el orden es opuesto, es decir, el cruce existe.
        Assert.Equal(clavesPorIdA, Enumerable.Reverse(clavesPorIdB));

        var ordenA = ClavesEnElOrdenDeTomaDe(listaA, idsA);
        var ordenB = ClavesEnElOrdenDeTomaDe(listaB, idsB);

        Assert.Equal(ordenA, ordenB);
        Assert.Equal(ordenA.Order(), ordenA);
    }

    /// <summary>Lo que hace que una colisión solo pueda ocurrir entre listas distintas: dentro de una misma
    /// lista cada artículo tiene su propia clave (<c>397</c> es impar, así que multiplicar por él es
    /// inyectivo módulo 2^32). Se recorre un rango de ids en dos listas, no todo el dominio.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(662)]
    public void DentroDeUnaListaDosArticulosDistintosNuncaComparteClave(int idLista)
    {
        var claves = Enumerable.Range(1, 200_000).Select(id => Clave(id, idLista)).ToList();

        Assert.Equal(claves.Count, claves.Distinct().Count());
    }

    [Fact]
    public void ElOrdenNoDependeDelOrdenEnQueLlegaLaFamilia()
    {
        int[] ascendentes = [3, 8, 21, 22, 90, 435, 442];

        var deIda = ServicioDePrecios.OrdenDeLocksDePares(Tenant, 5, ascendentes);
        var deVuelta = ServicioDePrecios.OrdenDeLocksDePares(Tenant, 5, Enumerable.Reverse(ascendentes));

        Assert.Equal(deIda, deVuelta);
    }

    [Fact]
    public void ElOrdenConservaExactamenteLosArticulosPedidos()
    {
        int[] ids = [90, 3, 442, 21, 8, 435];

        var orden = ServicioDePrecios.OrdenDeLocksDePares(Tenant, 5, ids);

        Assert.Equal(ids.Order(), orden.Order());
    }

    [Fact]
    public void ConUnSoloArticuloElOrdenEsElMismoArticulo()
    {
        Assert.Equal([42], ServicioDePrecios.OrdenDeLocksDePares(Tenant, 5, [42]));
    }
}
