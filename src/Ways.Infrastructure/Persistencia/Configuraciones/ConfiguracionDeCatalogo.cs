using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Catalogos;
using Ways.Domain.Organizacion;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

/// <summary>
/// Base compartida de los 5 catálogos de tenant (ADR-11): mapea la forma común de
/// <see cref="CatalogoSimple"/> — tabla, columnas, auditoría, la FK a <c>tenants</c>, la FK
/// compuesta opcional a <c>empresas</c> (ADR-9) y el par de índices que reemplaza a un
/// <c>UNIQUE (id_tenant, id_empresa, nombre)</c> que no deduplica cuando <c>id_empresa</c>
/// es <c>NULL</c> (mismo problema y misma solución que <c>parametros</c>, ADR-13) — y deja
/// el resto de cada catálogo en <see cref="ConfigurarPropio"/>.
/// </summary>
public abstract class ConfiguracionDeCatalogo<T> : IEntityTypeConfiguration<T>
    where T : CatalogoSimple
{
    /// <summary>Nombre de la tabla, minúscula y plural (doc 10). También se usa para
    /// derivar los nombres de constraint/índice por convención.</summary>
    protected abstract string Tabla { get; }

    /// <summary>Nombre de columna de la PK (p.ej. <c>id_area</c>).</summary>
    protected abstract string ColumnaId { get; }

    public void Configure(EntityTypeBuilder<T> builder)
    {
        builder.ToTable(Tabla);

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id)
            .HasColumnName(ColumnaId)
            .UseIdentityByDefaultColumn();

        builder.Property(e => e.IdTenant)
            .HasColumnName("id_tenant")
            .IsRequired();

        builder.Property(e => e.IdEmpresa)
            .HasColumnName("id_empresa");

        builder.Property(e => e.Nombre)
            .HasColumnName("nombre")
            .HasColumnType("citext")
            .HasMaxLength(150)
            .IsRequired();

        builder.Property(e => e.Activo)
            .HasColumnName("activo")
            .HasDefaultValue(true)
            .IsRequired();

        builder.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(e => e.DeletedAt).HasColumnName("deleted_at");

        builder.Ignore(e => e.EstaEliminada);

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(e => e.IdTenant)
            .HasConstraintName($"fk_{Tabla}_tenant")
            .OnDelete(DeleteBehavior.Restrict);

        // FK compuesta opcional a empresas (ADR-9): id_empresa NULL ⇒ compartido por todo
        // el tenant, MATCH SIMPLE salta el chequeo cuando esa columna es NULL. Misma técnica
        // que puntos_venta→empresas, acá con la parte opcional que ese caso no tiene.
        builder.HasOne<Empresa>()
            .WithMany()
            .HasForeignKey(e => new { e.IdEmpresa, e.IdTenant })
            .HasPrincipalKey(e => new { e.Id, e.IdTenant })
            .HasConstraintName($"fk_{Tabla}_empresa")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(e => e.IdTenant).HasDatabaseName($"ix_{Tabla}_tenant");
        builder.HasIndex(e => new { e.IdEmpresa, e.IdTenant }).HasDatabaseName($"ix_{Tabla}_empresa");

        // El par de índices que reemplaza a un UNIQUE (id_tenant, id_empresa, nombre): con
        // id_empresa nullable, Postgres trata cada fila compartida como distinta y el
        // duplicado se cuela. Separar por WHERE id_empresa IS NULL / IS NOT NULL lo cierra
        // sin depender de NULLS NOT DISTINCT.
        builder.HasIndex(e => new { e.IdTenant, e.Nombre })
            .HasDatabaseName($"ux_{Tabla}_nombre_compartido")
            .HasFilter("id_empresa IS NULL AND deleted_at IS NULL")
            .IsUnique();

        builder.HasIndex(e => new { e.IdTenant, e.IdEmpresa, e.Nombre })
            .HasDatabaseName($"ux_{Tabla}_nombre_empresa")
            .HasFilter("id_empresa IS NOT NULL AND deleted_at IS NULL")
            .IsUnique();

        ConfigurarPropio(builder);
    }

    /// <summary>Mapeo propio de cada catálogo: columnas extra, índices o FKs propias
    /// (~10 líneas por catálogo, ADR-11).</summary>
    protected abstract void ConfigurarPropio(EntityTypeBuilder<T> builder);
}
