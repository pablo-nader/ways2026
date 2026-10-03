namespace Ways.IntegrationTests;

/// <summary>El día calendario del negocio (Argentina). El servidor valida fechas contra este día,
/// no contra el día UTC: entre las 21 y las 24 hora local, el día UTC ya es el siguiente.</summary>
internal static class FechaDelNegocio
{
    private static readonly TimeZoneInfo Zona = TimeZoneInfo.FindSystemTimeZoneById("America/Argentina/Buenos_Aires");

    public static DateOnly Hoy() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Zona).DateTime);
}
