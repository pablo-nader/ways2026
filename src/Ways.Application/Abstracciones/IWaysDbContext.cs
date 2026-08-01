using Microsoft.EntityFrameworkCore;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;

namespace Ways.Application.Abstracciones;

/// <summary>
/// Superficie de persistencia que ve la capa de aplicación.
/// La implementación concreta (EF Core + Npgsql) vive en Infrastructure.
/// </summary>
public interface IWaysDbContext
{
    DbSet<Usuario> Usuarios { get; }
    DbSet<Rol> Roles { get; }
    DbSet<Tenant> Tenants { get; }
    DbSet<Empresa> Empresas { get; }
    DbSet<PuntoVenta> PuntosVenta { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
