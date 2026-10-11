using Ways.Application.Combos;
using Ways.Domain.Articulos;
using Ways.Domain.Combos;

namespace Ways.Application.Tests.Combos;

/// <summary>Reglas de <see cref="CombosEnVenta"/>: qué líneas de una venta entran a la detección y qué combo
/// se le asigna a cada línea consumida. El reparto del ahorro es del motor y está probado en Ways.Domain.Tests.</summary>
public class CombosEnVentaTests
{
    private const int Pancho = 10;
    private const int Coca = 20;

    private static LineaDeVentaParaCombos Linea(
        int idArticulo, decimal cantidad, decimal precio, decimal descuentoDeOferta = 0m,
        UnidadVenta unidad = UnidadVenta.Unidad, int? idFamilia = null) =>
        new(idArticulo, idFamilia, unidad, cantidad, precio, descuentoDeOferta);

    private static ComboCandidato PanchoYCoca(int id = 1, decimal precio = 5000m) => new(
        id, $"Combo {id}", precio,
        [new ComponenteDeCombo(Pancho, null, 1m), new ComponenteDeCombo(Coca, null, 1m)]);

    [Fact]
    public void UnaVentaConElConjuntoCompletoConsumeUnidadesYSumaElPrecioDelCombo()
    {
        var resultado = CombosEnVenta.Resolver(
            [Linea(Pancho, 1m, 3000m), Linea(Coca, 1m, 2500m)], [PanchoYCoca()]);

        Assert.Equal(2, resultado.Count);
        Assert.Equal(new ComboEnLinea(1, 272.73m, 1), resultado[0]);
        Assert.Equal(new ComboEnLinea(1, 227.27m, 1), resultado[1]);
    }

    [Fact]
    public void LaClaveDelResultadoEsLaPosicionDeLaLineaEnLaVenta()
    {
        var resultado = CombosEnVenta.Resolver(
            [Linea(99, 1m, 100m), Linea(Pancho, 1m, 3000m), Linea(Coca, 1m, 2500m)], [PanchoYCoca()]);

        Assert.Equal([1, 2], resultado.Keys.Order());
    }

    [Fact]
    public void UnaLineaPesableConCantidadEnteraNoSeConsumeAunqueElComboLaNombre()
    {
        var resultado = CombosEnVenta.Resolver(
            [Linea(Pancho, 1m, 3000m, unidad: UnidadVenta.Peso), Linea(Coca, 1m, 2500m)], [PanchoYCoca()]);

        Assert.Empty(resultado);
    }

    [Fact]
    public void UnaLineaSinPrecioVigenteNoEntraALaDeteccion()
    {
        var resultado = CombosEnVenta.Resolver(
            [new LineaDeVentaParaCombos(Pancho, null, UnidadVenta.Unidad, 1m, null, 0m), Linea(Coca, 1m, 2500m)],
            [PanchoYCoca()]);

        Assert.Empty(resultado);
    }

    [Fact]
    public void UnaLineaConDescuentoDeOfertaNegativoNoEntraALaDeteccion()
    {
        var resultado = CombosEnVenta.Resolver(
            [Linea(Pancho, 1m, 3000m, descuentoDeOferta: -10m), Linea(Coca, 1m, 2500m)], [PanchoYCoca()]);

        Assert.Empty(resultado);
    }

    [Fact]
    public void LaLineaConUnidadesDeDosCombosGuardaElIdMasBajo()
    {
        // 3 panchos y 2 cocas; el combo 9 (2 panchos + 1 coca, mayor ganancia) se aplica primero y el 4
        // (1 pancho + 1 coca) toma lo que queda.
        var combos = new[]
        {
            new ComboCandidato(9, "Nueve", 3500m,
                [new ComponenteDeCombo(Pancho, null, 2m), new ComponenteDeCombo(Coca, null, 1m)]),
            new ComboCandidato(4, "Cuatro", 4500m,
                [new ComponenteDeCombo(Pancho, null, 1m), new ComponenteDeCombo(Coca, null, 1m)])
        };

        var resultado = CombosEnVenta.Resolver(
            [Linea(Pancho, 3m, 3000m), Linea(Coca, 2m, 2500m)], combos);

        // Nueve consume 2 panchos + 1 coca; luego Cuatro consume el pancho y la coca que quedan.
        // La línea de pancho fue consumida por los dos combos: se guarda el de menor id (4), no el primero aplicado (9).
        Assert.Equal(3, resultado[0].UnidadesConsumidas);
        Assert.Equal(4, resultado[0].IdCombo);
        Assert.Equal(2, resultado[1].UnidadesConsumidas);
        Assert.Equal(4, resultado[1].IdCombo);
    }

    [Fact]
    public void SinCombosCandidatosElResultadoEsVacio()
    {
        Assert.Empty(CombosEnVenta.Resolver([Linea(Pancho, 1m, 3000m), Linea(Coca, 1m, 2500m)], []));
    }
}
