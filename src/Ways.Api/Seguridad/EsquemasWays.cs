namespace Ways.Api.Seguridad;

/// <summary>Nombres de esquema de autenticación propios — mismo criterio que
/// <see cref="CookiesWays"/>/<see cref="ClaimsWays"/> para cookies/claims.</summary>
public static class EsquemasWays
{
    /// <summary>El <c>AddPolicyScheme</c> que decide, por request, si autenticar contra
    /// <see cref="Bearer"/> o contra la cookie (<c>Program.cs</c>) — es el <c>DefaultScheme</c>/
    /// <c>DefaultAuthenticateScheme</c> de la app, así que ninguna policy nombrada
    /// (<c>Politicas.cs</c>) ni el fallback necesitan enumerar los dos esquemas a mano.</summary>
    public const string Selector = "ways.selector";

    /// <summary>El esquema bearer (stage-desktop-pos, slice bearer): una sesión de cajero
    /// iniciada por <c>POST /api/auth/login-dispositivo</c> puede viajar como un token opaco en
    /// el header <c>Authorization: Bearer &lt;token&gt;</c> — pensado para el shell de escritorio
    /// (Tauri, slice 3), que va a correr en un origen distinto del de la API, donde ninguna
    /// cookie <c>SameSite=Lax</c> viaja en un <c>fetch</c> cross-site. Convive con el esquema de
    /// cookie, nunca lo reemplaza — ver <see cref="Ways.Api.Seguridad.ManejadorBearerDeSesion"/>.</summary>
    public const string Bearer = "ways.bearer";
}
