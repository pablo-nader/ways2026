using Ways.Domain.Common;
using Ways.Domain.Ventas;

namespace Ways.Domain.Tests.Ventas;

/// <summary>
/// <see cref="ReglaDeAjusteManual.Validar"/> — pura, sin base de datos. Los casos incluyen el borde
/// exacto de cada cláusula (cero, mínimo, máximo, decimales), válido e inválido, para que borrar
/// cualquiera de ellas rompa al menos un caso propio.
/// </summary>
public class ReglaDeAjusteManualTests
{
    public static TheoryData<decimal> PorcentajesValidos => new()
    {
        0.01m, -0.01m, 100m, -100m, 50.5m, -99.99m, 99.99m, 1.25m, 10.00m
    };

    public static TheoryData<decimal> PorcentajesInvalidos => new()
    {
        0m, 0.00m, 100.01m, -100.01m, 101m, -101m, 0.001m, -0.001m, 1.005m, 33.333m, -12.345m
    };

    [Fact]
    public void SinPorcentajeEsValido()
    {
        ReglaDeAjusteManual.Validar(null);
    }

    [Theory]
    [MemberData(nameof(PorcentajesValidos))]
    public void UnPorcentajeNoNuloDentroDelRangoYConHastaDosDecimalesEsValido(decimal porcentaje)
    {
        ReglaDeAjusteManual.Validar(porcentaje);
    }

    [Theory]
    [MemberData(nameof(PorcentajesInvalidos))]
    public void UnPorcentajeCeroFueraDeRangoOConMasDeDosDecimalesSeRechazaCon400(decimal porcentaje)
    {
        var error = Assert.Throws<ErrorDominio>(() => ReglaDeAjusteManual.Validar(porcentaje));

        Assert.Equal("ajuste_manual_invalido", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }
}
