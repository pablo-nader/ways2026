using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Catalogos;
using Ways.Domain.Compras;
using Ways.Domain.Organizacion;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

/// <summary>
/// Mapea <see cref="AlicuotaComprobanteCompra"/>. Child scope: <c>id_tenant</c> únicamente, sin FK
/// propia a <c>puntos_venta</c> (se deriva del comprobante padre) — mismo criterio que
/// <see cref="ItemComprobanteCompraConfiguration"/>.
/// </summary>
public class AlicuotaComprobanteCompraConfiguration : IEntityTypeConfiguration<AlicuotaComprobanteCompra>
{
    public void Configure(EntityTypeBuilder<AlicuotaComprobanteCompra> builder)
    {
        builder.ToTable("alicuotas_comprobante_compra", t =>
        {
            t.HasCheckConstraint(
                "ck_alicuotas_comprobante_compra_importes_no_negativos",
                "neto >= 0 AND iva >= 0");
        });

        builder.HasKey(a => a.Id).HasName("pk_alicuotas_comprobante_compra");

        builder.Property(a => a.Id)
            .HasColumnName("id_alicuota_comprobante_compra")
            .UseIdentityByDefaultColumn();

        builder.Property(a => a.IdTenant).HasColumnName("id_tenant").IsRequired();
        builder.Property(a => a.IdComprobanteCompra).HasColumnName("id_comprobante_compra").IsRequired();
        builder.Property(a => a.IdAlicuotaIva).HasColumnName("id_alicuota_iva").IsRequired();

        builder.Property(a => a.Porcentaje).HasColumnName("porcentaje").HasColumnType("numeric(5,2)").IsRequired();
        builder.Property(a => a.Neto).HasColumnName("neto").HasColumnType("numeric(14,2)").IsRequired();
        builder.Property(a => a.Iva).HasColumnName("iva").HasColumnType("numeric(14,2)").IsRequired();

        builder.Property(a => a.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(a => a.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(a => a.DeletedAt).HasColumnName("deleted_at");

        builder.Ignore(a => a.EstaEliminada);

        builder.HasIndex(a => new { a.IdComprobanteCompra, a.IdAlicuotaIva })
            .HasDatabaseName("ux_alicuotas_comprobante_compra_alicuota")
            .IsUnique();

        builder.HasIndex(a => a.IdTenant).HasDatabaseName("ix_alicuotas_comprobante_compra_tenant");
        builder.HasIndex(a => new { a.IdComprobanteCompra, a.IdTenant }).HasDatabaseName("ix_alicuotas_comprobante_compra_comprobante");
        builder.HasIndex(a => a.IdAlicuotaIva).HasDatabaseName("ix_alicuotas_comprobante_compra_alicuota_iva");

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(a => a.IdTenant)
            .HasConstraintName("fk_alicuotas_comprobante_compra_tenant")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<ComprobanteCompra>()
            .WithMany()
            .HasForeignKey(a => new { a.IdComprobanteCompra, a.IdTenant })
            .HasPrincipalKey(c => new { c.Id, c.IdTenant })
            .HasConstraintName("fk_alicuotas_comprobante_compra_comprobante")
            .OnDelete(DeleteBehavior.Restrict);

        // alicuotas_iva es global (ADR-11) — FK simple, sin id_tenant.
        builder.HasOne<AlicuotaIva>()
            .WithMany()
            .HasForeignKey(a => a.IdAlicuotaIva)
            .HasConstraintName("fk_alicuotas_comprobante_compra_alicuota_iva")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
