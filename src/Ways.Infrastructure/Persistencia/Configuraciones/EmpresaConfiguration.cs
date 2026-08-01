using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Organizacion;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

public class EmpresaConfiguration : IEntityTypeConfiguration<Empresa>
{
    public void Configure(EntityTypeBuilder<Empresa> builder)
    {
        builder.ToTable("empresas");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id)
            .HasColumnName("id_empresa")
            .UseIdentityByDefaultColumn();

        builder.Property(e => e.IdTenant)
            .HasColumnName("id_tenant")
            .IsRequired();

        // Habilita las FKs compuestas de los dependientes (puntos_venta, catálogos) —
        // ADR-9: una fila de un tenant no puede referenciar la empresa de otro tenant.
        builder.HasAlternateKey(e => new { e.Id, e.IdTenant })
            .HasName("ak_empresas_id_empresa_id_tenant");

        builder.Property(e => e.RazonSocial)
            .HasColumnName("razon_social")
            .HasColumnType("citext")
            .HasMaxLength(150)
            .IsRequired();

        builder.Property(e => e.NombreFantasia)
            .HasColumnName("nombre_fantasia")
            .HasColumnType("citext")
            .HasMaxLength(150);

        builder.Property(e => e.Cuit)
            .HasColumnName("cuit")
            .HasMaxLength(13);

        builder.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(e => e.DeletedAt).HasColumnName("deleted_at");

        builder.Ignore(e => e.EstaEliminada);

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(e => e.IdTenant)
            .HasConstraintName("fk_empresas_tenant")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(e => e.IdTenant).HasDatabaseName("ix_empresas_tenant");
    }
}
