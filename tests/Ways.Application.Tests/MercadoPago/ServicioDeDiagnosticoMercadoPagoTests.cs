using Ways.Application.MercadoPago;
using Ways.Domain.Common;

namespace Ways.Application.Tests.MercadoPago;

public class ServicioDeDiagnosticoMercadoPagoTests
{
    [Theory]
    [InlineData(null, 24)]
    [InlineData(1, 1)]
    [InlineData(168, 168)]
    public void LasHorasValidasSeRespetanYSinValorSonVeinticuatro(int? entrada, int esperado) =>
        Assert.Equal(esperado, ServicioDeDiagnosticoMercadoPago.ValidarHoras(entrada));

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(169)]
    public void LasHorasFueraDeRangoSeRechazanConUn400SinAjustarlas(int horas)
    {
        var error = Assert.Throws<ErrorDominio>(() => ServicioDeDiagnosticoMercadoPago.ValidarHoras(horas));

        Assert.Equal((ServicioDeDiagnosticoMercadoPago.CodigoDeHorasInvalidas, 400), (error.Codigo, error.EstadoHttp));
    }
}
