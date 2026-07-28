using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Usuarios;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

public class RolConfiguration : IEntityTypeConfiguration<Rol>
{
    public void Configure(EntityTypeBuilder<Rol> builder)
    {
        builder.ToTable("roles");

        builder.HasKey(r => r.Id);

        // IDs fijos y conocidos por el código: no hay identity, se siembran a mano.
        builder.Property(r => r.Id)
            .HasColumnName("id_rol")
            .ValueGeneratedNever();

        builder.Property(r => r.Nombre)
            .HasColumnName("nombre")
            .HasColumnType("citext")
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(r => r.Descripcion)
            .HasColumnName("descripcion")
            .HasMaxLength(255);

        builder.Property(r => r.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(r => r.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(r => r.DeletedAt).HasColumnName("deleted_at");

        builder.Ignore(r => r.EstaEliminada);

        builder.HasIndex(r => r.Nombre)
            .HasDatabaseName("ux_roles_nombre")
            .HasFilter("deleted_at IS NULL")
            .IsUnique();
    }
}
