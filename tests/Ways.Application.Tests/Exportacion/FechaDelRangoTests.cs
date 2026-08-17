using Ways.Application.Exportacion;

namespace Ways.Application.Tests.Exportacion;

/// <summary>
/// Los límites que manda el cliente (<c>fechaIsoConOffset</c> en <c>reportes.ts</c>/
/// <c>auditoria.ts</c>) traen el día elegido en el picker expresado en su propio offset local, así
/// que la fecha mostrada tiene que salir de ESE reloj y no del instante llevado a UTC. Los tres
/// signos de offset se cubren acá porque el bug es asimétrico: con offset negativo el día se
/// adelanta, con positivo se atrasa, y con <c>Z</c> no pasa nada — que es exactamente por qué
/// ningún test de integración (todos mandaban <c>...T23:59:59Z</c>) lo descubrió.
/// </summary>
public class FechaDelRangoTests
{
    [Fact]
    public void ElFinDeDiaConOffsetNegativoNoSeVaAlDiaSiguiente()
    {
        var hasta = new DateTimeOffset(2026, 8, 16, 23, 59, 59, 999, TimeSpan.FromHours(-3));

        Assert.Equal(new DateOnly(2026, 8, 16), FechaDelRango.De(hasta));
        Assert.Equal(new DateOnly(2026, 8, 17), DateOnly.FromDateTime(hasta.UtcDateTime));
    }

    [Fact]
    public void ElInicioDeDiaConOffsetPositivoNoSeVaAlDiaAnterior()
    {
        var desde = new DateTimeOffset(2026, 8, 16, 0, 0, 0, TimeSpan.FromMinutes(330));

        Assert.Equal(new DateOnly(2026, 8, 16), FechaDelRango.De(desde));
        Assert.Equal(new DateOnly(2026, 8, 15), DateOnly.FromDateTime(desde.UtcDateTime));
    }

    [Fact]
    public void ConOffsetCeroLaFechaEsLaMisma()
    {
        var hasta = new DateTimeOffset(2026, 8, 16, 23, 59, 59, TimeSpan.Zero);

        Assert.Equal(new DateOnly(2026, 8, 16), FechaDelRango.De(hasta));
    }

    [Fact]
    public void ElParRespetaElOffsetDeCadaExtremo()
    {
        var (desde, hasta) = FechaDelRango.De(
            new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.FromHours(-3)),
            new DateTimeOffset(2026, 8, 16, 23, 59, 59, 999, TimeSpan.FromHours(-3)));

        Assert.Equal(new DateOnly(2026, 8, 1), desde);
        Assert.Equal(new DateOnly(2026, 8, 16), hasta);
    }
}
