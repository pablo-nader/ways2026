using Ways.Application.Abstracciones;

namespace Ways.Infrastructure.Multitenancy;

/// <summary>
/// Implementación inmutable de <see cref="ITenantActual"/> para puntos de entrada sin
/// request HTTP (semilla de arranque, factoría de diseño de EF, tests) — ADR-2.
/// </summary>
public sealed class TenantActualFijo : ITenantActual
{
    public TenantActualFijo(ModoDeAcceso modo, int? idTenant)
    {
        if (modo == ModoDeAcceso.Tenant && idTenant is null)
        {
            throw new InvalidOperationException(
                $"{nameof(ModoDeAcceso.Tenant)} requiere un id_tenant.");
        }

        Modo = modo;
        Id = modo == ModoDeAcceso.Tenant ? idTenant : null;
    }

    public int? Id { get; }

    public ModoDeAcceso Modo { get; }

    /// <summary>La semilla de arranque y las herramientas de diseño operan siempre en
    /// modo plataforma: siembran filas de cualquier tenant explícitamente.</summary>
    public static TenantActualFijo Plataforma { get; } = new(ModoDeAcceso.Plataforma, null);
}
