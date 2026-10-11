namespace Ways.Api.Dominios;

/// <summary>
/// Hosts que son de la plataforma (sección <c>Plataforma</c>): los que sirven el back office, el POS
/// y el alta de comercios. Todo otro host tiene que ser el dominio verificado de un tenant.
/// </summary>
public sealed class OpcionesDePlataforma
{
    public const string Seccion = "Plataforma";

    /// <summary>Los que valen cuando la configuración no define <see cref="Hosts"/>: producción y los
    /// hosts de desarrollo. <c>[::1]</c> es como <c>HostString.Host</c> devuelve el loopback IPv6.</summary>
    public static readonly IReadOnlyList<string> HostsPorDefecto =
        ["aipos.site", "localhost", "127.0.0.1", "[::1]"];

    /// <summary>El shell de escritorio (Tauri) sirve el POS desde este origen. Sus llamadas a la API
    /// llevan el host de la API, no este, pero es de la plataforma de todos modos y no depende de la
    /// configuración: quitarlo dejaría al POS empaquetado sin servicio.</summary>
    public const string HostDelShellDeEscritorio = "tauri.localhost";

    /// <summary>Si trae algún host reemplaza a <see cref="HostsPorDefecto"/> (no se suman). Sin valor,
    /// vacía o solo con entradas en blanco, valen los de por defecto: una configuración rota no deja a
    /// la plataforma sin host.</summary>
    public string[]? Hosts { get; set; }

    private HashSet<string>? efectivos;

    /// <param name="host">Ya en minúscula y sin puerto.</param>
    public bool EsHostDePlataforma(string host) => (efectivos ??= ConstruirEfectivos()).Contains(host);

    private HashSet<string> ConstruirEfectivos()
    {
        var configurados = (Hosts ?? [])
            .Where(host => !string.IsNullOrWhiteSpace(host))
            .Select(host => host.Trim().ToLowerInvariant())
            .ToArray();

        var hosts = new HashSet<string>(
            configurados.Length > 0 ? configurados : HostsPorDefecto, StringComparer.Ordinal)
        {
            HostDelShellDeEscritorio
        };

        return hosts;
    }
}
