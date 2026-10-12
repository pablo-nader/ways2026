using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Clientes;
using Ways.Domain.Organizacion;
using Ways.Domain.Plantel;
using Ways.Domain.Usuarios;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

public class PersonalConfiguration : IEntityTypeConfiguration<Personal>
{
    public void Configure(EntityTypeBuilder<Personal> builder)
    {
        builder.ToTable("personal", tabla => tabla.HasCheckConstraint("ck_personal_valor_hora", "valor_hora >= 0"));

        builder.HasKey(p => p.Id);

        builder.Property(p => p.Id)
            .HasColumnName("id_personal")
            .UseIdentityByDefaultColumn();

        builder.Property(p => p.IdTenant).HasColumnName("id_tenant").IsRequired();
        builder.Property(p => p.IdEmpresa).HasColumnName("id_empresa");

        builder.Property(p => p.Nombre)
            .HasColumnName("nombre")
            .HasColumnType("citext")
            .HasMaxLength(150)
            .IsRequired();

        builder.Property(p => p.Documento)
            .HasColumnName("documento")
            .HasColumnType("citext")
            .HasMaxLength(30);

        builder.Property(p => p.Telefono)
            .HasColumnName("telefono")
            .HasColumnType("citext")
            .HasMaxLength(50);

        builder.Property(p => p.Observaciones)
            .HasColumnName("observaciones")
            .HasColumnType("text");

        builder.Property(p => p.ValorHora)
            .HasColumnName("valor_hora")
            .HasColumnType("numeric(14,2)")
            .HasDefaultValue(0m)
            .IsRequired();

        builder.Property(p => p.IdUsuario).HasColumnName("id_usuario");
        builder.Property(p => p.IdCliente).HasColumnName("id_cliente");

        builder.Property(p => p.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(p => p.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(p => p.DeletedAt).HasColumnName("deleted_at");

        builder.Ignore(p => p.EstaEliminada);

        builder.HasIndex(p => p.IdTenant).HasDatabaseName("ix_personal_tenant");
        builder.HasIndex(p => new { p.IdEmpresa, p.IdTenant }).HasDatabaseName("ix_personal_empresa");

        builder.HasIndex(p => new { p.IdCliente, p.IdTenant }).HasDatabaseName("ix_personal_cliente");

        builder.HasIndex(p => p.IdUsuario)
            .HasDatabaseName("ux_personal_usuario")
            .HasFilter("deleted_at IS NULL AND id_usuario IS NOT NULL")
            .IsUnique();

        builder.HasIndex(p => p.IdCliente)
            .HasDatabaseName("ux_personal_cliente")
            .HasFilter("deleted_at IS NULL AND id_cliente IS NOT NULL")
            .IsUnique();

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(p => p.IdTenant)
            .HasConstraintName("fk_personal_tenant")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Empresa>()
            .WithMany()
            .HasForeignKey(p => new { p.IdEmpresa, p.IdTenant })
            .HasPrincipalKey(e => new { e.Id, e.IdTenant })
            .HasConstraintName("fk_personal_empresa")
            .OnDelete(DeleteBehavior.Restrict);

        // Simple: Usuario no tiene clave alterna (id_usuario, id_tenant) porque su tenant es nullable.
        builder.HasOne<Usuario>()
            .WithMany()
            .HasForeignKey(p => p.IdUsuario)
            .HasConstraintName("fk_personal_usuario")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Cliente>()
            .WithMany()
            .HasForeignKey(p => new { p.IdCliente, p.IdTenant })
            .HasPrincipalKey(c => new { c.Id, c.IdTenant })
            .HasConstraintName("fk_personal_cliente")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasAlternateKey(p => new { p.Id, p.IdTenant })
            .HasName("ak_personal_id_personal_id_tenant");
    }
}
