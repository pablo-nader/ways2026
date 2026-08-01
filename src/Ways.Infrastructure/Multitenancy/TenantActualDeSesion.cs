using Ways.Application.Abstracciones;

namespace Ways.Infrastructure.Multitenancy;

/// <summary>
/// Implementación de <see cref="ITenantActual"/> para requests HTTP: scoped, mutable.
/// La puebla <c>OnValidatePrincipal</c> una sola vez por request (ADR-2), antes de que
/// corra cualquier endpoint.
///
/// Nota (deferred): el re-aplicado de la suplantación de tenant sobre una conexión ya
/// abierta dentro de una misma transacción (ADR-16, aprovisionamiento) no está
/// implementado todavía — no hace falta en este slice, donde el único mutador es
/// <c>OnValidatePrincipal</c>, que corre antes de la primera conexión de la request.
/// Se retoma cuando <c>ServicioDeAprovisionamiento</c> aterrice.
/// </summary>
public sealed class TenantActualDeSesion : ITenantActual
{
    public int? Id { get; private set; }

    public ModoDeAcceso Modo { get; private set; } = ModoDeAcceso.Ninguno;

    public void Establecer(ModoDeAcceso modo, int? idTenant)
    {
        if (modo == ModoDeAcceso.Tenant && idTenant is null)
        {
            throw new InvalidOperationException(
                $"{nameof(ModoDeAcceso.Tenant)} requiere un id_tenant.");
        }

        Modo = modo;
        Id = modo == ModoDeAcceso.Tenant ? idTenant : null;
    }
}
