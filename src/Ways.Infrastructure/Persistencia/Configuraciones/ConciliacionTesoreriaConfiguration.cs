using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Caja;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

/// <summary>
/// Mapea <see cref="ConciliacionTesoreria"/>: marcas append-only por empresa. Sin índice único sobre <c>hasta</c>: la monotonía de
/// las marcas la sostiene el lock exclusivo del escritor (<c>GuardaDeConciliacion</c>), no la base.
/// </summary>
public class ConciliacionTesoreriaConfiguration : IEntityTypeConfiguration<ConciliacionTesoreria>
{
    public void Configure(EntityTypeBuilder<ConciliacionTesoreria> builder)
    {
        builder.ToTable("conciliaciones_tesoreria");

        builder.HasKey(c => c.Id).HasName("pk_conciliaciones_tesoreria");

        builder.Property(c => c.Id)
            .HasColumnName("id_conciliacion_tesoreria")
            .UseIdentityByDefaultColumn();

        builder.Property(c => c.IdTenant).HasColumnName("id_tenant").IsRequired();
        builder.Property(c => c.IdEmpresa).HasColumnName("id_empresa").IsRequired();
        builder.Property(c => c.Hasta).HasColumnName("hasta").IsRequired();
        builder.Property(c => c.IdUsuario).HasColumnName("id_usuario").IsRequired();
        builder.Property(c => c.CreadoEl).HasColumnName("creado_el").IsRequired();
        builder.Property(c => c.Nota).HasColumnName("nota").HasColumnType("text");

        builder.HasIndex(c => c.IdTenant).HasDatabaseName("ix_conciliaciones_tesoreria_tenant");

        // Soporte de la FK compuesta a la empresa y de la lectura de la marca vigente (máximo de hasta).
        builder.HasIndex(c => new { c.IdEmpresa, c.IdTenant, c.Hasta }).HasDatabaseName("ix_conciliaciones_tesoreria_empresa_hasta");
        builder.HasIndex(c => c.IdUsuario).HasDatabaseName("ix_conciliaciones_tesoreria_usuario");

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(c => c.IdTenant)
            .HasConstraintName("fk_conciliaciones_tesoreria_tenant")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Empresa>()
            .WithMany()
            .HasForeignKey(c => new { c.IdEmpresa, c.IdTenant })
            .HasPrincipalKey(e => new { e.Id, e.IdTenant })
            .HasConstraintName("fk_conciliaciones_tesoreria_empresa")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Usuario>()
            .WithMany()
            .HasForeignKey(c => c.IdUsuario)
            .HasConstraintName("fk_conciliaciones_tesoreria_usuario")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
