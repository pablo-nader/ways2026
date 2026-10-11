using Ways.Application.Abstracciones;

namespace Ways.Infrastructure.Multitenancy;

/// <summary>Traduce <see cref="ModoDeAcceso"/> al valor de texto que viaja en el GUC
/// <c>app.acceso</c> (ADR-4). Compartido por <see cref="InterceptorDeContextoDeTenant"/>
/// (conexión recién abierta) y <see cref="TenantActualDeSesion"/> (reaplicado sobre una
/// conexión ya abierta, ADR-16) para no repetir el mismo switch en los dos lugares.</summary>
public static class ModoDeAccesoExtensions
{
    public static string ComoGuc(this ModoDeAcceso modo) => modo switch
    {
        ModoDeAcceso.Tenant => "tenant",
        ModoDeAcceso.Plataforma => "plataforma",
        ModoDeAcceso.Login => "login",
        ModoDeAcceso.Publico => "publico",
        _ => string.Empty
    };

    /// <summary>
    /// Los tres valores que viajan a Postgres para un contexto: el modo, el tenant de las policies
    /// estándar (<c>app.tenant_id</c>) y el tenant de las de lectura pública
    /// (<c>app.tenant_publico</c>). Un contexto público lleva su tenant SOLO en el segundo: las
    /// policies <c>{tabla}_tenant</c> no miran el modo, así que con el tenant también en
    /// <c>app.tenant_id</c> el modo público leería y escribiría todas las tablas del comercio.
    /// </summary>
    public static (string Modo, string IdTenant, string IdTenantPublico) ComoGucs(this ITenantActual tenantActual)
    {
        var id = tenantActual.Id?.ToString() ?? string.Empty;

        return tenantActual.Modo == ModoDeAcceso.Publico
            ? (tenantActual.Modo.ComoGuc(), string.Empty, id)
            : (tenantActual.Modo.ComoGuc(), id, string.Empty);
    }
}
