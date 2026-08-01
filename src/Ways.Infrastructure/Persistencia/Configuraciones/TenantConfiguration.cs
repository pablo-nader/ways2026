using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Organizacion;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

public class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.ToTable("tenants");

        builder.HasKey(t => t.Id);

        builder.Property(t => t.Id)
            .HasColumnName("id_tenant")
            .UseIdentityByDefaultColumn();

        builder.Property(t => t.Nombre)
            .HasColumnName("nombre")
            .HasColumnType("citext")
            .HasMaxLength(150)
            .IsRequired();

        builder.Property(t => t.Estado)
            .HasColumnName("estado")
            .HasColumnType("estado_tenant")
            .HasDefaultValue(EstadoTenant.Activo)
            .IsRequired();

        builder.Property(t => t.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(t => t.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(t => t.DeletedAt).HasColumnName("deleted_at");

        builder.Ignore(t => t.EstaEliminada);
        builder.Ignore(t => t.PuedeOperar);
    }
}
