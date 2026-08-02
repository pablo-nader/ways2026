using Ways.Application.Abstracciones;

namespace Ways.Infrastructure.Multitenancy;

/// <summary>Traduce <see cref="ModoDeAcceso"/> al valor de texto que viaja en el GUC
/// <c>app.acceso</c> (ADR-4). Compartido por <see cref="InterceptorDeContextoDeTenant"/>
/// (conexión recién abierta) y <see cref="TenantActualDeSesion"/> (reaplicado sobre una
/// conexión ya abierta, ADR-16) para no repetir el mismo switch en los dos lugares.</summary>
internal static class ModoDeAccesoExtensions
{
    public static string ComoGuc(this ModoDeAcceso modo) => modo switch
    {
        ModoDeAcceso.Tenant => "tenant",
        ModoDeAcceso.Plataforma => "plataforma",
        ModoDeAcceso.Login => "login",
        _ => string.Empty
    };
}
