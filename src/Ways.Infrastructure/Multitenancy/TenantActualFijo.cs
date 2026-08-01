using Ways.Application.Abstracciones;

namespace Ways.Infrastructure.Multitenancy;

/// <summary>
/// Implementación inmutable de <see cref="ITenantActual"/> para puntos de entrada sin
/// request HTTP (semilla de arranque, factoría de diseño de EF, tests) — ADR-2.
/// </summary>
public sealed class TenantActualFijo(ModoDeAcceso modo, int? idTenant) : ITenantActual
{
    public int? Id { get; } = modo == ModoDeAcceso.Tenant ? idTenant : null;

    public ModoDeAcceso Modo { get; } = modo;

    /// <summary>La semilla de arranque y las herramientas de diseño operan siempre en
    /// modo plataforma: siembran filas de cualquier tenant explícitamente.</summary>
    public static TenantActualFijo Plataforma { get; } = new(ModoDeAcceso.Plataforma, null);
}
