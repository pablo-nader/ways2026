using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Compras;
using Ways.Domain.Organizacion;
using Ways.Domain.Pagos;
using Ways.Domain.Usuarios;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

/// <summary>
/// Mapea <see cref="AplicacionPago"/> (doc 10 §5, hija operativa de <c>pagos</c>). La CHECK es defensa en profundidad;
/// los constraint triggers <c>tg_aplicaciones_pago_no_excede_pago</c> y <c>tg_aplicaciones_pago_no_excede_compra</c> los
/// crea la migración con SQL (EF no los modela).
/// </summary>
public class AplicacionPagoConfiguration : IEntityTypeConfiguration<AplicacionPago>
{
    public void Configure(EntityTypeBuilder<AplicacionPago> builder)
    {
        builder.ToTable("aplicaciones_pago", t =>
        {
            t.HasCheckConstraint("ck_aplicaciones_pago_importe_positivo", "importe > 0");
        });

        builder.HasKey(a => a.Id).HasName("pk_aplicaciones_pago");

        builder.Property(a => a.Id)
            .HasColumnName("id_aplicacion_pago")
            .UseIdentityByDefaultColumn();

        builder.Property(a => a.IdTenant).HasColumnName("id_tenant").IsRequired();
        builder.Property(a => a.IdPago).HasColumnName("id_pago").IsRequired();
        builder.Property(a => a.IdComprobanteCompra).HasColumnName("id_comprobante_compra").IsRequired();
        builder.Property(a => a.Importe).HasColumnName("importe").HasColumnType("numeric(14,2)").IsRequired();

        builder.Property(a => a.Origen)
            .HasColumnName("origen")
            .HasColumnType("origen_aplicacion_pago")
            .IsRequired();

        builder.Property(a => a.IdEmpleado).HasColumnName("id_empleado").IsRequired();
        builder.Property(a => a.Fecha).HasColumnName("fecha").IsRequired();

        builder.Property(a => a.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(a => a.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(a => a.DeletedAt).HasColumnName("deleted_at");

        builder.Ignore(a => a.EstaEliminada);

        builder.HasIndex(a => a.IdTenant).HasDatabaseName("ix_aplicaciones_pago_tenant");
        builder.HasIndex(a => new { a.IdPago, a.IdTenant }).HasDatabaseName("ix_aplicaciones_pago_pago");
        builder.HasIndex(a => new { a.IdComprobanteCompra, a.IdTenant }).HasDatabaseName("ix_aplicaciones_pago_compra");
        builder.HasIndex(a => a.IdEmpleado).HasDatabaseName("ix_aplicaciones_pago_empleado");

        // Una sola aplicación viva por par pago-compra: aplicar más es subir el importe de esa fila.
        builder.HasIndex(a => new { a.IdPago, a.IdComprobanteCompra, a.IdTenant })
            .IsUnique()
            .HasFilter("deleted_at IS NULL")
            .HasDatabaseName("ux_aplicaciones_pago_pago_compra");

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(a => a.IdTenant)
            .HasConstraintName("fk_aplicaciones_pago_tenant")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Pago>()
            .WithMany()
            .HasForeignKey(a => new { a.IdPago, a.IdTenant })
            .HasPrincipalKey(p => new { p.Id, p.IdTenant })
            .HasConstraintName("fk_aplicaciones_pago_pago")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<ComprobanteCompra>()
            .WithMany()
            .HasForeignKey(a => new { a.IdComprobanteCompra, a.IdTenant })
            .HasPrincipalKey(c => new { c.Id, c.IdTenant })
            .HasConstraintName("fk_aplicaciones_pago_compra")
            .OnDelete(DeleteBehavior.Restrict);

        // id_empleado: FK simple, mismo motivo que PagoConfiguration.fk_pagos_empleado.
        builder.HasOne<Usuario>()
            .WithMany()
            .HasForeignKey(a => a.IdEmpleado)
            .HasConstraintName("fk_aplicaciones_pago_empleado")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
