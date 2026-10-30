using Ways.Domain.Pagos;

namespace Ways.Domain.Tests.Pagos;

public class CalculadorDeAjustesDePagoTests
{
    private static EstadoContableDePago Proveedor(int idProveedor, decimal importe, int idMedio = 1) =>
        new(CategoriaPago.Proveedor, idProveedor, new Dictionary<int, decimal> { [idMedio] = importe }, importe);

    private static EstadoContableDePago Otros(decimal importe, int idMedio = 1, int? idProveedor = null) =>
        new(CategoriaPago.Otros, idProveedor, new Dictionary<int, decimal> { [idMedio] = importe }, importe);

    private static EstadoContableDePago ConMedios(params (int IdMedio, decimal Importe)[] medios) =>
        new(CategoriaPago.Otros, null, medios.ToDictionary(m => m.IdMedio, m => m.Importe), medios.Sum(m => m.Importe));

    [Fact]
    public void ElMismoProveedorNeteaUnUnicoAjustePorLaDiferencia()
    {
        var ajustes = CalculadorDeAjustesDePago.AjustesDeProveedor(Proveedor(5, 300m), Proveedor(5, 120m));

        var ajuste = Assert.Single(ajustes);
        Assert.Equal(new AjusteDeSaldoDeProveedor(5, 180m), ajuste);
    }

    [Fact]
    public void SubirElImporteSobreElMismoProveedorRestaLaDiferenciaDelSaldo()
    {
        var ajustes = CalculadorDeAjustesDePago.AjustesDeProveedor(Proveedor(5, 100m), Proveedor(5, 250m));

        Assert.Equal([new AjusteDeSaldoDeProveedor(5, -150m)], ajustes);
    }

    [Fact]
    public void ElMismoProveedorConElMismoImporteNoAjustaNada()
    {
        Assert.Empty(CalculadorDeAjustesDePago.AjustesDeProveedor(Proveedor(5, 100m), Proveedor(5, 100m, idMedio: 2)));
    }

    [Fact]
    public void CambiarDeProveedorDevuelveElPagoAlAnteriorYCobraAlNuevoEnOrdenAscendente()
    {
        var ajustes = CalculadorDeAjustesDePago.AjustesDeProveedor(Proveedor(9, 300m), Proveedor(4, 120m));

        Assert.Equal([new AjusteDeSaldoDeProveedor(4, -120m), new AjusteDeSaldoDeProveedor(9, 300m)], ajustes);
    }

    [Fact]
    public void LaBajaDevuelveElPagoCompleto()
    {
        Assert.Equal(
            [new AjusteDeSaldoDeProveedor(5, 300m)],
            CalculadorDeAjustesDePago.AjustesDeProveedor(Proveedor(5, 300m), nuevo: null));
    }

    [Fact]
    public void PasarDeOtraCategoriaAProveedorCobraElPagoPorPrimeraVez()
    {
        Assert.Equal(
            [new AjusteDeSaldoDeProveedor(5, -80m)],
            CalculadorDeAjustesDePago.AjustesDeProveedor(Otros(80m), Proveedor(5, 80m)));
    }

    [Fact]
    public void DejarDeSerProveedorDevuelveElPagoAunqueConserveElIdDeProveedor()
    {
        Assert.Equal(
            [new AjusteDeSaldoDeProveedor(5, 80m)],
            CalculadorDeAjustesDePago.AjustesDeProveedor(Proveedor(5, 80m), Otros(80m, idProveedor: 5)));
    }

    [Fact]
    public void UnProveedorSinIdNuncaEscribioPagoNiLoEscribe()
    {
        var sinProveedor = new EstadoContableDePago(
            CategoriaPago.Proveedor, null, new Dictionary<int, decimal> { [1] = 50m }, 50m);

        Assert.Empty(CalculadorDeAjustesDePago.AjustesDeProveedor(sinProveedor, Otros(70m)));
        Assert.Empty(CalculadorDeAjustesDePago.AjustesDeProveedor(Otros(70m), sinProveedor));
        Assert.Empty(CalculadorDeAjustesDePago.AjustesDeProveedor(sinProveedor, nuevo: null));
    }

    [Theory]
    [InlineData(100, 160, 0, 60)]
    [InlineData(160, 100, 60, 0)]
    public void LaTesoreriaEgresaLoQueSubeYReingresaLoQueBaja(
        decimal anterior, decimal nuevo, decimal ingreso, decimal egreso)
    {
        Assert.Equal(
            new AjusteDeTesoreria(ingreso, egreso),
            CalculadorDeAjustesDePago.AjusteDeTesoreriaPara(OrigenFondosPago.Tesoreria, anterior, nuevo));
    }

    [Fact]
    public void LaBajaDeUnPagoDeTesoreriaReingresaElImporteCompleto()
    {
        Assert.Equal(
            new AjusteDeTesoreria(250m, 0m),
            CalculadorDeAjustesDePago.AjusteDeTesoreriaPara(OrigenFondosPago.Tesoreria, 250m, null));
    }

    [Fact]
    public void SinCambioDeImporteOConFondosDeCajaNoHayAjusteDeTesoreria()
    {
        Assert.Null(CalculadorDeAjustesDePago.AjusteDeTesoreriaPara(OrigenFondosPago.Tesoreria, 100m, 100m));
        Assert.Null(CalculadorDeAjustesDePago.AjusteDeTesoreriaPara(OrigenFondosPago.CajaTurno, 100m, 300m));
        Assert.Null(CalculadorDeAjustesDePago.AjusteDeTesoreriaPara(OrigenFondosPago.CajaTurno, 100m, null));
    }

    [Fact]
    public void ElArqueoSoloCambiaPorImporteMedioOBajaDeUnPagoDeCaja()
    {
        var caja = OrigenFondosPago.CajaTurno;

        Assert.True(CalculadorDeAjustesDePago.AfectaElArqueo(caja, Otros(100m), Otros(90m)));
        Assert.True(CalculadorDeAjustesDePago.AfectaElArqueo(caja, Otros(100m), Otros(100m, idMedio: 2)));
        Assert.True(CalculadorDeAjustesDePago.AfectaElArqueo(caja, Otros(100m), nuevo: null));
        Assert.False(CalculadorDeAjustesDePago.AfectaElArqueo(caja, Otros(100m), Proveedor(5, 100m)));
        Assert.False(CalculadorDeAjustesDePago.AfectaElArqueo(OrigenFondosPago.Tesoreria, Otros(100m), Otros(90m)));
        Assert.False(CalculadorDeAjustesDePago.AfectaElArqueo(OrigenFondosPago.Tesoreria, Otros(100m), nuevo: null));
    }

    [Fact]
    public void ElMismoReparto_PorMedio_NoAfectaElArqueo()
    {
        var caja = OrigenFondosPago.CajaTurno;

        Assert.False(CalculadorDeAjustesDePago.AfectaElArqueo(caja, Otros(100m), Otros(100m)));
        Assert.False(CalculadorDeAjustesDePago.AfectaElArqueo(caja, ConMedios((1, 60m), (2, 40m)), ConMedios((2, 40m), (1, 60m))));
    }

    [Fact]
    public void UnMapaDeDosMediosContraUnoDeUnMedio_AfectaElArqueoAunqueElTotalCoincida()
    {
        var caja = OrigenFondosPago.CajaTurno;

        Assert.True(CalculadorDeAjustesDePago.AfectaElArqueo(caja, Otros(100m), ConMedios((1, 60m), (2, 40m))));
        Assert.True(CalculadorDeAjustesDePago.AfectaElArqueo(caja, ConMedios((1, 60m), (2, 40m)), Otros(100m)));
    }

    [Fact]
    public void UnCambioDeImporteDeUnSoloMedioDelMapa_AfectaElArqueo()
    {
        var caja = OrigenFondosPago.CajaTurno;

        Assert.True(CalculadorDeAjustesDePago.AfectaElArqueo(caja, ConMedios((1, 60m), (2, 40m)), ConMedios((1, 60m), (2, 30m))));
        Assert.True(CalculadorDeAjustesDePago.AfectaElArqueo(caja, ConMedios((1, 60m), (2, 40m)), ConMedios((1, 60m), (3, 40m))));
    }

    [Fact]
    public void UnMapaQueEsSubconjuntoEstrictoDelOtro_AfectaElArqueoEnAmbosSentidos()
    {
        var caja = OrigenFondosPago.CajaTurno;

        Assert.True(CalculadorDeAjustesDePago.AfectaElArqueo(caja, ConMedios((1, 60m)), ConMedios((1, 60m), (2, 40m))));
        Assert.True(CalculadorDeAjustesDePago.AfectaElArqueo(caja, ConMedios((1, 60m), (2, 40m)), ConMedios((1, 60m))));
    }

    [Fact]
    public void LosAjustesDeProveedorUsanElTotalDeLaCabecera_AunConVariosMedios()
    {
        var anterior = new EstadoContableDePago(
            CategoriaPago.Proveedor, 5, new Dictionary<int, decimal> { [1] = 60m, [2] = 40m }, 100m);

        var ajuste = Assert.Single(CalculadorDeAjustesDePago.AjustesDeProveedor(anterior, nuevo: null));

        Assert.Equal(new AjusteDeSaldoDeProveedor(5, 100m), ajuste);
    }
}
