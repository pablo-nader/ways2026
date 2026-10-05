namespace Ways.Api.ConectorMcp;

/// <summary>Resultado de <see cref="ValidacionDelConector.Validar"/>: <paramref name="Error"/> en
/// <c>null</c> significa que la configuración se puede usar.</summary>
public sealed record ResultadoDeValidacionDelConector(Uri? UrlPublica, string? Error);

/// <summary>
/// Decide, antes de registrar nada, si la configuración del conector se puede usar. Una
/// configuración inválida deja el conector deshabilitado en lugar de impedir el arranque.
/// </summary>
public static class ValidacionDelConector
{
    public static ResultadoDeValidacionDelConector Validar(OpcionesDeMcp opciones, bool esDesarrollo)
    {
        if (string.IsNullOrWhiteSpace(opciones.IdDeCliente))
        {
            return Invalida("Mcp:IdDeCliente no puede estar vacío.");
        }

        if (opciones.MinutosDeAccessToken <= 0)
        {
            return Invalida("Mcp:MinutosDeAccessToken tiene que ser mayor que cero.");
        }

        if (opciones.SegundosDeToleranciaDeReusoDeRefresh is < 0)
        {
            return Invalida("Mcp:SegundosDeToleranciaDeReusoDeRefresh no puede ser negativo.");
        }

        if (string.IsNullOrWhiteSpace(opciones.UrlPublica))
        {
            return esDesarrollo
                ? new ResultadoDeValidacionDelConector(null, null)
                : Invalida("falta Mcp:UrlPublica, que es obligatoria fuera de Development.");
        }

        if (!Uri.TryCreate(opciones.UrlPublica.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            uri.AbsolutePath != "/" ||
            uri.Query.Length > 0 ||
            uri.Fragment.Length > 0 ||
            uri.UserInfo.Length > 0)
        {
            return Invalida("Mcp:UrlPublica tiene que ser una URL absoluta sin ruta, por ejemplo https://aipos.site.");
        }

        if (!esDesarrollo && uri.Scheme != Uri.UriSchemeHttps)
        {
            return Invalida("Mcp:UrlPublica tiene que usar https fuera de Development.");
        }

        return new ResultadoDeValidacionDelConector(uri, null);
    }

    private static ResultadoDeValidacionDelConector Invalida(string error) => new(null, error);
}
