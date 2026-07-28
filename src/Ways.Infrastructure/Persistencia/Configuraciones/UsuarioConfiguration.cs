using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Usuarios;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

public class UsuarioConfiguration : IEntityTypeConfiguration<Usuario>
{
    public void Configure(EntityTypeBuilder<Usuario> builder)
    {
        builder.ToTable("usuarios");

        builder.HasKey(u => u.Id);

        builder.Property(u => u.Id)
            .HasColumnName("id_usuario")
            .UseIdentityByDefaultColumn();

        builder.Property(u => u.NombreUsuario)
            .HasColumnName("usuario")
            .HasColumnType("citext")
            .HasMaxLength(40)
            .IsRequired();

        builder.Property(u => u.Mail)
            .HasColumnName("mail")
            .HasColumnType("citext")
            .HasMaxLength(255)
            .IsRequired();

        builder.Property(u => u.RolId)
            .HasColumnName("id_rol")
            .IsRequired();

        builder.Property(u => u.Estado)
            .HasColumnName("estado")
            .HasColumnType("estado_usuario")
            .HasDefaultValue(EstadoUsuario.Activo)
            .IsRequired();

        builder.Property(u => u.PasswordHash)
            .HasColumnName("password_hash")
            .HasMaxLength(255)
            .IsRequired();

        builder.Property(u => u.PasswordAlgoritmo)
            .HasColumnName("password_algoritmo")
            .HasMaxLength(30)
            .IsRequired();

        builder.Property(u => u.PasswordActualizadoEl)
            .HasColumnName("password_actualizado_el")
            .IsRequired();

        builder.Property(u => u.UltimaConexion).HasColumnName("ultima_conexion");
        builder.Property(u => u.UltimoIntentoFallido).HasColumnName("ultimo_intento_fallido");

        builder.Property(u => u.IntentosFallidos)
            .HasColumnName("intentos_fallidos")
            .HasDefaultValue((short)0)
            .IsRequired();

        builder.Property(u => u.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(u => u.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(u => u.DeletedAt).HasColumnName("deleted_at");

        builder.Ignore(u => u.EstaEliminada);
        builder.Ignore(u => u.PuedeIniciarSesion);

        builder.HasOne(u => u.Rol)
            .WithMany(r => r.Usuarios)
            .HasForeignKey(u => u.RolId)
            .HasConstraintName("fk_usuarios_rol")
            .OnDelete(DeleteBehavior.Restrict);

        // Unique parcial: un usuario dado de baja libera el nombre y el mail.
        // Con un unique común, reusar un alias exigiría purgar la fila.
        builder.HasIndex(u => u.NombreUsuario)
            .HasDatabaseName("ux_usuarios_usuario")
            .HasFilter("deleted_at IS NULL")
            .IsUnique();

        builder.HasIndex(u => u.Mail)
            .HasDatabaseName("ux_usuarios_mail")
            .HasFilter("deleted_at IS NULL")
            .IsUnique();

        builder.HasIndex(u => u.RolId).HasDatabaseName("ix_usuarios_rol");
    }
}
