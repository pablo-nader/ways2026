using Ways.Application.Exportacion;

namespace Ways.Application.Tests.Exportacion;

/// <summary>
/// <see cref="InstanteDeGeneracion.En"/> es el chokepoint que convierte <c>reloj.Ahora</c> (UTC)
/// a la zona horaria resuelta del punto de venta antes de guardarlo en
/// <c>ContextoDeExportacion.GeneradoEl</c> — el mismo instante que el encabezado del XLSX imprime
/// junto a la etiqueta de esa zona. Cada test nombra la cláusula puntual que prueba.
/// </summary>
public class InstanteDeGeneracionTests
{
    /// <summary>01:30 UTC del 6/8/2026 en <c>America/Argentina/Buenos_Aires</c> (UTC-3) es
    /// 2026-08-05 22:30 — día Y hora discriminan: si <see cref="InstanteDeGeneracion.En"/> no
    /// convirtiera, este assert compararía contra 2026-08-06 01:30 y fallaría.</summary>
    [Fact]
    public void ConvierteElInstanteALaHoraDeParedDeLaZonaResuelta()
    {
        var instanteUtc = new DateTimeOffset(2026, 8, 6, 1, 30, 0, TimeSpan.Zero);

        var resultado = InstanteDeGeneracion.En(instanteUtc, "America/Argentina/Buenos_Aires");

        Assert.Equal(new DateTime(2026, 8, 5, 22, 30, 0), resultado.DateTime);
    }

    [Fact]
    public void ElOffsetResultanteEsElDeLaZonaResuelta()
    {
        var instanteUtc = new DateTimeOffset(2026, 8, 6, 1, 30, 0, TimeSpan.Zero);

        var resultado = InstanteDeGeneracion.En(instanteUtc, "America/Argentina/Buenos_Aires");

        Assert.Equal(TimeSpan.FromHours(-3), resultado.Offset);
    }

    /// <summary>Regresión de <c>GET /api/reportes/compras/por-proveedor/export</c>: ese reporte no
    /// expone una zona de bucketeo propia y pasa el centinela <c>"N/A"</c>. El fallback tiene que
    /// devolver el instante intacto sin lanzar, porque la etiqueta de al lado del encabezado no
    /// afirma ninguna zona.</summary>
    [Fact]
    public void UnaZonaNoResolubleDevuelveElInstanteIntactoSinLanzar()
    {
        var instante = new DateTimeOffset(2026, 8, 6, 1, 30, 0, TimeSpan.Zero);

        var resultado = InstanteDeGeneracion.En(instante, "N/A");

        // DateTimeOffset.Equals compara solo el instante: el offset se asserta aparte para que un
        // corrimiento de zona no pase desapercibido.
        Assert.Equal(instante, resultado);
        Assert.Equal(TimeSpan.Zero, resultado.Offset);
    }
}
