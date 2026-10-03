using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Compras;
using Ways.Domain.Organizacion;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

/// <summary>
/// Mapea <see cref="PercepcionComprobanteCompra"/>. Child scope: <c>id_tenant</c> únicamente, sin
/// FK propia a <c>puntos_venta</c> (se deriva del comprobante padre) — mismo criterio que
/// <see cref="AlicuotaComprobanteCompraConfiguration"/>.
/// </summary>
public class PercepcionComprobanteCompraConfiguration : IEntityTypeConfiguration<PercepcionComprobanteCompra>
{
    public void Configure(EntityTypeBuilder<PercepcionComprobanteCompra> builder)
    {
        builder.ToTable("percepciones_comprobante_compra", t =>
        {
            t.HasCheckConstraint(
                "ck_percepciones_comprobante_compra_tipo",
                "tipo IN ('iibb', 'iva')");

            t.HasCheckConstraint(
                "ck_percepciones_comprobante_compra_importes_no_negativos",
                "base_imponible >= 0 AND importe >= 0");

            t.HasCheckConstraint(
                "ck_percepciones_comprobante_compra_alicuota_rango",
                "alicuota >= 0 AND alicuota <= 100");
        });

        builder.HasKey(p => p.Id).HasName("pk_percepciones_comprobante_compra");

        builder.Property(p => p.Id)
            .HasColumnName("id_percepcion_comprobante_compra")
            .UseIdentityByDefaultColumn();

        builder.Property(p => p.IdTenant).HasColumnName("id_tenant").IsRequired();
        builder.Property(p => p.IdComprobanteCompra).HasColumnName("id_comprobante_compra").IsRequired();

        builder.Property(p => p.Tipo).HasColumnName("tipo").HasColumnType("text").IsRequired();
        builder.Property(p => p.BaseImponible).HasColumnName("base_imponible").HasColumnType("numeric(14,2)").IsRequired();
        builder.Property(p => p.Alicuota).HasColumnName("alicuota").HasColumnType("numeric(6,3)").IsRequired();
        builder.Property(p => p.Importe).HasColumnName("importe").HasColumnType("numeric(14,2)").IsRequired();

        builder.Property(p => p.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(p => p.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(p => p.DeletedAt).HasColumnName("deleted_at");

        builder.Ignore(p => p.EstaEliminada);

        builder.HasIndex(p => new { p.IdComprobanteCompra, p.Tipo })
            .HasDatabaseName("ux_percepciones_comprobante_compra_tipo")
            .IsUnique();

        builder.HasIndex(p => p.IdTenant).HasDatabaseName("ix_percepciones_comprobante_compra_tenant");
        builder.HasIndex(p => new { p.IdComprobanteCompra, p.IdTenant })
            .HasDatabaseName("ix_percepciones_comprobante_compra_comprobante");

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(p => p.IdTenant)
            .HasConstraintName("fk_percepciones_comprobante_compra_tenant")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<ComprobanteCompra>()
            .WithMany()
            .HasForeignKey(p => new { p.IdComprobanteCompra, p.IdTenant })
            .HasPrincipalKey(c => new { c.Id, c.IdTenant })
            .HasConstraintName("fk_percepciones_comprobante_compra_comprobante")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
