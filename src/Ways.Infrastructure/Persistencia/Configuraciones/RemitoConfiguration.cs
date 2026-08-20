using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Clientes;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

/// <summary>
/// Mapea <see cref="Remito"/> (proposal: Modelo de datos propuesto — §E, DB CHANGE GATE
/// aprobado). Shaped on <c>PresupuestoConfiguration.cs</c>. Todos los índices de soporte se
/// declaran a mano con nombres doc-10 — cero sorpresa de <c>ForeignKeyIndexConvention</c>
/// (conteo vinculante: 7 índices en esta tabla, incl. el implícito de la AK).
/// </summary>
public class RemitoConfiguration : IEntityTypeConfiguration<Remito>
{
    public void Configure(EntityTypeBuilder<Remito> builder)
    {
        builder.ToTable("remitos", t =>
        {
            // CHECK 3 (proposal §E): numero y fecha_salida llegan JUNTOS, y todo estado
            // distinto de borrador/anulado tiene numero — mismo criterio que
            // ck_presupuestos_envio_completo (sin vencimiento, un remito no expira).
            t.HasCheckConstraint(
                "ck_remitos_salida_completa",
                "((numero IS NULL) = (fecha_salida IS NULL)) AND (estado IN ('borrador','anulado') OR numero IS NOT NULL)");

            // CHECK 4 (proposal §E): facturado y su link viajan JUNTOS, en las dos direcciones —
            // el desligue de la anulación del TXR (slice 6) limpia estado y
            // id_comprobante_venta a la vez o esta CHECK tira 23514.
            t.HasCheckConstraint(
                "ck_remitos_facturacion",
                "(id_comprobante_venta IS NULL) = (estado <> 'facturado')");
        });

        builder.HasKey(r => r.Id).HasName("pk_remitos");

        builder.Property(r => r.Id)
            .HasColumnName("id_remito")
            .UseIdentityByDefaultColumn();

        builder.Property(r => r.IdTenant)
            .HasColumnName("id_tenant")
            .IsRequired();

        // Habilita la FK compuesta de items_remito y el soporte de FK 24 de
        // movimientos_stock.id_remito (§H) — mismo patrón que
        // ak_presupuestos_id_presupuesto_id_tenant.
        builder.HasAlternateKey(r => new { r.Id, r.IdTenant })
            .HasName("ak_remitos_id_remito_id_tenant");

        builder.Property(r => r.IdPuntoVenta).HasColumnName("id_punto_venta").IsRequired();
        builder.Property(r => r.IdCliente).HasColumnName("id_cliente").IsRequired();
        builder.Property(r => r.IdEmpleado).HasColumnName("id_empleado").IsRequired();

        builder.Property(r => r.Numero).HasColumnName("numero").HasColumnType("bigint");

        builder.Property(r => r.FechaEmision).HasColumnName("fecha_emision").IsRequired();
        builder.Property(r => r.FechaSalida).HasColumnName("fecha_salida");

        builder.Property(r => r.DireccionEntrega).HasColumnName("direccion_entrega").HasColumnType("text");
        builder.Property(r => r.Observaciones).HasColumnName("observaciones").HasColumnType("text");

        builder.Property(r => r.Subtotal).HasColumnName("subtotal").HasColumnType("numeric(14,2)").IsRequired();
        builder.Property(r => r.DescuentoTotal).HasColumnName("descuento_total").HasColumnType("numeric(14,2)").IsRequired();
        builder.Property(r => r.Total).HasColumnName("total").HasColumnType("numeric(14,2)").IsRequired();

        builder.Property(r => r.Estado)
            .HasColumnName("estado")
            .HasColumnType("estado_remito")
            .IsRequired();

        builder.Property(r => r.IdComprobanteVenta).HasColumnName("id_comprobante_venta");

        builder.Property(r => r.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(r => r.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(r => r.DeletedAt).HasColumnName("deleted_at");

        builder.Ignore(r => r.EstaEliminada);

        builder.HasIndex(r => r.IdTenant).HasDatabaseName("ix_remitos_tenant");

        // Cubre la listing por PV Y el soporte de FK 12 por prefijo de columna líder — mismo
        // criterio que ix_presupuestos_punto_venta_fecha/ix_comprobantes_venta_punto_venta_fecha.
        builder.HasIndex(r => new { r.IdPuntoVenta, r.IdTenant, r.FechaEmision })
            .HasDatabaseName("ix_remitos_punto_venta_fecha");

        // Per-customer listing + la lectura de la consolidación ("los remitos emitidos de este
        // cliente, sin ligar") + soporte de FK 13.
        builder.HasIndex(r => new { r.IdCliente, r.IdTenant }).HasDatabaseName("ix_remitos_cliente");

        // Simple, no compuesto: un índice liderado por id_tenant NO cubre una FK simple (mismo
        // criterio que ix_presupuestos_empleado, la trampa documentada de la enmienda etapa 14).
        builder.HasIndex(r => r.IdEmpleado).HasDatabaseName("ix_remitos_empleado");

        // Soporte de FK 15 + la lectura "qué remitos cubre esta factura".
        builder.HasIndex(r => new { r.IdComprobanteVenta, r.IdTenant }).HasDatabaseName("ix_remitos_comprobante_venta");

        // ux_remitos_numero: UNIQUE PARCIAL (proposal §E, gate §A) WHERE numero IS NOT NULL — un
        // borrador no tiene número. ⚠ Su nombre contiene "_numero" — ManejadorDeErrores tiene
        // que resolverla por nombre EXACTO ANTES de ClasificarUnicidad, QUINTA ocurrencia del
        // ordering trap (design decisión 18).
        builder.HasIndex(r => new { r.IdTenant, r.IdPuntoVenta, r.Numero })
            .HasDatabaseName("ux_remitos_numero")
            .IsUnique()
            .HasFilter("numero IS NOT NULL");

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(r => r.IdTenant)
            .HasConstraintName("fk_remitos_tenant")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<PuntoVenta>()
            .WithMany()
            .HasForeignKey(r => new { r.IdPuntoVenta, r.IdTenant })
            .HasPrincipalKey(pv => new { pv.Id, pv.IdTenant })
            .HasConstraintName("fk_remitos_punto_venta")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Cliente>()
            .WithMany()
            .HasForeignKey(r => new { r.IdCliente, r.IdTenant })
            .HasPrincipalKey(c => new { c.Id, c.IdTenant })
            .HasConstraintName("fk_remitos_cliente")
            .OnDelete(DeleteBehavior.Restrict);

        // FK simple, deliberada (proposal §E): una AK compuesta forzaría id_tenant NOT NULL en
        // usuarios y rompería el sentinel NULL de staff de plataforma — mismo criterio que
        // fk_presupuestos_empleado/fk_comprobantes_venta_empleado.
        builder.HasOne<Usuario>()
            .WithMany()
            .HasForeignKey(r => r.IdEmpleado)
            .HasConstraintName("fk_remitos_empleado")
            .OnDelete(DeleteBehavior.Restrict);

        // Nullable, MATCH SIMPLE (default) — contra la AK YA existente de ComprobanteVenta
        // (ak_comprobantes_venta_id_comprobante_venta_id_tenant, verificado
        // ComprobanteVentaConfiguration.cs:40). NULL salvo en estado facturado
        // (ck_remitos_facturacion).
        builder.HasOne<ComprobanteVenta>()
            .WithMany()
            .HasForeignKey(r => new { r.IdComprobanteVenta, r.IdTenant })
            .HasPrincipalKey(c => new { c.Id, c.IdTenant })
            .HasConstraintName("fk_remitos_comprobante_venta")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
