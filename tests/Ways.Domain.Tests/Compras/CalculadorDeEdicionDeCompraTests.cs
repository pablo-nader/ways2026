using Ways.Domain.Compras;

namespace Ways.Domain.Tests.Compras;

public class CalculadorDeEdicionDeCompraTests
{
    /// <summary>Cada punto de venta en su propia sucursal (la del mismo número), salvo donde la prueba las separa.</summary>
    private static CantidadDeStockDeCompra Cant(int articulo, int pv, int? lote, decimal cantidad) =>
        new(articulo, pv, pv, lote, cantidad);

    // ---- stock: diferencia por clave ------------------------------------------------------------------------

    [Fact]
    public void SubirLaCantidadDeUnaLineaDaLaDiferenciaPositiva()
    {
        var resultado = CalculadorDeEdicionDeCompra.DiferenciasDeStock(
            [Cant(5, 1, null, 10m)], [Cant(5, 1, null, 14m)]);

        var unica = Assert.Single(resultado);
        Assert.Equal(new DiferenciaDeStockDeCompra(5, 1, 1, null, 4m), unica);
    }

    [Fact]
    public void BajarLaCantidadDeUnaLineaDaLaDiferenciaNegativa()
    {
        var resultado = CalculadorDeEdicionDeCompra.DiferenciasDeStock(
            [Cant(5, 1, null, 10m)], [Cant(5, 1, null, 6m)]);

        Assert.Equal(new DiferenciaDeStockDeCompra(5, 1, 1, null, -4m), Assert.Single(resultado));
    }

    /// <summary>Clausula bajo prueba: el neteo por clave. Un libro con +10 y −4 de la misma clave (una edición anterior)
    /// vale +6; si el neteo se borrara (una diferencia por asiento), el resultado serían dos filas o un valor distinto.</summary>
    [Fact]
    public void LosAsientosDeLaMismaClaveSeNetean()
    {
        var resultado = CalculadorDeEdicionDeCompra.DiferenciasDeStock(
            [Cant(5, 1, null, 10m), Cant(5, 1, null, -4m)], [Cant(5, 1, null, 9m)]);

        Assert.Equal(new DiferenciaDeStockDeCompra(5, 1, 1, null, 3m), Assert.Single(resultado));
    }

    [Fact]
    public void DosLineasDeLaMismaClaveSeSumanAntesDeCompararse()
    {
        var resultado = CalculadorDeEdicionDeCompra.DiferenciasDeStock(
            [Cant(5, 1, null, 10m)], [Cant(5, 1, null, 4m), Cant(5, 1, null, 6m)]);

        Assert.Empty(resultado);
    }

    /// <summary>Clausula bajo prueba: omitir la diferencia cero. Sin ese filtro habría un asiento de stock por una clave que
    /// no cambió (y <c>ck_movimientos_stock_cantidad_no_cero</c> lo rechazaría).</summary>
    [Fact]
    public void LaClaveQueNoCambiaNoFigura()
    {
        var resultado = CalculadorDeEdicionDeCompra.DiferenciasDeStock(
            [Cant(5, 1, null, 10m), Cant(6, 1, null, 3m)], [Cant(5, 1, null, 10m), Cant(6, 1, null, 8m)]);

        Assert.Equal(new DiferenciaDeStockDeCompra(6, 1, 1, null, 5m), Assert.Single(resultado));
    }

    [Fact]
    public void CambiarElPuntoDeVentaSacaDeUnoYPoneEnElOtro()
    {
        var resultado = CalculadorDeEdicionDeCompra.DiferenciasDeStock(
            [Cant(5, 1, null, 10m)], [Cant(5, 2, null, 10m)]);

        Assert.Equal(
            [new DiferenciaDeStockDeCompra(5, 1, 1, null, -10m), new DiferenciaDeStockDeCompra(5, 2, 2, null, 10m)], resultado);
    }

    [Fact]
    public void CambiarDeLoteSacaDelViejoYPoneEnElNuevo()
    {
        var resultado = CalculadorDeEdicionDeCompra.DiferenciasDeStock(
            [Cant(5, 1, 100, 10m)], [Cant(5, 1, 200, 10m)]);

        Assert.Equal(
            [new DiferenciaDeStockDeCompra(5, 1, 1, 100, -10m), new DiferenciaDeStockDeCompra(5, 1, 1, 200, 10m)], resultado);
    }

    [Fact]
    public void QuitarUnaLineaSacaTodoLoQueTenia()
    {
        var resultado = CalculadorDeEdicionDeCompra.DiferenciasDeStock([Cant(5, 1, null, 10m)], []);

        Assert.Equal(new DiferenciaDeStockDeCompra(5, 1, 1, null, -10m), Assert.Single(resultado));
    }

    [Fact]
    public void AgregarUnaLineaPoneTodoLoNuevo()
    {
        var resultado = CalculadorDeEdicionDeCompra.DiferenciasDeStock([], [Cant(7, 1, null, 2m)]);

        Assert.Equal(new DiferenciaDeStockDeCompra(7, 1, 1, null, 2m), Assert.Single(resultado));
    }

    /// <summary>Con la compra nueva vacía es la reversa exacta del libro: la que usa la anulación. +10 y −4 de la misma clave
    /// se revierten con un solo −6, y una clave que ya netea a cero no se revierte.</summary>
    [Fact]
    public void ConLaCompraNuevaVaciaEsLaReversaNetaDelLibro()
    {
        var resultado = CalculadorDeEdicionDeCompra.DiferenciasDeStock(
            [Cant(5, 1, null, 10m), Cant(5, 1, null, -4m), Cant(6, 1, 30, 2m), Cant(6, 1, 30, -2m)], []);

        Assert.Equal(new DiferenciaDeStockDeCompra(5, 1, 1, null, -6m), Assert.Single(resultado));
    }

    [Fact]
    public void ElResultadoVaAscendentePorSucursalArticuloLoteConLoteNuloPrimeroYPuntoDeVenta()
    {
        var resultado = CalculadorDeEdicionDeCompra.DiferenciasDeStock(
            [],
            [
                Cant(9, 1, 1, 1m), Cant(5, 2, null, 1m), Cant(5, 1, 7, 1m), Cant(5, 1, null, 1m), Cant(5, 1, 3, 1m),
                new CantidadDeStockDeCompra(5, 1, 4, null, 1m)
            ]);

        Assert.Equal(
            [(1, 5, (int?)null, 1), (1, 5, null, 4), (1, 5, 3, 1), (1, 5, 7, 1), (1, 9, 1, 1), (2, 5, null, 2)],
            resultado.Select(d => (d.IdSucursal, d.IdArticulo, d.IdLote, d.IdPuntoVenta)).ToList());
    }

    /// <summary>La sucursal es parte de la clave: un asiento del libro guardado en otra sucursal que la que hoy tiene el
    /// punto de venta no se netea con la línea nueva del mismo punto de venta. Se revierte donde se asentó y la línea
    /// nueva entra en la sucursal actual.</summary>
    [Fact]
    public void ElMismoPuntoDeVentaEnOtraSucursalNoSeNetea()
    {
        var resultado = CalculadorDeEdicionDeCompra.DiferenciasDeStock(
            [new CantidadDeStockDeCompra(5, 1, 3, null, 10m)],
            [new CantidadDeStockDeCompra(5, 2, 3, null, 10m)]);

        Assert.Equal(
            [new DiferenciaDeStockDeCompra(5, 1, 3, null, -10m), new DiferenciaDeStockDeCompra(5, 2, 3, null, 10m)],
            resultado);
    }

    // ---- cuenta corriente --------------------------------------------------------------------------------------

    [Fact]
    public void SubirElTotalDaLaDiferenciaPositivaDelMismoProveedor()
    {
        var resultado = CalculadorDeEdicionDeCompra.DiferenciasDeCuentaCorriente(
            [new AsientoDeCuentaDeCompra(7, 100m)], idProveedorNuevo: 7, totalNuevo: 130m);

        Assert.Equal(new DiferenciaDeCuentaDeCompra(7, 30m), Assert.Single(resultado));
    }

    /// <summary>La suma de los asientos manda, no el último: tras +100 y −40 de una edición previa, llevar el total a 90 suma +30.</summary>
    [Fact]
    public void SeCompletaContraLaSumaDeTodosLosAsientosDeLaCompra()
    {
        var resultado = CalculadorDeEdicionDeCompra.DiferenciasDeCuentaCorriente(
            [new AsientoDeCuentaDeCompra(7, 100m), new AsientoDeCuentaDeCompra(7, -40m)], 7, 90m);

        Assert.Equal(new DiferenciaDeCuentaDeCompra(7, 30m), Assert.Single(resultado));
    }

    [Fact]
    public void ElMismoTotalNoDaDiferencia()
    {
        var resultado = CalculadorDeEdicionDeCompra.DiferenciasDeCuentaCorriente(
            [new AsientoDeCuentaDeCompra(7, 100m)], 7, 100m);

        Assert.Empty(resultado);
    }

    [Fact]
    public void CambiarDeProveedorRevierteAlAnteriorYAsientaElTotalEnElNuevoEnOrdenAscendente()
    {
        var resultado = CalculadorDeEdicionDeCompra.DiferenciasDeCuentaCorriente(
            [new AsientoDeCuentaDeCompra(9, 100m)], idProveedorNuevo: 4, totalNuevo: 120m);

        Assert.Equal([new DiferenciaDeCuentaDeCompra(4, 120m), new DiferenciaDeCuentaDeCompra(9, -100m)], resultado);
    }

    [Fact]
    public void VolverAUnProveedorQueYaTuvoLaCompraNoDuplicaSuAsiento()
    {
        // A(7) +100, luego pasó a B(9): A −100, B +100; ahora vuelve a A con total 100.
        var resultado = CalculadorDeEdicionDeCompra.DiferenciasDeCuentaCorriente(
            [
                new AsientoDeCuentaDeCompra(7, 100m), new AsientoDeCuentaDeCompra(7, -100m),
                new AsientoDeCuentaDeCompra(9, 100m)
            ],
            7, 100m);

        Assert.Equal([new DiferenciaDeCuentaDeCompra(7, 100m), new DiferenciaDeCuentaDeCompra(9, -100m)], resultado);
    }

    // ---- los pagos de la compra --------------------------------------------------------------------------------

    [Fact]
    public void SinRedondeoVigenteNoHayNadaQueRevertirAunqueTodoCambie()
    {
        Assert.False(CalculadorDeEdicionDeCompra.RevierteElRedondeo(
            redondeoVigente: 0m, totalAnterior: 100m, totalNuevo: 10m, cambiaLaPertenencia: true));
    }

    [Theory]
    [InlineData(200)]
    [InlineData(50)]
    public void CambiarElTotalDeUnaCompraConRedondeoLoRevierteEnAmbosSentidos(double totalNuevo)
    {
        Assert.True(CalculadorDeEdicionDeCompra.RevierteElRedondeo(
            redondeoVigente: -3m, totalAnterior: 100m, totalNuevo: (decimal)totalNuevo, cambiaLaPertenencia: false));
    }

    [Fact]
    public void CambiarLaPertenenciaDeUnaCompraConRedondeoLoRevierteConElMismoTotal()
    {
        Assert.True(CalculadorDeEdicionDeCompra.RevierteElRedondeo(
            redondeoVigente: -3m, totalAnterior: 100m, totalNuevo: 100m, cambiaLaPertenencia: true));
    }

    [Fact]
    public void ElMismoTotalDeUnaCompraConRedondeoNoLoRevierte()
    {
        Assert.False(CalculadorDeEdicionDeCompra.RevierteElRedondeo(
            redondeoVigente: -3m, totalAnterior: 100m, totalNuevo: 100m, cambiaLaPertenencia: false));
    }

    // ---- lotes ------------------------------------------------------------------------------------------------

    [Fact]
    public void LaLineaConMismoArticuloCodigoYVencimientoConservaSuLote()
    {
        var existentes = new[]
        {
            new LineaDeLoteDeCompra(5, "L1", new DateOnly(2027, 1, 1), 100),
            new LineaDeLoteDeCompra(5, "L2", new DateOnly(2027, 2, 1), 200)
        };

        var encontrada = CalculadorDeEdicionDeCompra.LineaSinCambioDeLote(existentes, 5, " L2 ", new DateOnly(2027, 2, 1));

        Assert.Equal(200, encontrada!.IdLote);
    }

    [Theory]
    [InlineData(6, "L1", 2027, 1, 1)]
    [InlineData(5, "L9", 2027, 1, 1)]
    [InlineData(5, "L1", 2027, 3, 1)]
    public void CualquierDiferenciaDeArticuloCodigoOVencimientoEsUnLoteNuevo(int articulo, string codigo, int anio, int mes, int dia)
    {
        var existentes = new[] { new LineaDeLoteDeCompra(5, "L1", new DateOnly(2027, 1, 1), 100) };

        Assert.Null(CalculadorDeEdicionDeCompra.LineaSinCambioDeLote(existentes, articulo, codigo, new DateOnly(anio, mes, dia)));
    }

    [Fact]
    public void LaLineaSinCodigoConElMismoVencimientoTambienConservaSuLote()
    {
        var existentes = new[] { new LineaDeLoteDeCompra(5, null, new DateOnly(2027, 1, 1), 100) };

        var encontrada = CalculadorDeEdicionDeCompra.LineaSinCambioDeLote(existentes, 5, "  ", new DateOnly(2027, 1, 1));

        Assert.Equal(100, encontrada!.IdLote);
    }
}
