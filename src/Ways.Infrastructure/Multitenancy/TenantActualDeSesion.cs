using Ways.Application.Abstracciones;

namespace Ways.Infrastructure.Multitenancy;

/// <summary>
/// Implementación de <see cref="ITenantActual"/> para requests HTTP: scoped, mutable.
/// Tiene dos mutadores, los dos corren antes de la primera conexión de la request:
/// <c>OnValidatePrincipal</c> (ADR-2), para toda request con cookie ya emitida, y
/// <c>AuthEndpoints</c> (endpoint de login), que la pone en modo
/// <see cref="ModoDeAcceso.Login"/> antes de que exista cookie alguna.
///
/// Nota (deferred): el re-aplicado de la suplantación de tenant sobre una conexión ya
/// abierta dentro de una misma transacción (ADR-16, aprovisionamiento) no está
/// implementado todavía — no hace falta en este slice: los dos mutadores corren antes de
/// que se abra ninguna conexión (cada query de EF abre la suya, sin transacción ambiente
/// que la cruce), así que no hay conexión ya abierta a la que reaplicarle nada. Se retoma
/// cuando <c>ServicioDeAprovisionamiento</c> aterrice.
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
