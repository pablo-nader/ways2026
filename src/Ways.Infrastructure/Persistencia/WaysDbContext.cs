using System.Linq.Expressions;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Domain.Common;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;

namespace Ways.Infrastructure.Persistencia;

public class WaysDbContext(DbContextOptions<WaysDbContext> options, ITenantActual tenantActual)
    : DbContext(options), IWaysDbContext, IDataProtectionKeyContext
{
    public DbSet<Usuario> Usuarios => Set<Usuario>();
    public DbSet<Rol> Roles => Set<Rol>();
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Empresa> Empresas => Set<Empresa>();
    public DbSet<PuntoVenta> PuntosVenta => Set<PuntoVenta>();

    /// <summary>Referenciado por los query filters de tenant (ver <see cref="AplicarFiltroDeTenant"/>):
    /// EF reconoce el acceso a un miembro de instancia del propio DbContext dentro de un
    /// filtro y lo reata a la instancia que ejecuta cada query, no a la que armó el modelo.</summary>
    internal ITenantActual TenantActual { get; } = tenantActual;

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

        // El enum estado_usuario / estado_tenant NO se declara acá: lo registra el
        // MapEnum<T>() de las opciones de Npgsql. Declararlo en los dos lados genera el
        // tipo dos veces en la migración, y con los valores en orden alfabético en vez
        // del orden del enum.

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(WaysDbContext).Assembly);

        AplicarFiltroDeBajaLogica(modelBuilder);
        AplicarFiltroDeTenant(modelBuilder);
        AplicarFiltroDeTenantEnTenant(modelBuilder);
    }

    /// <summary>
    /// Estampa <c>IdTenant</c> en cada fila nueva y rechaza que se modifique en una
    /// existente: ningún caso de uso lee ni escribe <c>IdTenant</c> a mano (doc 09).
    /// En modo plataforma no se pisa: quien siembra o aprovisiona ya lo setea explícito.
    /// </summary>
    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        EstamparTenant();
        return base.SaveChangesAsync(cancellationToken);
    }

    private void EstamparTenant()
    {
        foreach (var entrada in ChangeTracker.Entries<EntidadTenant>())
        {
            switch (entrada.State)
            {
                case EntityState.Added when !TenantActual.EsPlataforma:
                    entrada.Entity.IdTenant = TenantActual.Id
                        ?? throw new InvalidOperationException(
                            "No hay tenant en contexto: no se puede insertar una fila scopeada.");
                    break;

                case EntityState.Modified when entrada.Property(e => e.IdTenant).IsModified:
                    throw new InvalidOperationException(
                        "El id_tenant de una fila existente no se puede modificar.");
            }
        }
    }

    /// <summary>
    /// Toda entidad que hereda de <see cref="EntidadBase"/> filtra las bajas lógicas
    /// automáticamente, bajo la clave <c>"BajaLogica"</c>. Para verlas hay que pedir
    /// <c>IgnoreQueryFilters(["BajaLogica"])</c> explícitamente — así no se arrastra
    /// también el filtro de tenant (ADR-6).
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

            var parametro = Expression.Parameter(entidad.ClrType, "e");
            var propiedad = Expression.Property(parametro, nameof(EntidadBase.DeletedAt));
            var comparacion = Expression.Equal(
                propiedad, Expression.Constant(null, typeof(DateTimeOffset?)));

            entidad.SetQueryFilter("BajaLogica", Expression.Lambda(comparacion, parametro));
        }
    }

    /// <summary>
    /// Toda entidad que hereda de <see cref="EntidadTenant"/> filtra por tenant bajo la
    /// clave <c>"Tenant"</c> (ADR-1, ADR-6): plataforma ve todo, un tenant solo lo suyo.
    /// <c>IgnoreQueryFilters(["Tenant"])</c> lo saltea sin tocar la baja lógica — solo
    /// tiene sentido bajo una sesión de plataforma; RLS es quien realmente lo impide.
    /// </summary>
    private void AplicarFiltroDeTenant(ModelBuilder modelBuilder)
    {
        foreach (var entidad in modelBuilder.Model.GetEntityTypes())
        {
            if (!typeof(EntidadTenant).IsAssignableFrom(entidad.ClrType))
            {
                continue;
            }

            var parametro = Expression.Parameter(entidad.ClrType, "e");
            var propiedadIdTenant = Expression.Property(parametro, nameof(EntidadTenant.IdTenant));
            var filtro = ConstruirFiltroDeTenant(parametro, propiedadIdTenant);

            entidad.SetQueryFilter("Tenant", filtro);
        }
    }

    /// <summary>
    /// <see cref="Tenant"/> no hereda de <see cref="EntidadTenant"/> (su propia <c>Id</c>
    /// ES el alcance, ADR-1), así que necesita la variante escrita a mano en vez de la
    /// del loop por convención de <see cref="AplicarFiltroDeTenant"/>.
    /// </summary>
    private void AplicarFiltroDeTenantEnTenant(ModelBuilder modelBuilder)
    {
        var entidad = modelBuilder.Model.FindEntityType(typeof(Tenant))!;

        var parametro = Expression.Parameter(typeof(Tenant), "e");
        var propiedadId = Expression.Property(parametro, nameof(Tenant.Id));
        var filtro = ConstruirFiltroDeTenant(parametro, propiedadId);

        entidad.SetQueryFilter("Tenant", filtro);
    }

    /// <summary><c>e => this.TenantActual.EsPlataforma || propiedadDeAlcance == this.TenantActual.Id</c>.
    /// <c>Expression.Constant(this, typeof(WaysDbContext))</c> es lo que EF reconoce como
    /// acceso a la instancia en ejecución, no un valor fijo capturado al armar el modelo.</summary>
    private LambdaExpression ConstruirFiltroDeTenant(ParameterExpression parametro, Expression propiedadDeAlcance)
    {
        var contexto = Expression.Constant(this, typeof(WaysDbContext));
        var tenantActualDelContexto = Expression.Property(contexto, nameof(TenantActual));

        var esPlataforma = Expression.Property(tenantActualDelContexto, nameof(ITenantActual.EsPlataforma));
        var idDelContexto = Expression.Property(tenantActualDelContexto, nameof(ITenantActual.Id));

        var alcanceComoNullable = propiedadDeAlcance.Type == typeof(int?)
            ? propiedadDeAlcance
            : Expression.Convert(propiedadDeAlcance, typeof(int?));

        var comparacion = Expression.Equal(alcanceComoNullable, idDelContexto);

        return Expression.Lambda(Expression.OrElse(esPlataforma, comparacion), parametro);
    }
}
