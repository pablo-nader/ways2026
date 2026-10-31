using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Organizacion;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

public class SucursalConfiguration : IEntityTypeConfiguration<Sucursal>
{
    public void Configure(EntityTypeBuilder<Sucursal> builder)
    {
        builder.ToTable("sucursales");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.Id)
            .HasColumnName("id_sucursal")
            .UseIdentityByDefaultColumn();

        builder.Property(s => s.IdTenant)
            .HasColumnName("id_tenant")
            .IsRequired();

        builder.Property(s => s.IdEmpresa)
            .HasColumnName("id_empresa")
            .IsRequired();

        // Hijos de la sucursal con FK compuesta por tenant.
        builder.HasAlternateKey(s => new { s.Id, s.IdTenant })
            .HasName("ak_sucursales_id_sucursal_id_tenant");

        // Permite que el punto de venta referencie su sucursal incluyendo la empresa: la base
        // rechaza una sucursal de otra empresa.
        builder.HasAlternateKey(s => new { s.Id, s.IdEmpresa, s.IdTenant })
            .HasName("ak_sucursales_id_sucursal_id_empresa_id_tenant");

        // Mismo tipo y largo que puntos_venta.nombre: cada sucursal nace con el nombre de su punto de venta.
        builder.Property(s => s.Nombre)
            .HasColumnName("nombre")
            .HasColumnType("citext")
            .HasMaxLength(150)
            .IsRequired();

        builder.Property(s => s.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(s => s.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(s => s.DeletedAt).HasColumnName("deleted_at");

        builder.Ignore(s => s.EstaEliminada);

        builder.HasOne<Empresa>()
            .WithMany()
            .HasForeignKey(s => new { s.IdEmpresa, s.IdTenant })
            .HasPrincipalKey(e => new { e.Id, e.IdTenant })
            .HasConstraintName("fk_sucursales_empresa")
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(s => s.IdSucursalFusion).HasColumnName("id_sucursal_fusion");

        // La sucursal que absorbió a esta es de su misma empresa y de su mismo tenant.
        builder.HasOne<Sucursal>()
            .WithMany()
            .HasForeignKey(s => new { s.IdSucursalFusion, s.IdEmpresa, s.IdTenant })
            .HasPrincipalKey(s => new { s.Id, s.IdEmpresa, s.IdTenant })
            .HasConstraintName("fk_sucursales_fusion")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(s => s.IdTenant).HasDatabaseName("ix_sucursales_tenant");
        builder.HasIndex(s => new { s.IdEmpresa, s.IdTenant }).HasDatabaseName("ix_sucursales_empresa");
    }
}
