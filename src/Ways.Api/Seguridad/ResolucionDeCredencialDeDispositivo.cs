namespace Ways.Api.Seguridad;

/// <summary>
/// stage-desktop-pos, slice bearer: ÚNICO punto de lectura del secreto de dispositivo, para que
/// <c>GET /api/dispositivos/actual</c> (<c>DispositivosEndpoints.cs</c>) y
/// <c>POST /api/auth/login-dispositivo</c> (<c>AuthEndpoints.cs</c>) no puedan divergir en cómo
/// lo resuelven — antes de este slice cada uno leía <c>contexto.Request.Cookies[CookiesWays.Dispositivo]</c>
/// por su cuenta.
///
/// Dos superficies posibles: el header <c>Authorization: Dispositivo &lt;secreto&gt;</c> (Tauri,
/// slice 3 — el shell va a correr en <c>http://tauri.localhost</c>, cross-site respecto de la
/// API, y una cookie <c>SameSite=Lax</c> nunca viaja ahí) y la cookie <c>ways.dispositivo</c>
/// (navegador/slice actual, sigue funcionando exactamente igual). Se usa un esquema de header
/// PROPIO ("Dispositivo"), nunca "Bearer": el mismo header <c>Authorization</c> también puede
/// llevar el token bearer de sesión del cajero (<see cref="EsquemasWays.Bearer"/>) en otras
/// requests del mismo dispositivo — mezclar los dos bajo el mismo esquema sería ambiguo.
///
/// Precedencia: el header gana si está presente. Un dispositivo que ya migró a bearer nunca
/// debería autenticar por accidente contra una cookie vieja que le quedó en el disco/caché; la
/// cookie es el fallback para el navegador de hoy, que nunca manda ese header.
/// </summary>
public static class ResolucionDeCredencialDeDispositivo
{
    private const string EsquemaHeader = "Dispositivo";

    public static string? Resolver(HttpContext contexto)
    {
        var encabezado = contexto.Request.Headers["Authorization"].ToString();
        if (!string.IsNullOrEmpty(encabezado) &&
            encabezado.StartsWith(EsquemaHeader + " ", StringComparison.OrdinalIgnoreCase))
        {
            var secreto = encabezado[(EsquemaHeader.Length + 1)..].Trim();
            if (secreto.Length > 0)
            {
                return secreto;
            }
        }

        return contexto.Request.Cookies[CookiesWays.Dispositivo];
    }
}
