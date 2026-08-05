using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Catalogos;
using Ways.Domain.Compras;
using Ways.Domain.Organizacion;
using Ways.Domain.Proveedores;
using Ways.Domain.Usuarios;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

/// <summary>
/// Mapea <see cref="ComprobanteCompra"/> (design: Table Shapes — A). Las dos CHECKs son defensa
/// en profundidad — <c>CalculadorDeCompra</c>/<c>ServicioDeCompras</c> (Slice 2) ya garantizan
/// ambos invariantes en el camino de servicio, misma familia que
/// <c>ck_comprobantes_venta_numero_positivo</c>.
/// </summary>
public class ComprobanteCompraConfiguration : IEntityTypeConfiguration<ComprobanteCompra>
{
    public void Configure(EntityTypeBuilder<ComprobanteCompra> builder)
    {
        builder.ToTable("comprobantes_compra", t =>
        {
            // design: Table Shapes — A. "confirmada sin identidad de factura" queda
            // irrepresentable a nivel esquema: un anulada FUE confirmada primero, así que
            // también la satisface.
            t.HasCheckConstraint(
                "ck_comprobantes_compra_confirmada_completa",
                "estado = 'borrador' OR (numero_externo IS NOT NULL AND fecha_comprobante IS NOT NULL AND fecha_recepcion IS NOT NULL)");

            // >= 0, no > 0: un remito totalmente bonificado que totaliza cero es real (design:
            // Table Shapes — A).
            t.HasCheckConstraint(
                "ck_comprobantes_compra_totales_no_negativos",
                "subtotal >= 0 AND descuento_total >= 0 AND total >= 0 AND (iva_total IS NULL OR iva_total >= 0)");
        });

        builder.HasKey(c => c.Id).HasName("pk_comprobantes_compra");

        builder.Property(c => c.Id)
            .HasColumnName("id_comprobante_compra")
            .UseIdentityByDefaultColumn();

        builder.Property(c => c.IdTenant)
            .HasColumnName("id_tenant")
            .IsRequired();

        // Habilita las FKs compuestas de items_comprobante_compra (mismo patrón que
        // ComprobanteVentaConfiguration).
        builder.HasAlternateKey(c => new { c.Id, c.IdTenant })
            .HasName("ak_comprobantes_compra_id_comprobante_compra_id_tenant");

        builder.Property(c => c.IdProveedor).HasColumnName("id_proveedor").IsRequired();
        builder.Property(c => c.IdTipoComprobante).HasColumnName("id_tipo_comprobante").IsRequired();

        builder.Property(c => c.NumeroExterno).HasColumnName("numero_externo").HasColumnType("citext");
        builder.Property(c => c.FechaComprobante).HasColumnName("fecha_comprobante").HasColumnType("date");
        builder.Property(c => c.FechaRecepcion).HasColumnName("fecha_recepcion");

        builder.Property(c => c.IdPuntoVenta).HasColumnName("id_punto_venta").IsRequired();
        builder.Property(c => c.IdEmpleado).HasColumnName("id_empleado").IsRequired();

        builder.Property(c => c.Subtotal).HasColumnName("subtotal").HasColumnType("numeric(14,2)").IsRequired();
        builder.Property(c => c.DescuentoTotal).HasColumnName("descuento_total").HasColumnType("numeric(14,2)").IsRequired();
        builder.Property(c => c.Total).HasColumnName("total").HasColumnType("numeric(14,2)").IsRequired();
        builder.Property(c => c.IvaTotal).HasColumnName("iva_total").HasColumnType("numeric(14,2)");

        builder.Property(c => c.Observaciones).HasColumnName("observaciones").HasColumnType("text");

        builder.Property(c => c.Estado)
            .HasColumnName("estado")
            .HasColumnType("estado_compra")
            .IsRequired();

        builder.Property(c => c.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(c => c.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(c => c.DeletedAt).HasColumnName("deleted_at");

        builder.Ignore(c => c.EstaEliminada);

        // ux_comprobantes_compra_numero_externo: partial UNIQUE (design: Table Shapes — A, DB
        // CHANGE GATE). Excluye estado='anulada' (una factura mal cargada anulada se puede
        // reingresar) y numero_externo NULL (mientras es borrador). ⚠ Su nombre contiene
        // "_numero" — ManejadorDeErrores tiene que resolverla por nombre EXACTO ANTES de
        // ClasificarUnicidad (design: Backstop Map, "ordering trap"), el mismo tratamiento que
        // ux_comprobantes_venta_numero.
        builder.HasIndex(c => new { c.IdTenant, c.IdProveedor, c.IdTipoComprobante, c.NumeroExterno })
            .HasDatabaseName("ux_comprobantes_compra_numero_externo")
            .IsUnique()
            .HasFilter("estado <> 'anulada' AND numero_externo IS NOT NULL");

        builder.HasIndex(c => c.IdTenant).HasDatabaseName("ix_comprobantes_compra_tenant");
        builder.HasIndex(c => new { c.IdProveedor, c.IdTenant }).HasDatabaseName("ix_comprobantes_compra_proveedor");

        builder.HasIndex(c => new { c.IdPuntoVenta, c.IdTenant, c.FechaRecepcion })
            .HasDatabaseName("ix_comprobantes_compra_punto_venta_fecha");

        builder.HasIndex(c => c.IdTipoComprobante).HasDatabaseName("ix_comprobantes_compra_tipo_comprobante");
        builder.HasIndex(c => c.IdEmpleado).HasDatabaseName("ix_comprobantes_compra_empleado");

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(c => c.IdTenant)
            .HasConstraintName("fk_comprobantes_compra_tenant")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Proveedor>()
            .WithMany()
            .HasForeignKey(c => new { c.IdProveedor, c.IdTenant })
            .HasPrincipalKey(p => new { p.Id, p.IdTenant })
            .HasConstraintName("fk_comprobantes_compra_proveedor")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<PuntoVenta>()
            .WithMany()
            .HasForeignKey(c => new { c.IdPuntoVenta, c.IdTenant })
            .HasPrincipalKey(p => new { p.Id, p.IdTenant })
            .HasConstraintName("fk_comprobantes_compra_punto_venta")
            .OnDelete(DeleteBehavior.Restrict);

        // tipos_comprobante es global (ADR-11) — FK simple, sin id_tenant. clase = compra se
        // exige en el servicio (Slice 2), no acá.
        builder.HasOne<TipoComprobante>()
            .WithMany()
            .HasForeignKey(c => c.IdTipoComprobante)
            .HasConstraintName("fk_comprobantes_compra_tipo_comprobante")
            .OnDelete(DeleteBehavior.Restrict);

        // id_empleado: FK SIMPLE, misma deviación deliberada que
        // ComprobanteVentaConfiguration.fk_comprobantes_venta_empleado documenta.
        builder.HasOne<Usuario>()
            .WithMany()
            .HasForeignKey(c => c.IdEmpleado)
            .HasConstraintName("fk_comprobantes_compra_empleado")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
