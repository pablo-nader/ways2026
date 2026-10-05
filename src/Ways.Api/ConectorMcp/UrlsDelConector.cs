namespace Ways.Api.ConectorMcp;

/// <summary>URLs del conector que arma la API (base, recurso MCP y emisor). El emisor sale de
/// <see cref="Uri.AbsoluteUri"/> porque así lo serializa OpenIddict (con barra final en la raíz);
/// la metadata del recurso protegido tiene que repetirlo idéntico.</summary>
public sealed class UrlsDelConector(Uri? urlPublica, IReadOnlyList<string> recursosRegistrados)
{
    public Uri? UrlPublica { get; } = urlPublica;

    /// <summary>Recursos que OpenIddict acepta en el parámetro <c>resource</c> (ver
    /// <see cref="RecursosRegistrables"/>).</summary>
    public IReadOnlyList<string> RecursosRegistrados { get; } = recursosRegistrados;

    public string Base(HttpContext http) =>
        UrlPublica is not null
            ? UrlPublica.GetLeftPart(UriPartial.Authority).ToLowerInvariant()
            : $"{http.Request.Scheme}://{http.Request.Host.ToUriComponent()}".ToLowerInvariant() + http.Request.PathBase;

    public string Recurso(HttpContext http) => Base(http) + ConstantesDeMcp.RutaMcp;

    public string Emisor(HttpContext http) => new Uri(Base(http) + "/").AbsoluteUri;

    /// <summary>Host con el que se reescriben las requests del conector cuando hay URL pública.
    /// Sale de <see cref="Uri.Authority"/> porque <c>HostString.FromUriComponent(Uri)</c> conserva
    /// el puerto por defecto (":443") y terminaría publicado en el WWW-Authenticate.</summary>
    public static HostString HostFijado(Uri urlPublica) => HostString.FromUriComponent(urlPublica.Authority);

    /// <summary>OpenIddict valida el parámetro <c>resource</c> contra una lista fija armada al
    /// arrancar, así que sin URL pública solo se aceptan los recursos de las URLs en las que
    /// escucha Kestrel (<c>urls</c>/<c>ASPNETCORE_URLS</c>).</summary>
    public static IReadOnlyList<string> RecursosRegistrables(IConfiguration configuracion, Uri? urlPublica)
    {
        if (urlPublica is not null)
        {
            return [urlPublica.GetLeftPart(UriPartial.Authority).ToLowerInvariant() + ConstantesDeMcp.RutaMcp];
        }

        var urls = configuracion["urls"] ?? "http://localhost:5000";

        return urls
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(url => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri : null)
            .Where(uri => uri is not null && uri.Host is not ("0.0.0.0" or "[::]"))
            .Select(uri => uri!.GetLeftPart(UriPartial.Authority).ToLowerInvariant() + ConstantesDeMcp.RutaMcp)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }
}
