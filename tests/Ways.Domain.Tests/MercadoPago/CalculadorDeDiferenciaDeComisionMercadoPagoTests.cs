using Ways.Domain.MercadoPago;

namespace Ways.Domain.Tests.MercadoPago;

public class CalculadorDeDiferenciaDeComisionMercadoPagoTests
{
    private static Dictionary<(int, int), CierreDeMedioEnCuenta> Cierres(params ((int Turno, int Medio) Clave, int Cuenta, decimal Porcentaje)[] cierres) =>
        cierres.ToDictionary(c => c.Clave, c => new CierreDeMedioEnCuenta(c.Cuenta, c.Porcentaje));

    private static PagoConciliadoParaComision Pago(int idCobroMp, decimal importe, decimal? real, int? turno = 1, int medio = 7) =>
        new(idCobroMp, idCobroMp * 10, turno, medio, importe, real);

    [Fact]
    public void SiMercadoPagoCobroMasDeLoQueElCierreDescontoLaCuentaSeDebita()
    {
        var resultado = CalculadorDeDiferenciaDeComisionMercadoPago.Calcular(
            [Pago(1, 100m, real: 6.50m)], Cierres(((1, 7), 3, 5m)));

        var cuenta = Assert.Single(resultado.PorCuenta);
        Assert.Equal(3, cuenta.IdCuentaTesoreria);
        Assert.Equal(-1.50m, cuenta.Importe);
        Assert.Equal(1, cuenta.CantidadDeCobros);
        Assert.Equal(6.50m, cuenta.ComisionRealTotal);
        Assert.Equal(5.00m, cuenta.ComisionEstimadaTotal);
        var linea = Assert.Single(resultado.Lineas);
        Assert.Equal(1.50m, linea.Diferencia);
        Assert.Equal(5m, linea.PorcentajeEstimado);
    }

    [Fact]
    public void SiMercadoPagoCobroMenosDeLoQueElCierreDescontoLaCuentaSeAcredita()
    {
        var resultado = CalculadorDeDiferenciaDeComisionMercadoPago.Calcular(
            [Pago(1, 100m, real: 3m)], Cierres(((1, 7), 3, 5m)));

        Assert.Equal(2m, Assert.Single(resultado.PorCuenta).Importe);
    }

    [Fact]
    public void LasDiferenciasDeUnaMismaCuentaSeSumanYLasDeOtraCuentaVanAparte()
    {
        var resultado = CalculadorDeDiferenciaDeComisionMercadoPago.Calcular(
            [Pago(1, 100m, 6m), Pago(2, 200m, 11m), Pago(3, 100m, 4m, turno: 2, medio: 8)],
            Cierres(((1, 7), 3, 5m), ((2, 8), 4, 5m)));

        Assert.Equal(2, resultado.PorCuenta.Count);
        var primera = resultado.PorCuenta[0];
        Assert.Equal(3, primera.IdCuentaTesoreria);
        Assert.Equal(-2m, primera.Importe);
        Assert.Equal(2, primera.CantidadDeCobros);
        Assert.Equal(17m, primera.ComisionRealTotal);
        Assert.Equal(15m, primera.ComisionEstimadaTotal);
        var segunda = resultado.PorCuenta[1];
        Assert.Equal(4, segunda.IdCuentaTesoreria);
        Assert.Equal(1m, segunda.Importe);
    }

    [Fact]
    public void UnaCuentaCuyasDiferenciasSeCompensanNoGeneraPropuesta()
    {
        var resultado = CalculadorDeDiferenciaDeComisionMercadoPago.Calcular(
            [Pago(1, 100m, 6m), Pago(2, 100m, 4m)], Cierres(((1, 7), 3, 5m)));

        Assert.Empty(resultado.PorCuenta);
        Assert.Equal(2, resultado.Lineas.Count);
    }

    [Fact]
    public void UnCobroSinCierreDelMedioNoEntraYSeCuenta()
    {
        var resultado = CalculadorDeDiferenciaDeComisionMercadoPago.Calcular(
            [Pago(1, 100m, 6m), Pago(2, 100m, 6m, turno: 9), Pago(3, 100m, 6m, turno: null)],
            Cierres(((1, 7), 3, 5m)));

        Assert.Equal(2, resultado.CobrosSinCierre);
        Assert.Single(resultado.Lineas);
        Assert.Equal(-1m, Assert.Single(resultado.PorCuenta).Importe);
    }

    [Fact]
    public void UnCobroSinComisionRealNoEntraYSeCuenta()
    {
        var resultado = CalculadorDeDiferenciaDeComisionMercadoPago.Calcular(
            [Pago(1, 100m, real: null)], Cierres(((1, 7), 3, 5m)));

        Assert.Equal(1, resultado.CobrosSinComisionReal);
        Assert.Empty(resultado.Lineas);
        Assert.Empty(resultado.PorCuenta);
    }

    [Fact]
    public void LaComisionEstimadaSeRedondeaACentavosConElPuntoMedioHaciaAfuera()
    {
        var resultado = CalculadorDeDiferenciaDeComisionMercadoPago.Calcular(
            [Pago(1, 50.50m, real: 2.53m)], Cierres(((1, 7), 3, 5m)));

        Assert.Equal(2.53m, Assert.Single(resultado.Lineas).ComisionEstimada);
        Assert.Empty(resultado.PorCuenta);
    }

    [Fact]
    public void UnCierreSinPorcentajeEstimaCeroYLaComisionRealEntraEntera()
    {
        var resultado = CalculadorDeDiferenciaDeComisionMercadoPago.Calcular(
            [Pago(1, 100m, real: 4m)], Cierres(((1, 7), 3, 0m)));

        Assert.Equal(-4m, Assert.Single(resultado.PorCuenta).Importe);
    }
}
