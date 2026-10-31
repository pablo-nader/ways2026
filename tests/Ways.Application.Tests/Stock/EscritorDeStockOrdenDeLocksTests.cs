using Ways.Application.Stock;

namespace Ways.Application.Tests.Stock;

/// <summary>
/// Fija el orden de locks de <see cref="EscritorDeStock.EnOrdenDeLocks{T}"/>: sucursal, artículo y lote, con la fila
/// agregada (lote nulo) antes que las de sus lotes. Es lo que evita el deadlock entre dos escritores que toquen las
/// mismas claves en sentido opuesto. Prueba la función pura; que cada escritor la use lo cubren los tests de
/// integración de stock por sucursal.
/// </summary>
public class EscritorDeStockOrdenDeLocksTests
{
    private sealed record Clave(int Sucursal, int Articulo, int? Lote);

    private static IReadOnlyList<Clave> Ordenar(IEnumerable<Clave> claves) =>
        [.. EscritorDeStock.EnOrdenDeLocks(claves, c => c.Sucursal, c => c.Articulo, c => c.Lote)];

    /// <summary>Cláusulas bajo prueba, una por criterio: con las claves en orden inverso, cada nivel solo se ordena bien si
    /// su propia clave está. Mutaciones registradas: invertir <c>OrderBy(sucursal)</c>, invertir <c>ThenBy(articulo)</c>,
    /// quitar <c>ThenBy(lote.HasValue)</c> (el nulo cae donde caiga) e invertir <c>ThenBy(lote ?? 0)</c> hacen fallar
    /// este test.</summary>
    [Fact]
    public void OrdenaPorSucursalLuegoArticuloLuegoLoteConElAgregadoPrimero()
    {
        var desordenadas = new Clave[]
        {
            new(2, 1, 5),
            new(2, 1, null),
            new(1, 9, 3),
            new(1, 9, 2),
            new(1, 9, null),
            new(1, 4, 8),
            new(2, 1, 4)
        };

        var ordenadas = Ordenar(desordenadas);

        Assert.Equal(
            [
                new Clave(1, 4, 8),
                new Clave(1, 9, null),
                new Clave(1, 9, 2),
                new Clave(1, 9, 3),
                new Clave(2, 1, null),
                new Clave(2, 1, 4),
                new Clave(2, 1, 5)
            ],
            ordenadas);
    }

    /// <summary>Un lote con id 0 no puede colarse antes de la fila agregada: el nulo va primero por su propia clave, no
    /// porque <c>lote ?? 0</c> lo ponga primero. Mutación registrada: quitar <c>ThenBy(lote.HasValue)</c> deja empatados
    /// el agregado y el lote 0, y el orden depende de la entrada.</summary>
    [Fact]
    public void ElAgregadoVaAntesQueUnLoteConIdCeroSeaCualSeaLaEntrada()
    {
        var conLoteCeroPrimero = Ordenar([new Clave(1, 1, 0), new Clave(1, 1, null)]);
        var conAgregadoPrimero = Ordenar([new Clave(1, 1, null), new Clave(1, 1, 0)]);

        Assert.Equal([new Clave(1, 1, null), new Clave(1, 1, 0)], conLoteCeroPrimero);
        Assert.Equal([new Clave(1, 1, null), new Clave(1, 1, 0)], conAgregadoPrimero);
    }
}
