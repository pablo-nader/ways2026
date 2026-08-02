using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Ways.Domain.Catalogos;
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

    DbSet<Area> Areas { get; }
    DbSet<Categoria> Categorias { get; }
    DbSet<Marca> Marcas { get; }
    DbSet<Grupo> Grupos { get; }
    DbSet<MedioPago> MediosPago { get; }
    DbSet<CondicionFiscal> CondicionesFiscales { get; }
    DbSet<AlicuotaIva> AlicuotasIva { get; }
    DbSet<TipoComprobante> TiposComprobante { get; }
    DbSet<Parametro> Parametros { get; }

    /// <summary>Superficie de transacción/conexión de EF Core (slice 3, tarea 3F,
    /// <c>ServicioDeAprovisionamiento</c>, ADR-16): <c>CreateExecutionStrategy().ExecuteAsync</c>
    /// y <c>BeginTransactionAsync</c> no tienen un equivalente más angosto en este proyecto.
    /// Sigue sin exponer el <see cref="Microsoft.EntityFrameworkCore.DbContext"/> concreto:
    /// <c>DatabaseFacade</c> es la misma abstracción de EF Core que ya expone la superficie
    /// pública de cualquier <c>DbContext</c>, no un tipo de Infrastructure.</summary>
    DatabaseFacade Database { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
