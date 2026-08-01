using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Catalogos;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

/// <summary>
/// <c>[global]</c> (ADR-11, gate #4): sin <c>id_tenant</c>, sin RLS.
/// </summary>
public class TipoComprobanteConfiguration : IEntityTypeConfiguration<TipoComprobante>
{
    public void Configure(EntityTypeBuilder<TipoComprobante> builder)
    {
        builder.ToTable("tipos_comprobante");

        builder.HasKey(t => t.Id);

        builder.Property(t => t.Id)
            .HasColumnName("id_tipo_comprobante")
            .UseIdentityByDefaultColumn();

        builder.Property(t => t.Clase)
            .HasColumnName("clase")
            .HasColumnType("clase_comprobante")
            .IsRequired();

        builder.Property(t => t.Codigo)
            .HasColumnName("codigo")
            .HasColumnType("citext")
            .HasMaxLength(10)
            .IsRequired();

        builder.Property(t => t.Nombre)
            .HasColumnName("nombre")
            .HasColumnType("citext")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(t => t.Letra).HasColumnName("letra").HasColumnType("char(1)");
        builder.Property(t => t.Signo).HasColumnName("signo").IsRequired();
        builder.Property(t => t.DiscriminaIva).HasColumnName("discrimina_iva").IsRequired();
        builder.Property(t => t.EsFiscal).HasColumnName("es_fiscal").IsRequired();
        builder.Property(t => t.AfectaStock).HasColumnName("afecta_stock").IsRequired();
        builder.Property(t => t.CodigoAfip).HasColumnName("codigo_afip");
        builder.Property(t => t.Activo).HasColumnName("activo").HasDefaultValue(true).IsRequired();

        builder.Property(t => t.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(t => t.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(t => t.DeletedAt).HasColumnName("deleted_at");

        builder.Ignore(t => t.EstaEliminada);

        builder.HasIndex(t => t.Codigo)
            .HasDatabaseName("ux_tipos_comprobante_codigo")
            .HasFilter("deleted_at IS NULL")
            .IsUnique();
    }
}
