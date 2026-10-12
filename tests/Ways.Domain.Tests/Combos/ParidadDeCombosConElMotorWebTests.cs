using Ways.Domain.Articulos;
using Ways.Domain.Combos;
using Ways.Domain.Ventas;

namespace Ways.Domain.Tests.Combos;

/// <summary>Tabla de paridad servidor/web: los mismos carritos con los mismos netos esperados por línea que
/// <c>src/Ways.Web/src/pos/paridadDeCombos.test.ts</c> (motor de combos de TypeScript más la fórmula de línea
/// <c>calcularTotalesDeLinea</c>). Acá la salida de <see cref="ResolvedorDeCombos"/> pasa por
/// <see cref="CalculadorDeTotales"/>. Un cambio de un número en una de las dos tablas exige el mismo cambio en la otra.</summary>
public class ParidadDeCombosConElMotorWebTests
{
    private const int Jamon = 5;
    private const int Queso = 6;
    private const int Salame = 7;
    private const int FamiliaFiambres = 30;

    private static readonly ComboCandidato DoscientosMasDoscientos = ComboDe(7, 3000m, Art(Jamon, 0.200m), Art(Queso, 0.200m));

    private static LineaParaCombos Linea(
        int indice, int idArticulo, decimal precio, decimal cantidad = 1m, decimal descuentoDeOferta = 0m,
        UnidadVenta unidad = UnidadVenta.Unidad, int? idFamilia = null) =>
        new(indice, idArticulo, idFamilia, cantidad, precio, descuentoDeOferta, unidad);

    private static LineaParaCombos PorPeso(int indice, int idArticulo, decimal precioPorKilo, decimal kilos, int? idFamilia = null) =>
        Linea(indice, idArticulo, precioPorKilo, kilos, 0m, UnidadVenta.Peso, idFamilia);

    private static ComponenteDeCombo Art(int idArticulo, decimal cantidad) => new(idArticulo, null, cantidad);

    private static ComponenteDeCombo Fam(int idFamilia, decimal cantidad) => new(null, idFamilia, cantidad);

    private static ComboCandidato ComboDe(int id, decimal precio, params ComponenteDeCombo[] componentes) =>
        new(id, $"combo {id}", precio, componentes);

    private static ComboCandidato Combo(int id, decimal precio, params int[] articulos) =>
        new(id, $"combo {id}", precio, articulos.Select(a => new ComponenteDeCombo(a, null, 1m)).ToArray());

    private static void Verificar(LineaParaCombos[] lineas, ComboCandidato combo, decimal[] netosEsperados, decimal[] ahorrosEsperados)
    {
        var resolucion = ResolvedorDeCombos.Resolver(lineas, [combo]);

        var paraCalcular = lineas.Select(l =>
        {
            var consumo = resolucion.Lineas.Cast<ConsumoDeLinea?>().FirstOrDefault(c => c!.Value.Indice == l.Indice);
            return new LineaParaCalcular(
                l.Cantidad, l.PrecioLista, l.DescuentoUnitarioDeOferta, null,
                consumo?.UnidadesConsumidas ?? 0m, consumo?.AhorroDeCombo ?? 0m);
        }).ToArray();

        var totales = CalculadorDeTotales.Calcular(paraCalcular);

        Assert.Equal(netosEsperados, totales.Items.Select(i => i.Total).ToArray());
        var ahorros = lineas.Select(l => resolucion.Lineas.FirstOrDefault(c => c.Indice == l.Indice).AhorroDeCombo).ToArray();
        Assert.Equal(ahorrosEsperados, ahorros);
    }

    [Fact]
    public void ReparteElCobroEnPesosEnterosEntreDosLineas() =>
        Verificar([Linea(0, 1, 3000m), Linea(1, 2, 2500m)], Combo(7, 5000m, 1, 2), [2727m, 2273m], [273m, 227m]);

    [Fact]
    public void TresLineasIgualesDejanElRestoEnLaUltima() =>
        Verificar(
            [Linea(0, 1, 1000m), Linea(1, 2, 1000m), Linea(2, 3, 1000m)], Combo(7, 2000m, 1, 2, 3),
            [667m, 667m, 666m], [333m, 333m, 334m]);

    [Fact]
    public void ImportesDeListaConCentavosLlevanCentavosEnElAhorroYNoEnElNeto() =>
        Verificar([Linea(0, 1, 500.50m), Linea(1, 2, 999.50m)], Combo(7, 1000m, 1, 2), [334m, 666m], [166.50m, 333.50m]);

    [Fact]
    public void ElPrecioDelComboSeRedondeaAlPesoAntesDeRepartir() =>
        Verificar([Linea(0, 1, 1000m), Linea(1, 2, 500m)], Combo(7, 999.50m, 1, 2), [667m, 333m], [333m, 167m]);

    [Fact]
    public void DosAplicacionesConPreciosConCentavosMultiplicanElReparto() =>
        Verificar([Linea(0, 1, 100.50m, 2m), Linea(1, 2, 49.50m, 2m)], Combo(7, 120m, 1, 2), [160m, 80m], [41m, 19m]);

    [Fact]
    public void LaOfertaSoloAlcanzaALasUnidadesQueElComboNoConsumio() =>
        Verificar(
            [Linea(0, 1, 1000m, 3m, 100m), Linea(1, 2, 1000m)], Combo(7, 1500m, 1, 2), [2550m, 750m], [250m, 250m]);

    [Fact]
    public void ElComboNoSeAplicaSiElCobroRedondeadoNoEsMasBaratoQueLasOfertas() =>
        Verificar(
            [Linea(0, 1, 1000m, 1m, 400m), Linea(1, 2, 1000m, 1m, 400m)], Combo(7, 1199.60m, 1, 2),
            [600m, 600m], [0m, 0m]);

    [Fact]
    public void ElComboNoSeAplicaSiElCobroNoEntraEnLosTopesDeLasLineas() =>
        Verificar(
            [Linea(0, 1, 100.60m), Linea(1, 2, 100.60m), Linea(2, 3, 100.60m)], Combo(7, 301m, 1, 2, 3),
            [101m, 101m, 101m], [0m, 0m, 0m]);

    [Fact]
    public void LaParteProporcionalSeRecortaAlImporteDeListaHaciaAbajoAlPeso() =>
        Verificar(
            [Linea(0, 1, 0.9m), Linea(1, 2, 2.2m), Linea(2, 3, 6.9m)], Combo(7, 8m, 1, 2, 3),
            [0m, 2m, 6m], [0.9m, 0.2m, 0.9m]);

    [Fact]
    public void UnEmpateExactoDelRepartoSubeAlPesoAunqueEnPuntoFlotanteQuedeDebajo() =>
        Verificar([Linea(0, 1, 1.2m), Linea(1, 2, 6m)], Combo(7, 3m, 1, 2), [1m, 2m], [0.2m, 4m]);

    [Fact]
    public void DosComponentesPesablesDeDoscientosGramosRepartenElCobroEntreLasDosLineas() =>
        Verificar(
            [PorPeso(0, Jamon, 10000m, 0.200m), PorPeso(1, Queso, 8000m, 0.200m)], DoscientosMasDoscientos,
            [1667m, 1333m], [333m, 267m]);

    [Fact]
    public void UnaLineaDeDoscientosQuinceGramosAportaDoscientosAlComboYLoQueSobraSeCobraAPrecioDeLista()
    {
        LineaParaCombos[] lineas = [PorPeso(0, Jamon, 10000m, 0.215m), PorPeso(1, Queso, 8000m, 0.200m)];

        Verificar(lineas, DoscientosMasDoscientos, [1817m, 1333m], [333m, 267m]);
        Assert.Equal(
            [0.200m, 0.200m],
            ResolvedorDeCombos.Resolver(lineas, [DoscientosMasDoscientos]).Lineas.Select(l => l.UnidadesConsumidas));
    }

    [Fact]
    public void CuatrocientosGramosDeCadaComponenteAplicanElComboDosVeces() =>
        Verificar(
            [PorPeso(0, Jamon, 10000m, 0.400m), PorPeso(1, Queso, 8000m, 0.400m)], DoscientosMasDoscientos,
            [3334m, 2666m], [666m, 534m]);

    [Fact]
    public void CientoCincuentaGramosNoCompletanUnComponenteDeDoscientosYElComboNoSeAplica() =>
        Verificar(
            [PorPeso(0, Jamon, 10000m, 0.150m), PorPeso(1, Queso, 8000m, 0.200m)], DoscientosMasDoscientos,
            [1500m, 1600m], [0m, 0m]);

    [Fact]
    public void ElImporteDeListaDeLaPorcionConsumidaRedondeaElMedioCentavoHaciaArriba() =>
        Verificar(
            [PorPeso(0, Jamon, 100.10m, 0.250m), PorPeso(1, Queso, 8000m, 0.200m)],
            ComboDe(7, 1000m, Art(Jamon, 0.250m), Art(Queso, 0.200m)),
            [15m, 985m], [10.03m, 615m]);

    [Fact]
    public void EnUnaFamiliaMixtaElMiembroPorUnidadNoEsCandidatoDeUnComponenteDeUnoYMedioYLoCompletaElPesable() =>
        Verificar(
            [
                Linea(0, Salame, 400m, 2m, 0m, UnidadVenta.Unidad, FamiliaFiambres),
                PorPeso(1, Jamon, 1000m, 1.500m, FamiliaFiambres),
                Linea(2, 2, 2500m)
            ],
            ComboDe(7, 1000m, Fam(FamiliaFiambres, 1.500m), Art(2, 1m)),
            [800m, 375m, 625m], [0m, 1125m, 1875m]);

    [Fact]
    public void EnUnaFamiliaMixtaUnComponenteDeDoscientosGramosLoCompletaElMiembroPesableYNoElDeUnidad() =>
        Verificar(
            [
                Linea(0, Salame, 400m, 1m, 0m, UnidadVenta.Unidad, FamiliaFiambres),
                PorPeso(1, Jamon, 10000m, 0.200m, FamiliaFiambres),
                PorPeso(2, Queso, 8000m, 0.200m)
            ],
            ComboDe(7, 3000m, Fam(FamiliaFiambres, 0.200m), Art(Queso, 0.200m)),
            [400m, 1667m, 1333m], [0m, 333m, 267m]);

    [Fact]
    public void UnaLineaPorUnidadConCantidadFraccionariaSeIgnora() =>
        Verificar([Linea(0, 1, 3000m, 1.5m), Linea(1, 2, 2500m)], Combo(7, 5000m, 1, 2), [4500m, 2500m], [0m, 0m]);
}
