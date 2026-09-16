namespace Ways.Api.Seguridad;

/// <summary>Nombres de cookie propios — mismo criterio que <see cref="ClaimsWays"/> para los
/// nombres de claim.</summary>
public static class CookiesWays
{
    /// <summary>Secreto de dispositivo (stage-desktop-pos): HttpOnly, nunca la lee JavaScript.
    /// Emitida por <c>POST /api/dispositivos</c>, leída por <c>GET /api/dispositivos/actual</c> y
    /// <c>POST /api/auth/login-dispositivo</c>.</summary>
    public const string Dispositivo = "ways.dispositivo";
}
