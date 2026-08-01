using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Organizacion;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

public class PuntoVentaConfiguration : IEntityTypeConfiguration<PuntoVenta>
{
    public void Configure(EntityTypeBuilder<PuntoVenta> builder)
    {
        builder.ToTable("puntos_venta");

        builder.HasKey(p => p.Id);

        builder.Property(p => p.Id)
            .HasColumnName("id_punto_venta")
            .UseIdentityByDefaultColumn();

        builder.Property(p => p.IdTenant)
            .HasColumnName("id_tenant")
            .IsRequired();

        builder.Property(p => p.IdEmpresa)
            .HasColumnName("id_empresa")
            .IsRequired();

        // Por si el día de mañana algo cuelga de un punto_venta con FK compuesta.
        builder.HasAlternateKey(p => new { p.Id, p.IdTenant })
            .HasName("ak_puntos_venta_id_punto_venta_id_tenant");

        builder.Property(p => p.Nombre)
            .HasColumnName("nombre")
            .HasColumnType("citext")
            .HasMaxLength(150)
            .IsRequired();

        builder.Property(p => p.Domicilio).HasColumnName("domicilio").HasMaxLength(255);
        builder.Property(p => p.Horario).HasColumnName("horario").HasMaxLength(255);
        builder.Property(p => p.Whatsapp).HasColumnName("whatsapp").HasMaxLength(30);
        builder.Property(p => p.Instagram).HasColumnName("instagram").HasMaxLength(150);
        builder.Property(p => p.Facebook).HasColumnName("facebook").HasMaxLength(150);
        builder.Property(p => p.Web).HasColumnName("web").HasMaxLength(255);

        builder.Property(p => p.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(p => p.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(p => p.DeletedAt).HasColumnName("deleted_at");

        builder.Ignore(p => p.EstaEliminada);

        // FK compuesta (id_empresa, id_tenant) → empresas: una fila del tenant 1 no puede
        // referenciar la empresa del tenant 2 ni por bug (ADR-9). id_empresa es obligatorio
        // acá (doc 09) — a diferencia de la FK opcional catálogo→empresa, esta no tiene el
        // problema de nullability de ADR-9.
        builder.HasOne<Empresa>()
            .WithMany()
            .HasForeignKey(p => new { p.IdEmpresa, p.IdTenant })
            .HasPrincipalKey(e => new { e.Id, e.IdTenant })
            .HasConstraintName("fk_puntos_venta_empresa")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(p => p.IdTenant).HasDatabaseName("ix_puntos_venta_tenant");
        builder.HasIndex(p => new { p.IdEmpresa, p.IdTenant }).HasDatabaseName("ix_puntos_venta_empresa");
    }
}
