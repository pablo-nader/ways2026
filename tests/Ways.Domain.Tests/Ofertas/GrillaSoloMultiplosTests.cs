using Ways.Domain.Ofertas;

namespace Ways.Domain.Tests.Ofertas;

/// <summary>
/// Grilla de familia con ofertas "solo múltiplos" en competencia: los puntos de quiebre de una "solo múltiplos" de
/// familia van al eje de la familia y los de una de artículo al propio. La propiedad es la misma que la de la curva: en
/// cada par (cantidad de familia, cantidad propia) de tres decimales, lo que elige el dispositivo
/// (<c>elegirCeldaDeFamilia</c>, y la curva cuando la familia no llega a ninguna fila) es la resolución del servidor.
/// </summary>
public class GrillaSoloMultiplosTests
{
    private static readonly DateOnly Fecha = new(2026, 8, 3);
    private static readonly TimeOnly Hora = new(12, 0);
    private const decimal Precio = 4000m;
    private const int IdFamilia = 5;

    private static readonly LineaAResolver Linea =
        new(1, null, [], 1, 1m, Precio, Fecha, Hora, 1, IdFamilia, 1m);

    private static OfertaCandidata Oferta(
        int id, decimal precioUnitario, decimal? cantidadMinima, bool soloMultiplos, bool deFamilia, int prioridad = 0) =>
        new(id, "oferta", prioridad, false,
            deFamilia ? AlcanceDeOferta.DeFamilia(IdFamilia) : AlcanceDeOferta.DeArticulo(1),
            BeneficioDeOferta.DePrecioUnitario(precioUnitario), cantidadMinima, null, null, null, null,
            new HashSet<int>(), new HashSet<int>(), soloMultiplos);

    private static string Resumen(PrecioConOfertas resolucion) =>
        $"{resolucion.DescuentoUnitario}:" + string.Join(",", resolucion.Aplicadas.Select(a => $"{a.IdOferta}/{a.MultiploDe}"));

    /// <summary>Lo que hace el dispositivo: la grilla si la familia llegó a una fila, si no la curva.</summary>
    private static PrecioConOfertas Elegir(
        IReadOnlyList<CeldaDeFamilia> grilla, CurvaDeCantidad curva, decimal cantidad, decimal cantidadDeFamilia)
    {
        var alcanzadas = grilla.Where(c => c.CantidadDeFamiliaDesde <= cantidadDeFamilia).ToList();
        if (alcanzadas.Count == 0)
        {
            return curva.Escalones.Where(e => e.CantidadDesde <= cantidad)
                .Select(e => (PrecioConOfertas?)e.Resolucion).LastOrDefault() ?? curva.Base;
        }

        var fila = alcanzadas.Max(c => c.CantidadDeFamiliaDesde);
        var delUmbral = alcanzadas.Where(c => c.CantidadDeFamiliaDesde == fila).ToList();
        var quePueden = delUmbral.Where(c => c.CantidadDesde <= cantidad).ToList();
        return quePueden.Count > 0
            ? quePueden.MaxBy(c => c.CantidadDesde).Resolucion
            : delUmbral.MinBy(c => c.CantidadDesde).Resolucion;
    }

    private static PrecioConOfertas DelServidor(OfertaCandidata[] candidatas, decimal cantidad, decimal cantidadDeFamilia) =>
        ResolvedorDeOfertas.Resolver(Linea with { Cantidad = cantidad, CantidadDeFamilia = cantidadDeFamilia }, candidatas);

    private static void AssertParidad(
        OfertaCandidata[] candidatas, IEnumerable<decimal> cantidadesDeFamilia, IReadOnlyList<decimal> cantidadesPropias)
    {
        var grilla = EscalonesDeCantidad.ConstruirGrillaDeFamilia(Linea, candidatas);
        var curva = EscalonesDeCantidad.Construir(Linea, candidatas);

        foreach (var deFamilia in cantidadesDeFamilia)
        {
            foreach (var q in cantidadesPropias.Where(q => q <= deFamilia))
            {
                var servidor = Resumen(DelServidor(candidatas, q, deFamilia));
                var dispositivo = Resumen(Elegir(grilla, curva, q, deFamilia));
                if (servidor != dispositivo)
                {
                    Assert.Fail($"a {q} propias y {deFamilia} de familia: servidor {servidor}, dispositivo {dispositivo}");
                }
            }
        }
    }

    private static IEnumerable<decimal> Grilla(decimal desde, decimal hasta)
    {
        for (var q = desde; q <= hasta; q += EscalonesDeCantidad.PasoDeCantidad)
        {
            yield return q;
        }
    }

    /// <summary>Una "solo múltiplos" de familia contra una común de artículo: las filas son los puntos de quiebre sobre
    /// la cantidad de la familia en los que cambia la base (2, 2,501, 4…; en 2,5 empatan y sigue la de 2), con cualquier
    /// cantidad propia. Desde 8 gana siempre la "solo múltiplos": no hay más filas.</summary>
    [Fact]
    public void UnaSoloMultiplosDeFamiliaAgregaSusPuntosDeQuiebreALasFilas()
    {
        var candidatas = new[]
        {
            Oferta(1, 3750m, 2m, soloMultiplos: true, deFamilia: true),
            Oferta(2, 3800m, null, soloMultiplos: false, deFamilia: false),
        };

        var filas = EscalonesDeCantidad.ConstruirGrillaDeFamilia(Linea, candidatas)
            .Select(c => c.CantidadDeFamiliaDesde).Distinct();

        Assert.Equal([2m, 2.501m, 4m, 5.001m, 6m, 7.501m, 8m], filas);

        AssertParidad(candidatas, Grilla(1m, 13m), [1m, 1.5m, 2.5m, 3m]);
    }

    /// <summary>Una "solo múltiplos" de artículo contra una común, con una común de familia de cantidad mínima 3: las
    /// columnas son sus puntos de quiebre en los que cambia la base y la fila es la de la común de familia.</summary>
    [Fact]
    public void UnaSoloMultiplosDeArticuloAgregaSusPuntosDeQuiebreALasColumnas()
    {
        var candidatas = new[]
        {
            Oferta(1, 3750m, 2m, soloMultiplos: true, deFamilia: false),
            Oferta(2, 3800m, null, soloMultiplos: false, deFamilia: false),
            Oferta(3, 3850m, 3m, soloMultiplos: false, deFamilia: true),
        };

        var grilla = EscalonesDeCantidad.ConstruirGrillaDeFamilia(Linea, candidatas);

        Assert.Equal([3m], grilla.Select(c => c.CantidadDeFamiliaDesde).Distinct());
        Assert.Equal(
            [1m, 2m, 2.501m, 4m, 5.001m, 6m, 7.501m, 8m],
            grilla.Select(c => c.CantidadDesde));

        AssertParidad(candidatas, [1m, 2.999m, 3m, 4.5m, 7m, 14m], Grilla(1m, 14m).ToList());
    }

    /// <summary>Dos "solo múltiplos" de familia compiten sobre el mismo eje.</summary>
    [Fact]
    public void DosSoloMultiplosDeFamiliaCompitenSobreLaCantidadDeLaFamilia()
    {
        var candidatas = new[]
        {
            Oferta(1, 3750m, 2m, soloMultiplos: true, deFamilia: true),
            Oferta(3, 3700m, 3m, soloMultiplos: true, deFamilia: true),
            Oferta(2, 3790m, null, soloMultiplos: false, deFamilia: false),
        };

        AssertParidad(candidatas, Grilla(1m, 24m), [1m, 2m]);
    }

    /// <summary>Sin ofertas de familia la grilla sigue vacía aunque haya "solo múltiplos" en competencia: la curva
    /// alcanza.</summary>
    [Fact]
    public void SinOfertasDeFamiliaLaGrillaSigueVacia()
    {
        var candidatas = new[]
        {
            Oferta(1, 3750m, 2m, soloMultiplos: true, deFamilia: false),
            Oferta(2, 3800m, null, soloMultiplos: false, deFamilia: false),
        };

        Assert.Empty(EscalonesDeCantidad.ConstruirGrillaDeFamilia(Linea, candidatas));
    }

    /// <summary>Cláusula: el colapso de filas y columnas. Prioridad 1: una "solo múltiplos" de familia contra una común de
    /// familia (12 puntos sobre la familia); prioridad 0: una "solo múltiplos" de artículo contra una común de artículo
    /// (13 sobre la cantidad propia). El producto pasa de 160 celdas, pero la base siempre es de prioridad 1, así que en
    /// cada fila decide solo la familia: colapsada entra en el tope, no hace falta el recurso y el dispositivo cobra como
    /// el servidor.</summary>
    [Fact]
    public void ColapsadaUnaGrillaEnLaQueDecideUnEjeEntraEnElTopeYCobraComoElServidor()
    {
        var candidatas = new[]
        {
            Oferta(1, 3750m, 2m, soloMultiplos: true, deFamilia: true, prioridad: 1),
            Oferta(2, 3800m, null, soloMultiplos: false, deFamilia: true, prioridad: 1),
            Oferta(3, 3750m, 2m, soloMultiplos: true, deFamilia: false),
            Oferta(4, 3800m, null, soloMultiplos: false, deFamilia: false),
        };

        var grilla = EscalonesDeCantidad.ConstruirGrillaDeFamilia(Linea, candidatas);

        Assert.InRange(grilla.Count, 1, EscalonesDeCantidad.MaximoDeCeldasDeFamilia);
        Assert.Contains(grilla, c => c.Resolucion.Aplicadas.Any(a => a.IdOferta == 1));
        AssertParidad(candidatas, Grilla(1m, 13m), [1m, 1.5m, 2m, 2.5m, 2.501m, 3m, 4m, 7.501m, 11m]);
    }

    /// <summary>Cláusula: el tope de celdas. Una "solo múltiplos" de familia contra una común de familia (7 filas que
    /// cambian la base) y 20 acumulables de artículo con cantidades mínimas distintas (21 columnas que cambian el
    /// resultado en todas las filas): colapsada sigue pasando el tope, así que rige el recurso, sin las no acumulables de
    /// esa prioridad. La grilla queda dentro del tope y el dispositivo nunca descuenta más que el servidor.</summary>
    [Fact]
    public void PasadoElTopeDeCeldasRigeElRecursoYNuncaCobraMenosQueElServidor()
    {
        var acumulables = Enumerable.Range(0, 20).Select(i => new OfertaCandidata(
            10 + i, "acumulable", 0, true, AlcanceDeOferta.DeArticulo(1), BeneficioDeOferta.DeImporteFijo(1m),
            1.5m + (i * 0.5m), null, null, null, null, new HashSet<int>(), new HashSet<int>()));
        var candidatas = new[]
        {
            Oferta(1, 3750m, 2m, soloMultiplos: true, deFamilia: true),
            Oferta(2, 3800m, null, soloMultiplos: false, deFamilia: true),
        }.Concat(acumulables).ToArray();

        var grilla = EscalonesDeCantidad.ConstruirGrillaDeFamilia(Linea, candidatas);
        var curva = EscalonesDeCantidad.Construir(Linea, candidatas);

        Assert.InRange(grilla.Count, 1, EscalonesDeCantidad.MaximoDeCeldasDeFamilia);
        Assert.All(grilla, c => Assert.DoesNotContain(c.Resolucion.Aplicadas, a => a.IdOferta is 1 or 2));

        foreach (var deFamilia in new[] { 1.001m, 2m, 2.5m, 3m, 4m, 7.5m, 9m, 12m })
        {
            for (var q = 1m; q <= deFamilia; q += 0.25m)
            {
                var servidor = DelServidor(candidatas, q, deFamilia);
                var dispositivo = Elegir(grilla, curva, q, deFamilia);
                Assert.True(
                    CoberturaDeOfertas.DescuentoDeLinea(dispositivo, q) <= CoberturaDeOfertas.DescuentoDeLinea(servidor, q),
                    $"a {q} propias y {deFamilia} de familia el dispositivo descuenta más que el servidor");
            }
        }
    }

    public static TheoryData<string> Recursos() => ["no separable", "pasa el tope"];

    /// <summary>Cláusula: el recurso de la grilla. Una "solo múltiplos" de familia contra una de artículo de la misma
    /// prioridad depende de las dos cantidades a la vez; una contra una común de 249 pasa el tope. En los dos casos las
    /// celdas salen sin las no acumulables de esa prioridad, con una fila desde la cantidad de familia siguiente (la del
    /// umbral 2 resuelve igual y no viaja): el
    /// dispositivo nunca descuenta más que el servidor, y desde 6 de familia, donde gana la de prioridad 1, cobra lo
    /// mismo.</summary>
    [Theory]
    [MemberData(nameof(Recursos))]
    public void ConElRecursoLaGrillaNuncaCobraMenosQueElServidor(string recurso)
    {
        var competidora = recurso == "no separable"
            ? Oferta(2, 3700m, 3m, soloMultiplos: true, deFamilia: false)
            : Oferta(2, 3751m, null, soloMultiplos: false, deFamilia: false);
        var candidatas = new[]
        {
            Oferta(1, 3750m, 2m, soloMultiplos: true, deFamilia: true),
            competidora,
            Oferta(3, 3900m, 6m, soloMultiplos: false, deFamilia: true, prioridad: 1),
        };

        var grilla = EscalonesDeCantidad.ConstruirGrillaDeFamilia(Linea, candidatas);
        var curva = EscalonesDeCantidad.Construir(Linea, candidatas);

        Assert.Equal([1.001m, 6m], grilla.Select(c => c.CantidadDeFamiliaDesde).Distinct());
        Assert.All(grilla.Where(c => c.CantidadDeFamiliaDesde < 6m), c => Assert.Empty(c.Resolucion.Aplicadas));

        foreach (var deFamilia in new[] { 1.001m, 2m, 3m, 4.5m, 6m, 9m })
        {
            for (var q = 1m; q <= deFamilia; q += 0.25m)
            {
                var servidor = DelServidor(candidatas, q, deFamilia);
                var dispositivo = Elegir(grilla, curva, q, deFamilia);
                Assert.True(
                    CoberturaDeOfertas.DescuentoDeLinea(dispositivo, q) <= CoberturaDeOfertas.DescuentoDeLinea(servidor, q),
                    $"a {q} propias y {deFamilia} de familia el dispositivo descuenta más que el servidor");
                if (deFamilia >= 6m)
                {
                    Assert.Equal(Resumen(servidor), Resumen(dispositivo));
                }
            }
        }
    }
}
