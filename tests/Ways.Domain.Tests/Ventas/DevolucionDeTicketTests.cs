using Ways.Domain.Common;
using Ways.Domain.Ventas;

namespace Ways.Domain.Tests.Ventas;

/// <summary>Reparto de una nota de crédito entre los items del ticket de origen y tope por artículo. Pura, sin base de datos.</summary>
public class DevolucionDeTicketTests
{
    private const int Pancho = 10;
    private const int Coca = 20;
    private const int Fiambre = 30;

    private static ItemVendido Item(
        int idItem, int orden, int idArticulo, decimal cantidad, decimal precio, decimal total, int? idCombo = null,
        bool porImporte = false, int? idLote = null) =>
        new(idItem, orden, idArticulo, porImporte, cantidad, precio, total, idCombo, null, 1, 3, 21m, idLote);

    private static IReadOnlyList<UnidadesYaDevueltas> Nada => [];

    private static IReadOnlyList<IReadOnlyList<TramoDeDevolucion>> Repartir(
        IReadOnlyList<ItemVendido> vendidos, params LineaDevuelta[] devueltas) =>
        DevolucionDeTicket.Repartir(vendidos, Nada, devueltas);

    [Fact]
    public void DevolverTodasLasUnidadesDeUnItemDevuelveExactamenteSuTotal()
    {
        var vendidos = new[] { Item(1, 1, Pancho, 3m, 1000m, 2333m) };

        var tramo = Assert.Single(Assert.Single(Repartir(vendidos, new LineaDevuelta(Pancho, 3m))));

        Assert.Equal(2333m, tramo.Credito);
        Assert.Equal(3m, tramo.Cantidad);
        Assert.Equal(1, tramo.Origen.IdItem);
    }

    [Fact]
    public void UnaDevolucionParcialDevuelveElTotalProporcionalALasUnidadesRedondeadoAlPeso()
    {
        // 3 unidades pagaron 100: una vale 33 (33.33…), dos valen 67 (66.66…).
        var vendidos = new[] { Item(1, 1, Pancho, 3m, 40m, 100m) };

        Assert.Equal(33m, Assert.Single(Assert.Single(Repartir(vendidos, new LineaDevuelta(Pancho, 1m)))).Credito);
        Assert.Equal(67m, Assert.Single(Assert.Single(Repartir(vendidos, new LineaDevuelta(Pancho, 2m)))).Credito);
    }

    [Fact]
    public void LoQueSeDevuelveEsLoCobradoPorElItemSinImportarElPrecioDeLista()
    {
        // 2 unidades de lista 1000 se cobraron 1200 (oferta, descuento de línea y ajuste manual ya incluidos en el total):
        // devolver una acredita 600, no 1000.
        var vendidos = new[] { Item(1, 1, Pancho, 2m, 1000m, 1200m) };

        Assert.Equal(600m, Assert.Single(Assert.Single(Repartir(vendidos, new LineaDevuelta(Pancho, 1m)))).Credito);
    }

    [Fact]
    public void DosUnidadesCobradasPorUnTotalImparSeDevuelvenEnPesosEnterosYLasMitadesSumanElTotal()
    {
        // 2 unidades pagaron 2727: cada una vale 1363.5, que redondea lejos de cero a 1364. Devolver la primera
        // acredita 1364 y devolver la segunda el resto, 1363: nunca un centavo, y entre las dos exactamente 2727.
        var vendidos = new[] { Item(1, 1, Pancho, 2m, 1500m, 2727m) };

        var deAUna = Repartir(vendidos, new LineaDevuelta(Pancho, 1m), new LineaDevuelta(Pancho, 1m));
        var creditos = deAUna.Select(t => Assert.Single(t).Credito).ToList();

        Assert.Equal([1364m, 1363m], creditos);
        Assert.Equal(2727m, creditos.Sum());

        Assert.Equal(2727m, Assert.Single(Assert.Single(Repartir(vendidos, new LineaDevuelta(Pancho, 2m)))).Credito);
    }

    [Fact]
    public void LaMitadDeUnTotalImparRedondeaLejosDeCero()
    {
        var vendidos = new[] { Item(1, 1, Pancho, 2m, 1m, 1m) };

        Assert.Equal(1m, Assert.Single(Assert.Single(Repartir(vendidos, new LineaDevuelta(Pancho, 1m)))).Credito);
    }

    [Fact]
    public void RepartirLasUnidadesEnVariasLineasDelMismoDocumentoDevuelveElTotalSinPerderPesos()
    {
        var vendidos = new[] { Item(1, 1, Pancho, 3m, 40m, 100m) };

        var tramos = Repartir(
            vendidos, new LineaDevuelta(Pancho, 1m), new LineaDevuelta(Pancho, 1m), new LineaDevuelta(Pancho, 1m));

        // Por unidades acumuladas: 33, 67 − 33 = 34 y 100 − 67 = 33.
        Assert.Equal([33m, 34m, 33m], tramos.Select(t => Assert.Single(t).Credito));
        Assert.Equal(100m, tramos.Sum(t => t[0].Credito));
    }

    [Fact]
    public void PorCadaTotalPequenoYCantidadNingunaTomaEsNegativaYLaSumaEsElTotalEnLineasDeUnaUnidadOEnDos()
    {
        for (var cantidad = 1; cantidad <= 12; cantidad++)
        {
            for (var total = 0; total <= 60; total++)
            {
                var vendidos = new[] { Item(1, 1, Pancho, cantidad, 1m, total) };

                var deAUna = Repartir(vendidos, [.. Enumerable.Repeat(new LineaDevuelta(Pancho, 1m), cantidad)]);
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
                var enDosCreditos = Repartir(vendidos, [.. partidas]).SelectMany(t => t).Select(t => t.Credito).ToList();
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

        var tramo = Assert.Single(Assert.Single(Repartir(vendidos, new LineaDevuelta(Pancho, 1m))));

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

        var tramo = Assert.Single(Assert.Single(Repartir(vendidos, new LineaDevuelta(Pancho, 1m))));

        Assert.Equal(4, tramo.Origen.IdItem);
    }

    [Fact]
    public void UnaLineaQueCruzaDosItemsLosRecorreEnOrdenYCobraCadaTramoPorSuItem()
    {
        var vendidos = new[]
        {
            Item(1, 1, Pancho, 1m, 3000m, 2727m, idCombo: 1),
            Item(2, 3, Pancho, 1m, 3000m, 2800m),
        };

        var tramos = Assert.Single(Repartir(vendidos, new LineaDevuelta(Pancho, 2m)));

        Assert.Equal([1, 2], tramos.Select(t => t.Origen.IdItem));
        Assert.Equal([2727m, 2800m], tramos.Select(t => t.Credito));
    }

    // ---- Tope por artículo --------------------------------------------------------------------

    [Fact]
    public void PedirMasDeLoVendidoSeRechazaConElCodigoDelTope()
    {
        var vendidos = new[] { Item(1, 1, Pancho, 2m, 100m, 200m) };

        var excepcion = Assert.Throws<ErrorDominio>(() => Repartir(vendidos, new LineaDevuelta(Pancho, 3m)));

        Assert.Equal("devolucion_excede_lo_vendido", excepcion.Codigo);
        Assert.Equal(409, excepcion.EstadoHttp);
    }

    [Fact]
    public void DevolverExactamenteLoVendidoNoSeRechaza()
    {
        var vendidos = new[] { Item(1, 1, Pancho, 2m, 100m, 200m) };

        Assert.Equal(200m, Repartir(vendidos, new LineaDevuelta(Pancho, 2m)).Sum(t => t.Sum(x => x.Credito)));
    }

    [Fact]
    public void UnaSegundaLineaDelMismoArticuloNoVuelveAConsumirLoQueLaPrimeraYaTomo()
    {
        var vendidos = new[] { Item(1, 1, Pancho, 1m, 3000m, 2727m) };

        var excepcion = Assert.Throws<ErrorDominio>(() =>
            Repartir(vendidos, new LineaDevuelta(Pancho, 1m), new LineaDevuelta(Pancho, 1m)));

        Assert.Equal("devolucion_excede_lo_vendido", excepcion.Codigo);
    }

    [Fact]
    public void UnArticuloQueElTicketNoVendioSeRechaza()
    {
        var vendidos = new[] { Item(1, 1, Pancho, 1m, 3000m, 2727m) };

        var excepcion = Assert.Throws<ErrorDominio>(() => Repartir(vendidos, new LineaDevuelta(Coca, 1m)));

        Assert.Equal("devolucion_excede_lo_vendido", excepcion.Codigo);
    }

    [Fact]
    public void SinItemsNingunaDevolucionSeAcepta()
    {
        Assert.Throws<ErrorDominio>(() => Repartir([], new LineaDevuelta(Pancho, 1m)));
    }

    [Fact]
    public void LosItemsConCantidadNoPositivaNoSeDevuelven()
    {
        var vendidos = new[] { Item(1, 1, Pancho, -1m, 3000m, -2727m) };

        Assert.Throws<ErrorDominio>(() => Repartir(vendidos, new LineaDevuelta(Pancho, 1m)));
    }

    [Fact]
    public void LoYaDevueltoPorOtrasNotasSeDescuentaDelTope()
    {
        var vendidos = new[] { Item(1, 1, Pancho, 5m, 100m, 500m) };
        var yaDevueltas = new[] { new UnidadesYaDevueltas(Pancho, false, 0m, 4m) };

        Assert.Single(Assert.Single(DevolucionDeTicket.Repartir(vendidos, yaDevueltas, [new LineaDevuelta(Pancho, 1m)])));

        var excepcion = Assert.Throws<ErrorDominio>(() =>
            DevolucionDeTicket.Repartir(vendidos, yaDevueltas, [new LineaDevuelta(Pancho, 2m)]));
        Assert.Equal("devolucion_excede_lo_vendido", excepcion.Codigo);
    }

    [Fact]
    public void LoYaDevueltoConsumeLosItemsEnOrdenParaQueLasNotasPartidasAcrediteLoMismoQueUnaSola()
    {
        // Dos items del mismo artículo: 2 u por 500 (orden 1) y 2 u por 800 (orden 2). Devolver 3 de una vez o 1 + 2 en dos notas
        // acredita lo mismo, y el reparto de la segunda nota ve que la primera ya tomó una unidad del primer item.
        var vendidos = new[] { Item(1, 1, Pancho, 2m, 300m, 500m), Item(2, 2, Pancho, 2m, 400m, 800m) };

        var deUna = Assert.Single(Repartir(vendidos, new LineaDevuelta(Pancho, 3m)));
        var primera = Assert.Single(Repartir(vendidos, new LineaDevuelta(Pancho, 1m)));
        var segunda = Assert.Single(DevolucionDeTicket.Repartir(
            vendidos, [new UnidadesYaDevueltas(Pancho, false, 0m, 1m)], [new LineaDevuelta(Pancho, 2m)]));

        Assert.Equal(deUna.Sum(t => t.Credito), primera.Sum(t => t.Credito) + segunda.Sum(t => t.Credito));
        Assert.Equal([1, 2], segunda.Select(t => t.Origen.IdItem));
        Assert.Equal([250m, 400m], segunda.Select(t => t.Credito));
    }

    [Fact]
    public void AnularUnaNotaIntermediaNoHaceQueLoAcreditadoSuperePorUnPesoElTotalDelItem()
    {
        // T=100, C=3: se devuelve 1 (33), 1 (34) y se anula la primera. Sobrevive la nota que acreditó 34 por una unidad; reconstruirla
        // repartiendo desde cero daría 33 y la última devolución de 2 acreditaría 67 (total 101).
        var vendidos = new[] { Item(1, 1, Pancho, 3m, 40m, 100m) };
        var sobrevivientes = new[] { new UnidadesYaDevueltas(Pancho, false, 0m, 1m, Credito: 34m) };

        var ultima = Assert.Single(Assert.Single(DevolucionDeTicket.Repartir(
            vendidos, sobrevivientes, [new LineaDevuelta(Pancho, 2m)])));

        Assert.Equal(66m, ultima.Credito);
        Assert.Equal(100m, 34m + ultima.Credito);
    }

    [Fact]
    public void LoAcreditadoPorLasNotasSobrevivientesSeCargaAlUltimoItemTocado()
    {
        // Dos items del mismo artículo (2 u por 100 y 2 u por 100). Las notas sobrevivientes devolvieron 3 u y acreditaron 151 en total
        // (la repetición daría 150): la unidad pendiente del segundo item acredita lo que falta para su Total.
        var vendidos = new[] { Item(1, 1, Pancho, 2m, 50m, 100m), Item(2, 2, Pancho, 2m, 50m, 100m) };
        var sobrevivientes = new[] { new UnidadesYaDevueltas(Pancho, false, 0m, 3m, Credito: 151m) };

        var ultima = Assert.Single(Assert.Single(DevolucionDeTicket.Repartir(
            vendidos, sobrevivientes, [new LineaDevuelta(Pancho, 1m)])));

        Assert.Equal(49m, ultima.Credito);
    }

    [Fact]
    public void LoYaDevueltoDeUnArticuloNoTocaElTopeDeOtro()
    {
        var vendidos = new[] { Item(1, 1, Pancho, 1m, 100m, 100m), Item(2, 2, Coca, 1m, 50m, 50m) };
        var yaDevueltas = new[] { new UnidadesYaDevueltas(Pancho, false, 0m, 1m) };

        Assert.Single(Assert.Single(DevolucionDeTicket.Repartir(vendidos, yaDevueltas, [new LineaDevuelta(Coca, 1m)])));
    }

    [Fact]
    public void LoYaDevueltoQueSuperaLoVendidoDejaElTopeEnCero()
    {
        var vendidos = new[] { Item(1, 1, Pancho, 1m, 100m, 100m) };
        var yaDevueltas = new[] { new UnidadesYaDevueltas(Pancho, false, 0m, 5m) };

        Assert.Throws<ErrorDominio>(() =>
            DevolucionDeTicket.Repartir(vendidos, yaDevueltas, [new LineaDevuelta(Pancho, 1m)]));
    }

    // ---- Artículos por importe ----------------------------------------------------------------

    [Fact]
    public void UnArticuloPorImporteSeTopaPorImporteYNoMezclaLosDistintosImportes()
    {
        // El fiambre se vendió dos veces: por 150 (con −50 % cobró 75) y por 200. Cada importe es su propio conjunto.
        var vendidos = new[]
        {
            Item(1, 1, Fiambre, 1m, 150m, 75m, porImporte: true),
            Item(2, 2, Fiambre, 1m, 200m, 200m, porImporte: true),
        };

        var de150 = Assert.Single(Assert.Single(Repartir(vendidos, new LineaDevuelta(Fiambre, 1m, 150m))));
        Assert.Equal(1, de150.Origen.IdItem);
        Assert.Equal(75m, de150.Credito);

        var excepcion = Assert.Throws<ErrorDominio>(() =>
            Repartir(vendidos, new LineaDevuelta(Fiambre, 1m, 150m), new LineaDevuelta(Fiambre, 1m, 150m)));
        Assert.Equal("devolucion_excede_lo_vendido", excepcion.Codigo);

        Assert.Equal(200m, Assert.Single(Assert.Single(Repartir(vendidos, new LineaDevuelta(Fiambre, 1m, 200m)))).Credito);
    }

    [Fact]
    public void LoYaDevueltoPorImporteSoloDescuentaDeEseImporte()
    {
        var vendidos = new[]
        {
            Item(1, 1, Fiambre, 1m, 150m, 150m, porImporte: true),
            Item(2, 2, Fiambre, 1m, 200m, 200m, porImporte: true),
        };
        var yaDevueltas = new[] { new UnidadesYaDevueltas(Fiambre, true, 150m, 1m) };

        Assert.Throws<ErrorDominio>(() =>
            DevolucionDeTicket.Repartir(vendidos, yaDevueltas, [new LineaDevuelta(Fiambre, 1m, 150m)]));
        Assert.Single(Assert.Single(DevolucionDeTicket.Repartir(vendidos, yaDevueltas, [new LineaDevuelta(Fiambre, 1m, 200m)])));
    }

    [Fact]
    public void UnImporteQueElTicketNoVendioSeRechaza()
    {
        var vendidos = new[] { Item(1, 1, Fiambre, 1m, 150m, 150m, porImporte: true) };

        Assert.Throws<ErrorDominio>(() => Repartir(vendidos, new LineaDevuelta(Fiambre, 1m, 999m)));
    }

    // ---- Saldos -------------------------------------------------------------------------------

    [Fact]
    public void LosSaldosInformanLoVendidoLoDevueltoYLoQueQueda()
    {
        var vendidos = new[] { Item(1, 1, Pancho, 5m, 100m, 450m), Item(2, 2, Coca, 1m, 50m, 50m) };
        var yaDevueltas = new[] { new UnidadesYaDevueltas(Pancho, false, 0m, 2m) };

        var saldos = DevolucionDeTicket.Saldos(vendidos, yaDevueltas);

        var pancho = saldos[0];
        Assert.Equal(Pancho, pancho.IdArticulo);
        Assert.Equal(5m, pancho.Vendida);
        Assert.Equal(2m, pancho.Devuelta);
        Assert.Equal(3m, pancho.Devolvible);
        Assert.Equal(90m, pancho.ImporteUnitario);
        Assert.Equal(270m, pancho.ImporteDeLoRestante);

        Assert.Equal(1m, saldos[1].Devolvible);
    }

    [Fact]
    public void UnSaldoAgotadoNoTieneNadaPorDevolver()
    {
        var vendidos = new[] { Item(1, 1, Pancho, 1m, 100m, 100m) };
        var yaDevueltas = new[] { new UnidadesYaDevueltas(Pancho, false, 0m, 1m) };

        var saldo = Assert.Single(DevolucionDeTicket.Saldos(vendidos, yaDevueltas));

        Assert.Equal(0m, saldo.Devolvible);
        Assert.Equal(0m, saldo.ImporteDeLoRestante);
        Assert.Equal(0m, saldo.ImporteUnitario);
    }

    [Fact]
    public void LosSaldosDeUnArticuloPorImporteLlevanSuImporte()
    {
        var vendidos = new[] { Item(1, 1, Fiambre, 1m, 150m, 75m, porImporte: true) };

        var saldo = Assert.Single(DevolucionDeTicket.Saldos(vendidos, Nada));

        Assert.True(saldo.PorImporte);
        Assert.Equal(150m, saldo.PrecioUnitario);
        Assert.Equal(75m, saldo.ImporteDeLoRestante);
    }
}
