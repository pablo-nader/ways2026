using Npgsql;

namespace Ways.Infrastructure.Persistencia;

/// <summary>
/// Normaliza la cadena de conexión.
///
/// Los paneles de hosting (EasyPanel, Railway, Render, Heroku) entregan la conexión
/// como URI — <c>postgres://usuario:clave@host:5432/base?sslmode=disable</c> — y Npgsql
/// espera el formato clave=valor. Acá se aceptan los dos y se devuelve siempre el segundo.
/// </summary>
public static class CadenaDeConexion
{
    public static string Normalizar(string cadena)
    {
        if (string.IsNullOrWhiteSpace(cadena))
        {
            throw new ArgumentException("La cadena de conexión está vacía.", nameof(cadena));
        }

        cadena = cadena.Trim();

        var esUri = cadena.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
                 || cadena.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase);

        return esUri ? DesdeUri(cadena) : cadena;
    }

    private static string DesdeUri(string cadena)
    {
        var uri = new Uri(cadena);
        var credenciales = uri.UserInfo.Split(':', 2);

        var constructor = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.IsDefaultPort ? 5432 : uri.Port,
            Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')),
            Username = Uri.UnescapeDataString(credenciales[0]),
            Password = credenciales.Length > 1 ? Uri.UnescapeDataString(credenciales[1]) : null
        };

        foreach (var parametro in ParsearQuery(uri.Query))
        {
            AplicarParametro(constructor, parametro.Key, parametro.Value);
        }

        return constructor.ConnectionString;
    }

    private static IEnumerable<KeyValuePair<string, string>> ParsearQuery(string query)
    {
        foreach (var parte in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var partes = parte.Split('=', 2);
            yield return new KeyValuePair<string, string>(
                Uri.UnescapeDataString(partes[0]),
                partes.Length > 1 ? Uri.UnescapeDataString(partes[1]) : string.Empty);
        }
    }

    private static void AplicarParametro(
        NpgsqlConnectionStringBuilder constructor, string clave, string valor)
    {
        switch (clave.ToLowerInvariant())
        {
            case "sslmode":
                constructor.SslMode = valor.ToLowerInvariant() switch
                {
                    "disable" => SslMode.Disable,
                    "allow" => SslMode.Allow,
                    "prefer" => SslMode.Prefer,
                    "require" => SslMode.Require,
                    "verify-ca" => SslMode.VerifyCA,
                    "verify-full" => SslMode.VerifyFull,
                    _ => constructor.SslMode
                };

                // En Npgsql, 'require' cifra sin validar la cadena de confianza, que es
                // justo lo que hace falta con los certificados autofirmados de los Postgres
                // administrados. Para validar de verdad hay que pedir verify-ca o verify-full.
                break;

            case "application_name":
                constructor.ApplicationName = valor;
                break;

            case "connect_timeout":
                if (int.TryParse(valor, out var timeout))
                {
                    constructor.Timeout = timeout;
                }
                break;

            case "search_path":
                constructor.SearchPath = valor;
                break;
        }
    }
}
