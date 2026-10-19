using System.Globalization;
using Ways.Domain.Common;

namespace Ways.Domain.Reportes;

/// <summary>
/// Período de una consulta de ventas del conector MCP: un nombre (<c>hoy</c>, <c>esta_semana</c>…)
/// o un par de fechas explícitas. Puro y sin base de datos: el "hoy" local lo calcula quien llama
/// con <see cref="HoyLocal"/>, y la validación del rango (orden, tope de 366 días) queda en
/// <see cref="RangoDeReporte"/>. La semana va de lunes a domingo, igual que <c>date_trunc('week')</c>.
/// </summary>
public static class PeriodoDeConsulta
{
    public const string Hoy = "hoy";
    public const string Ayer = "ayer";
    public const string EstaSemana = "esta_semana";
    public const string SemanaPasada = "semana_pasada";
    public const string EsteMes = "este_mes";
    public const string MesPasado = "mes_pasado";

    public static IReadOnlyList<string> Nombres { get; } = [Hoy, Ayer, EstaSemana, SemanaPasada, EsteMes, MesPasado];

    /// <summary>La fecha de calendario que marca el reloj en <paramref name="zona"/>, no la fecha UTC.</summary>
    public static DateOnly HoyLocal(DateTimeOffset ahora, TimeZoneInfo zona) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(ahora, zona).DateTime);

    /// <summary>Resuelve el período pedido: o <paramref name="periodo"/>, o <paramref name="desde"/> y
    /// <paramref name="hasta"/> juntos (<c>yyyy-MM-dd</c>, ambos inclusivos). Cualquier otra
    /// combinación es <c>periodo_invalido</c>; un nombre que no existe es <c>periodo_desconocido</c>.</summary>
    public static (DateOnly Desde, DateOnly Hasta) Resolver(string? periodo, string? desde, string? hasta, DateOnly hoy)
    {
        var hayPeriodo = !string.IsNullOrWhiteSpace(periodo);
        var hayDesde = !string.IsNullOrWhiteSpace(desde);
        var hayHasta = !string.IsNullOrWhiteSpace(hasta);

        if (hayPeriodo && (hayDesde || hayHasta))
        {
            throw PeriodoInvalido("Indicá 'periodo' o 'desde' y 'hasta', no ambos.");
        }

        if (hayPeriodo)
        {
            return ResolverNombre(periodo!, hoy);
        }

        if (hayDesde != hayHasta)
        {
            throw PeriodoInvalido("'desde' y 'hasta' van juntos: falta uno de los dos.");
        }

        if (!hayDesde)
        {
            throw PeriodoInvalido($"Indicá 'periodo' ({string.Join(", ", Nombres)}) o 'desde' y 'hasta' (yyyy-MM-dd).");
        }

        return (LeerFecha(desde!, "desde"), LeerFecha(hasta!, "hasta"));
    }

    public static (DateOnly Desde, DateOnly Hasta) ResolverNombre(string nombre, DateOnly hoy)
    {
        switch (nombre.Trim().ToLowerInvariant())
        {
            case Hoy:
                return (hoy, hoy);
            case Ayer:
                return (hoy.AddDays(-1), hoy.AddDays(-1));
            case EstaSemana:
                return (LunesDeLaSemanaDe(hoy), hoy);
            case SemanaPasada:
                var lunesPasado = LunesDeLaSemanaDe(hoy).AddDays(-7);
                return (lunesPasado, lunesPasado.AddDays(6));
            case EsteMes:
                return (new DateOnly(hoy.Year, hoy.Month, 1), hoy);
            case MesPasado:
                var primeroDeEsteMes = new DateOnly(hoy.Year, hoy.Month, 1);
                return (primeroDeEsteMes.AddMonths(-1), primeroDeEsteMes.AddDays(-1));
            default:
                throw new ErrorDominio(
                    "periodo_desconocido",
                    $"El período '{nombre}' no existe. Los válidos son: {string.Join(", ", Nombres)}.",
                    400);
        }
    }

    private static DateOnly LeerFecha(string texto, string campo) =>
        DateOnly.TryParseExact(texto.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fecha)
            ? fecha
            : throw PeriodoInvalido($"'{campo}' tiene que ser una fecha yyyy-MM-dd; llegó '{texto}'.");

    private static ErrorDominio PeriodoInvalido(string mensaje) => new("periodo_invalido", mensaje, 400);

    private static DateOnly LunesDeLaSemanaDe(DateOnly fecha)
    {
        var diferencia = ((int)fecha.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
        return fecha.AddDays(-diferencia);
    }
}
