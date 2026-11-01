using Ways.Domain.Ofertas;
using Ways.Domain.Ventas;

namespace Ways.Domain.Tests.Ofertas;

/// <summary>
/// Curva de la instantánea offline con ofertas "solo múltiplos" en competencia: como la base se elige por descuento de
/// línea (<see cref="ResolvedorDeOfertas"/>), la ganadora puede cambiar entre dos <c>cantidad_minima</c> y
/// <see cref="EscalonesDeCantidad.DescubrirPuntosDeQuiebre"/> agrega esas cantidades. La propiedad central es la
/// paridad: en cada cantidad del punto de venta (tres decimales) el escalón que elige el dispositivo — el último con
/// <c>CantidadDesde &lt;= cantidad</c>, o la base — es exactamente la resolución del servidor a esa cantidad.
/// </summary>
public class PuntosDeQuiebreSoloMultiplosTests
{
    private static readonly DateOnly Fecha = new(2026, 8, 3);
    private static readonly TimeOnly Hora = new(12, 0);
    private const decimal Precio = 4000m;
    private const int IdFamilia = 5;

    private static LineaAResolver Linea(decimal cantidad = 1m, decimal? cantidadDeFamilia = null) =>
        new(1, null, [], 1, cantidad, Precio, Fecha, Hora, 1, IdFamilia, cantidadDeFamilia ?? cantidad);

    private static OfertaCandidata Estricta(
        int id, decimal precioUnitario, decimal cantidadMinima, int prioridad = 0, AlcanceDeOferta? alcance = null) =>
        new(id, "estricta", prioridad, false, alcance ?? AlcanceDeOferta.DeArticulo(1),
            BeneficioDeOferta.DePrecioUnitario(precioUnitario), cantidadMinima, null, null, null, null,
            new HashSet<int>(), new HashSet<int>(), SoloMultiplos: true);

    private static OfertaCandidata Comun(
        int id, BeneficioDeOferta beneficio, decimal? cantidadMinima = null, int prioridad = 0, bool acumulable = false) =>
        new(id, "comun", prioridad, acumulable, AlcanceDeOferta.DeArticulo(1), beneficio, cantidadMinima,
            null, null, null, null, new HashSet<int>(), new HashSet<int>());

    private static OfertaCandidata Comun(int id, decimal precioUnitario, int prioridad = 0) =>
        Comun(id, BeneficioDeOferta.DePrecioUnitario(precioUnitario), prioridad: prioridad);

    /// <summary>Lo que hace el dispositivo (<c>elegirEscalon</c>): el último escalón que entra en la cantidad, o la
    /// base.</summary>
    private static PrecioConOfertas Elegir(CurvaDeCantidad curva, decimal cantidad) =>
        curva.Escalones.Where(e => e.CantidadDesde <= cantidad).Select(e => (PrecioConOfertas?)e.Resolucion).LastOrDefault()
            ?? curva.Base;

    private static LineaAResolver ConCantidad(LineaAResolver linea, decimal cantidad) =>
        linea with { Cantidad = cantidad, CantidadDeFamilia = linea.CantidadDeFamilia + (cantidad - linea.Cantidad) };

    private static string Resumen(PrecioConOfertas resolucion) =>
        $"{resolucion.DescuentoUnitario}:" + string.Join(",", resolucion.Aplicadas.Select(a => $"{a.IdOferta}/{a.MultiploDe}"));

    private static decimal TotalDeLinea(PrecioConOfertas resolucion, decimal cantidad) =>
        CalculadorDeTotales.Calcular(
            [new LineaParaCalcular(cantidad, resolucion.PrecioOriginal, resolucion.DescuentoUnitario,
                DescuentoDeOfertaDeLinea: CoberturaDeOfertas.DescuentoDeLinea(resolucion, cantidad))]).Total;

    private static readonly OfertaCandidata[] DelEjemplo = [Estricta(1, 3750m, 2m), Comun(2, 3800m)];

    /// <summary>Cláusula: los múltiplos y los cruces de <see cref="EscalonesDeCantidad.DescubrirPuntosDeQuiebre"/>.
    /// 250 por unidad en grupos de 2 contra 200 en todas: múltiplos 2…10 (desde 10 la "solo múltiplos" gana siempre:
    /// 250 × 2 / 50) y los cruces 2 × 250 × k / 200 = 2,5; 5; 7,5; 10, que caen justo en una cantidad y empatan ahí, así
    /// que también va la siguiente.</summary>
    [Fact]
    public void LosPuntosDeQuiebreSonLosMultiplosYLosCrucesConLaCantidadSiguiente()
    {
        var puntos = EscalonesDeCantidad.DescubrirPuntosDeQuiebre(Linea(), DelEjemplo);

        Assert.Equal(
            [2m, 2.5m, 2.501m, 4m, 5m, 5.001m, 6m, 7.5m, 7.501m, 8m, 10m, 10.001m],
            puntos);
    }

    /// <summary>La curva colapsada alterna entre las dos y desde 8 queda la "solo múltiplos". En el empate exacto
    /// (5 unidades: 4 × 250 = 5 × 200) gana el mayor descuento unitario.</summary>
    [Fact]
    public void LaCurvaAlternaEntreLaSoloMultiplosYLaComunHastaQueLaSoloMultiplosGanaSiempre()
    {
        var curva = EscalonesDeCantidad.Construir(Linea(), DelEjemplo);

        Assert.Equal("200:2/", Resumen(curva.Base));
        Assert.Equal(
            [(2m, "250:1/2"), (2.501m, "200:2/"), (4m, "250:1/2"), (5.001m, "200:2/"), (6m, "250:1/2"),
                (7.501m, "200:2/"), (8m, "250:1/2")],
            curva.Escalones.Select(e => (e.CantidadDesde, Resumen(e.Resolucion))));
    }

    /// <summary>Totales del dispositivo con la curva del ejemplo: los mismos que en <c>coberturaDeOfertas.test.ts</c>
    /// (paridad con el POS). Cada uno es también el del servidor a esa cantidad.</summary>
    [Theory]
    [InlineData(1, 3800)]
    [InlineData(2, 7500)]
    [InlineData(2.5, 9500)]
    [InlineData(2.501, 9504)]
    [InlineData(3, 11400)]
    [InlineData(4, 15000)]
    [InlineData(5, 19000)]
    [InlineData(5.001, 19004)]
    [InlineData(7, 26500)]
    [InlineData(7.5, 28500)]
    [InlineData(7.501, 28504)]
    [InlineData(9, 34000)]
    [InlineData(11, 41500)]
    public void LosTotalesDelDispositivoConLaCurvaDelEjemploSonLosDelServidor(decimal cantidad, decimal total)
    {
        var curva = EscalonesDeCantidad.Construir(Linea(), DelEjemplo);

        Assert.Equal(total, TotalDeLinea(Elegir(curva, cantidad), cantidad));
        Assert.Equal(total, TotalDeLinea(ResolvedorDeOfertas.Resolver(Linea(cantidad), DelEjemplo), cantidad));
    }

    private static (decimal Cantidad, decimal? CantidadDeFamilia, OfertaCandidata[] Candidatas) Caso(
        decimal cantidad, decimal? cantidadDeFamilia, params OfertaCandidata[] candidatas) =>
        (cantidad, cantidadDeFamilia, candidatas);

    private static readonly Dictionary<string, (decimal Cantidad, decimal? CantidadDeFamilia, OfertaCandidata[] Candidatas)>
        Casos = new()
        {
            ["250 x 2 contra 200"] = Caso(0.001m, null, DelEjemplo),
            ["250 x 2 contra 230"] = Caso(0.001m, null, Estricta(1, 3750m, 2m), Comun(2, 3770m)),
            ["300 x 1,5 contra 250"] = Caso(0.001m, null, Estricta(1, 3700m, 1.5m), Comun(2, 3750m)),
            ["250 x 3 contra 210, cruces entre dos cantidades"] = Caso(
                0.001m, null, Estricta(1, 3750m, 3m), Comun(2, 3790m)),
            ["7 % x 2 contra 230 fijo"] = Caso(
                0.001m, null,
                Estricta(1, 0m, 2m) with { Beneficio = BeneficioDeOferta.DePorcentaje(7m) },
                Comun(2, BeneficioDeOferta.DeImporteFijo(230m))),
            ["100 x 0,75 contra 70"] = Caso(0.001m, null, Estricta(1, 3900m, 0.75m), Comun(2, 3930m)),
            ["dos solo múltiplos y una común"] = Caso(
                0.001m, null, Estricta(1, 3750m, 2m), Estricta(3, 3700m, 3m), Comun(2, 3790m)),
            ["una común de menor prioridad no compite"] = Caso(
                0.001m, null, Estricta(3, 3750m, 2m), Comun(2, 3780m), Comun(1, 3700m, prioridad: -1)),
            ["solo múltiplos de familia con 2 unidades de otros artículos"] = Caso(
                1m, 3m, Estricta(1, 3750m, 2m, alcance: AlcanceDeOferta.DeFamilia(IdFamilia)), Comun(2, 3800m)),
            ["con una común de cantidad mínima y una acumulable"] = Caso(
                0.001m, null, Estricta(1, 3750m, 2m), Comun(2, 3800m),
                Comun(4, BeneficioDeOferta.DePrecioUnitario(3770m), cantidadMinima: 3.5m),
                Comun(5, BeneficioDeOferta.DeImporteFijo(10m), acumulable: true)),
        };

    public static TheoryData<string> NombresDeLosCasos() => new(Casos.Keys);

    /// <summary>La propiedad de paridad, cantidad por cantidad de tres decimales desde la de la línea hasta pasado el
    /// último punto de quiebre: lo que elige el dispositivo es lo que resuelve el servidor (mismo descuento unitario,
    /// mismas ofertas en el mismo orden, mismo tamaño de grupo).</summary>
    [Theory]
    [MemberData(nameof(NombresDeLosCasos))]
    public void EnCadaCantidadElEscalonDelDispositivoEsLaResolucionDelServidor(string caso)
    {
        var (cantidad, cantidadDeFamilia, candidatas) = Casos[caso];
        var linea = Linea(cantidad, cantidadDeFamilia);
        var puntos = EscalonesDeCantidad.DescubrirPuntosDeQuiebre(linea, candidatas);
        Assert.NotNull(puntos);
        Assert.NotEmpty(puntos);

        var curva = EscalonesDeCantidad.Construir(linea, candidatas);
        var hasta = puntos[^1] + 4m;

        for (var q = cantidad; q <= hasta; q += EscalonesDeCantidad.PasoDeCantidad)
        {
            var delServidor = ResolvedorDeOfertas.Resolver(ConCantidad(linea, q), candidatas);
            var delDispositivo = Elegir(curva, q);
            if (Resumen(delServidor) != Resumen(delDispositivo))
            {
                Assert.Fail($"{caso}: a {q} el servidor resuelve {Resumen(delServidor)} y el dispositivo {Resumen(delDispositivo)}");
            }
        }
    }

    /// <summary>Cláusula: el redondeo hacia arriba de un cruce que cae entre dos cantidades. 250 en grupos de 3 contra
    /// 210: en el tramo 3–6 se cruzan en 750 / 210 = 3,5714…; a 3,571 todavía gana la "solo múltiplos" y a 3,572 la
    /// común.</summary>
    [Fact]
    public void UnCruceEntreDosCantidadesSeLlevaALaSiguiente()
    {
        var candidatas = new[] { Estricta(1, 3750m, 3m), Comun(2, 3790m) };

        var puntos = EscalonesDeCantidad.DescubrirPuntosDeQuiebre(Linea(), candidatas)!;

        Assert.Contains(3.572m, puntos);
        Assert.DoesNotContain(3.571m, puntos);
        Assert.Equal(1, ResolvedorDeOfertas.Resolver(Linea(3.571m), candidatas).Aplicadas.Single().IdOferta);
        Assert.Equal(2, ResolvedorDeOfertas.Resolver(Linea(3.572m), candidatas).Aplicadas.Single().IdOferta);
    }

    /// <summary>Con menos descuento unitario la "solo múltiplos" no le gana nunca a la común; con el mismo y un id
    /// mayor, tampoco (pierde todos los empates): no hay puntos de quiebre.</summary>
    [Theory]
    [InlineData(3790, 1)]
    [InlineData(3750, 3)]
    public void SiLaSoloMultiplosNoPuedeGanarNoHayPuntosDeQuiebre(decimal precioDeLaEstricta, int idDeLaEstricta)
    {
        var candidatas = new[] { Estricta(idDeLaEstricta, precioDeLaEstricta, 2m), Comun(2, 3750m) };

        Assert.Equal([], EscalonesDeCantidad.DescubrirPuntosDeQuiebre(Linea(), candidatas));
    }

    /// <summary>250 contra 249 alterna hasta 1000 unidades: pasa el tope.</summary>
    [Fact]
    public void UnaCompetenciaQuePasaElTopeNoTienePuntosDeQuiebre()
    {
        var candidatas = new[] { Estricta(1, 3750m, 2m), Comun(2, 3751m) };

        Assert.Null(EscalonesDeCantidad.DescubrirPuntosDeQuiebre(Linea(), candidatas));
    }

    /// <summary>Cláusula: el desempate entre una "solo múltiplos" y una común con el mismo descuento de línea y unitario
    /// (solo pasa en los múltiplos): gana la común aunque la "solo múltiplos" tenga menor id, así que la base no cambia
    /// con la cantidad, la curva no tiene puntos de quiebre y el dispositivo cobra, cantidad por cantidad, lo mismo que
    /// el servidor (antes caía en el recurso y cobraba el precio de lista).</summary>
    [Fact]
    public void ConElMismoDescuentoYLaSoloMultiplosDeMenorIdElDispositivoCobraComoElServidor()
    {
        var candidatas = new[] { Estricta(1, 3750m, 2m), Comun(2, 3750m) };
        var linea = Linea(0.001m);

        Assert.Equal([], EscalonesDeCantidad.DescubrirPuntosDeQuiebre(linea, candidatas));

        var curva = EscalonesDeCantidad.Construir(linea, candidatas);
        for (var q = 0.001m; q <= 12m; q += EscalonesDeCantidad.PasoDeCantidad)
        {
            var delServidor = ResolvedorDeOfertas.Resolver(ConCantidad(linea, q), candidatas);
            var delDispositivo = Elegir(curva, q);
            if (Resumen(delServidor) != Resumen(delDispositivo) ||
                TotalDeLinea(delServidor, q) != TotalDeLinea(delDispositivo, q))
            {
                Assert.Fail($"a {q} el servidor resuelve {Resumen(delServidor)} y el dispositivo {Resumen(delDispositivo)}");
            }
        }
    }

    /// <summary>Cláusula: el recurso pasado el tope. Sin puntos de quiebre, la curva saca las no acumulables de la
    /// prioridad en competencia y de las menores desde la cantidad siguiente a la de la línea: el dispositivo nunca da
    /// más descuento de línea que el servidor. La de mayor prioridad (cantidad mínima 10) sigue igual que en el
    /// servidor, y la acumulable también.</summary>
    [Fact]
    public void PasadoElTopeElDispositivoNuncaCobraMenosQueElServidor()
    {
        var candidatas = new[]
        {
            Estricta(1, 3750m, 2m), Comun(2, 3751m), Comun(3, 3000m, prioridad: -1),
            Comun(4, BeneficioDeOferta.DePrecioUnitario(3900m), cantidadMinima: 10m, prioridad: 1),
            Comun(5, BeneficioDeOferta.DeImporteFijo(10m), acumulable: true),
        };

        var curva = EscalonesDeCantidad.Construir(Linea(), candidatas);

        Assert.Equal("259:2/,5/", Resumen(curva.Base));
        Assert.Equal(
            [(1.001m, "10:5/"), (10m, "110:4/,5/")],
            curva.Escalones.Select(e => (e.CantidadDesde, Resumen(e.Resolucion))));

        for (var q = 1.001m; q <= 20m; q += 0.001m)
        {
            var delServidor = ResolvedorDeOfertas.Resolver(Linea(q), candidatas);
            var delDispositivo = Elegir(curva, q);
            Assert.True(
                CoberturaDeOfertas.DescuentoDeLinea(delDispositivo, q) <= CoberturaDeOfertas.DescuentoDeLinea(delServidor, q),
                $"a {q} el dispositivo descuenta más que el servidor");
            if (q >= 10m)
            {
                Assert.Equal(Resumen(delServidor), Resumen(delDispositivo));
            }
        }
    }

    /// <summary>Sin "solo múltiplos" en competencia la curva es la de siempre: solo los umbrales de cantidad mínima.</summary>
    [Fact]
    public void SinSoloMultiplosEnCompetenciaNoHayPuntosDeQuiebre()
    {
        var candidatas = new[]
        {
            Estricta(1, 3750m, 2m, prioridad: 1), Comun(2, 3800m),
            Comun(3, BeneficioDeOferta.DePrecioUnitario(3700m), cantidadMinima: 5m),
        };

        Assert.Equal([], EscalonesDeCantidad.DescubrirPuntosDeQuiebre(Linea(), candidatas));
        Assert.Equal(
            [2m],
            EscalonesDeCantidad.Construir(Linea(), candidatas).Escalones.Select(e => e.CantidadDesde));
    }
}
