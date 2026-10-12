using System.Globalization;
using Ways.Domain.Articulos;
using Ways.Domain.Combos;
using Ways.Domain.Ventas;

namespace Ways.Domain.Tests.Combos;

/// <summary>Motor puro de combos (D1-D6 del feature): sin base de datos, mismo criterio que
/// <c>ResolvedorDeOfertasTests</c>.</summary>
public class ResolvedorDeCombosTests
{
    private const int Pancho = 1;
    private const int Coca = 2;
    private const int Sprite = 3;
    private const int Papas = 4;
    private const int FamiliaBebidas = 10;
    private const int FamiliaSnacks = 20;
    private const int Jamon = 5;
    private const int Queso = 6;
    private const int Salame = 7;
    private const int FamiliaFiambres = 30;

    private static LineaParaCombos Linea(
        int indice, int idArticulo, decimal precio, decimal cantidad = 1m, int? idFamilia = null,
        decimal descuentoDeOferta = 0m, UnidadVenta unidad = UnidadVenta.Unidad) =>
        new(indice, idArticulo, idFamilia, cantidad, precio, descuentoDeOferta, unidad);

    private static LineaParaCombos PorPeso(
        int indice, int idArticulo, decimal precioPorKilo, decimal kilos, int? idFamilia = null) =>
        Linea(indice, idArticulo, precioPorKilo, kilos, idFamilia, unidad: UnidadVenta.Peso);

    private static decimal Numero(string valor) => decimal.Parse(valor, CultureInfo.InvariantCulture);

    private static ComponenteDeCombo Art(int idArticulo, decimal cantidad = 1m) => new(idArticulo, null, cantidad);

    private static ComponenteDeCombo Fam(int idFamilia, decimal cantidad = 1m) => new(null, idFamilia, cantidad);

    private static ComboCandidato Combo(int id, decimal precio, params ComponenteDeCombo[] componentes) =>
        new(id, $"combo {id}", precio, componentes);

    private static ConsumoDeLinea DeLinea(ResolucionDeCombos resolucion, int indice) =>
        Assert.Single(resolucion.Lineas, l => l.Indice == indice);

    private static ResolucionDeCombos Resolver(LineaParaCombos[] lineas, params ComboCandidato[] combos) =>
        ResolvedorDeCombos.Resolver(lineas, combos);

    [Fact]
    public void ElEjemploDelDuenoPanchoMasCocaRepartePorProporcionYSumaElPrecioDelCombo()
    {
        var resolucion = Resolver(
            [Linea(0, Pancho, 3000m), Linea(1, Coca, 2500m)],
            Combo(7, 5000m, Art(Pancho), Art(Coca)));

        var pancho = DeLinea(resolucion, 0);
        var coca = DeLinea(resolucion, 1);

        Assert.Equal(1, pancho.UnidadesConsumidas);
        Assert.Equal(273m, pancho.AhorroDeCombo);
        Assert.Equal(1, coca.UnidadesConsumidas);
        Assert.Equal(227m, coca.AhorroDeCombo);
        Assert.Equal(5000m, (3000m - pancho.AhorroDeCombo) + (2500m - coca.AhorroDeCombo));
        Assert.Equal([7], pancho.IdsDeCombo);

        var aplicado = Assert.Single(resolucion.Combos);
        Assert.Equal(new ComboAplicado(7, "combo 7", 1, 500m), aplicado);
    }

    [Fact]
    public void UnComponenteArticuloMasUnaFamiliaConDosMiembrosDistintosLosConsumeATodos()
    {
        var resolucion = Resolver(
            [
                Linea(0, Pancho, 3000m),
                Linea(1, Coca, 2500m, idFamilia: FamiliaBebidas),
                Linea(2, Sprite, 2000m, idFamilia: FamiliaBebidas)
            ],
            Combo(1, 6000m, Art(Pancho), Fam(FamiliaBebidas, 2m)));

        Assert.Equal(600m, DeLinea(resolucion, 0).AhorroDeCombo);
        Assert.Equal(500m, DeLinea(resolucion, 1).AhorroDeCombo);
        Assert.Equal(400m, DeLinea(resolucion, 2).AhorroDeCombo);
        Assert.All(resolucion.Lineas, l => Assert.Equal(1, l.UnidadesConsumidas));
    }

    [Fact]
    public void DosFamiliasComoComponentesDejanElRestoDelRedondeoEnLaUltimaLinea()
    {
        var resolucion = Resolver(
            [
                Linea(0, Pancho, 2500m, idFamilia: FamiliaSnacks),
                Linea(1, Coca, 2000m, idFamilia: FamiliaBebidas)
            ],
            Combo(1, 4000m, Fam(FamiliaSnacks), Fam(FamiliaBebidas)));

        Assert.Equal(278m, DeLinea(resolucion, 0).AhorroDeCombo);
        Assert.Equal(222m, DeLinea(resolucion, 1).AhorroDeCombo);
    }

    [Fact]
    public void UnComboDeTresComponentesRepartePorProporcionDelImporteDeLista()
    {
        var resolucion = Resolver(
            [Linea(0, Pancho, 1000m), Linea(1, Coca, 2000m), Linea(2, Sprite, 3000m)],
            Combo(1, 5000m, Art(Pancho), Art(Coca), Art(Sprite)));

        Assert.Equal(167m, DeLinea(resolucion, 0).AhorroDeCombo);
        Assert.Equal(333m, DeLinea(resolucion, 1).AhorroDeCombo);
        Assert.Equal(500m, DeLinea(resolucion, 2).AhorroDeCombo);
    }

    [Fact]
    public void ElRestoDelRedondeoVaALaUltimaLineaConsumidaYElConjuntoSumaExactoElPrecio()
    {
        var resolucion = Resolver(
            [Linea(0, Pancho, 100m), Linea(1, Coca, 100m), Linea(2, Sprite, 100m)],
            Combo(1, 200m, Art(Pancho), Art(Coca), Art(Sprite)));

        Assert.Equal(33m, DeLinea(resolucion, 0).AhorroDeCombo);
        Assert.Equal(33m, DeLinea(resolucion, 1).AhorroDeCombo);
        Assert.Equal(34m, DeLinea(resolucion, 2).AhorroDeCombo);
        Assert.Equal(200m, 300m - resolucion.Lineas.Sum(l => l.AhorroDeCombo));
    }

    [Fact]
    public void UnComponenteConCantidadMayorAUnoConsumeEsasUnidades()
    {
        var resolucion = Resolver(
            [Linea(0, Pancho, 1000m, cantidad: 2m), Linea(1, Coca, 2000m)],
            Combo(1, 3000m, Art(Pancho, 2m), Art(Coca)));

        var pancho = DeLinea(resolucion, 0);
        Assert.Equal(2, pancho.UnidadesConsumidas);
        Assert.Equal(500m, pancho.AhorroDeCombo);
        Assert.Equal(500m, DeLinea(resolucion, 1).AhorroDeCombo);
    }

    [Fact]
    public void DosConjuntosCompletosAplicanElComboDosVecesYSumanLosAhorros()
    {
        var resolucion = Resolver(
            [Linea(0, Pancho, 3000m, cantidad: 2m), Linea(1, Coca, 2500m, cantidad: 2m)],
            Combo(1, 5000m, Art(Pancho), Art(Coca)));

        var pancho = DeLinea(resolucion, 0);
        var coca = DeLinea(resolucion, 1);
        Assert.Equal(2, pancho.UnidadesConsumidas);
        Assert.Equal(546m, pancho.AhorroDeCombo);
        Assert.Equal(2, coca.UnidadesConsumidas);
        Assert.Equal(454m, coca.AhorroDeCombo);

        var aplicado = Assert.Single(resolucion.Combos);
        Assert.Equal(2, aplicado.Veces);
        Assert.Equal(1000m, aplicado.Ahorro);
    }

    [Fact]
    public void UnConjuntoYMedioDejaLaUnidadSobranteAPrecioNormal()
    {
        var resolucion = Resolver(
            [Linea(0, Pancho, 3000m, cantidad: 2m), Linea(1, Coca, 2500m)],
            Combo(1, 5000m, Art(Pancho), Art(Coca)));

        var pancho = DeLinea(resolucion, 0);
        Assert.Equal(1, pancho.UnidadesConsumidas);
        Assert.Equal(273m, pancho.AhorroDeCombo);
        Assert.Equal(1, Assert.Single(resolucion.Combos).Veces);
    }

    [Fact]
    public void ElMismoArticuloPuedeCubrirElComponenteArticuloYUnaUnidadDeLaFamilia()
    {
        var resolucion = Resolver(
            [Linea(0, Pancho, 3000m, cantidad: 2m, idFamilia: FamiliaBebidas)],
            Combo(1, 4500m, Art(Pancho), Fam(FamiliaBebidas)));

        var pancho = DeLinea(resolucion, 0);
        Assert.Equal(2, pancho.UnidadesConsumidas);
        Assert.Equal(1500m, pancho.AhorroDeCombo);
    }

    [Fact]
    public void LaUnidadTomadaComoArticuloNoQuedaDisponibleParaLaFamiliaYSeUsaOtroMiembro()
    {
        var resolucion = Resolver(
            [
                Linea(0, Pancho, 3000m, idFamilia: FamiliaBebidas),
                Linea(1, Coca, 2000m, idFamilia: FamiliaBebidas)
            ],
            Combo(1, 4000m, Art(Pancho), Fam(FamiliaBebidas)));

        Assert.Equal(1, DeLinea(resolucion, 0).UnidadesConsumidas);
        Assert.Equal(600m, DeLinea(resolucion, 0).AhorroDeCombo);
        Assert.Equal(1, DeLinea(resolucion, 1).UnidadesConsumidas);
        Assert.Equal(400m, DeLinea(resolucion, 1).AhorroDeCombo);
    }

    [Fact]
    public void ElComponenteArticuloSeCubreAntesQueLaFamiliaAunSiLaFamiliaApareceAntesEnElDocumento()
    {
        var resolucion = Resolver(
            [
                Linea(0, Pancho, 3000m, idFamilia: FamiliaBebidas),
                Linea(1, Coca, 2000m, idFamilia: FamiliaBebidas)
            ],
            Combo(1, 4000m, Fam(FamiliaBebidas), Art(Pancho)));

        Assert.Equal(600m, DeLinea(resolucion, 0).AhorroDeCombo);
        Assert.Equal(400m, DeLinea(resolucion, 1).AhorroDeCombo);
    }

    [Fact]
    public void ConVariasLineasCandidatasParaUnComponenteGanaLaPrimeraDelDocumento()
    {
        var resolucion = Resolver(
            [
                Linea(0, Coca, 2000m, idFamilia: FamiliaBebidas),
                Linea(1, Sprite, 2000m, idFamilia: FamiliaBebidas),
                Linea(2, Pancho, 3000m)
            ],
            Combo(1, 4000m, Fam(FamiliaBebidas), Art(Pancho)));

        Assert.Equal(1, DeLinea(resolucion, 0).UnidadesConsumidas);
        Assert.DoesNotContain(resolucion.Lineas, l => l.Indice == 1);
    }

    [Fact]
    public void ElComboQueNoEsMasBaratoQueLaOfertaNoSeAplicaYSiLoEsPorUnPesoSi()
    {
        LineaParaCombos[] lineas =
            [Linea(0, Pancho, 3000m, descuentoDeOferta: 1000m), Linea(1, Coca, 2500m)];

        var empate = Resolver(lineas, Combo(1, 4500m, Art(Pancho), Art(Coca)));
        Assert.Empty(empate.Lineas);
        Assert.Empty(empate.Combos);

        var masBarato = Resolver(lineas, Combo(1, 4499m, Art(Pancho), Art(Coca)));
        Assert.Equal(2, masBarato.Lineas.Count);
        Assert.Equal(1001m, masBarato.Lineas.Sum(l => l.AhorroDeCombo));
    }

    [Fact]
    public void UnComboCuyoPrecioConCentavosNoBajaDeUnPesoElCostoConOfertasNoSeAplicaPorqueCobraLoMismo()
    {
        LineaParaCombos[] lineas =
            [Linea(0, Pancho, 3000m, descuentoDeOferta: 1000m), Linea(1, Coca, 2500m)];

        var resolucion = Resolver(lineas, Combo(1, 4499.99m, Art(Pancho), Art(Coca)));

        Assert.Empty(resolucion.Lineas);
        Assert.Empty(resolucion.Combos);
    }

    [Fact]
    public void ElPrecioConCentavosSeRedondeaAlPesoAntesDeRepartirseYLasPartesCobradasSonEnteras()
    {
        // Lista 5500 y combo 4499.60: ahorro 1000.40 → 1000. El conjunto cobra 4500 (no 4499.60):
        // la venta es de pesos enteros.
        var resolucion = Resolver(
            [Linea(0, Pancho, 3000m), Linea(1, Coca, 2500m)],
            Combo(1, 4499.60m, Art(Pancho), Art(Coca)));

        Assert.Equal(545m, DeLinea(resolucion, 0).AhorroDeCombo);
        Assert.Equal(455m, DeLinea(resolucion, 1).AhorroDeCombo);
        Assert.Equal(1000m, Assert.Single(resolucion.Combos).Ahorro);
    }

    [Fact]
    public void UnaLineaDeListaConCentavosCobraUnaParteEnteraYSuAhorroLlevaLosCentavos()
    {
        // Lista 100.60 y combo 0.20: el conjunto cobra 0 (0.20 redondeado al peso). Cada línea cobra
        // una parte entera y su ahorro es el importe de lista menos esa parte, con los 0.60 de la primera.
        var resolucion = Resolver(
            [Linea(0, Pancho, 0.60m), Linea(1, Coca, 100m)],
            Combo(1, 0.20m, Art(Pancho), Art(Coca)));

        Assert.Equal(0.60m, DeLinea(resolucion, 0).AhorroDeCombo);
        Assert.Equal(100m, DeLinea(resolucion, 1).AhorroDeCombo);
    }

    [Fact]
    public void TresLineasDeMilConUnComboDeDosMilCobranExactamenteElPrecioConLasNetasEnteras()
    {
        var resolucion = Resolver(
            [Linea(0, Pancho, 1000m), Linea(1, Coca, 1000m), Linea(2, Sprite, 1000m)],
            Combo(1, 2000m, Art(Pancho), Art(Coca), Art(Sprite)));

        Assert.Equal([333m, 333m, 334m], resolucion.Lineas.Select(l => l.AhorroDeCombo));

        var totales = CalculadorDeTotales.Calcular(
            resolucion.Lineas
                .Select(l => new LineaParaCalcular(
                    1m, 1000m, 0m, null, l.UnidadesConsumidas, l.AhorroDeCombo))
                .ToList());

        Assert.Equal(2000m, totales.Total);
    }

    [Fact]
    public void ElComboQueConsumeUnaSolaUnidadDeUnaLineaConOfertaCobraElPrecioMasLasUnidadesFueraDelCombo()
    {
        var resolucion = Resolver(
            [Linea(0, Pancho, 3000m, cantidad: 2m, descuentoDeOferta: 500m), Linea(1, Coca, 2500m)],
            Combo(1, 4800m, Art(Pancho), Art(Coca)));

        var pancho = DeLinea(resolucion, 0);
        var coca = DeLinea(resolucion, 1);
        var totales = CalculadorDeTotales.Calcular(
        [
            new LineaParaCalcular(2m, 3000m, 500m, null, pancho.UnidadesConsumidas, pancho.AhorroDeCombo),
            new LineaParaCalcular(1m, 2500m, 0m, null, coca.UnidadesConsumidas, coca.AhorroDeCombo)
        ]);

        // Combo 4800 + la segunda unidad de pancho con su oferta (3000 − 500).
        Assert.Equal(4800m + 2500m, totales.Total);
    }

    [Fact]
    public void LasUnidadesDeLaLineaFueraDelComboConservanSuOfertaPorqueSoloSeConsumeLoNecesario()
    {
        var resolucion = Resolver(
            [Linea(0, Pancho, 3000m, cantidad: 2m, descuentoDeOferta: 500m), Linea(1, Coca, 2500m)],
            Combo(1, 4800m, Art(Pancho), Art(Coca)));

        var pancho = DeLinea(resolucion, 0);
        var coca = DeLinea(resolucion, 1);
        Assert.Equal(1, pancho.UnidadesConsumidas);
        Assert.Equal(382m, pancho.AhorroDeCombo);
        Assert.Equal(318m, coca.AhorroDeCombo);

        // Fórmula documentada para quien llama: oferta × unidades no consumidas + parte del combo.
        var descuentoPancho = (500m * (2m - pancho.UnidadesConsumidas)) + pancho.AhorroDeCombo;
        var descuentoCoca = coca.AhorroDeCombo;
        Assert.Equal(882m, descuentoPancho);
        Assert.Equal(4800m + 3000m - 500m, (3000m * 2m - descuentoPancho) + (2500m - descuentoCoca));
    }

    [Theory]
    [InlineData(5500)]
    [InlineData(6000)]
    public void UnComboCuyoPrecioNoBajaElTotalDeListaNoSeAplica(double precio)
    {
        var resolucion = Resolver(
            [Linea(0, Pancho, 3000m), Linea(1, Coca, 2500m)],
            Combo(1, (decimal)precio, Art(Pancho), Art(Coca)));

        Assert.Empty(resolucion.Lineas);
        Assert.Empty(resolucion.Combos);
    }

    [Fact]
    public void ConDosCombosCompitiendoGanaElDeMayorAhorroYElOtroNoPuedeReusarLasUnidades()
    {
        var resolucion = Resolver(
            [Linea(0, Pancho, 3000m), Linea(1, Coca, 2500m), Linea(2, Sprite, 2000m)],
            Combo(1, 5000m, Art(Pancho), Art(Coca)),
            Combo(2, 4000m, Art(Pancho), Art(Sprite)));

        var aplicado = Assert.Single(resolucion.Combos);
        Assert.Equal(2, aplicado.IdCombo);
        Assert.Equal(1000m, aplicado.Ahorro);
        Assert.DoesNotContain(resolucion.Lineas, l => l.Indice == 1);
    }

    [Fact]
    public void ElEmpateDeAhorroLoGanaElMenorIdSinImportarElOrdenDeEntrada()
    {
        LineaParaCombos[] lineas = [Linea(0, Pancho, 3000m), Linea(1, Coca, 2500m), Linea(2, Sprite, 2000m)];
        var cinco = Combo(5, 5000m, Art(Pancho), Art(Coca));
        var tres = Combo(3, 4500m, Art(Pancho), Art(Sprite));

        Assert.Equal(3, Assert.Single(Resolver(lineas, cinco, tres).Combos).IdCombo);
        Assert.Equal(3, Assert.Single(Resolver(lineas, tres, cinco).Combos).IdCombo);
    }

    [Fact]
    public void ElRankingUsaLaGananciaRealFrenteAlCostoConOfertasYNoElAhorroContraLista()
    {
        var resolucion = Resolver(
            [
                Linea(0, Pancho, 60m),
                Linea(1, Coca, 40m),
                Linea(2, Sprite, 100m, descuentoDeOferta: 20m)
            ],
            Combo(1, 79m, Art(Pancho), Art(Sprite)),
            Combo(2, 30m, Art(Coca), Art(Pancho)));

        var aplicado = Assert.Single(resolucion.Combos);
        Assert.Equal(2, aplicado.IdCombo);
    }

    [Fact]
    public void UnaCantidadEnormeSeAplicaEnUnSoloPasoYCadaConjuntoSumaElPrecio()
    {
        const int conjuntos = 500_000;

        var resolucion = Resolver(
            [Linea(0, Pancho, 3000m, cantidad: conjuntos), Linea(1, Coca, 2500m, cantidad: conjuntos)],
            Combo(1, 5000m, Art(Pancho), Art(Coca)));

        var aplicado = Assert.Single(resolucion.Combos);
        Assert.Equal(conjuntos, aplicado.Veces);
        Assert.Equal(conjuntos * 500m, aplicado.Ahorro);
        Assert.Equal(conjuntos * 273m, DeLinea(resolucion, 0).AhorroDeCombo);
        Assert.Equal(conjuntos * 227m, DeLinea(resolucion, 1).AhorroDeCombo);
        Assert.Equal(aplicado.Ahorro, resolucion.Lineas.Sum(l => l.AhorroDeCombo));
    }

    [Fact]
    public void UnaLineaDePrecioCeroNoRecibeUnaParteNegativaDelRedondeo()
    {
        var resolucion = Resolver(
            [Linea(0, Pancho, 10m), Linea(1, Coca, 10m), Linea(2, Sprite, 0m)],
            Combo(1, 19m, Art(Pancho), Art(Coca), Art(Sprite)));

        Assert.All(resolucion.Lineas, l => Assert.InRange(l.AhorroDeCombo, 0m, 10m));
        Assert.Equal(0m, DeLinea(resolucion, 2).AhorroDeCombo);
        Assert.Equal(1m, resolucion.Lineas.Sum(l => l.AhorroDeCombo));
    }

    [Fact]
    public void LaParteDeCadaLineaNuncaSuperaSuImporteDeListaYElAhorroEsLaListaMenosElCobroRedondeado()
    {
        var lineas = Enumerable.Range(0, 10)
            .Select(i => Linea(i, 100 + i, 1m))
            .Append(Linea(10, 200, 0.01m))
            .ToArray();
        var combo = Combo(1, 0.06m, lineas.Select(l => Art(l.IdArticulo)).ToArray());

        var resolucion = Resolver(lineas, combo);

        Assert.Equal(11, resolucion.Lineas.Count);
        foreach (var consumo in resolucion.Lineas)
        {
            Assert.InRange(consumo.AhorroDeCombo, 0m, lineas[consumo.Indice].PrecioLista);
        }

        // Lista 10.01 y combo 0.06: el conjunto cobra 0 y el ahorro es toda la lista.
        Assert.Equal(10.01m, resolucion.Lineas.Sum(l => l.AhorroDeCombo));
    }

    [Fact]
    public void UnComboConUnComponenteSobreElTopeDeCantidadNoSeAplicaYNoDesborda()
    {
        var resolucion = Resolver(
            [Linea(0, Pancho, 1m, cantidad: int.MaxValue), Linea(1, Coca, 1m)],
            Combo(1, 1m, Art(Pancho, 10_000_000_000_000_000m), Art(Coca)));

        Assert.Empty(resolucion.Combos);
    }

    [Fact]
    public void DespuesDelGanadorUnComboQueTodaviaCabeSeAplicaConLasUnidadesRestantes()
    {
        var resolucion = Resolver(
            [
                Linea(0, Pancho, 3000m),
                Linea(1, Coca, 2500m),
                Linea(2, Sprite, 2000m),
                Linea(3, Papas, 1500m)
            ],
            Combo(1, 4000m, Art(Pancho), Art(Sprite)),
            Combo(2, 3500m, Art(Coca), Art(Papas)));

        Assert.Equal([1, 2], resolucion.Combos.Select(c => c.IdCombo).Order());
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(1.5)]
    [InlineData(-1)]
    [InlineData(0)]
    public void LasLineasPorUnidadConCantidadNoEnteraONoPositivaSeIgnoranEnLaDeteccion(double cantidad)
    {
        var resolucion = Resolver(
            [Linea(0, Pancho, 3000m, cantidad: (decimal)cantidad), Linea(1, Coca, 2500m)],
            Combo(1, 5000m, Art(Pancho), Art(Coca)));

        Assert.Empty(resolucion.Lineas);
        Assert.Empty(resolucion.Combos);
    }

    /// <summary>Cláusula: una línea solo es candidata de un componente si su unidad de venta admite la cantidad
    /// del componente. Un artículo por unidad no completa un componente fraccionario aunque le sobren unidades.</summary>
    [Theory]
    [InlineData("1.5")]
    [InlineData("0.5")]
    public void UnaLineaPorUnidadNoCompletaUnComponenteDeCantidadFraccionaria(string cantidad)
    {
        var resolucion = Resolver(
            [Linea(0, Pancho, 3000m, cantidad: 2m), Linea(1, Coca, 2500m)],
            Combo(1, 2000m, Art(Pancho, Numero(cantidad)), Art(Coca)));

        Assert.Empty(resolucion.Lineas);
        Assert.Empty(resolucion.Combos);
    }

    [Fact]
    public void DosComponentesPesablesDeDoscientosGramosAplicanElComboUnaVez()
    {
        LineaParaCombos[] lineas = [PorPeso(0, Jamon, 10000m, 0.200m), PorPeso(1, Queso, 8000m, 0.200m)];

        var resolucion = Resolver(lineas, Combo(1, 3000m, Art(Jamon, 0.200m), Art(Queso, 0.200m)));

        var jamon = DeLinea(resolucion, 0);
        var queso = DeLinea(resolucion, 1);
        Assert.Equal(0.200m, jamon.UnidadesConsumidas);
        Assert.Equal(333m, jamon.AhorroDeCombo);
        Assert.Equal(0.200m, queso.UnidadesConsumidas);
        Assert.Equal(267m, queso.AhorroDeCombo);
        Assert.Equal(new ComboAplicado(1, "combo 1", 1, 600m), Assert.Single(resolucion.Combos));
        Assert.Equal(3000m, TotalCobrado(lineas, resolucion));
    }

    [Fact]
    public void ElExcedenteDeUnaLineaPesableQuedaAPrecioDeListaYLaLineaNoSeParte()
    {
        LineaParaCombos[] lineas = [PorPeso(0, Jamon, 10000m, 0.215m), PorPeso(1, Queso, 8000m, 0.200m)];

        var resolucion = Resolver(lineas, Combo(1, 3000m, Art(Jamon, 0.200m), Art(Queso, 0.200m)));

        var jamon = DeLinea(resolucion, 0);
        Assert.Equal(0.200m, jamon.UnidadesConsumidas);
        Assert.Equal(333m, jamon.AhorroDeCombo);
        Assert.Equal(1, Assert.Single(resolucion.Combos).Veces);

        // El combo cobra 3000 y los 15 g que sobran del jamón, 150 a precio de lista.
        Assert.Equal(3000m + 150m, TotalCobrado(lineas, resolucion));
    }

    [Fact]
    public void ElDobleDeCadaComponentePesableAplicaElComboDosVeces()
    {
        LineaParaCombos[] lineas = [PorPeso(0, Jamon, 10000m, 0.400m), PorPeso(1, Queso, 8000m, 0.400m)];

        var resolucion = Resolver(lineas, Combo(1, 3000m, Art(Jamon, 0.200m), Art(Queso, 0.200m)));

        Assert.Equal(0.400m, DeLinea(resolucion, 0).UnidadesConsumidas);
        Assert.Equal(666m, DeLinea(resolucion, 0).AhorroDeCombo);
        Assert.Equal(0.400m, DeLinea(resolucion, 1).UnidadesConsumidas);
        Assert.Equal(534m, DeLinea(resolucion, 1).AhorroDeCombo);
        Assert.Equal(new ComboAplicado(1, "combo 1", 2, 1200m), Assert.Single(resolucion.Combos));
        Assert.Equal(6000m, TotalCobrado(lineas, resolucion));
    }

    [Fact]
    public void LasVecesSonLaParteEnteraDeLoDisponibleSobreLoQuePideElComponente()
    {
        // 0,590 kg de jamón alcanzan para dos conjuntos de 0,200 (sobran 0,190) aunque el queso alcance para tres.
        LineaParaCombos[] lineas = [PorPeso(0, Jamon, 10000m, 0.590m), PorPeso(1, Queso, 8000m, 0.600m)];

        var resolucion = Resolver(lineas, Combo(1, 3000m, Art(Jamon, 0.200m), Art(Queso, 0.200m)));

        Assert.Equal(2, Assert.Single(resolucion.Combos).Veces);
        Assert.Equal(0.400m, DeLinea(resolucion, 0).UnidadesConsumidas);
        Assert.Equal(0.400m, DeLinea(resolucion, 1).UnidadesConsumidas);
    }

    [Fact]
    public void UnaLineaPesableQueNoLlegaALaCantidadDelComponenteNoAplicaElCombo()
    {
        var resolucion = Resolver(
            [PorPeso(0, Jamon, 10000m, 0.150m), PorPeso(1, Queso, 8000m, 0.200m)],
            Combo(1, 2000m, Art(Jamon, 0.200m), Art(Queso, 0.200m)));

        Assert.Empty(resolucion.Lineas);
        Assert.Empty(resolucion.Combos);
    }

    /// <summary>Cláusula: el importe de lista de la porción consumida se redondea a dos decimales antes del reparto
    /// (0,125 × 9999,50 = 1249,9375 → 1249,94; 0,333 × 7000,10 = 2331,0333 → 2331,03), así que el ahorro de cada
    /// línea no lleva más de dos decimales.</summary>
    [Fact]
    public void ElAhorroDeUnaLineaPesableNuncaLlevaMasDeDosDecimales()
    {
        LineaParaCombos[] lineas = [PorPeso(0, Jamon, 9999.50m, 0.125m), PorPeso(1, Queso, 7000.10m, 0.333m)];

        var resolucion = Resolver(lineas, Combo(1, 3000m, Art(Jamon, 0.125m), Art(Queso, 0.333m)));

        Assert.Equal(202.94m, DeLinea(resolucion, 0).AhorroDeCombo);
        Assert.Equal(378.03m, DeLinea(resolucion, 1).AhorroDeCombo);
        Assert.Equal(580.97m, Assert.Single(resolucion.Combos).Ahorro);
        Assert.Equal(3000m, TotalCobrado(lineas, resolucion));
    }

    /// <summary>Cláusula: un combo cuyo cobro no baja del total de lista ya redondeado no se aplica. Con dos gramos a
    /// 1 el kilo el total de lista redondea a cero: no hay importe que repartir.</summary>
    [Fact]
    public void UnComboCuyoTotalDeListaRedondeaACeroNoSeAplica()
    {
        var resolucion = Resolver(
            [PorPeso(0, Jamon, 1m, 0.001m), PorPeso(1, Queso, 1m, 0.001m)],
            Combo(1, 0.20m, Art(Jamon, 0.001m), Art(Queso, 0.001m)));

        Assert.Empty(resolucion.Lineas);
        Assert.Empty(resolucion.Combos);
    }

    [Fact]
    public void UnArticuloPesableCompletaUnComponenteDeCantidadEnteraYElRestoQuedaFuera()
    {
        var resolucion = Resolver(
            [PorPeso(0, Jamon, 10000m, 1.500m), Linea(1, Coca, 2500m)],
            Combo(1, 11000m, Art(Jamon), Art(Coca)));

        Assert.Equal(1m, DeLinea(resolucion, 0).UnidadesConsumidas);
        Assert.Equal(1m, DeLinea(resolucion, 1).UnidadesConsumidas);
    }

    [Fact]
    public void UnSoloComponentePesableDeDosKilosOMasSeAplicaYUnoMenorNo()
    {
        LineaParaCombos[] lineas = [PorPeso(0, Jamon, 10000m, 2.500m)];

        Assert.Equal(2m, DeLinea(Resolver(lineas, Combo(1, 15000m, Art(Jamon, 2m))), 0).UnidadesConsumidas);
        Assert.Empty(Resolver(lineas, Combo(2, 1000m, Art(Jamon, 0.500m))).Combos);
    }

    /// <summary>Cláusula: la unidad de venta de la línea decide qué cantidades son detectables. Una línea pesable
    /// con más de tres decimales o no positiva no entra.</summary>
    [Theory]
    [InlineData("0.2005")]
    [InlineData("0")]
    [InlineData("-0.200")]
    public void UnaLineaPesableConCantidadQueSuUnidadNoAdmiteSeIgnora(string kilos)
    {
        var resolucion = Resolver(
            [PorPeso(0, Jamon, 10000m, Numero(kilos)), PorPeso(1, Queso, 8000m, 0.200m)],
            Combo(1, 1000m, Art(Jamon, 0.200m), Art(Queso, 0.200m)));

        Assert.Empty(resolucion.Combos);
    }

    /// <summary>Cláusula: una línea pesable cuya cantidad en milésimas no entra en un entero de 32 bits no se
    /// detecta, igual que una línea por unidad con más de <see cref="int.MaxValue"/> unidades.</summary>
    [Fact]
    public void UnaLineaPesableCuyasMilesimasNoEntranEnUnEnteroSeIgnoraYLaDelTopeNo()
    {
        var combo = Combo(1, 1000m, Art(Jamon), Art(Coca));

        var sobreElTope = Resolver([PorPeso(0, Jamon, 10000m, 2_147_483.648m), Linea(1, Coca, 2500m)], combo);
        var enElTope = Resolver([PorPeso(0, Jamon, 10000m, 2_147_483.647m), Linea(1, Coca, 2500m)], combo);

        Assert.Empty(sobreElTope.Combos);
        Assert.Equal(1, Assert.Single(enElTope.Combos).Veces);
    }

    /// <summary>Cláusula: de una línea por unidad solo se toman unidades enteras. Con una familia de miembros
    /// mixtos, lo que falta tras un miembro pesable puede ser una fracción; el miembro por unidad aporta la parte
    /// entera y la fracción la completa otro pesable.</summary>
    [Fact]
    public void DeUnaLineaPorUnidadSoloSeTomanUnidadesEnterasAunqueAlComponenteLeFalteUnaFraccion()
    {
        LineaParaCombos[] lineas =
        [
            PorPeso(0, Jamon, 1000m, 0.500m, idFamilia: FamiliaFiambres),
            Linea(1, Salame, 400m, cantidad: 3m, idFamilia: FamiliaFiambres),
            PorPeso(2, Queso, 1000m, 0.500m, idFamilia: FamiliaFiambres),
            Linea(3, Coca, 2500m)
        ];

        var resolucion = Resolver(lineas, Combo(1, 3000m, Fam(FamiliaFiambres, 2m), Art(Coca)));

        Assert.Equal(0.500m, DeLinea(resolucion, 0).UnidadesConsumidas);
        Assert.Equal(1m, DeLinea(resolucion, 1).UnidadesConsumidas);
        Assert.Equal(0.500m, DeLinea(resolucion, 2).UnidadesConsumidas);
        Assert.Equal(3000m + 800m, TotalCobrado(lineas, resolucion));
    }

    /// <summary>Cláusula: una línea por unidad a la que le llega menos de una unidad por completar no aporta nada y
    /// no figura en el reparto; el componente lo termina la línea pesable siguiente.</summary>
    [Fact]
    public void UnaLineaPorUnidadALaQueLeLlegaMenosDeUnaUnidadNoAportaNadaYElComboSeCompletaConLaSiguiente()
    {
        LineaParaCombos[] lineas =
        [
            PorPeso(0, Jamon, 1000m, 1.500m, idFamilia: FamiliaFiambres),
            Linea(1, Salame, 400m, cantidad: 3m, idFamilia: FamiliaFiambres),
            PorPeso(2, Queso, 1000m, 0.500m, idFamilia: FamiliaFiambres),
            Linea(3, Coca, 2500m)
        ];

        var resolucion = Resolver(lineas, Combo(1, 3000m, Fam(FamiliaFiambres, 2m), Art(Coca)));

        Assert.Equal([0, 2, 3], resolucion.Lineas.Select(l => l.Indice));
        Assert.Equal(1.500m, DeLinea(resolucion, 0).UnidadesConsumidas);
        Assert.Equal(0.500m, DeLinea(resolucion, 2).UnidadesConsumidas);
    }

    /// <summary>Cláusula: un miembro de la familia cuya unidad de venta no admite la cantidad del componente no es
    /// candidato, ni siquiera por su parte entera. 1,5 de la familia no se arma con una unidad del miembro por unidad
    /// más medio kilo del pesable; sí con un kilo y medio del pesable solo.</summary>
    [Fact]
    public void UnMiembroPorUnidadNoEsCandidatoDeUnComponenteFamiliaFraccionarioNiPorSuParteEntera()
    {
        var combo = Combo(1, 1000m, Fam(FamiliaFiambres, 1.500m), Art(Coca));

        var mezclado = Resolver(
            [
                Linea(0, Salame, 400m, cantidad: 2m, idFamilia: FamiliaFiambres),
                PorPeso(1, Jamon, 1000m, 0.500m, idFamilia: FamiliaFiambres),
                Linea(2, Coca, 2500m)
            ],
            combo);
        var soloPesable = Resolver(
            [
                Linea(0, Salame, 400m, cantidad: 2m, idFamilia: FamiliaFiambres),
                PorPeso(1, Jamon, 1000m, 1.500m, idFamilia: FamiliaFiambres),
                Linea(2, Coca, 2500m)
            ],
            combo);

        Assert.Empty(mezclado.Combos);
        Assert.Equal([1, 2], soloPesable.Lineas.Select(l => l.Indice));
    }

    /// <summary>Cláusula: el redondeo a dos decimales del importe de lista aleja el punto medio de cero, como todo
    /// redondeo de venta: 0,250 × 100,10 = 25,025 → 25,03 (el redondeo bancario daría 25,02).</summary>
    [Fact]
    public void ElImporteDeListaDeLaPorcionConsumidaRedondeaElMedioCentavoHaciaArriba()
    {
        var resolucion = Resolver(
            [PorPeso(0, Jamon, 100.10m, 0.250m), PorPeso(1, Queso, 8000m, 0.200m)],
            Combo(1, 1000m, Art(Jamon, 0.250m), Art(Queso, 0.200m)));

        Assert.Equal(10.03m, DeLinea(resolucion, 0).AhorroDeCombo);
        Assert.Equal(615m, DeLinea(resolucion, 1).AhorroDeCombo);
    }

    [Fact]
    public void UnComponenteFamiliaPesableSeCompletaConVariasLineasEnOrdenDeDocumento()
    {
        var resolucion = Resolver(
            [
                PorPeso(0, Jamon, 10000m, 0.150m, idFamilia: FamiliaFiambres),
                PorPeso(1, Queso, 8000m, 0.300m, idFamilia: FamiliaFiambres),
                Linea(2, Coca, 2500m)
            ],
            Combo(1, 4000m, Fam(FamiliaFiambres, 0.400m), Art(Coca)));

        Assert.Equal(0.150m, DeLinea(resolucion, 0).UnidadesConsumidas);
        Assert.Equal(0.250m, DeLinea(resolucion, 1).UnidadesConsumidas);
    }

    /// <summary>Cláusula: un combo con un componente de cantidad inválida no se aplica. Sin ella, un componente que
    /// no llega a una milésima no pediría nada y el combo se completaría solo con los demás.</summary>
    [Theory]
    [InlineData("0.0004")]
    [InlineData("0")]
    [InlineData("-1")]
    public void UnComboConUnComponenteDeCantidadInvalidaNoSeAplicaAunqueLosDemasSeCompleten(string cantidad)
    {
        var resolucion = Resolver(
            [PorPeso(0, Jamon, 10000m, 1m), Linea(1, Pancho, 3000m, cantidad: 2m), Linea(2, Coca, 2500m)],
            Combo(1, 1000m, Art(Jamon, Numero(cantidad)), Art(Pancho), Art(Coca)));

        Assert.Empty(resolucion.Lineas);
        Assert.Empty(resolucion.Combos);
    }

    [Fact]
    public void UnComboDeUnaSolaUnidadOSinPrecioPositivoNoSeAplica()
    {
        LineaParaCombos[] lineas = [Linea(0, Pancho, 3000m), Linea(1, Coca, 2500m)];

        Assert.Empty(Resolver(lineas, Combo(1, 1000m, Art(Pancho))).Combos);
        Assert.Empty(Resolver(lineas, Combo(2, 0m, Art(Pancho), Art(Coca))).Combos);
    }

    [Fact]
    public void ElMismoArticuloEnDosLineasSeConsumeEnOrdenDeDocumento()
    {
        var resolucion = Resolver(
            [Linea(0, Pancho, 3000m), Linea(1, Coca, 2500m), Linea(2, Pancho, 3000m)],
            Combo(1, 5000m, Art(Pancho), Art(Coca)));

        Assert.Equal([0, 1], resolucion.Lineas.Select(l => l.Indice));
    }

    private static decimal TotalCobrado(LineaParaCombos[] lineas, ResolucionDeCombos resolucion) =>
        CalculadorDeTotales.Calcular(
            lineas.Select(l =>
            {
                var consumo = resolucion.Lineas.Where(c => c.Indice == l.Indice).Select(c => (ConsumoDeLinea?)c).FirstOrDefault();
                return new LineaParaCalcular(
                    l.Cantidad, l.PrecioLista, l.DescuentoUnitarioDeOferta, null,
                    consumo?.UnidadesConsumidas ?? 0m, consumo?.AhorroDeCombo ?? 0m);
            }).ToList()).Total;

    [Fact]
    public void DosLineasConCentavosCobranExactamenteElPrecioDelComboYNoUnPesoDeMas()
    {
        LineaParaCombos[] lineas = [Linea(0, Pancho, 500.50m), Linea(1, Coca, 999.50m)];

        var resolucion = Resolver(lineas, Combo(1, 1000m, Art(Pancho), Art(Coca)));

        Assert.Equal(1000m, TotalCobrado(lineas, resolucion));
        Assert.Equal(500m, Assert.Single(resolucion.Combos).Ahorro);
    }

    [Fact]
    public void UnPrecioDeComboConCentavosCobraElPrecioRedondeadoAlPesoYNoLaListaMenosElAhorroRedondeado()
    {
        LineaParaCombos[] lineas = [Linea(0, Pancho, 1000m), Linea(1, Coca, 500m)];

        var resolucion = Resolver(lineas, Combo(1, 999.50m, Art(Pancho), Art(Coca)));

        Assert.Equal(1000m, TotalCobrado(lineas, resolucion));
    }

    [Fact]
    public void DosAplicacionesConCentavosCobranExactamenteDosVecesElPrecioRedondeado()
    {
        LineaParaCombos[] lineas = [Linea(0, Pancho, 500.50m, cantidad: 2m), Linea(1, Coca, 499.60m, cantidad: 2m)];

        var resolucion = Resolver(lineas, Combo(1, 900.40m, Art(Pancho), Art(Coca)));

        Assert.Equal(2, Assert.Single(resolucion.Combos).Veces);
        Assert.Equal(2 * 900m, TotalCobrado(lineas, resolucion));
    }

    [Fact]
    public void LaLineaParcialmenteEnElComboCobraLaPartePorElComboExactaYElRestoConSuOfertaAlPeso()
    {
        // Pancho 2 × 1000.50 con oferta de 100.25 por unidad; el combo toma una unidad.
        LineaParaCombos[] lineas =
            [Linea(0, Pancho, 1000.50m, cantidad: 2m, descuentoDeOferta: 100.25m), Linea(1, Coca, 999.50m)];

        var resolucion = Resolver(lineas, Combo(1, 1500m, Art(Pancho), Art(Coca)));

        Assert.Equal(1, DeLinea(resolucion, 0).UnidadesConsumidas);

        // La unidad fuera del combo cobra peso(1000.50 − 100.25) = peso(900.25) = 900, y el combo 1500.
        Assert.Equal(1500m + 900m, TotalCobrado(lineas, resolucion));
    }

    [Fact]
    public void UnComboCuyoCobroNoCabeEnLosTopesEnterosDeLasLineasNoSeAplica()
    {
        // Lista 1.20 con topes enteros 0 + 0: el cobro de 1 no entra en ninguna línea.
        var resolucion = Resolver(
            [Linea(0, Pancho, 0.60m), Linea(1, Coca, 0.60m)],
            Combo(1, 1m, Art(Pancho), Art(Coca)));

        Assert.Empty(resolucion.Lineas);
        Assert.Empty(resolucion.Combos);
    }

    [Fact]
    public void UnaLineaSinComboCobraElNetoAlPesoDeSiempreAunQueOtrasTenganCombo()
    {
        LineaParaCombos[] lineas =
            [Linea(0, Pancho, 1000m), Linea(1, Coca, 1000m), Linea(2, Sprite, 1166.66m, cantidad: 3m, descuentoDeOferta: 100.10m)];

        var resolucion = Resolver(lineas, Combo(1, 1500m, Art(Pancho), Art(Coca)));

        // Combo 1500 + peso(3 × (1166.66 − 100.10)) = peso(3199.68) = 3200.
        Assert.DoesNotContain(resolucion.Lineas, l => l.Indice == 2);
        Assert.Equal(1500m + 3200m, TotalCobrado(lineas, resolucion));
    }

    [Fact]
    public void SinLineasOSinCombosLaResolucionEsVacia()
    {
        Assert.Empty(Resolver([], Combo(1, 5000m, Art(Pancho), Art(Coca))).Combos);
        Assert.Empty(Resolver([Linea(0, Pancho, 3000m)]).Lineas);
    }
}
