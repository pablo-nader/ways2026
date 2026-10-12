using Ways.Application.Dominios;

namespace Ways.Api.Dominios;

/// <summary>
/// Implementación scoped de <see cref="IDominioActual"/>, que completa
/// <see cref="ResolucionDeDominioMiddleware"/> al principio del pipeline. Mientras nadie la complete
/// (un servicio resuelto fuera de una request) se comporta como la plataforma: sin tenant ni host.
/// </summary>
public sealed class DominioActual : IDominioActual
{
    public bool EsPlataforma { get; private set; } = true;

    public string Host { get; private set; } = string.Empty;

    public int? IdTenant { get; private set; }

    public string? NombreComercio { get; private set; }

    public bool TenantPuedeOperar { get; private set; }

    public void EstablecerPlataforma(string host)
    {
        EsPlataforma = true;
        Host = host;
        IdTenant = null;
        NombreComercio = null;
        TenantPuedeOperar = false;
    }

    public void EstablecerDeTenant(string host, DominioResuelto resuelto)
    {
        EsPlataforma = false;
        Host = host;
        IdTenant = resuelto.IdTenant;
        NombreComercio = resuelto.NombreComercio;
        TenantPuedeOperar = resuelto.TenantPuedeOperar;
    }
}
