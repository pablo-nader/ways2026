using Ways.Domain.Common;
using Ways.Domain.Reportes;

namespace Ways.Domain.Tests.Reportes;

/// <summary>
/// stage-10-agregacion-dashboard, Slice 2 (task 2.9, design: Timezone Mechanics / Range
/// resolution; spec reportes-de-gestion: Business-Day Bucketing Resolved Through The Punto De
/// Venta's Timezone) — pura, sin base de datos, mismo criterio que <c>PoliticaDeRoles</c>.
/// </summary>
public class RangoDeReporteTests
{
    private static readonly TimeZoneInfo ZonaArgentina = TimeZoneInfo.FindSystemTimeZoneById("America/Argentina/Buenos_Aires");
    private static readonly TimeZoneInfo ZonaUtc = TimeZoneInfo.Utc;

    // ---- 22:30 ART, "A late-evening sale lands on its own business day" -----------------------

    [Fact]
    public void UnaVentaDeLas2230EnArtQuedaDentroDelRangoDelMismoDiaLocal()
    {
        // 2026-08-05T22:30:00-03:00 == 2026-08-06T01:30:00Z.
        var instante = new DateTimeOffset(2026, 8, 6, 1, 30, 0, TimeSpan.Zero);
        var rango = RangoDeReporte.Crear(new DateOnly(2026, 8, 5), new DateOnly(2026, 8, 5), Granularidad.Dia, ZonaArgentina);

        Assert.True(instante >= rango.DesdeUtc);
        Assert.True(instante < rango.HastaUtcExclusivo);
    }

    [Fact]
    public void LaMismaVentaBajoUnaZonaUtcQuedaFueraDelRangoDelDia5DemostrandoQueElParametroEstaVivo()
    {
        var instante = new DateTimeOffset(2026, 8, 6, 1, 30, 0, TimeSpan.Zero);
        var rango = RangoDeReporte.Crear(new DateOnly(2026, 8, 5), new DateOnly(2026, 8, 5), Granularidad.Dia, ZonaUtc);

        Assert.False(instante < rango.HastaUtcExclusivo);
    }

    // ---- hasta inclusivity ----------------------------------------------------------------------

    [Fact]
    public void HastaEsInclusivoElLimiteSuperiorEsLaMedianocheLocalDelDiaSiguiente()
    {
        var rango = RangoDeReporte.Crear(new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 5), Granularidad.Dia, ZonaArgentina);

        var medianocheLocalDelSeis = new DateTimeOffset(2026, 8, 6, 0, 0, 0, TimeSpan.FromHours(-3));
        Assert.Equal(medianocheLocalDelSeis, rango.HastaUtcExclusivo);
    }

    // ---- ISO Monday-start + year rollover ---------------------------------------------------------

    [Fact]
    public void LosBucketsSemanalesArrancanElLunes()
    {
        var rango = RangoDeReporte.Crear(new DateOnly(2026, 8, 9), new DateOnly(2026, 8, 10), Granularidad.Semana, ZonaArgentina);

        var buckets = rango.Buckets();

        // 2026-08-09 es domingo, 2026-08-10 es lunes — dos semanas ISO distintas.
        Assert.Equal(2, buckets.Count);
        Assert.Equal(new DateOnly(2026, 8, 3), buckets[0].Inicio);
        Assert.Equal(new DateOnly(2026, 8, 10), buckets[1].Inicio);
    }

    [Fact]
    public void LaEtiquetaIsoHaceElRolloverDeAnioEnLaSemana1()
    {
        // 2026-01-01 es jueves; su semana ISO pertenece a 2026-W01 (empieza el lunes 2025-12-29).
        var rango = RangoDeReporte.Crear(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 1), Granularidad.Semana, ZonaArgentina);

        var bucket = Assert.Single(rango.Buckets());

        Assert.Equal("2026-W01", bucket.Etiqueta);
        Assert.Equal(new DateOnly(2025, 12, 29), bucket.Inicio);
    }

    // ---- month boundaries -------------------------------------------------------------------------

    [Fact]
    public void LosBucketsMensualesArrancanElPrimeroDeCadaMes()
    {
        var rango = RangoDeReporte.Crear(new DateOnly(2026, 1, 15), new DateOnly(2026, 3, 3), Granularidad.Mes, ZonaArgentina);

        var buckets = rango.Buckets();

        Assert.Equal(3, buckets.Count);
        Assert.Equal(new DateOnly(2026, 1, 1), buckets[0].Inicio);
        Assert.Equal("2026-01", buckets[0].Etiqueta);
        Assert.Equal(new DateOnly(2026, 2, 1), buckets[1].Inicio);
        Assert.Equal(new DateOnly(2026, 3, 1), buckets[2].Inicio);
    }

    // ---- gap fill: cada bucket del rango existe, tenga o no filas SQL -----------------------------

    [Fact]
    public void BucketsDevuelveUnaEntradaPorCadaDiaDelRangoSinImportarSiTuvoVentas()
    {
        var rango = RangoDeReporte.Crear(new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 5), Granularidad.Dia, ZonaArgentina);

        var buckets = rango.Buckets();

        Assert.Equal(5, buckets.Count);
        Assert.Equal(new DateOnly(2026, 8, 1), buckets[0].Inicio);
        Assert.Equal(new DateOnly(2026, 8, 5), buckets[^1].Inicio);
    }

    // ---- invalid range + 366-day guard --------------------------------------------------------------

    [Fact]
    public void HastaAnteriorADesdeSeRechaza()
    {
        var excepcion = Assert.Throws<ErrorDominio>(() =>
            RangoDeReporte.Crear(new DateOnly(2026, 8, 5), new DateOnly(2026, 8, 1), Granularidad.Dia, ZonaArgentina));

        Assert.Equal("rango_invalido", excepcion.Codigo);
        Assert.Equal(400, excepcion.EstadoHttp);
    }

    [Fact]
    public void UnRangoDeExactamente366DiasEsValido()
    {
        var desde = new DateOnly(2026, 1, 1);
        var hasta = desde.AddDays(366);

        var rango = RangoDeReporte.Crear(desde, hasta, Granularidad.Dia, ZonaArgentina);

        Assert.Equal(hasta, rango.Hasta);
    }

    [Fact]
    public void UnRangoDeMasDe366DiasSeRechaza()
    {
        var desde = new DateOnly(2026, 1, 1);
        var hasta = desde.AddDays(367);

        var excepcion = Assert.Throws<ErrorDominio>(() => RangoDeReporte.Crear(desde, hasta, Granularidad.Dia, ZonaArgentina));

        Assert.Equal("rango_demasiado_amplio", excepcion.Codigo);
    }
}
