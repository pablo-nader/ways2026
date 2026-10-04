using Ways.Domain.Compras;

namespace Ways.Domain.Tests.Compras;

/// <summary>
/// El costo y el precio sugerido de una compra frente a las familias de artículos (doc 10 §3), como funciones
/// puras: <see cref="CalculadorDeCompra.ResolverActualizacionesDeCosto"/> decide qué línea gana el costo de cada
/// familia o de cada artículo suelto, y <see cref="CalculadorDeCompra.ResolverLineasSuperadasDePrecio"/> qué
/// líneas con precio sugerido no se aplican por separado porque otra de su misma familia las supera. Los
/// artículos de una misma familia son idénticos en <c>costo_nominal</c> y en el estado de precios, así que de
/// todas sus líneas gana UNA: la de mayor <c>orden</c>, no la de mayor costo.
/// </summary>
public class CalculadorDeCompraFamiliasTests
{
    private static readonly IReadOnlyDictionary<int, int> SinFamilias = new Dictionary<int, int>();

    private static (int Orden, int? IdArticulo, bool ActualizaCosto, decimal CostoUnitario, decimal CostoEfectivo) Linea(
        int orden, int? idArticulo, decimal costo, bool actualizaCosto = true, decimal? costoUnitario = null) =>
        (orden, idArticulo, actualizaCosto, costoUnitario ?? costo, costo);

    // ---- ResolverActualizacionesDeCosto: por familia ----------------------------------------------

    /// <summary>De dos líneas de la misma familia gana la de mayor orden, aunque su costo sea el menor: es lo
    /// último que cargó quien armó la compra.</summary>
    [Fact]
    public void DeLasLineasDeUnaFamiliaGanaLaDeMayorOrdenYNoLaDeMayorCosto()
    {
        var familias = new Dictionary<int, int> { [101] = 5, [102] = 5 };

        var resultado = CalculadorDeCompra.ResolverActualizacionesDeCosto(
            [Linea(1, 101, 300m), Linea(2, 102, 200m)], familias);

        Assert.Equal([new ActualizacionDeCosto(IdFamilia: 5, IdArticulo: 102, Costo: 200m)], resultado);
    }

    /// <summary>El orden de las líneas es el que dice su <c>orden</c>, no su posición en la lista.</summary>
    [Fact]
    public void LaLineaGanadoraSalePorSuOrdenYNoPorSuPosicion()
    {
        var familias = new Dictionary<int, int> { [101] = 5, [102] = 5 };

        var resultado = CalculadorDeCompra.ResolverActualizacionesDeCosto(
            [Linea(2, 102, 200m), Linea(1, 101, 300m)], familias);

        Assert.Equal([new ActualizacionDeCosto(5, 102, 200m)], resultado);
    }

    /// <summary>Un artículo suelto conserva el dedupe por artículo, y convive con una familia en la misma
    /// compra: cada uno resuelve por su cuenta.</summary>
    [Fact]
    public void UnArticuloSueltoSigueDeduplicandoPorArticuloYConviveConUnaFamilia()
    {
        var familias = new Dictionary<int, int> { [101] = 5, [102] = 5 };

        var resultado = CalculadorDeCompra.ResolverActualizacionesDeCosto(
            [
                Linea(1, 101, 10m),
                Linea(2, 900, 20m),
                Linea(3, 102, 30m),
                Linea(4, 900, 40m)
            ],
            familias);

        Assert.Equal(
            [
                new ActualizacionDeCosto(5, 102, 30m),
                new ActualizacionDeCosto(null, 900, 40m)
            ],
            resultado);
    }

    [Fact]
    public void DosFamiliasDistintasResuelvenCadaUnaPorSuCuenta()
    {
        var familias = new Dictionary<int, int> { [101] = 5, [102] = 5, [201] = 6, [202] = 6 };

        var resultado = CalculadorDeCompra.ResolverActualizacionesDeCosto(
            [Linea(1, 101, 10m), Linea(2, 201, 20m), Linea(3, 102, 30m), Linea(4, 202, 40m)], familias);

        Assert.Equal(
            [
                new ActualizacionDeCosto(5, 102, 30m),
                new ActualizacionDeCosto(6, 202, 40m)
            ],
            resultado);
    }

    /// <summary>La familia 7 y el artículo 7 son identidades distintas: el id de una familia no se confunde con el
    /// de un artículo suelto.</summary>
    [Fact]
    public void UnIdDeFamiliaIgualAlDeUnArticuloSueltoNoLosFusiona()
    {
        var familias = new Dictionary<int, int> { [101] = 7 };

        var resultado = CalculadorDeCompra.ResolverActualizacionesDeCosto(
            [Linea(1, 101, 10m), Linea(2, 7, 20m)], familias);

        Assert.Equal(
            [
                new ActualizacionDeCosto(null, 7, 20m),
                new ActualizacionDeCosto(7, 101, 10m)
            ],
            resultado);
    }

    /// <summary>Las líneas que no actualizan el costo, o con costo unitario cero o negativo (el guard
    /// anti-bonificación), o por concepto, se descartan ANTES de elegir la ganadora: una línea de mayor orden
    /// que no cuenta no le gana a una de menor orden que sí.</summary>
    [Fact]
    public void LasLineasQueNoCuentanSeDescartanAntesDeElegirLaGanadoraDeLaFamilia()
    {
        var familias = new Dictionary<int, int> { [101] = 5, [102] = 5, [103] = 5, [104] = 5 };

        var resultado = CalculadorDeCompra.ResolverActualizacionesDeCosto(
            [
                Linea(1, 101, 100m),
                Linea(2, 102, 500m, actualizaCosto: false),
                Linea(3, 103, 0m, costoUnitario: 0m),
                Linea(4, null, 900m),
                Linea(5, 104, 700m, costoUnitario: -1m)
            ],
            familias);

        Assert.Equal([new ActualizacionDeCosto(5, 101, 100m)], resultado);
    }

    [Fact]
    public void SiNingunaLineaDeLaFamiliaCuentaLaFamiliaNoApareceEnElResultado()
    {
        var familias = new Dictionary<int, int> { [101] = 5 };

        Assert.Empty(CalculadorDeCompra.ResolverActualizacionesDeCosto(
            [Linea(1, 101, 100m, actualizaCosto: false)], familias));
    }

    /// <summary>Sin familias el resultado es el dedupe por artículo de siempre, ascendente por artículo.</summary>
    [Fact]
    public void SinFamiliasEsElDedupePorArticuloDeSiempre()
    {
        var resultado = CalculadorDeCompra.ResolverActualizacionesDeCosto(
            [Linea(1, 7, 100m), Linea(2, 7, 200m), Linea(3, 3, 50m)], SinFamilias);

        Assert.Equal(
            [
                new ActualizacionDeCosto(null, 3, 50m),
                new ActualizacionDeCosto(null, 7, 200m)
            ],
            resultado);
    }

    [Fact]
    public void ElResultadoNoDependeDelOrdenEnQueLlegaLaLista()
    {
        var familias = new Dictionary<int, int> { [101] = 5, [102] = 5 };
        var lineas = new[] { Linea(1, 101, 10m), Linea(2, 900, 20m), Linea(3, 102, 30m) };

        var deIda = CalculadorDeCompra.ResolverActualizacionesDeCosto(lineas, familias);
        var deVuelta = CalculadorDeCompra.ResolverActualizacionesDeCosto([.. lineas.Reverse()], familias);

        Assert.Equal(deIda, deVuelta);
    }

    // ---- ResolverLineasSuperadasDePrecio ----------------------------------------------------------

    private static (int Orden, int IdArticulo) Precio(int orden, int idArticulo) => (orden, idArticulo);

    [Fact]
    public void DeLasLineasDeUnaFamiliaSoloLaDeMayorOrdenSeAplicaYLasDemasQuedanSuperadasPorElla()
    {
        var familias = new Dictionary<int, int> { [101] = 5, [102] = 5, [103] = 5 };

        var superadas = CalculadorDeCompra.ResolverLineasSuperadasDePrecio(
            [Precio(1, 101), Precio(2, 102), Precio(3, 103)], familias);

        Assert.Equal(new Dictionary<int, int> { [1] = 3, [2] = 3 }, superadas);
    }

    /// <summary>La ganadora es la de mayor orden aunque llegue primero en la lista.</summary>
    [Fact]
    public void LaGanadoraSalePorSuOrdenYNoPorSuPosicion()
    {
        var familias = new Dictionary<int, int> { [101] = 5, [102] = 5 };

        var superadas = CalculadorDeCompra.ResolverLineasSuperadasDePrecio(
            [Precio(2, 102), Precio(1, 101)], familias);

        Assert.Equal(new Dictionary<int, int> { [1] = 2 }, superadas);
    }

    /// <summary>Un artículo suelto nunca queda superado, ni siquiera por otra línea del mismo artículo: cada
    /// línea se aplica, como siempre.</summary>
    [Fact]
    public void LasLineasDeArticulosSueltosNuncaQuedanSuperadas()
    {
        var superadas = CalculadorDeCompra.ResolverLineasSuperadasDePrecio(
            [Precio(1, 900), Precio(2, 900), Precio(3, 901)], SinFamilias);

        Assert.Empty(superadas);
    }

    [Fact]
    public void UnaFamiliaConUnaSolaLineaNoSuperaNiEsSuperada()
    {
        var familias = new Dictionary<int, int> { [101] = 5 };

        Assert.Empty(CalculadorDeCompra.ResolverLineasSuperadasDePrecio([Precio(1, 101), Precio(2, 900)], familias));
    }

    [Fact]
    public void LasFamiliasYLosSueltosNoSeAfectanEntreSi()
    {
        var familias = new Dictionary<int, int> { [101] = 5, [102] = 5, [201] = 6, [202] = 6 };

        var superadas = CalculadorDeCompra.ResolverLineasSuperadasDePrecio(
            [Precio(1, 101), Precio(2, 201), Precio(3, 900), Precio(4, 102), Precio(5, 202)], familias);

        Assert.Equal(new Dictionary<int, int> { [1] = 4, [2] = 5 }, superadas);
    }
}
