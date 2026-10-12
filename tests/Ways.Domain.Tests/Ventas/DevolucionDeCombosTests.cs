using Ways.Domain.Ventas;

namespace Ways.Domain.Tests.Ventas;

/// <summary>Reparto de una nota de crédito entre los items de combo de la venta original. Pura, sin base de datos.</summary>
public class DevolucionDeCombosTests
{
    private const int Pancho = 10;
    private const int Coca = 20;

    private static ItemVendidoConCombo Item(
        int idItem, int orden, int idArticulo, decimal cantidad, decimal precio, decimal total, int idCombo = 1) =>
        new(idItem, orden, idArticulo, cantidad, precio, total, idCombo, null, 1, 3, 21m);

    [Fact]
    public void DevolverTodasLasUnidadesDeUnItemDevuelveExactamenteSuTotal()
    {
        var vendidos = new[] { Item(1, 1, Pancho, 3m, 1000m, 2333m) };

        var tramos = DevolucionDeCombos.Repartir(vendidos, [new LineaDevuelta(Pancho, 3m)]);

        var tramo = Assert.Single(Assert.Single(tramos));
        Assert.Equal(2333m, tramo.Credito);
        Assert.Equal(3m, tramo.Cantidad);
        Assert.Equal(1, tramo.Origen.IdItem);
    }

    [Fact]
    public void UnaDevolucionParcialDevuelveElTotalProporcionalALasUnidadesRedondeadoAlPeso()
    {
        // 3 unidades pagaron 100: una vale 33 (33.33…), dos valen 67 (66.66…).
        var vendidos = new[] { Item(1, 1, Pancho, 3m, 40m, 100m) };

        Assert.Equal(33m, Assert.Single(Assert.Single(DevolucionDeCombos.Repartir(vendidos, [new LineaDevuelta(Pancho, 1m)]))).Credito);
        Assert.Equal(67m, Assert.Single(Assert.Single(DevolucionDeCombos.Repartir(vendidos, [new LineaDevuelta(Pancho, 2m)]))).Credito);
    }

    [Fact]
    public void DosUnidadesCobradasPorUnTotalImparSeDevuelvenEnPesosEnterosYLasMitadesSumanElTotal()
    {
        // 2 unidades pagaron 2727: cada una vale 1363.5, que redondea lejos de cero a 1364. Devolver la primera
        // acredita 1364 y devolver la segunda el resto, 1363: nunca un centavo, y entre las dos exactamente 2727.
        var vendidos = new[] { Item(1, 1, Pancho, 2m, 1500m, 2727m) };

        var deAUna = DevolucionDeCombos.Repartir(vendidos, [new LineaDevuelta(Pancho, 1m), new LineaDevuelta(Pancho, 1m)]);
        var creditos = deAUna.Select(t => Assert.Single(t).Credito).ToList();

        Assert.Equal([1364m, 1363m], creditos);
        Assert.Equal(2727m, creditos.Sum());

        var todas = Assert.Single(Assert.Single(DevolucionDeCombos.Repartir(vendidos, [new LineaDevuelta(Pancho, 2m)])));
        Assert.Equal(2727m, todas.Credito);
    }

    [Fact]
    public void LaMitadDeUnTotalImparRedondeaLejosDeCero()
    {
        var vendidos = new[] { Item(1, 1, Pancho, 2m, 1m, 1m) };

        Assert.Equal(1m, Assert.Single(Assert.Single(DevolucionDeCombos.Repartir(vendidos, [new LineaDevuelta(Pancho, 1m)]))).Credito);
    }

    [Fact]
    public void RepartirLasUnidadesEnVariasLineasDelMismoDocumentoDevuelveElTotalSinPerderPesos()
    {
        var vendidos = new[] { Item(1, 1, Pancho, 3m, 40m, 100m) };

        var tramos = DevolucionDeCombos.Repartir(
            vendidos, [new LineaDevuelta(Pancho, 1m), new LineaDevuelta(Pancho, 1m), new LineaDevuelta(Pancho, 1m)]);

        // Por unidades acumuladas: 33, 67 − 33 = 34 y 100 − 67 = 33.
        Assert.Equal([33m, 34m, 33m], tramos.Select(t => Assert.Single(t).Credito));
        Assert.Equal(100m, tramos.Sum(t => t[0].Credito));
    }

    [Fact]
    public void UnTotalMinimoRepartidoEnUnaUnidadPorLineaNuncaDaUnCreditoNegativoYSumaElTotal()
    {
        // 5 unidades pagaron 3: redondear toma por toma daba 1 × 5 (suma 5); por acumuladas: 1, 0, 1, 0, 1.
        var vendidos = new[] { Item(1, 1, Pancho, 5m, 1m, 3m) };

        var tramos = DevolucionDeCombos.Repartir(vendidos, Enumerable.Repeat(new LineaDevuelta(Pancho, 1m), 5).ToList());

        var creditos = tramos.Select(t => Assert.Single(t).Credito).ToList();
        Assert.Equal([1m, 0m, 1m, 0m, 1m], creditos);
        Assert.All(creditos, c => Assert.True(c >= 0m));
        Assert.Equal(3m, creditos.Sum());
    }

    [Fact]
    public void PorCadaTotalPequenoYCantidadNingunaTomaEsNegativaYLaSumaEsElTotalEnLineasDeUnaUnidadOEnDos()
    {
        for (var cantidad = 1; cantidad <= 12; cantidad++)
        {
            for (var total = 0; total <= 60; total++)
            {
                var vendidos = new[] { Item(1, 1, Pancho, cantidad, 1m, total) };

                // Una unidad por línea y, además, todo junto en una sola línea partida en dos.
                var deAUna = DevolucionDeCombos.Repartir(
                    vendidos, Enumerable.Repeat(new LineaDevuelta(Pancho, 1m), cantidad).ToList());
                var deAUnaCreditos = deAUna.Select(t => Assert.Single(t).Credito).ToList();
                Assert.All(deAUnaCreditos, c => Assert.True(c >= 0m && c == decimal.Truncate(c), $"cantidad {cantidad}, total {total}"));
                Assert.Equal(total, deAUnaCreditos.Sum());

                var mitad = cantidad / 2;
                var partidas = new List<LineaDevuelta>();
                if (mitad > 0)
                {
                    partidas.Add(new LineaDevuelta(Pancho, mitad));
                }

                partidas.Add(new LineaDevuelta(Pancho, cantidad - mitad));
                var enDos = DevolucionDeCombos.Repartir(vendidos, partidas);
                var enDosCreditos = enDos.SelectMany(t => t).Select(t => t.Credito).ToList();
                Assert.All(enDosCreditos, c => Assert.True(c >= 0m && c == decimal.Truncate(c), $"cantidad {cantidad}, total {total}"));
                Assert.Equal(total, enDosCreditos.Sum());
            }
        }
    }

    [Fact]
    public void LasUnidadesSeTomanDeLosItemsDelMismoArticuloEnOrdenDeOrdenYNoDeLlegada()
    {
        // El item de orden 1 (id 7) pagó menos que el de orden 2 (id 3): se devuelve primero el de menor orden,
        // aunque llegue último en la lista y tenga mayor id.
        var vendidos = new[]
        {
            Item(3, 2, Pancho, 1m, 3000m, 3000m, idCombo: 2),
            Item(7, 1, Pancho, 1m, 3000m, 2727m, idCombo: 1),
        };

        var tramos = DevolucionDeCombos.Repartir(vendidos, [new LineaDevuelta(Pancho, 1m)]);

        var tramo = Assert.Single(Assert.Single(tramos));
        Assert.Equal(7, tramo.Origen.IdItem);
        Assert.Equal(2727m, tramo.Credito);
    }

    [Fact]
    public void ConIgualOrdenDesempataElMenorIdDeItem()
    {
        var vendidos = new[]
        {
            Item(9, 1, Pancho, 1m, 3000m, 3000m),
            Item(4, 1, Pancho, 1m, 3000m, 2727m),
        };

        var tramo = Assert.Single(Assert.Single(DevolucionDeCombos.Repartir(vendidos, [new LineaDevuelta(Pancho, 1m)])));

        Assert.Equal(4, tramo.Origen.IdItem);
    }

    [Fact]
    public void UnaLineaQueCruzaDosItemsLosRecorreEnOrdenYCobraCadaTramoPorSuItem()
    {
        var vendidos = new[]
        {
            Item(1, 1, Pancho, 1m, 3000m, 2727m, idCombo: 1),
            Item(2, 3, Pancho, 1m, 3000m, 2800m, idCombo: 2),
        };

        var tramos = Assert.Single(DevolucionDeCombos.Repartir(vendidos, [new LineaDevuelta(Pancho, 2m)]));

        Assert.Equal([1, 2], tramos.Select(t => t.Origen.IdItem));
        Assert.Equal([2727m, 2800m], tramos.Select(t => t.Credito));
    }

    [Fact]
    public void LasUnidadesQueExcedenLasVendidasEnComboNoFiguranEnNingunTramo()
    {
        var vendidos = new[] { Item(1, 1, Pancho, 1m, 3000m, 2727m) };

        var tramos = Assert.Single(DevolucionDeCombos.Repartir(vendidos, [new LineaDevuelta(Pancho, 3m)]));

        var tramo = Assert.Single(tramos);
        Assert.Equal(1m, tramo.Cantidad);
        Assert.Equal(2727m, tramo.Credito);
    }

    [Fact]
    public void UnaSegundaLineaDelMismoArticuloNoVuelveAConsumirLoQueLaPrimeraYaTomo()
    {
        var vendidos = new[] { Item(1, 1, Pancho, 1m, 3000m, 2727m) };

        var tramos = DevolucionDeCombos.Repartir(vendidos, [new LineaDevuelta(Pancho, 1m), new LineaDevuelta(Pancho, 1m)]);

        Assert.Single(tramos[0]);
        Assert.Empty(tramos[1]);
    }

    [Fact]
    public void UnArticuloSinItemDeComboQuedaSinTramos()
    {
        var vendidos = new[] { Item(1, 1, Pancho, 1m, 3000m, 2727m) };

        var tramos = Assert.Single(DevolucionDeCombos.Repartir(vendidos, [new LineaDevuelta(Coca, 1m)]));

        Assert.Empty(tramos);
    }

    [Fact]
    public void SinItemsDeComboNingunaLineaTieneTramos()
    {
        var tramos = DevolucionDeCombos.Repartir([], [new LineaDevuelta(Pancho, 1m), new LineaDevuelta(Coca, 2m)]);

        Assert.All(tramos, Assert.Empty);
        Assert.Equal(2, tramos.Count);
    }

    [Fact]
    public void LosItemsConCantidadNoPositivaNoSeDevuelven()
    {
        var vendidos = new[] { Item(1, 1, Pancho, -1m, 3000m, -2727m) };

        Assert.Empty(Assert.Single(DevolucionDeCombos.Repartir(vendidos, [new LineaDevuelta(Pancho, 1m)])));
    }
}
