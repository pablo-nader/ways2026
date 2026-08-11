using System.Globalization;
using Ways.Domain.Common;

namespace Ways.Domain.Reportes;

/// <summary>
/// Rango de fechas + granularidad + zona horaria de un reporte de gestión (stage-10), puro y
/// DB-free como <c>PoliticaDeRoles</c> (design: Timezone Mechanics — "Range resolution"). Resuelve
/// los límites UTC del rango sobre la zona del punto de venta y la lista completa de buckets del
/// período — incluidos los que no tienen ninguna fila en <c>LectorDeSerieTemporal</c> — para que
/// el servicio de aplicación pueda left-joinearlos en C# (design decisión 4: un día sin ventas
/// tiene que renderizar <c>0</c>, no desaparecer del gráfico).
/// </summary>
public sealed class RangoDeReporte
{
    private const int SpanMaximoEnDias = 366;

    public DateOnly Desde { get; }
    public DateOnly Hasta { get; }
    public Granularidad Granularidad { get; }
    public TimeZoneInfo Zona { get; }

    private RangoDeReporte(DateOnly desde, DateOnly hasta, Granularidad granularidad, TimeZoneInfo zona)
    {
        Desde = desde;
        Hasta = hasta;
        Granularidad = granularidad;
        Zona = zona;
    }

    /// <summary>Rechaza <c>hasta &lt; desde</c> y un span mayor a <see cref="SpanMaximoEnDias"/>
    /// días (agregado acotado ⇒ sin paginación, design: Range resolution).</summary>
    public static RangoDeReporte Crear(DateOnly desde, DateOnly hasta, Granularidad granularidad, TimeZoneInfo zona)
    {
        if (hasta < desde)
        {
            throw new ErrorDominio(
                "rango_invalido", "'hasta' no puede ser anterior a 'desde'.", 400);
        }

        if (hasta.DayNumber - desde.DayNumber > SpanMaximoEnDias)
        {
            throw new ErrorDominio(
                "rango_demasiado_amplio", $"El rango no puede superar {SpanMaximoEnDias} días.", 400);
        }

        return new RangoDeReporte(desde, hasta, granularidad, zona);
    }

    /// <summary>Instante UTC de la medianoche local de <see cref="Desde"/> en <see cref="Zona"/>
    /// — el límite inferior INCLUSIVO que <c>LectorDeSerieTemporal</c> liga a <c>fecha &gt;=</c>.</summary>
    public DateTimeOffset DesdeUtc => InstanteLocal(Desde);

    /// <summary>Límite superior EXCLUSIVO: medianoche local del día siguiente a
    /// <see cref="Hasta"/> — <see cref="Hasta"/> es inclusivo en el rango de fechas del reporte.</summary>
    public DateTimeOffset HastaUtcExclusivo => InstanteLocal(Hasta.AddDays(1));

    private DateTimeOffset InstanteLocal(DateOnly fecha)
    {
        var local = fecha.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        var offset = Zona.GetUtcOffset(local);

        // Npgsql solo acepta offset 0 para "timestamp with time zone" — el resultado sigue
        // siendo el MISMO instante, normalizado a UTC (design: Timezone Mechanics).
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    /// <summary>Todos los buckets del período, tengan o no filas en el resultado SQL — el punto
    /// de entrada del left-join en C# (design decisión 4).</summary>
    public IReadOnlyList<(DateOnly Inicio, string Etiqueta)> Buckets() => Granularidad switch
    {
        Granularidad.Dia => BucketsPorDia(),
        Granularidad.Semana => BucketsPorSemana(),
        Granularidad.Mes => BucketsPorMes(),
        _ => throw new ArgumentOutOfRangeException(nameof(Granularidad))
    };

    private List<(DateOnly, string)> BucketsPorDia()
    {
        var buckets = new List<(DateOnly, string)>();
        for (var fecha = Desde; fecha <= Hasta; fecha = fecha.AddDays(1))
        {
            buckets.Add((fecha, fecha.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        }

        return buckets;
    }

    /// <summary>Postgres <c>date_trunc('week', …)</c> es Monday-start por defecto — la etiqueta
    /// ISO (<c>2026-W33</c>) sale de <see cref="ISOWeek"/> sobre el propio lunes del bucket, nunca
    /// de <c>to_char</c> (design: Timezone Mechanics, "ISO week").</summary>
    private List<(DateOnly, string)> BucketsPorSemana()
    {
        var buckets = new List<(DateOnly, string)>();
        var lunes = LunesDeLaSemanaDe(Desde);
        var ultimoLunes = LunesDeLaSemanaDe(Hasta);

        while (lunes <= ultimoLunes)
        {
            var fechaDeReferencia = lunes.ToDateTime(TimeOnly.MinValue);
            var etiqueta = $"{ISOWeek.GetYear(fechaDeReferencia)}-W{ISOWeek.GetWeekOfYear(fechaDeReferencia):D2}";
            buckets.Add((lunes, etiqueta));
            lunes = lunes.AddDays(7);
        }

        return buckets;
    }

    private List<(DateOnly, string)> BucketsPorMes()
    {
        var buckets = new List<(DateOnly, string)>();
        var mes = new DateOnly(Desde.Year, Desde.Month, 1);
        var ultimoMes = new DateOnly(Hasta.Year, Hasta.Month, 1);

        while (mes <= ultimoMes)
        {
            buckets.Add((mes, mes.ToString("yyyy-MM", CultureInfo.InvariantCulture)));
            mes = mes.AddMonths(1);
        }

        return buckets;
    }

    private static DateOnly LunesDeLaSemanaDe(DateOnly fecha)
    {
        var diferencia = ((int)fecha.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
        return fecha.AddDays(-diferencia);
    }
}
