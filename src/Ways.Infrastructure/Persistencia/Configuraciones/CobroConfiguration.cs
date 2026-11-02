using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Catalogos;
using Ways.Domain.MercadoPago;
using Ways.Domain.Organizacion;
using Ways.Domain.Ventas;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

/// <summary>
/// Mapea <see cref="Cobro"/> (design: Table Shapes — write path A). Las CHECKs
/// <c>ck_cobros_vuelto_no_negativo</c> e <c>ck_cobros_importe_no_negativo</c>
/// son defensa en profundidad — <c>ValidadorDePagos</c> ya rechaza cualquier vuelto o importe
/// negativo aritméticamente antes de llegar acá (misma familia que
/// <c>ck_comprobantes_venta_numero_positivo</c>).
/// </summary>
public class CobroConfiguration : IEntityTypeConfiguration<Cobro>
{
    public void Configure(EntityTypeBuilder<Cobro> builder)
    {
        builder.ToTable("cobros", t =>
        {
            t.HasCheckConstraint("ck_cobros_vuelto_no_negativo", "vuelto >= 0");
            t.HasCheckConstraint("ck_cobros_importe_no_negativo", "importe >= 0");
            t.HasCheckConstraint(
                "ck_cobros_verificacion_mercadopago",
                "verificacion_mercadopago IS NULL OR verificacion_mercadopago IN ('mercadopago', 'sin_verificar', 'verificado_manualmente')");
            t.HasCheckConstraint(
                "ck_cobros_verificacion_mercadopago_cobro",
                "(id_cobro_mercadopago IS NOT NULL) = (verificacion_mercadopago IS NOT DISTINCT FROM 'mercadopago')");
        });

        builder.HasKey(p => p.Id).HasName("pk_cobros");

        builder.Property(p => p.Id)
            .HasColumnName("id_cobro")
            .UseIdentityByDefaultColumn();

        builder.Property(p => p.IdTenant)
            .HasColumnName("id_tenant")
            .IsRequired();

        // Referenciada por movimientos_cuenta_corriente.id_cobro (design: Table
        // Shapes — write path A, "referenced by the CC movimiento").
        builder.HasAlternateKey(p => new { p.Id, p.IdTenant })
            .HasName("ak_cobros_id_cobro_id_tenant");

        builder.Property(p => p.IdComprobanteVenta).HasColumnName("id_comprobante_venta").IsRequired();
        builder.Property(p => p.IdMedioPago).HasColumnName("id_medio_pago").IsRequired();
        builder.Property(p => p.Importe).HasColumnName("importe").HasColumnType("numeric(14,2)").IsRequired();
        builder.Property(p => p.Referencia).HasColumnName("referencia").HasColumnType("text");

        builder.Property(p => p.Vuelto)
            .HasColumnName("vuelto")
            .HasColumnType("numeric(14,2)")
            .HasDefaultValue(0m)
            .IsRequired();

        builder.Property(p => p.IdCobroMercadoPago).HasColumnName("id_cobro_mercadopago");
        builder.Property(p => p.VerificacionMercadoPago).HasColumnName("verificacion_mercadopago").HasMaxLength(20);

        builder.Property(p => p.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(p => p.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(p => p.DeletedAt).HasColumnName("deleted_at");

        builder.Ignore(p => p.EstaEliminada);

        builder.HasIndex(p => p.IdTenant).HasDatabaseName("ix_cobros_tenant");
        builder.HasIndex(p => new { p.IdComprobanteVenta, p.IdTenant }).HasDatabaseName("ix_cobros_comprobante");
        builder.HasIndex(p => new { p.IdMedioPago, p.IdTenant }).HasDatabaseName("ix_cobros_medio_pago");
        builder.HasIndex(p => new { p.IdCobroMercadoPago, p.IdTenant }).HasDatabaseName("ix_cobros_cobro_mercadopago");

        // El listado de pagos aprobados a mano sin verificar recorre solo estas filas, pocas frente al total de pagos.
        builder.HasIndex(p => new { p.IdTenant, p.IdComprobanteVenta })
            .HasDatabaseName("ix_cobros_sin_verificar")
            .HasFilter("verificacion_mercadopago = 'sin_verificar' AND deleted_at IS NULL");

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(p => p.IdTenant)
            .HasConstraintName("fk_cobros_tenant")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<ComprobanteVenta>()
            .WithMany()
            .HasForeignKey(p => new { p.IdComprobanteVenta, p.IdTenant })
            .HasPrincipalKey(c => new { c.Id, c.IdTenant })
            .HasConstraintName("fk_cobros_comprobante")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<MedioPago>()
            .WithMany()
            .HasForeignKey(p => new { p.IdMedioPago, p.IdTenant })
            .HasPrincipalKey(m => new { m.Id, m.IdTenant })
            .HasConstraintName("fk_cobros_medio_pago")
            .OnDelete(DeleteBehavior.Restrict);

        // El cobro de Mercado Pago que pagó este medio. La venta lo valida bajo FOR UPDATE; la FK es el respaldo (23503 -> 400).
        builder.HasOne<CobroMercadoPago>()
            .WithMany()
            .HasForeignKey(p => new { p.IdCobroMercadoPago, p.IdTenant })
            .HasPrincipalKey(c => new { c.Id, c.IdTenant })
            .HasConstraintName("fk_cobros_cobro_mercadopago")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
