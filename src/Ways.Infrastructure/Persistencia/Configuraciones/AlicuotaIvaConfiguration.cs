using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Catalogos;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

/// <summary>
/// <c>[global]</c> (ADR-11, gate #4): sin <c>id_tenant</c>; RLS lectura-todos/escritura-plataforma
/// (override de ADR-11, decisión del usuario 2026-08-01).
/// </summary>
public class AlicuotaIvaConfiguration : IEntityTypeConfiguration<AlicuotaIva>
{
    public void Configure(EntityTypeBuilder<AlicuotaIva> builder)
    {
        builder.ToTable("alicuotas_iva");

        builder.HasKey(a => a.Id);

        builder.Property(a => a.Id)
            .HasColumnName("id_alicuota_iva")
            .UseIdentityByDefaultColumn();

        builder.Property(a => a.Nombre)
            .HasColumnName("nombre")
            .HasColumnType("citext")
            .HasMaxLength(30)
            .IsRequired();

        builder.Property(a => a.Porcentaje)
            .HasColumnName("porcentaje")
            .HasColumnType("numeric(5,2)")
            .IsRequired();

        builder.Property(a => a.CodigoAfip).HasColumnName("codigo_afip");
        builder.Property(a => a.Activo).HasColumnName("activo").HasDefaultValue(true).IsRequired();

        builder.Property(a => a.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(a => a.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(a => a.DeletedAt).HasColumnName("deleted_at");

        builder.Ignore(a => a.EstaEliminada);

        // Decisión del usuario (2026-08-01, DB CHANGE GATE #4): doc 10 no pedía unicidad acá,
        // pero dos alícuotas con el mismo nombre visible ("21%" repetido) no tiene sentido de
        // negocio y confundiría cualquier selector.
        builder.HasIndex(a => a.Nombre)
            .HasDatabaseName("ux_alicuotas_iva_nombre")
            .HasFilter("deleted_at IS NULL")
            .IsUnique();
    }
}
