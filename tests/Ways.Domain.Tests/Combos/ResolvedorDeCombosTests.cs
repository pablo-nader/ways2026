using Ways.Domain.Combos;

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

    private static LineaParaCombos Linea(
        int indice, int idArticulo, decimal precio, decimal cantidad = 1m, int? idFamilia = null,
        decimal descuentoDeOferta = 0m) =>
        new(indice, idArticulo, idFamilia, cantidad, precio, descuentoDeOferta);

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
        Assert.Equal(272.73m, pancho.AhorroDeCombo);
        Assert.Equal(1, coca.UnidadesConsumidas);
        Assert.Equal(227.27m, coca.AhorroDeCombo);
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

        Assert.Equal(277.78m, DeLinea(resolucion, 0).AhorroDeCombo);
        Assert.Equal(222.22m, DeLinea(resolucion, 1).AhorroDeCombo);
    }

    [Fact]
    public void UnComboDeTresComponentesRepartePorProporcionDelImporteDeLista()
    {
        var resolucion = Resolver(
            [Linea(0, Pancho, 1000m), Linea(1, Coca, 2000m), Linea(2, Sprite, 3000m)],
            Combo(1, 5000m, Art(Pancho), Art(Coca), Art(Sprite)));

        Assert.Equal(166.67m, DeLinea(resolucion, 0).AhorroDeCombo);
        Assert.Equal(333.33m, DeLinea(resolucion, 1).AhorroDeCombo);
        Assert.Equal(500m, DeLinea(resolucion, 2).AhorroDeCombo);
    }

    [Fact]
    public void ElRestoDelRedondeoVaALaUltimaLineaConsumidaYElConjuntoSumaExactoElPrecio()
    {
        var resolucion = Resolver(
            [Linea(0, Pancho, 100m), Linea(1, Coca, 100m), Linea(2, Sprite, 100m)],
            Combo(1, 200m, Art(Pancho), Art(Coca), Art(Sprite)));

        Assert.Equal(33.33m, DeLinea(resolucion, 0).AhorroDeCombo);
        Assert.Equal(33.33m, DeLinea(resolucion, 1).AhorroDeCombo);
        Assert.Equal(33.34m, DeLinea(resolucion, 2).AhorroDeCombo);
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
        Assert.Equal(545.46m, pancho.AhorroDeCombo);
        Assert.Equal(2, coca.UnidadesConsumidas);
        Assert.Equal(454.54m, coca.AhorroDeCombo);

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
        Assert.Equal(272.73m, pancho.AhorroDeCombo);
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
    public void ElComboQueNoEsMasBaratoQueLaOfertaNoSeAplicaYSiLoEsPorUnCentavoSi()
    {
        LineaParaCombos[] lineas =
            [Linea(0, Pancho, 3000m, descuentoDeOferta: 1000m), Linea(1, Coca, 2500m)];

        var empate = Resolver(lineas, Combo(1, 4500m, Art(Pancho), Art(Coca)));
        Assert.Empty(empate.Lineas);
        Assert.Empty(empate.Combos);

        var masBarato = Resolver(lineas, Combo(1, 4499.99m, Art(Pancho), Art(Coca)));
        Assert.Equal(2, masBarato.Lineas.Count);
        Assert.Equal(1000.01m, masBarato.Lineas.Sum(l => l.AhorroDeCombo));
    }

    [Fact]
    public void LasUnidadesDeLaLineaFueraDelComboConservanSuOfertaPorqueSoloSeConsumeLoNecesario()
    {
        var resolucion = Resolver(
            [Linea(0, Pancho, 3000m, cantidad: 2m, descuentoDeOferta: 500m), Linea(1, Coca, 2500m)],
            Combo(1, 4800m, Art(Pancho), Art(Coca)));

        var pancho = DeLinea(resolucion, 0);
        Assert.Equal(1, pancho.UnidadesConsumidas);
        Assert.Equal(381.82m, pancho.AhorroDeCombo);
        Assert.Equal(318.18m, DeLinea(resolucion, 1).AhorroDeCombo);
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

        var resolucion = Resolver(
            lineas,
            Combo(5, 5000m, Art(Pancho), Art(Coca)),
            Combo(3, 4500m, Art(Pancho), Art(Sprite)));

        Assert.Equal(3, Assert.Single(resolucion.Combos).IdCombo);
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
    public void LasLineasConCantidadNoEnteraONoPositivaSeIgnoranEnLaDeteccion(double cantidad)
    {
        var resolucion = Resolver(
            [Linea(0, Pancho, 3000m, cantidad: (decimal)cantidad), Linea(1, Coca, 2500m)],
            Combo(1, 5000m, Art(Pancho), Art(Coca)));

        Assert.Empty(resolucion.Lineas);
        Assert.Empty(resolucion.Combos);
    }

    [Fact]
    public void UnComboCuyoComponenteTieneCantidadFraccionariaNoSeAplica()
    {
        var resolucion = Resolver(
            [Linea(0, Pancho, 3000m, cantidad: 2m), Linea(1, Coca, 2500m)],
            Combo(1, 5000m, Art(Pancho, 1.5m), Art(Coca)));

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

    [Fact]
    public void SinLineasOSinCombosLaResolucionEsVacia()
    {
        Assert.Empty(Resolver([], Combo(1, 5000m, Art(Pancho), Art(Coca))).Combos);
        Assert.Empty(Resolver([Linea(0, Pancho, 3000m)]).Lineas);
    }
}
