using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

/// <summary>
/// Mapea <see cref="AplicacionCobro"/> (doc 10 §8, hija operativa de <c>comprobantes_venta</c> por el recibo y por la
/// venta). La CHECK es defensa en profundidad; los constraint triggers <c>tg_aplicaciones_cobro_no_excede_cobro</c> y
/// <c>tg_aplicaciones_cobro_no_excede_venta</c> los crea la migración con SQL (EF no los modela).
/// </summary>
public class AplicacionCobroConfiguration : IEntityTypeConfiguration<AplicacionCobro>
{
    public void Configure(EntityTypeBuilder<AplicacionCobro> builder)
    {
        builder.ToTable("aplicaciones_cobro", t =>
        {
            t.HasCheckConstraint("ck_aplicaciones_cobro_importe_positivo", "importe > 0");
        });

        builder.HasKey(a => a.Id).HasName("pk_aplicaciones_cobro");

        builder.Property(a => a.Id)
            .HasColumnName("id_aplicacion_cobro")
            .UseIdentityByDefaultColumn();

        builder.Property(a => a.IdTenant).HasColumnName("id_tenant").IsRequired();
        builder.Property(a => a.IdComprobanteCobro).HasColumnName("id_comprobante_cobro").IsRequired();
        builder.Property(a => a.IdComprobanteVenta).HasColumnName("id_comprobante_venta").IsRequired();
        builder.Property(a => a.Importe).HasColumnName("importe").HasColumnType("numeric(14,2)").IsRequired();
        builder.Property(a => a.Origen)
            .HasColumnName("origen")
            .HasColumnType("origen_aplicacion_cobro")
            .HasDefaultValueSql("'manual'::origen_aplicacion_cobro")
            .IsRequired();

        builder.Property(a => a.IdEmpleado).HasColumnName("id_empleado").IsRequired();
        builder.Property(a => a.Fecha).HasColumnName("fecha").IsRequired();

        builder.Property(a => a.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(a => a.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(a => a.DeletedAt).HasColumnName("deleted_at");

        builder.Ignore(a => a.EstaEliminada);

        builder.HasIndex(a => a.IdTenant).HasDatabaseName("ix_aplicaciones_cobro_tenant");
        builder.HasIndex(a => new { a.IdComprobanteCobro, a.IdTenant }).HasDatabaseName("ix_aplicaciones_cobro_cobro");
        builder.HasIndex(a => new { a.IdComprobanteVenta, a.IdTenant }).HasDatabaseName("ix_aplicaciones_cobro_venta");
        builder.HasIndex(a => a.IdEmpleado).HasDatabaseName("ix_aplicaciones_cobro_empleado");

        // Una sola aplicación viva por par recibo-venta: aplicar más es subir el importe de esa fila.
        builder.HasIndex(a => new { a.IdComprobanteCobro, a.IdComprobanteVenta, a.IdTenant })
            .IsUnique()
            .HasFilter("deleted_at IS NULL")
            .HasDatabaseName("ux_aplicaciones_cobro_cobro_venta");

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(a => a.IdTenant)
            .HasConstraintName("fk_aplicaciones_cobro_tenant")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<ComprobanteVenta>()
            .WithMany()
            .HasForeignKey(a => new { a.IdComprobanteCobro, a.IdTenant })
            .HasPrincipalKey(c => new { c.Id, c.IdTenant })
            .HasConstraintName("fk_aplicaciones_cobro_cobro")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<ComprobanteVenta>()
            .WithMany()
            .HasForeignKey(a => new { a.IdComprobanteVenta, a.IdTenant })
            .HasPrincipalKey(c => new { c.Id, c.IdTenant })
            .HasConstraintName("fk_aplicaciones_cobro_venta")
            .OnDelete(DeleteBehavior.Restrict);

        // id_empleado: FK simple, mismo motivo que AplicacionPagoConfiguration.fk_aplicaciones_pago_empleado.
        builder.HasOne<Usuario>()
            .WithMany()
            .HasForeignKey(a => a.IdEmpleado)
            .HasConstraintName("fk_aplicaciones_cobro_empleado")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
