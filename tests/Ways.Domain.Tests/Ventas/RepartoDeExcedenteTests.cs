using Ways.Domain.Common;
using Ways.Domain.Ventas;

namespace Ways.Domain.Tests.Ventas;

public class RepartoDeExcedenteTests
{
    private static PagoARepartir Qr(decimal importe) => new(importe, 0m, AdmiteVuelto: false, Divisible: true);

    private static PagoARepartir Efectivo(decimal importe, decimal vuelto = 0m) =>
        new(importe, vuelto, AdmiteVuelto: true, Divisible: true);

    private static PagoARepartir NoDivisible(decimal importe) => new(importe, 0m, AdmiteVuelto: false, Divisible: false);

    [Fact]
    public void UnQrPorEncimaDelTotalDejaElTotalEnLaVentaYLaDiferenciaACuenta()
    {
        var reparto = RepartoDeExcedente.Repartir(990m, [Qr(1000m)]);

        Assert.Equal(10m, reparto.Excedente);
        Assert.Equal([990m], reparto.ImportesDeLaVenta);
        Assert.Equal([new PagoCedidoACuenta(0, 10m)], reparto.PagosACuenta);
    }

    [Fact]
    public void ElExcedenteSaleDeLosMediosSinVueltoAntesQueDelEfectivo()
    {
        var reparto = RepartoDeExcedente.Repartir(900m, [Qr(500m), Efectivo(500m)]);

        Assert.Equal([400m, 500m], reparto.ImportesDeLaVenta);
        Assert.Equal([new PagoCedidoACuenta(0, 100m)], reparto.PagosACuenta);
    }

    [Fact]
    public void DentroDeUnGrupoElExcedenteSaleDesdeElUltimoPago()
    {
        var reparto = RepartoDeExcedente.Repartir(50m, [Qr(80m), Qr(70m)]);

        Assert.Equal([50m, 0m], reparto.ImportesDeLaVenta);
        Assert.Equal([new PagoCedidoACuenta(1, 70m), new PagoCedidoACuenta(0, 30m)], reparto.PagosACuenta);
    }

    [Fact]
    public void ElEfectivoCedeLoQueNoVolvioComoVuelto()
    {
        var reparto = RepartoDeExcedente.Repartir(990m, [Efectivo(1000m, vuelto: 5m)]);

        Assert.Equal(5m, reparto.Excedente);
        Assert.Equal([995m], reparto.ImportesDeLaVenta);
        Assert.Equal([new PagoCedidoACuenta(0, 5m)], reparto.PagosACuenta);
    }

    [Fact]
    public void UnPagoNoDivisibleNuncaCedeImporte()
    {
        var reparto = RepartoDeExcedente.Repartir(100m, [Qr(50m), NoDivisible(80m)]);

        Assert.Equal([20m, 80m], reparto.ImportesDeLaVenta);
        Assert.Equal([new PagoCedidoACuenta(0, 30m)], reparto.PagosACuenta);
    }

    [Fact]
    public void SiElExcedenteSoloPuedeSalirDeUnPagoNoDivisibleSeRechaza()
    {
        var error = Assert.Throws<ErrorDominio>(() => RepartoDeExcedente.Repartir(100m, [NoDivisible(150m)]));

        Assert.Equal("pago_a_cuenta_no_divisible", error.Codigo);
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(900)]
    public void SinExcedenteSeRechaza(decimal importe)
    {
        var error = Assert.Throws<ErrorDominio>(() => RepartoDeExcedente.Repartir(1000m, [Qr(importe)]));

        Assert.Equal("pago_a_cuenta_sin_excedente", error.Codigo);
    }

    [Fact]
    public void ElVueltoNoCuentaComoExcedente()
    {
        var error = Assert.Throws<ErrorDominio>(() => RepartoDeExcedente.Repartir(990m, [Efectivo(1000m, vuelto: 10m)]));

        Assert.Equal("pago_a_cuenta_sin_excedente", error.Codigo);
    }
}
