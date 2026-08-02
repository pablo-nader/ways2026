using System.Data.Common;
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

    /// <summary>La suplantación de ADR-16 es un mecanismo de sesión HTTP mutable
    /// (<see cref="TenantActualDeSesion"/>); ningún punto de entrada que use esta
    /// implementación inmutable (semilla, design-time, tests) aprovisiona tenants.</summary>
    public IDisposable Suplantar(int idTenant) =>
        throw new NotSupportedException(
            $"{nameof(TenantActualFijo)} no soporta suplantación de tenant (ADR-16).");

    public Task ReaplicarSobreConexionAsync(DbConnection conexion, CancellationToken ct = default) =>
        throw new NotSupportedException(
            $"{nameof(TenantActualFijo)} no soporta suplantación de tenant (ADR-16).");
}
