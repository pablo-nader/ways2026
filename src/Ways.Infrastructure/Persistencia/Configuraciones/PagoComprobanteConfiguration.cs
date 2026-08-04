using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Catalogos;
using Ways.Domain.Organizacion;
using Ways.Domain.Ventas;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

/// <summary>
/// Mapea <see cref="PagoComprobante"/> (design: Table Shapes — write path A). Las CHECKs
/// <c>ck_pagos_comprobante_vuelto_no_negativo</c> e <c>ck_pagos_comprobante_importe_no_negativo</c>
/// son defensa en profundidad — <c>ValidadorDePagos</c> ya rechaza cualquier vuelto o importe
/// negativo aritméticamente antes de llegar acá (misma familia que
/// <c>ck_comprobantes_venta_numero_positivo</c>).
/// </summary>
public class PagoComprobanteConfiguration : IEntityTypeConfiguration<PagoComprobante>
{
    public void Configure(EntityTypeBuilder<PagoComprobante> builder)
    {
        builder.ToTable("pagos_comprobante", t =>
        {
            t.HasCheckConstraint("ck_pagos_comprobante_vuelto_no_negativo", "vuelto >= 0");
            t.HasCheckConstraint("ck_pagos_comprobante_importe_no_negativo", "importe >= 0");
        });

        builder.HasKey(p => p.Id).HasName("pk_pagos_comprobante");

        builder.Property(p => p.Id)
            .HasColumnName("id_pago")
            .UseIdentityByDefaultColumn();

        builder.Property(p => p.IdTenant)
            .HasColumnName("id_tenant")
            .IsRequired();

        // Referenciada por movimientos_cuenta_corriente.id_pago_comprobante (design: Table
        // Shapes — write path A, "referenced by the CC movimiento").
        builder.HasAlternateKey(p => new { p.Id, p.IdTenant })
            .HasName("ak_pagos_comprobante_id_pago_id_tenant");

        builder.Property(p => p.IdComprobanteVenta).HasColumnName("id_comprobante_venta").IsRequired();
        builder.Property(p => p.IdMedioPago).HasColumnName("id_medio_pago").IsRequired();
        builder.Property(p => p.Importe).HasColumnName("importe").HasColumnType("numeric(14,2)").IsRequired();
        builder.Property(p => p.Referencia).HasColumnName("referencia").HasColumnType("text");

        builder.Property(p => p.Vuelto)
            .HasColumnName("vuelto")
            .HasColumnType("numeric(14,2)")
            .HasDefaultValue(0m)
            .IsRequired();

        builder.Property(p => p.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(p => p.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(p => p.DeletedAt).HasColumnName("deleted_at");

        builder.Ignore(p => p.EstaEliminada);

        builder.HasIndex(p => p.IdTenant).HasDatabaseName("ix_pagos_comprobante_tenant");
        builder.HasIndex(p => new { p.IdComprobanteVenta, p.IdTenant }).HasDatabaseName("ix_pagos_comprobante_comprobante");
        builder.HasIndex(p => new { p.IdMedioPago, p.IdTenant }).HasDatabaseName("ix_pagos_comprobante_medio_pago");

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(p => p.IdTenant)
            .HasConstraintName("fk_pagos_comprobante_tenant")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<ComprobanteVenta>()
            .WithMany()
            .HasForeignKey(p => new { p.IdComprobanteVenta, p.IdTenant })
            .HasPrincipalKey(c => new { c.Id, c.IdTenant })
            .HasConstraintName("fk_pagos_comprobante_comprobante")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<MedioPago>()
            .WithMany()
            .HasForeignKey(p => new { p.IdMedioPago, p.IdTenant })
            .HasPrincipalKey(m => new { m.Id, m.IdTenant })
            .HasConstraintName("fk_pagos_comprobante_medio_pago")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
