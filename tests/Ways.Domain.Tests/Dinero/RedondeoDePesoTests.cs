using Ways.Domain.Dinero;

namespace Ways.Domain.Tests.Dinero;

public class RedondeoDePesoTests
{
    [Theory]
    [InlineData(3499.98, 3500)]
    [InlineData(3500.49, 3500)]
    [InlineData(3500.50, 3501)]
    [InlineData(0.50, 1)]
    [InlineData(0.49, 0)]
    [InlineData(2.50, 3)]
    [InlineData(-0.50, -1)]
    [InlineData(-2.50, -3)]
    [InlineData(-3499.98, -3500)]
    public void RedondeaAlPesoEnteroConElMedioAlejandoseDeCero(double entrada, double esperado)
    {
        Assert.Equal((decimal)esperado, RedondeoDePeso.APesoEntero((decimal)entrada));
    }
}
