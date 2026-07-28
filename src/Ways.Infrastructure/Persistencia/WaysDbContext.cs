using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Domain.Common;
using Ways.Domain.Usuarios;

namespace Ways.Infrastructure.Persistencia;

public class WaysDbContext(DbContextOptions<WaysDbContext> options)
    : DbContext(options), IWaysDbContext, IDataProtectionKeyContext
{
    public DbSet<Usuario> Usuarios => Set<Usuario>();
    public DbSet<Rol> Roles => Set<Rol>();

    /// <summary>
    /// Claves de Data Protection, que son las que firman la cookie de sesión.
    /// Viven en la base y no en el sistema de archivos del contenedor: si no,
    /// cada redeploy genera claves nuevas y echa a todos los usuarios logueados.
    /// </summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // citext: comparación de texto case-insensitive a nivel motor.
        // Evita índices sobre lower(columna) para el unique de usuario y mail.
        modelBuilder.HasPostgresExtension("citext");

        // El enum estado_usuario NO se declara acá: lo registra el MapEnum<EstadoUsuario>()
        // de las opciones de Npgsql. Declararlo en los dos lados genera el tipo dos veces
        // en la migración, y con los valores en orden alfabético en vez del orden del enum.

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(WaysDbContext).Assembly);

        AplicarFiltroDeBajaLogica(modelBuilder);
    }

    /// <summary>
    /// Toda entidad que hereda de <see cref="EntidadBase"/> filtra las bajas lógicas
    /// automáticamente. Para verlas hay que pedir <c>IgnoreQueryFilters()</c> explícitamente.
    /// </summary>
    private static void AplicarFiltroDeBajaLogica(ModelBuilder modelBuilder)
    {
        foreach (var entidad in modelBuilder.Model.GetEntityTypes())
        {
            if (!typeof(EntidadBase).IsAssignableFrom(entidad.ClrType))
            {
                continue;
            }

            if (entidad.ClrType == typeof(DataProtectionKey))
            {
                continue;
            }

            var parametro = System.Linq.Expressions.Expression.Parameter(entidad.ClrType, "e");
            var propiedad = System.Linq.Expressions.Expression.Property(
                parametro, nameof(EntidadBase.DeletedAt));
            var comparacion = System.Linq.Expressions.Expression.Equal(
                propiedad,
                System.Linq.Expressions.Expression.Constant(null, typeof(DateTimeOffset?)));

            entidad.SetQueryFilter(
                System.Linq.Expressions.Expression.Lambda(comparacion, parametro));
        }
    }
}
