using Ways.Application.Plantel;

namespace Ways.Application.Tests.Plantel;

public class ReglaDeAnulacionDePagoDePersonalTests
{
    [Fact]
    public void ElAjusteDevuelveElTotalDelPagoYElDescuentoVuelveALaDeuda()
    {
        var plan = ReglaDeAnulacionDePagoDePersonal.Planificar(-2500m, new DateOnly(2026, 11, 3), -500m);

        Assert.Equal(2500m, plan.ImporteDelAjuste);
        Assert.Equal(500m, plan.DeudaDevuelta);
        Assert.Equal("Anulación del pago del 03/11/2026", plan.Detalle);
    }

    [Fact]
    public void UnPagoSinDescuentoNoDevuelveDeuda()
    {
        var plan = ReglaDeAnulacionDePagoDePersonal.Planificar(-120.5m, new DateOnly(2026, 1, 9), null);

        Assert.Equal(120.5m, plan.ImporteDelAjuste);
        Assert.Null(plan.DeudaDevuelta);
        Assert.Equal("Anulación del pago del 09/01/2026", plan.Detalle);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(10.0)]
    public void UnAsientoDePagoQueNoEsNegativoEsUnDefectoDelLlamador(double importe)
    {
        Assert.Throws<InvalidOperationException>(
            () => ReglaDeAnulacionDePagoDePersonal.Planificar((decimal)importe, new DateOnly(2026, 1, 1), null));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(10.0)]
    public void UnDescuentoQueNoEsNegativoEsUnDefectoDelLlamador(double descuento)
    {
        Assert.Throws<InvalidOperationException>(
            () => ReglaDeAnulacionDePagoDePersonal.Planificar(-10m, new DateOnly(2026, 1, 1), (decimal)descuento));
    }
}
