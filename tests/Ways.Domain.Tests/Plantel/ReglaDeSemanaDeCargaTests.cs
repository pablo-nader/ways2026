using Ways.Domain.Common;
using Ways.Domain.Plantel;

namespace Ways.Domain.Tests.Plantel;

public class ReglaDeSemanaDeCargaTests
{
    [Theory]
    [InlineData(2026, 10, 26, 0)]
    [InlineData(2026, 10, 27, 1)]
    [InlineData(2026, 10, 31, 5)]
    [InlineData(2026, 11, 1, 6)]
    public void ElIndiceIsoVaDeLunesACeroADomingoASeis(int anio, int mes, int dia, int esperado)
    {
        Assert.Equal(esperado, ReglaDeSemanaDeCarga.IndiceIso(new DateOnly(anio, mes, dia)));
    }

    [Fact]
    public void ElLunesDeUnDomingoEsElDeLaSemanaQueTermina()
    {
        Assert.Equal(new DateOnly(2026, 10, 26), ReglaDeSemanaDeCarga.LunesDe(new DateOnly(2026, 11, 1)));
        Assert.Equal(new DateOnly(2026, 10, 26), ReglaDeSemanaDeCarga.LunesDe(new DateOnly(2026, 10, 26)));
    }

    [Fact]
    public void UnLunesSeAceptaComoInicioDeSemana()
    {
        ReglaDeSemanaDeCarga.ExigirLunes(new DateOnly(2026, 10, 26));
    }

    [Fact]
    public void OtroDiaSeRechazaComoSemanaInvalida()
    {
        var error = Assert.Throws<ErrorDominio>(() => ReglaDeSemanaDeCarga.ExigirLunes(new DateOnly(2026, 10, 27)));

        Assert.Equal("semana_invalida", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8.5)]
    [InlineData(24)]
    [InlineData(0.01)]
    public void LasHorasDelDiaEntreCeroYVeinticuatroSeAceptan(double horas)
    {
        ReglaDeSemanaDeCarga.ValidarHorasDelDia((decimal)horas);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(24.01)]
    [InlineData(1.005)]
    public void LasHorasFueraDeRangoOConTresDecimalesSeRechazan(double horas)
    {
        var error = Assert.Throws<ErrorDominio>(() => ReglaDeSemanaDeCarga.ValidarHorasDelDia((decimal)horas));

        Assert.Equal("horas_invalidas", error.Codigo);
    }
}
