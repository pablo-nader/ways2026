namespace Ways.Domain.Organizacion;

/// <summary>
/// Regla de qué sesiones valen en qué host: una sesión de un tenant solo vale en el dominio de
/// ese tenant (o en el host de la plataforma), así que una cookie de un comercio no sirve en el
/// dominio de otro.
/// </summary>
public static class PoliticaDeDominio
{
    /// <param name="idTenantDelHost"><c>null</c> cuando el host es el de la plataforma.</param>
    /// <param name="esSesionDePlataforma">Staff de plataforma (root): opera sobre cualquier host.</param>
    /// <param name="idTenantDeLaSesion">El tenant del claim de la sesión; <c>null</c> si no lo lleva.</param>
    public static bool SesionCorrespondeAlHost(
        int? idTenantDelHost, bool esSesionDePlataforma, int? idTenantDeLaSesion) =>
        idTenantDelHost is null
        || esSesionDePlataforma
        || idTenantDeLaSesion == idTenantDelHost;
}
