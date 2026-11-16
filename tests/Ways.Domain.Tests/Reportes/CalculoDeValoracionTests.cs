using Ways.Domain.Reportes;

namespace Ways.Domain.Tests.Reportes;

/// <summary>Aritmética pura del reporte de valoración: stock positivo por par artículo-sucursal, cobertura de costo y precio, costo
/// estimado, totales y agrupación por área. Sin base de datos, como <c>PoliticaDeRoles</c>.</summary>
public class CalculoDeValoracionTests
{
    private static readonly IReadOnlySet<int> SinAreas = new HashSet<int>();

    private static Dictionary<int, ArticuloAValorar> Articulos(params ArticuloAValorar[] articulos) =>
        articulos.ToDictionary(a => a.IdArticulo);

    private static MercaderiaValorada ValorarAhora(
        Dictionary<int, decimal> stock, Dictionary<int, ArticuloAValorar> articulos, Dictionary<int, decimal?> precios,
        IReadOnlySet<int>? areasVisibles = null) =>
        CalculoDeValoracion.Valorar(stock, articulos, costoDeCompra: null, precios, areasVisibles ?? SinAreas);

    // ---- stock positivo por par ---------------------------------------------------------------

    [Fact]
    public void UnParNegativoSeAcotaACeroSinRestarleAlOtroLocal()
    {
        var pares = new[]
        {
            new StockDeSucursal(1, 10, 5m), new StockDeSucursal(1, 20, -3m)
        };

        var stock = CalculoDeValoracion.StockPositivoPorArticulo(pares);

        Assert.Equal(5m, stock[1]);
    }

    [Fact]
    public void LosParesPositivosDeDistintasSucursalesSeSuman()
    {
        var pares = new[]
        {
            new StockDeSucursal(1, 10, 5m), new StockDeSucursal(1, 20, 2.5m), new StockDeSucursal(2, 10, 1m)
        };

        var stock = CalculoDeValoracion.StockPositivoPorArticulo(pares);

        Assert.Equal(7.5m, stock[1]);
        Assert.Equal(1m, stock[2]);
    }

    [Fact]
    public void UnArticuloSoloConParesNegativosOEnCeroNoTieneStockPositivo()
    {
        var pares = new[]
        {
            new StockDeSucursal(1, 10, -4m), new StockDeSucursal(1, 20, 0m), new StockDeSucursal(2, 10, -1m)
        };

        Assert.Empty(CalculoDeValoracion.StockPositivoPorArticulo(pares));
    }

    [Fact]
    public void FilasRepetidasDeUnMismoParSeSumanAntesDeAcotar()
    {
        var pares = new[]
        {
            new StockDeSucursal(1, 10, 5m), new StockDeSucursal(1, 10, -8m), new StockDeSucursal(1, 20, 2m)
        };

        var stock = CalculoDeValoracion.StockPositivoPorArticulo(pares);

        Assert.Equal(2m, stock[1]);
    }

    // ---- valoración ---------------------------------------------------------------------------

    [Fact]
    public void ValoraACostoYAVentaElStockPositivo()
    {
        var articulos = Articulos(new ArticuloAValorar(1, null, 10m), new ArticuloAValorar(2, null, 4m));

        var m = ValorarAhora(
            new() { [1] = 3m, [2] = 2m }, articulos, new() { [1] = 15m, [2] = 6m });

        Assert.Equal(38m, m.Costo);
        Assert.Equal(57m, m.Venta);
        Assert.Equal(0, m.SinCosto);
        Assert.Equal(0, m.SinPrecio);
        Assert.Equal(0, m.ConCostoEstimado);
    }

    [Fact]
    public void UnCostoNuloNoSeValoraEnCeroSinoQueSumaALaCoberturaYSigueValuadoAVenta()
    {
        var articulos = Articulos(new ArticuloAValorar(1, null, null), new ArticuloAValorar(2, null, 4m));

        var m = ValorarAhora(new() { [1] = 3m, [2] = 2m }, articulos, new() { [1] = 15m, [2] = 6m });

        Assert.Equal(8m, m.Costo);
        Assert.Equal(1, m.SinCosto);
        Assert.Equal(57m, m.Venta);
    }

    [Fact]
    public void UnCostoCeroEsUnCostoValidoYNoCuentaComoSinCosto()
    {
        var articulos = Articulos(new ArticuloAValorar(1, null, 0m));

        var m = ValorarAhora(new() { [1] = 3m }, articulos, new() { [1] = 5m });

        Assert.Equal(0m, m.Costo);
        Assert.Equal(0, m.SinCosto);
    }

    [Fact]
    public void UnPrecioAusenteONuloNoSeValoraEnCeroSinoQueSumaALaCobertura()
    {
        var articulos = Articulos(new ArticuloAValorar(1, null, 10m), new ArticuloAValorar(2, null, 4m));

        var m = ValorarAhora(new() { [1] = 3m, [2] = 2m }, articulos, new() { [1] = null });

        Assert.Equal(0m, m.Venta);
        Assert.Equal(2, m.SinPrecio);
        Assert.Equal(38m, m.Costo);
    }

    [Fact]
    public void UnArticuloSinFichaVigenteNoSeValora()
    {
        var m = ValorarAhora(new() { [9] = 3m }, Articulos(), new() { [9] = 5m });

        Assert.Equal(0m, m.Costo);
        Assert.Equal(0m, m.Venta);
        Assert.Equal(0, m.SinCosto);
        Assert.Empty(m.PorArea);
    }

    // ---- costo histórico y estimado -----------------------------------------------------------

    [Fact]
    public void ConHistorialUsaElCostoDeLaCompraYSinHistorialEstimaConElActualYLoCuenta()
    {
        var articulos = Articulos(new ArticuloAValorar(1, null, 100m), new ArticuloAValorar(2, null, 50m));
        var costoDeCompra = new Dictionary<int, decimal> { [1] = 8m };

        var m = CalculoDeValoracion.Valorar(
            new Dictionary<int, decimal> { [1] = 2m, [2] = 3m }, articulos, costoDeCompra,
            new Dictionary<int, decimal?> { [1] = 20m, [2] = 70m }, SinAreas);

        Assert.Equal(16m + 150m, m.Costo);
        Assert.Equal(1, m.ConCostoEstimado);
        Assert.Equal(150m, m.ValorCostoEstimado);
        Assert.Equal(0, m.SinCosto);
    }

    [Fact]
    public void SinHistorialNiCostoActualEsSinCostoYNoEstimado()
    {
        var articulos = Articulos(new ArticuloAValorar(1, null, null));

        var m = CalculoDeValoracion.Valorar(
            new Dictionary<int, decimal> { [1] = 2m }, articulos, new Dictionary<int, decimal>(),
            new Dictionary<int, decimal?> { [1] = 20m }, SinAreas);

        Assert.Equal(1, m.SinCosto);
        Assert.Equal(0, m.ConCostoEstimado);
        Assert.Equal(0m, m.ValorCostoEstimado);
    }

    [Fact]
    public void ElPuntoActualUsaElCostoNominalSinMarcarEstimados()
    {
        var articulos = Articulos(new ArticuloAValorar(1, null, 100m));

        var m = ValorarAhora(new() { [1] = 2m }, articulos, new() { [1] = 120m });

        Assert.Equal(200m, m.Costo);
        Assert.Equal(0, m.ConCostoEstimado);
    }

    // ---- áreas --------------------------------------------------------------------------------

    [Fact]
    public void AgrupaPorAreaVisibleYUnaAreaDeBajaVaConLasSinArea()
    {
        var articulos = Articulos(
            new ArticuloAValorar(1, 7, 10m), new ArticuloAValorar(2, 8, 10m),
            new ArticuloAValorar(3, null, 10m), new ArticuloAValorar(4, 7, 10m));

        var m = ValorarAhora(
            new() { [1] = 1m, [2] = 1m, [3] = 1m, [4] = 2m }, articulos,
            new() { [1] = 20m, [2] = 20m, [3] = 20m, [4] = 20m },
            areasVisibles: new HashSet<int> { 7 });

        var area7 = Assert.Single(m.PorArea, a => a.IdArea == 7);
        Assert.Equal(30m, area7.Costo);
        Assert.Equal(60m, area7.Venta);
        var sinArea = Assert.Single(m.PorArea, a => a.IdArea is null);
        Assert.Equal(20m, sinArea.Costo);
        Assert.Equal(40m, sinArea.Venta);
        Assert.DoesNotContain(m.PorArea, a => a.IdArea == 8);
        Assert.Equal(50m, m.PorArea.Sum(a => a.Costo));
    }

    // ---- totales ------------------------------------------------------------------------------

    [Fact]
    public void LosTotalesSumanMercaderiaMasDineroACostoYAVenta()
    {
        var m = new MercaderiaValorada(100m, 150m, 0, 0, 0, 0m, []);

        var dinero = CalculoDeValoracion.Dinero(efectivo: 40m, cuentas: 60m);

        Assert.Equal(100m, dinero);
        Assert.Equal(200m, CalculoDeValoracion.TotalACosto(m, dinero));
        Assert.Equal(250m, CalculoDeValoracion.TotalAVenta(m, dinero));
    }

    [Fact]
    public void ElDineroPuedeSerNegativoYRestaDelTotal()
    {
        var m = new MercaderiaValorada(100m, 150m, 0, 0, 0, 0m, []);

        var dinero = CalculoDeValoracion.Dinero(efectivo: -30m, cuentas: 10m);

        Assert.Equal(-20m, dinero);
        Assert.Equal(80m, CalculoDeValoracion.TotalACosto(m, dinero));
    }
}
