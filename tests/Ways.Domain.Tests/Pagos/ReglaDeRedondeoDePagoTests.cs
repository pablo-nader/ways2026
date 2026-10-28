using Ways.Domain.Pagos;

namespace Ways.Domain.Tests.Pagos;

public class ReglaDeRedondeoDePagoTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(10, true)]
    [InlineData(500, true)]
    [InlineData(0.01, true)]
    [InlineData(999999999999.99, true)]
    [InlineData(-0.01, false)]
    [InlineData(1.005, false)]
    [InlineData(1000000000000, false)]
    public void LaToleranciaEsValidaDeCeroAlTopeConDosDecimales(double tolerancia, bool esperado) =>
        Assert.Equal(esperado, ReglaDeRedondeoDePago.ToleranciaEsValida((decimal)tolerancia));

    [Theory]
    [InlineData(3.5, 500, true)]
    [InlineData(500, 500, true)]
    [InlineData(500.01, 500, false)]
    [InlineData(0, 500, false)]
    [InlineData(0.01, 0, false)]
    [InlineData(69.75, 10, false)]
    public void LaDiferenciaSeRedondeaSoloSiEsPositivaYNoSuperaLaTolerancia(double diferencia, double tolerancia, bool esperado) =>
        Assert.Equal(esperado, ReglaDeRedondeoDePago.EstaEnTolerancia((decimal)diferencia, (decimal)tolerancia));
}
