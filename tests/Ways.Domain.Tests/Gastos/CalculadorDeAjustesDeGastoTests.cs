using Ways.Domain.Gastos;

namespace Ways.Domain.Tests.Gastos;

public class CalculadorDeAjustesDeGastoTests
{
    private static EstadoContableDeGasto Proveedor(int idProveedor, decimal importe, int idMedio = 1) =>
        new(CategoriaGasto.Proveedor, idProveedor, idMedio, importe);

    private static EstadoContableDeGasto Otros(decimal importe, int idMedio = 1, int? idProveedor = null) =>
        new(CategoriaGasto.Otros, idProveedor, idMedio, importe);

    [Fact]
    public void ElMismoProveedorNeteaUnUnicoAjustePorLaDiferencia()
    {
        var ajustes = CalculadorDeAjustesDeGasto.AjustesDeProveedor(Proveedor(5, 300m), Proveedor(5, 120m));

        var ajuste = Assert.Single(ajustes);
        Assert.Equal(new AjusteDeSaldoDeProveedor(5, 180m), ajuste);
    }

    [Fact]
    public void SubirElImporteSobreElMismoProveedorRestaLaDiferenciaDelSaldo()
    {
        var ajustes = CalculadorDeAjustesDeGasto.AjustesDeProveedor(Proveedor(5, 100m), Proveedor(5, 250m));

        Assert.Equal([new AjusteDeSaldoDeProveedor(5, -150m)], ajustes);
    }

    [Fact]
    public void ElMismoProveedorConElMismoImporteNoAjustaNada()
    {
        Assert.Empty(CalculadorDeAjustesDeGasto.AjustesDeProveedor(Proveedor(5, 100m), Proveedor(5, 100m, idMedio: 2)));
    }

    [Fact]
    public void CambiarDeProveedorDevuelveElPagoAlAnteriorYCobraAlNuevoEnOrdenAscendente()
    {
        var ajustes = CalculadorDeAjustesDeGasto.AjustesDeProveedor(Proveedor(9, 300m), Proveedor(4, 120m));

        Assert.Equal([new AjusteDeSaldoDeProveedor(4, -120m), new AjusteDeSaldoDeProveedor(9, 300m)], ajustes);
    }

    [Fact]
    public void LaBajaDevuelveElPagoCompleto()
    {
        Assert.Equal(
            [new AjusteDeSaldoDeProveedor(5, 300m)],
            CalculadorDeAjustesDeGasto.AjustesDeProveedor(Proveedor(5, 300m), nuevo: null));
    }

    [Fact]
    public void PasarDeOtraCategoriaAProveedorCobraElPagoPorPrimeraVez()
    {
        Assert.Equal(
            [new AjusteDeSaldoDeProveedor(5, -80m)],
            CalculadorDeAjustesDeGasto.AjustesDeProveedor(Otros(80m), Proveedor(5, 80m)));
    }

    [Fact]
    public void DejarDeSerProveedorDevuelveElPagoAunqueConserveElIdDeProveedor()
    {
        Assert.Equal(
            [new AjusteDeSaldoDeProveedor(5, 80m)],
            CalculadorDeAjustesDeGasto.AjustesDeProveedor(Proveedor(5, 80m), Otros(80m, idProveedor: 5)));
    }

    [Fact]
    public void UnProveedorSinIdNuncaEscribioPagoNiLoEscribe()
    {
        var sinProveedor = new EstadoContableDeGasto(CategoriaGasto.Proveedor, null, 1, 50m);

        Assert.Empty(CalculadorDeAjustesDeGasto.AjustesDeProveedor(sinProveedor, Otros(70m)));
        Assert.Empty(CalculadorDeAjustesDeGasto.AjustesDeProveedor(Otros(70m), sinProveedor));
        Assert.Empty(CalculadorDeAjustesDeGasto.AjustesDeProveedor(sinProveedor, nuevo: null));
    }

    [Theory]
    [InlineData(100, 160, 0, 60)]
    [InlineData(160, 100, 60, 0)]
    public void LaTesoreriaEgresaLoQueSubeYReingresaLoQueBaja(
        decimal anterior, decimal nuevo, decimal ingreso, decimal egreso)
    {
        Assert.Equal(
            new AjusteDeTesoreria(ingreso, egreso),
            CalculadorDeAjustesDeGasto.AjusteDeTesoreriaPara(OrigenFondosGasto.Tesoreria, anterior, nuevo));
    }

    [Fact]
    public void LaBajaDeUnGastoDeTesoreriaReingresaElImporteCompleto()
    {
        Assert.Equal(
            new AjusteDeTesoreria(250m, 0m),
            CalculadorDeAjustesDeGasto.AjusteDeTesoreriaPara(OrigenFondosGasto.Tesoreria, 250m, null));
    }

    [Fact]
    public void SinCambioDeImporteOConFondosDeCajaNoHayAjusteDeTesoreria()
    {
        Assert.Null(CalculadorDeAjustesDeGasto.AjusteDeTesoreriaPara(OrigenFondosGasto.Tesoreria, 100m, 100m));
        Assert.Null(CalculadorDeAjustesDeGasto.AjusteDeTesoreriaPara(OrigenFondosGasto.CajaTurno, 100m, 300m));
        Assert.Null(CalculadorDeAjustesDeGasto.AjusteDeTesoreriaPara(OrigenFondosGasto.CajaTurno, 100m, null));
    }

    [Fact]
    public void ElArqueoSoloCambiaPorImporteMedioOBajaDeUnGastoDeCaja()
    {
        var caja = OrigenFondosGasto.CajaTurno;

        Assert.True(CalculadorDeAjustesDeGasto.AfectaElArqueo(caja, Otros(100m), Otros(90m)));
        Assert.True(CalculadorDeAjustesDeGasto.AfectaElArqueo(caja, Otros(100m), Otros(100m, idMedio: 2)));
        Assert.True(CalculadorDeAjustesDeGasto.AfectaElArqueo(caja, Otros(100m), nuevo: null));
        Assert.False(CalculadorDeAjustesDeGasto.AfectaElArqueo(caja, Otros(100m), Proveedor(5, 100m)));
        Assert.False(CalculadorDeAjustesDeGasto.AfectaElArqueo(OrigenFondosGasto.Tesoreria, Otros(100m), Otros(90m)));
        Assert.False(CalculadorDeAjustesDeGasto.AfectaElArqueo(OrigenFondosGasto.Tesoreria, Otros(100m), nuevo: null));
    }
}
