using Ways.Domain.Common;
using Ways.Domain.Reportes;

namespace Ways.Domain.Tests.Reportes;

/// <summary>
/// Períodos con nombre del conector MCP: puros, sin base de datos, mismo criterio que
/// <see cref="RangoDeReporteTests"/>. 2026-10-14 es miércoles, 2026-10-12 lunes y 2026-10-18 domingo.
/// </summary>
public class PeriodoDeConsultaTests
{
    private static readonly TimeZoneInfo ZonaArgentina = TimeZoneInfo.FindSystemTimeZoneById("America/Argentina/Buenos_Aires");

    private static DateOnly D(int anio, int mes, int dia) => new(anio, mes, dia);

    [Theory]
    // miércoles a mitad de semana y de mes
    [InlineData("hoy", 2026, 10, 14, 2026, 10, 14, 2026, 10, 14)]
    [InlineData("ayer", 2026, 10, 14, 2026, 10, 13, 2026, 10, 13)]
    [InlineData("esta_semana", 2026, 10, 14, 2026, 10, 12, 2026, 10, 14)]
    [InlineData("semana_pasada", 2026, 10, 14, 2026, 10, 5, 2026, 10, 11)]
    [InlineData("este_mes", 2026, 10, 14, 2026, 10, 1, 2026, 10, 14)]
    [InlineData("mes_pasado", 2026, 10, 14, 2026, 9, 1, 2026, 9, 30)]
    // lunes: la semana en curso es solo el lunes
    [InlineData("esta_semana", 2026, 10, 12, 2026, 10, 12, 2026, 10, 12)]
    [InlineData("semana_pasada", 2026, 10, 12, 2026, 10, 5, 2026, 10, 11)]
    // domingo: todavía pertenece a la semana que empezó el lunes anterior
    [InlineData("esta_semana", 2026, 10, 18, 2026, 10, 12, 2026, 10, 18)]
    [InlineData("semana_pasada", 2026, 10, 18, 2026, 10, 5, 2026, 10, 11)]
    // primer día del mes: la semana cruza al mes anterior, "este mes" es un solo día
    [InlineData("esta_semana", 2026, 10, 1, 2026, 9, 28, 2026, 10, 1)]
    [InlineData("este_mes", 2026, 10, 1, 2026, 10, 1, 2026, 10, 1)]
    [InlineData("mes_pasado", 2026, 10, 1, 2026, 9, 1, 2026, 9, 30)]
    [InlineData("ayer", 2026, 10, 1, 2026, 9, 30, 2026, 9, 30)]
    // último día del mes
    [InlineData("este_mes", 2026, 10, 31, 2026, 10, 1, 2026, 10, 31)]
    [InlineData("mes_pasado", 2026, 10, 31, 2026, 9, 1, 2026, 9, 30)]
    // enero: el mes pasado cruza de año
    [InlineData("mes_pasado", 2026, 1, 15, 2025, 12, 1, 2025, 12, 31)]
    [InlineData("ayer", 2026, 1, 1, 2025, 12, 31, 2025, 12, 31)]
    // marzo: el mes pasado es febrero (no bisiesto)
    [InlineData("mes_pasado", 2026, 3, 1, 2026, 2, 1, 2026, 2, 28)]
    public void CadaPeriodoConNombreResuelveSuRango(
        string nombre, int anioHoy, int mesHoy, int diaHoy,
        int anioDesde, int mesDesde, int diaDesde, int anioHasta, int mesHasta, int diaHasta)
    {
        var (desde, hasta) = PeriodoDeConsulta.Resolver(nombre, null, null, D(anioHoy, mesHoy, diaHoy));

        Assert.Equal(D(anioDesde, mesDesde, diaDesde), desde);
        Assert.Equal(D(anioHasta, mesHasta, diaHasta), hasta);
    }

    [Fact]
    public void ElNombreSeAceptaConEspaciosYMayusculas()
    {
        var (desde, hasta) = PeriodoDeConsulta.Resolver("  Esta_Semana ", null, null, D(2026, 10, 14));

        Assert.Equal(D(2026, 10, 12), desde);
        Assert.Equal(D(2026, 10, 14), hasta);
    }

    [Fact]
    public void UnNombreDesconocidoSeRechazaListandoLosValidos()
    {
        var error = Assert.Throws<ErrorDominio>(() => PeriodoDeConsulta.Resolver("anteayer", null, null, D(2026, 10, 14)));

        Assert.Equal("periodo_desconocido", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
        foreach (var nombre in PeriodoDeConsulta.Nombres)
        {
            Assert.Contains(nombre, error.Message);
        }
    }

    [Fact]
    public void LasFechasExplicitasSeLeenInclusivasTalCualLlegan()
    {
        var (desde, hasta) = PeriodoDeConsulta.Resolver(null, "2026-09-30", "2026-10-02", D(2026, 10, 14));

        Assert.Equal(D(2026, 9, 30), desde);
        Assert.Equal(D(2026, 10, 2), hasta);
    }

    [Theory]
    [InlineData("hoy", "2026-10-01", "2026-10-02")]
    [InlineData("hoy", "2026-10-01", null)]
    [InlineData("hoy", null, "2026-10-02")]
    [InlineData(null, "2026-10-01", null)]
    [InlineData(null, null, "2026-10-02")]
    [InlineData(null, null, null)]
    [InlineData("  ", " ", "")]
    public void CualquierCombinacionQueNoSeaPeriodoOParDeFechasEsInvalida(string? periodo, string? desde, string? hasta)
    {
        var error = Assert.Throws<ErrorDominio>(() => PeriodoDeConsulta.Resolver(periodo, desde, hasta, D(2026, 10, 14)));

        Assert.Equal("periodo_invalido", error.Codigo);
        Assert.Equal(400, error.EstadoHttp);
    }

    [Theory]
    [InlineData("2026-13-01", "2026-12-31")]
    [InlineData("2026-10-01", "ayer")]
    [InlineData("01/10/2026", "2026-10-02")]
    public void UnaFechaQueNoEsYyyyMmDdEsInvalida(string desde, string hasta)
    {
        var error = Assert.Throws<ErrorDominio>(() => PeriodoDeConsulta.Resolver(null, desde, hasta, D(2026, 10, 14)));

        Assert.Equal("periodo_invalido", error.Codigo);
    }

    [Fact]
    public void ElHoyLocalCruzaLaMedianocheUtcHaciaAtras()
    {
        // 2026-10-18T02:00Z son las 23:00 del 17 en Buenos Aires.
        var ahora = new DateTimeOffset(2026, 10, 18, 2, 0, 0, TimeSpan.Zero);

        Assert.Equal(D(2026, 10, 17), PeriodoDeConsulta.HoyLocal(ahora, ZonaArgentina));
        Assert.Equal(D(2026, 10, 18), PeriodoDeConsulta.HoyLocal(ahora, TimeZoneInfo.Utc));
    }

    [Fact]
    public void ElHoyLocalEnLaMedianocheLocalYaEsElDiaSiguiente()
    {
        // 2026-10-18T03:00Z son las 00:00 del 18 en Buenos Aires.
        var ahora = new DateTimeOffset(2026, 10, 18, 3, 0, 0, TimeSpan.Zero);

        Assert.Equal(D(2026, 10, 18), PeriodoDeConsulta.HoyLocal(ahora, ZonaArgentina));
    }

    [Fact]
    public void ElHoyLocalIgnoraElOffsetConElQueLlegaElInstante()
    {
        var ahora = new DateTimeOffset(2026, 10, 17, 23, 0, 0, TimeSpan.FromHours(-3));

        Assert.Equal(D(2026, 10, 17), PeriodoDeConsulta.HoyLocal(ahora, ZonaArgentina));
    }
}
