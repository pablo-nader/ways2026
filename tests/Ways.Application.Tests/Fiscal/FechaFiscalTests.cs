using Ways.Application.Fiscal;

namespace Ways.Application.Tests.Fiscal;

public class FechaFiscalTests
{
    private static readonly TimeZoneInfo Argentina = TimeZoneInfo.FindSystemTimeZoneById("America/Argentina/Buenos_Aires");

    [Fact]
    public void ElDiaDeNegocioEsElLocalAunqueElDiaUtcYaSeaElSiguiente()
    {
        var instante = new DateTimeOffset(2026, 3, 31, 23, 30, 0, TimeSpan.FromHours(-3));

        Assert.Equal(new DateOnly(2026, 3, 31), FechaFiscal.DeInstante(instante, Argentina));
        Assert.Equal(new DateOnly(2026, 4, 1), FechaFiscal.DeInstante(instante, TimeZoneInfo.Utc));
    }

    [Fact]
    public void UnInstanteUtcDeMadrugadaCaeEnElDiaLocalAnterior()
    {
        var instante = new DateTimeOffset(2026, 4, 1, 2, 59, 0, TimeSpan.Zero);

        Assert.Equal(new DateOnly(2026, 3, 31), FechaFiscal.DeInstante(instante, Argentina));
        Assert.Equal(new DateOnly(2026, 4, 1), FechaFiscal.DeInstante(instante.AddMinutes(1), Argentina));
    }
}
