using Ways.Domain.Ofertas;
using Ways.Domain.Ventas;

namespace Ways.Domain.Tests.Ofertas;

/// <summary>
/// Elección de la base entre las ofertas NO acumulables de la misma prioridad cuando alguna es "solo múltiplos": gana el
/// mayor descuento de LÍNEA a la cantidad del umbral (la de la línea, o la de la familia para una oferta de familia), no
/// el mayor descuento unitario. Los totales salen del camino real: <see cref="ResolvedorDeOfertas"/> →
/// <see cref="CoberturaDeOfertas"/> → <see cref="CalculadorDeTotales"/>.
/// </summary>
public class BaseConSoloMultiplosTests
{
    private static readonly DateOnly Fecha = new(2026, 8, 3);
    private static readonly TimeOnly Hora = new(12, 0);
    private const decimal Precio = 4000m;
    private const int IdFamilia = 5;

    private static LineaAResolver Linea(decimal cantidad, decimal? cantidadDeFamilia = null) =>
        new(1, null, [], 1, cantidad, Precio, Fecha, Hora, 1, IdFamilia, cantidadDeFamilia ?? cantidad);

    /// <summary>"2 x 7500" solo múltiplos: 250 por unidad en los grupos completos de 2.</summary>
    private static OfertaCandidata Estricta(
        int id = 1, decimal precioUnitario = 3750m, decimal cantidadMinima = 2m, int prioridad = 0,
        AlcanceDeOferta? alcance = null) =>
        new(id, "estricta", prioridad, false, alcance ?? AlcanceDeOferta.DeArticulo(1),
            BeneficioDeOferta.DePrecioUnitario(precioUnitario), cantidadMinima, null, null, null, null,
            new HashSet<int>(), new HashSet<int>(), SoloMultiplos: true);

    /// <summary>Oferta común sin cantidad mínima: 200 por unidad en todas.</summary>
    private static OfertaCandidata Comun(int id = 2, decimal precioUnitario = 3800m, int prioridad = 0) =>
        new(id, "comun", prioridad, false, AlcanceDeOferta.DeArticulo(1),
            BeneficioDeOferta.DePrecioUnitario(precioUnitario), null, null, null, null, null,
            new HashSet<int>(), new HashSet<int>());

    private static decimal Total(LineaAResolver linea, params OfertaCandidata[] candidatas)
    {
        var resuelto = ResolvedorDeOfertas.Resolver(linea, candidatas);
        var descuento = CoberturaDeOfertas.DescuentoDeLinea(resuelto, linea.Cantidad);

        return CalculadorDeTotales.Calcular(
            [new LineaParaCalcular(linea.Cantidad, resuelto.PrecioOriginal, resuelto.DescuentoUnitario,
                DescuentoDeOfertaDeLinea: descuento)]).Total;
    }

    private static int Base(LineaAResolver linea, params OfertaCandidata[] candidatas) =>
        Assert.Single(ResolvedorDeOfertas.Resolver(linea, candidatas).Aplicadas).IdOferta;

    /// <summary>Cláusula: el descuento de línea como primer criterio de desempate. A 3 unidades la "solo múltiplos" da
    /// 2 × 250 = 500 y la común 3 × 200 = 600: gana la común aunque su descuento unitario sea menor.</summary>
    [Fact]
    public void ATresUnidadesGanaLaComunPorqueDaMasDescuentoDeLinea()
    {
        Assert.Equal(2, Base(Linea(3m), Estricta(), Comun()));
        Assert.Equal(11400m, Total(Linea(3m), Estricta(), Comun()));
    }

    /// <summary>A 4 unidades la "solo múltiplos" da 4 × 250 = 1000 contra 800 de la común.</summary>
    [Fact]
    public void ACuatroUnidadesGanaLaSoloMultiplos()
    {
        Assert.Equal(1, Base(Linea(4m), Estricta(), Comun()));
        Assert.Equal(15000m, Total(Linea(4m), Estricta(), Comun()));
    }

    /// <summary>Debajo de su cantidad mínima la "solo múltiplos" no coincide y no tapa la base común. Coincidiendo, cubre
    /// al menos un grupo: su cantidad mínima es el tamaño del grupo.</summary>
    [Fact]
    public void UnaSoloMultiplosQueNoLlegaASuCantidadNoTapaLaBaseComun()
    {
        Assert.Equal(2, Base(Linea(1.999m), Estricta(), Comun()));
        Assert.Equal(3800m, ResolvedorDeOfertas.Resolver(Linea(1.999m), [Estricta(), Comun()]).PrecioFinal);
    }

    /// <summary>La prioridad sigue mandando: con más prioridad la "solo múltiplos" gana aunque dé menos.</summary>
    [Fact]
    public void LaPrioridadSigueMandandoSobreElDescuentoDeLinea()
    {
        Assert.Equal(1, Base(Linea(3m), Estricta(prioridad: 1), Comun()));
        Assert.Equal(11500m, Total(Linea(3m), Estricta(prioridad: 1), Comun()));

        Assert.Equal(2, Base(Linea(4m), Estricta(), Comun(prioridad: 1)));
    }

    /// <summary>Cláusula: el desempate por descuento unitario después del de línea. A 3 unidades, 2 × 300 = 3 × 200:
    /// gana la de mayor descuento unitario.</summary>
    [Fact]
    public void ConElMismoDescuentoDeLineaGanaElMayorDescuentoUnitario()
    {
        Assert.Equal(3, Base(Linea(3m), Estricta(id: 3, precioUnitario: 3700m), Comun(id: 2)));
    }

    /// <summary>Cláusula: con el mismo descuento de línea y unitario, la común le gana a la "solo múltiplos" sea cual sea
    /// el id. A 2 unidades, 2 × 250 = 2 × 250 y el mismo unitario.</summary>
    [Theory]
    [InlineData(1, 2)]
    [InlineData(3, 2)]
    public void ConElMismoDescuentoDeLineaYUnitarioGanaLaComun(int idEstricta, int idComun)
    {
        Assert.Equal(idComun, Base(Linea(2m), Estricta(id: idEstricta), Comun(id: idComun, precioUnitario: 3750m)));
    }

    /// <summary>Cláusula: el último desempate, el menor id, entre dos "solo múltiplos" iguales.</summary>
    [Theory]
    [InlineData(1, 3, 1)]
    [InlineData(4, 3, 3)]
    public void ConTodoIgualGanaElMenorId(int idUna, int idOtra, int ganadora)
    {
        Assert.Equal(ganadora, Base(Linea(2m), Estricta(id: idUna), Estricta(id: idOtra)));
    }

    /// <summary>Cláusula: la cantidad del umbral para una oferta de familia es la de la familia, no la de la línea. Con
    /// 1 unidad propia y 4 de familia, la de familia cubre todo (gana); con 2 propias y 3 de familia cubre 2 de 3
    /// (166,67 promedio contra 200: gana la común). Con la cantidad de la línea los dos resultados se invierten.</summary>
    [Theory]
    [InlineData(1, 4, 1)]
    [InlineData(2, 3, 2)]
    public void UnaSoloMultiplosDeFamiliaSeComparaConLaCantidadDeLaFamilia(
        decimal cantidad, decimal cantidadDeFamilia, int ganadora)
    {
        var deFamilia = Estricta(alcance: AlcanceDeOferta.DeFamilia(IdFamilia));

        Assert.Equal(ganadora, Base(Linea(cantidad, cantidadDeFamilia), deFamilia, Comun()));
    }

    /// <summary>Dos "solo múltiplos": a 5 unidades la de grupos de 2 (250) cubre 4 → 1000, la de grupos de 5 (220)
    /// cubre 5 → 1100. Gana la segunda aunque su descuento unitario sea menor.</summary>
    [Fact]
    public void EntreDosSoloMultiplosGanaLaQueMasCubre()
    {
        Assert.Equal(3, Base(Linea(5m), Estricta(id: 1), Estricta(id: 3, precioUnitario: 3780m, cantidadMinima: 5m)));
    }

    /// <summary>Sin "solo múltiplos" el orden es el de siempre: el mayor descuento unitario, aunque una oferta de familia
    /// tenga más unidades en el documento.</summary>
    [Fact]
    public void SinSoloMultiplosElOrdenEsElDescuentoUnitario()
    {
        var deFamilia = Comun(id: 3, precioUnitario: 3900m) with { Alcance = AlcanceDeOferta.DeFamilia(IdFamilia) };

        Assert.Equal(2, Base(Linea(1m, cantidadDeFamilia: 10m), deFamilia, Comun()));
    }
}
