namespace Ways.IntegrationTests;

/// <summary>El día calendario según la zona sembrada del negocio: entre las 21 y las 24 de
/// Buenos Aires el día UTC ya es mañana y el servidor lo rechaza como fecha futura.</summary>
internal static class FechaDelNegocio
{
    private static readonly TimeZoneInfo Zona = TimeZoneInfo.FindSystemTimeZoneById("America/Argentina/Buenos_Aires");

    public static DateOnly Hoy() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Zona).DateTime);
}
