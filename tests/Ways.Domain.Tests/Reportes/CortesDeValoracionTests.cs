using Ways.Domain.Common;
using Ways.Domain.Reportes;

namespace Ways.Domain.Tests.Reportes;

/// <summary>Instantes de corte de cada punto del reporte de valoración: fin de cada bucket en hora local, tope en el ahora y
/// límite de cantidad de puntos.</summary>
public class CortesDeValoracionTests
{
    private static readonly TimeZoneInfo ZonaArgentina = TimeZoneInfo.FindSystemTimeZoneById("America/Argentina/Buenos_Aires");
    private static readonly TimeSpan UnMicrosegundo = TimeSpan.FromTicks(10);

    private static DateTimeOffset Utc(int y, int m, int d, int h = 0, int min = 0) => new(y, m, d, h, min, 0, TimeSpan.Zero);

    [Fact]
    public void CadaPuntoCortaUnMicrosegundoAntesDeLaMedianocheLocalQueCierraSuBucket()
    {
        var rango = RangoDeReporte.Crear(new DateOnly(2026, 7, 1), new DateOnly(2026, 8, 31), Granularidad.Mes, ZonaArgentina);

        var cortes = CortesDeValoracion.Calcular(rango, ahora: Utc(2026, 11, 15));

        Assert.Equal(2, cortes.Count);
        // 2026-08-01 00:00 en Argentina (UTC-3) == 03:00 UTC.
        Assert.Equal(Utc(2026, 8, 1, 3) - UnMicrosegundo, cortes[0].Instante);
        Assert.Equal(Utc(2026, 9, 1, 3) - UnMicrosegundo, cortes[1].Instante);
        Assert.All(cortes, c => Assert.False(c.EsAhora));
    }

    [Fact]
    public void ElUltimoPuntoSeAcotaAlAhoraYSeMarcaComoActual()
    {
        var rango = RangoDeReporte.Crear(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30), Granularidad.Mes, ZonaArgentina);
        var ahora = Utc(2026, 11, 15, 12, 30);

        var cortes = CortesDeValoracion.Calcular(rango, ahora);

        var unico = Assert.Single(cortes);
        Assert.Equal(ahora, unico.Instante);
        Assert.True(unico.EsAhora);
    }

    [Fact]
    public void UnBucketQueTodaviaNoEmpezoNoGeneraPunto()
    {
        var rango = RangoDeReporte.Crear(new DateOnly(2026, 11, 10), new DateOnly(2026, 11, 20), Granularidad.Dia, ZonaArgentina);
        var ahora = Utc(2026, 11, 12, 15);

        var cortes = CortesDeValoracion.Calcular(rango, ahora);

        Assert.Equal(3, cortes.Count);
        Assert.Equal(ahora, cortes[^1].Instante);
        Assert.True(cortes[^1].EsAhora);
        Assert.False(cortes[0].EsAhora);
    }

    [Fact]
    public void UnaSemanaQueExcedeElHastaCortaEnElFinDelHasta()
    {
        // 2026-08-03 es lunes; el rango termina el miércoles 5, a mitad de la semana.
        var rango = RangoDeReporte.Crear(new DateOnly(2026, 8, 3), new DateOnly(2026, 8, 5), Granularidad.Semana, ZonaArgentina);

        var cortes = CortesDeValoracion.Calcular(rango, ahora: Utc(2026, 11, 15));

        var unico = Assert.Single(cortes);
        Assert.Equal(Utc(2026, 8, 6, 3) - UnMicrosegundo, unico.Instante);
    }

    [Fact]
    public void MasDeSesentaPuntosSeRechazaConUn400()
    {
        var rango = RangoDeReporte.Crear(new DateOnly(2026, 1, 1), new DateOnly(2026, 3, 15), Granularidad.Dia, ZonaArgentina);

        var error = Assert.Throws<ErrorDominio>(() => CortesDeValoracion.Calcular(rango, Utc(2026, 11, 15)));

        Assert.Equal(400, error.EstadoHttp);
        Assert.Equal("demasiados_puntos", error.Codigo);
    }

    [Fact]
    public void ExactamenteSesentaPuntosSeAcepta()
    {
        var rango = RangoDeReporte.Crear(new DateOnly(2026, 1, 1), new DateOnly(2026, 3, 1), Granularidad.Dia, ZonaArgentina);

        Assert.Equal(60, CortesDeValoracion.Calcular(rango, Utc(2026, 11, 15)).Count);
    }

    [Fact]
    public void UnRangoEnElFuturoSeRechazaConUn400()
    {
        var rango = RangoDeReporte.Crear(new DateOnly(2027, 1, 1), new DateOnly(2027, 1, 31), Granularidad.Mes, ZonaArgentina);

        var error = Assert.Throws<ErrorDominio>(() => CortesDeValoracion.Calcular(rango, Utc(2026, 11, 15)));

        Assert.Equal(400, error.EstadoHttp);
        Assert.Equal("rango_en_el_futuro", error.Codigo);
    }
}
