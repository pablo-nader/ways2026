using Ways.Domain.Combos;
using Ways.Domain.Ventas;

namespace Ways.Domain.Tests.Combos;

/// <summary>Tabla de paridad servidor/web: los mismos carritos con los mismos netos esperados por línea que
/// <c>src/Ways.Web/src/pos/paridadDeCombos.test.ts</c> (motor de combos de TypeScript más la fórmula de línea
/// <c>calcularTotalesDeLinea</c>). Acá la salida de <see cref="ResolvedorDeCombos"/> pasa por
/// <see cref="CalculadorDeTotales"/>. Un cambio de un número en una de las dos tablas exige el mismo cambio en la otra.</summary>
public class ParidadDeCombosConElMotorWebTests
{
    private static LineaParaCombos Linea(int indice, int idArticulo, decimal precio, decimal cantidad = 1m, decimal descuentoDeOferta = 0m) =>
        new(indice, idArticulo, null, cantidad, precio, descuentoDeOferta);

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
}
