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
        Assert.Equal(new ComboEnLinea(1, 273m, 1), resultado[0]);
        Assert.Equal(new ComboEnLinea(1, 227m, 1), resultado[1]);
    }

    [Fact]
    public void LaClaveDelResultadoEsLaPosicionDeLaLineaEnLaVenta()
    {
        var resultado = CombosEnVenta.Resolver(
            [Linea(99, 1m, 100m), Linea(Pancho, 1m, 3000m), Linea(Coca, 1m, 2500m)], [PanchoYCoca()]);

        Assert.Equal([1, 2], resultado.Keys.Order());
    }

    /// <summary>Cláusula: la unidad de venta de la línea viaja al motor. Una línea pesable completa un componente
    /// fraccionario y la misma línea, por unidad, no.</summary>
    [Fact]
    public void UnaLineaPesableEntraALaDeteccionConSuUnidadDeVentaYConsumeLaCantidadDelComponente()
    {
        const int Jamon = 30;
        var combo = new ComboCandidato(
            1, "Picada", 3500m, [new ComponenteDeCombo(Jamon, null, 0.200m), new ComponenteDeCombo(Coca, null, 1m)]);

        var pesable = CombosEnVenta.Resolver(
            [Linea(Jamon, 0.215m, 10000m, unidad: UnidadVenta.Peso), Linea(Coca, 1m, 2500m)], [combo]);
        var porUnidad = CombosEnVenta.Resolver(
            [Linea(Jamon, 2m, 10000m), Linea(Coca, 1m, 2500m)], [combo]);

        Assert.Equal(new ComboEnLinea(0.200m, 444m, 1), pesable[0]);
        Assert.Equal(new ComboEnLinea(1m, 556m, 1), pesable[1]);
        Assert.Empty(porUnidad);
    }

    [Fact]
    public void UnaLineaPesableConCantidadEnteraCompletaUnComponenteDeCantidadEntera()
    {
        var resultado = CombosEnVenta.Resolver(
            [Linea(Pancho, 1m, 3000m, unidad: UnidadVenta.Peso), Linea(Coca, 1m, 2500m)], [PanchoYCoca()]);

        Assert.Equal([0, 1], resultado.Keys.Order());
    }

    /// <summary>Cláusula: la unidad de venta por importe viaja al motor y la línea no consume el combo, aunque traiga
    /// precio (el importe tipeado) y cantidad 1.</summary>
    [Fact]
    public void UnaLineaDeUnArticuloPorImporteNoConsumeElCombo()
    {
        var resultado = CombosEnVenta.Detectar(
            [Linea(Pancho, 1m, 3000m, unidad: UnidadVenta.Importe), Linea(Coca, 1m, 2500m)], [PanchoYCoca()]);

        Assert.Empty(resultado.Lineas);
        Assert.Empty(resultado.Combos);
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
        // 3 panchos y una unidad de coca, papas y gaseosa. Cada combo es pancho + una de las otras, y la otra
        // alcanza para una sola aplicación. Ganancias: el 7 (coca) 1000, el 2 (papas) 400, el 5 (gaseosa) 100;
        // el orden de aplicación es 7, 2, 5. El pancho lo consumen los tres: el primero aplicado es el 7, el
        // último el 5 y el de menor id el 2, tres ids distintos.
        const int Papas = 30;
        const int Gaseosa = 40;
        var combos = new[]
        {
            new ComboCandidato(7, "Siete", 4500m,
                [new ComponenteDeCombo(Pancho, null, 1m), new ComponenteDeCombo(Coca, null, 1m)]),
            new ComboCandidato(2, "Dos", 3600m,
                [new ComponenteDeCombo(Pancho, null, 1m), new ComponenteDeCombo(Papas, null, 1m)]),
            new ComboCandidato(5, "Cinco", 3700m,
                [new ComponenteDeCombo(Pancho, null, 1m), new ComponenteDeCombo(Gaseosa, null, 1m)])
        };

        var resultado = CombosEnVenta.Resolver(
            [Linea(Pancho, 3m, 3000m), Linea(Coca, 1m, 2500m), Linea(Papas, 1m, 1000m), Linea(Gaseosa, 1m, 800m)],
            combos);

        Assert.Equal(3, resultado[0].UnidadesConsumidas);
        Assert.Equal(2, resultado[0].IdCombo);
        Assert.Equal(7, resultado[1].IdCombo);
        Assert.Equal(2, resultado[2].IdCombo);
        Assert.Equal(5, resultado[3].IdCombo);
    }

    [Fact]
    public void SinCombosCandidatosElResultadoEsVacio()
    {
        Assert.Empty(CombosEnVenta.Resolver([Linea(Pancho, 1m, 3000m), Linea(Coca, 1m, 2500m)], []));
    }
}
