using Ways.Domain.Common;

namespace Ways.Domain.Organizacion;

/// <summary>
/// Dominio propio de un tenant (por ejemplo <c>waysautoservicio.com</c>): el servidor resuelve el
/// tenant desde el host de la request antes de que exista una sesión. Solo la plataforma los
/// administra. Un dominio que todavía no fue verificado por la plataforma no resuelve.
/// </summary>
public class Dominio : EntidadTenant
{
    private const int LargoMaximoDelHost = 253;
    private const int LargoMaximoDeEtiqueta = 63;
    private const int LargoMaximoDelPuerto = 5;

    public int Id { get; set; }

    /// <summary>Host en minúscula, sin puerto ni punto final. Ver <see cref="NormalizarHost"/>.</summary>
    public required string Host { get; set; }

    /// <summary>A lo sumo un dominio vivo por tenant es el principal.</summary>
    public bool EsPrincipal { get; set; }

    /// <summary><c>null</c> hasta que la plataforma lo marca como verificado.</summary>
    public DateTimeOffset? VerificadoEn { get; set; }

    public bool EstaVerificado => VerificadoEn is not null;

    /// <summary>
    /// Deja un host como lo guarda y lo compara el sistema: sin espacios alrededor, en minúscula,
    /// sin puerto y sin el punto final de un FQDN. Devuelve <c>null</c> cuando el texto no es un
    /// nombre de host de DNS (esquema, barra, espacios, caracteres fuera de <c>a-z 0-9 -</c>,
    /// etiquetas vacías o de más de 63 caracteres, más de 253 en total): el llamador lo traduce
    /// al 400 <c>host_invalido</c>. Los nombres con acentos viajan en su forma punycode.
    /// </summary>
    public static string? NormalizarHost(string? crudo)
    {
        if (string.IsNullOrWhiteSpace(crudo))
        {
            return null;
        }

        var host = crudo.Trim().ToLowerInvariant();

        var separadorDePuerto = host.IndexOf(':');
        if (separadorDePuerto >= 0)
        {
            var puerto = host[(separadorDePuerto + 1)..];

            if (puerto.Length is 0 or > LargoMaximoDelPuerto || !puerto.All(char.IsAsciiDigit))
            {
                return null;
            }

            host = host[..separadorDePuerto];
        }

        if (host.EndsWith('.'))
        {
            host = host[..^1];
        }

        if (host.Length is 0 or > LargoMaximoDelHost)
        {
            return null;
        }

        return host.Split('.').All(EsEtiquetaValida) ? host : null;
    }

    private static bool EsEtiquetaValida(string etiqueta) =>
        etiqueta.Length is > 0 and <= LargoMaximoDeEtiqueta
        && etiqueta[0] != '-'
        && etiqueta[^1] != '-'
        && etiqueta.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');
}
