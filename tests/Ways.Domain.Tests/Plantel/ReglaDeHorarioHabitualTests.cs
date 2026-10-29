using Ways.Domain.Common;
using Ways.Domain.Plantel;

namespace Ways.Domain.Tests.Plantel;

public class ReglaDeHorarioHabitualTests
{
    private static decimal[] Semana(params decimal[] dias) => dias;

    [Fact]
    public void SinHorarioEsValido()
    {
        ReglaDeHorarioHabitual.Validar(null);
    }

    [Fact]
    public void SieteValoresEntreCeroYVeinticuatroSeAceptan()
    {
        ReglaDeHorarioHabitual.Validar(Semana(8, 8, 8.5m, 8, 7.25m, 0, 24));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(8)]
    public void OtraLongitudSeRechaza(int largo)
    {
        var error = Assert.Throws<ErrorDominio>(() => ReglaDeHorarioHabitual.Validar(new decimal[largo]));

        Assert.Equal("horario_habitual_invalido", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(-1)]
    [InlineData(24.01)]
    [InlineData(25)]
    [InlineData(8.001)]
    public void UnValorFueraDeRangoOConMasDeDosDecimalesSeRechaza(double valor)
    {
        var dias = Semana(8, 8, 8, 8, 8, 0, 0);
        dias[3] = (decimal)valor;

        var error = Assert.Throws<ErrorDominio>(() => ReglaDeHorarioHabitual.Validar(dias));

        Assert.Equal("horario_habitual_invalido", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }
}
