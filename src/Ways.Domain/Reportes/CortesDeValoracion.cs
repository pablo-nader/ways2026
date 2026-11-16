using Ways.Domain.Common;

namespace Ways.Domain.Reportes;

/// <summary>Un punto del reporte de valoración: el instante al que se valora todo y si es el momento actual.</summary>
public sealed record CorteDeValoracion(DateTimeOffset Instante, bool EsAhora);

/// <summary>
/// Instantes de corte del reporte de valoración. Cada bucket corta un microsegundo antes de la medianoche local que lo cierra (la
/// resolución de <c>timestamptz</c>), de modo que un movimiento ocurrido justo a medianoche cae en el bucket siguiente aunque los
/// cortes se comparen con <c>&lt;=</c>. El corte de un bucket no pasa del fin del día <c>hasta</c> ni del ahora: el último punto,
/// si el rango llega a hoy, es el momento actual. Un bucket que todavía no empezó no genera punto.
/// </summary>
public static class CortesDeValoracion
{
    public const int MaximoDePuntos = 60;

    private const long TicksPorMicrosegundo = 10;

    public static IReadOnlyList<CorteDeValoracion> Calcular(RangoDeReporte rango, DateTimeOffset ahora)
    {
        var buckets = rango.Buckets();
        if (buckets.Count > MaximoDePuntos)
        {
            throw new ErrorDominio(
                "demasiados_puntos",
                $"El rango pide {buckets.Count} puntos y el máximo es {MaximoDePuntos}: usá una granularidad más gruesa o un rango más corto.",
                400);
        }

        var ahoraUtc = ATickDeMicrosegundo(ahora.ToUniversalTime());
        var finDelRango = rango.HastaUtcExclusivo;
        var cortes = new List<CorteDeValoracion>();

        foreach (var (inicio, _) in buckets)
        {
            if (rango.MedianocheLocal(inicio) > ahoraUtc)
            {
                break;
            }

            var fin = rango.MedianocheLocal(SiguienteInicio(inicio, rango.Granularidad));
            if (fin > finDelRango)
            {
                fin = finDelRango;
            }

            var instante = fin - TimeSpan.FromTicks(TicksPorMicrosegundo);
            var esAhora = instante >= ahoraUtc;
            cortes.Add(new CorteDeValoracion(esAhora ? ahoraUtc : instante, esAhora));
        }

        if (cortes.Count == 0)
        {
            throw new ErrorDominio("rango_en_el_futuro", "El rango pedido empieza después de ahora.", 400);
        }

        return cortes;
    }

    private static DateOnly SiguienteInicio(DateOnly inicio, Granularidad granularidad) => granularidad switch
    {
        Granularidad.Dia => inicio.AddDays(1),
        Granularidad.Semana => inicio.AddDays(7),
        Granularidad.Mes => inicio.AddMonths(1),
        _ => throw new ArgumentOutOfRangeException(nameof(granularidad))
    };

    private static DateTimeOffset ATickDeMicrosegundo(DateTimeOffset instante) =>
        instante.AddTicks(-(instante.Ticks % TicksPorMicrosegundo));
}
