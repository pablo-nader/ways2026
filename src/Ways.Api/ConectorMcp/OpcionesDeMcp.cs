namespace Ways.Api.ConectorMcp;

/// <summary>Sección <c>Mcp</c> de la configuración del conector MCP (experimental). Con
/// <see cref="Habilitado"/> en <c>false</c> (default) no se registra ni se mapea nada.</summary>
public sealed class OpcionesDeMcp
{
    public const string Seccion = "Mcp";

    public bool Habilitado { get; set; }

    /// <summary>Base pública del servidor (por ejemplo <c>https://aipos.site</c>). Fija el emisor
    /// OAuth y el recurso <c>&lt;UrlPublica&gt;/mcp</c>. Obligatoria fuera de Development; en
    /// Development, si falta, ambos se derivan de cada request.</summary>
    public string? UrlPublica { get; set; }

    /// <summary>Mails que pueden autorizar el conector, separados por coma o punto y coma (ver
    /// <see cref="ListaDeMailsHabilitados"/>). Vacío: nadie puede autorizar.</summary>
    public string? MailsHabilitados { get; set; }

    public int MinutosDeAccessToken { get; set; } = 60;

    public string IdDeCliente { get; set; } = "claude-ways";

    /// <summary>Segundos durante los que un refresh token ya canjeado se sigue aceptando. Sin
    /// valor rige la tolerancia por defecto de OpenIddict.</summary>
    public int? SegundosDeToleranciaDeReusoDeRefresh { get; set; }
}

public static class ConstantesDeMcp
{
    public const string AlcanceMcp = "ways.mcp";

    /// <summary>Esquema propio que valida el access token de OpenIddict y lo traduce a la
    /// identidad de Ways (<see cref="ManejadorDeTokenMcp"/>).</summary>
    public const string EsquemaDeToken = "ways.mcp.token";

    public const string PoliticaDeAcceso = "ways.mcp.acceso";

    public const string RutaMcp = "/mcp";
    public const string RutaDeAutorizacion = "/connect/authorize";
    public const string RutaDeToken = "/connect/token";

    public static readonly string[] UrisDeRedireccionDeClaude =
    [
        "https://claude.ai/api/mcp/auth_callback",
        "https://claude.com/api/mcp/auth_callback"
    ];
}
