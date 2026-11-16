using Ways.Domain.Reportes;

namespace Ways.Domain.Tests.Reportes;

/// <summary>Reconstrucción del costo nominal pasado desde las líneas de compra que lo escribieron.</summary>
public class CostoHistoricoDeComprasTests
{
    private static readonly IReadOnlyDictionary<int, int> SinFamilias = new Dictionary<int, int>();

    private static DateTimeOffset Dia(int d) => new(2026, 10, d, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void GanaLaUltimaLineaAnteriorOIgualAlCorteYIgnoraLasPosteriores()
    {
        var lineas = new[]
        {
            new LineaQueEscribioCosto(1, Dia(1), 10, 1, 5m),
            new LineaQueEscribioCosto(1, Dia(10), 11, 1, 7m),
            new LineaQueEscribioCosto(1, Dia(20), 12, 1, 9m)
        };

        var costos = CostoHistoricoDeCompras.Resolver(lineas, Dia(10), [1], SinFamilias);

        Assert.Equal(7m, costos[1]);
    }

    [Fact]
    public void SinLineasAnterioresAlCorteElArticuloNoTieneCostoHistorico()
    {
        var lineas = new[] { new LineaQueEscribioCosto(1, Dia(10), 10, 1, 5m) };

        var costos = CostoHistoricoDeCompras.Resolver(lineas, Dia(9), [1], SinFamilias);

        Assert.Empty(costos);
    }

    [Fact]
    public void EnUnMismoInstanteDesempataPorComprobanteYPorOrden()
    {
        var lineas = new[]
        {
            new LineaQueEscribioCosto(1, Dia(5), 10, 2, 5m),
            new LineaQueEscribioCosto(1, Dia(5), 11, 1, 6m),
            new LineaQueEscribioCosto(1, Dia(5), 11, 3, 8m)
        };

        var costos = CostoHistoricoDeCompras.Resolver(lineas, Dia(5), [1], SinFamilias);

        Assert.Equal(8m, costos[1]);
    }

    [Fact]
    public void UnMiembroDeFamiliaHeredaElCostoDeLaUltimaLineaDeCualquierMiembro()
    {
        var familias = new Dictionary<int, int> { [1] = 100, [2] = 100, [3] = 200 };
        var lineas = new[]
        {
            new LineaQueEscribioCosto(1, Dia(1), 10, 1, 5m),
            new LineaQueEscribioCosto(2, Dia(8), 11, 1, 9m),
            new LineaQueEscribioCosto(3, Dia(9), 12, 1, 3m)
        };

        var costos = CostoHistoricoDeCompras.Resolver(lineas, Dia(15), [1, 2, 3], familias);

        Assert.Equal(9m, costos[1]);
        Assert.Equal(9m, costos[2]);
        Assert.Equal(3m, costos[3]);
    }

    [Fact]
    public void UnArticuloSueltoNoTomaLasLineasDeOtroArticulo()
    {
        var lineas = new[] { new LineaQueEscribioCosto(2, Dia(1), 10, 1, 5m) };

        var costos = CostoHistoricoDeCompras.Resolver(lineas, Dia(15), [1], SinFamilias);

        Assert.Empty(costos);
    }
}
