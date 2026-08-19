using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Clientes;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

/// <summary>
/// Mapea <see cref="Presupuesto"/> (proposal: Modelo de datos propuesto — §C, DB CHANGE GATE
/// aprobado). Shaped on <c>OrdenCompraConfiguration.cs</c>. Todos los índices de soporte se
/// declaran a mano con nombres doc-10 — cero sorpresa de <c>ForeignKeyIndexConvention</c>
/// (conteo vinculante: 6 índices en esta tabla, incl. el implícito de la AK).
/// </summary>
public class PresupuestoConfiguration : IEntityTypeConfiguration<Presupuesto>
{
    public void Configure(EntityTypeBuilder<Presupuesto> builder)
    {
        builder.ToTable("presupuestos", t =>
        {
            // CHECK 1 (proposal §C): numero, fecha_envio y vencimiento llegan JUNTOS, y todo
            // estado distinto de borrador/anulado tiene los tres. anulado se admite sin ellos
            // porque un borrador puede anularse antes de ser enviado (mismo criterio que
            // ck_ordenes_compra_envio_completo).
            t.HasCheckConstraint(
                "ck_presupuestos_envio_completo",
                "((numero IS NULL) = (fecha_envio IS NULL)) AND ((numero IS NULL) = (vencimiento IS NULL)) AND (estado IN ('borrador','anulado') OR numero IS NOT NULL)");
        });

        builder.HasKey(p => p.Id).HasName("pk_presupuestos");

        builder.Property(p => p.Id)
            .HasColumnName("id_presupuesto")
            .UseIdentityByDefaultColumn();

        builder.Property(p => p.IdTenant)
            .HasColumnName("id_tenant")
            .IsRequired();

        // Habilita las FKs compuestas de items_presupuesto y de la ALTER de comprobantes_venta
        // (id_presupuesto_origen) — mismo patrón que ak_ordenes_compra_id_orden_compra_id_tenant.
        builder.HasAlternateKey(p => new { p.Id, p.IdTenant })
            .HasName("ak_presupuestos_id_presupuesto_id_tenant");

        builder.Property(p => p.IdPuntoVenta).HasColumnName("id_punto_venta").IsRequired();
        builder.Property(p => p.IdCliente).HasColumnName("id_cliente").IsRequired();
        builder.Property(p => p.IdEmpleado).HasColumnName("id_empleado").IsRequired();

        builder.Property(p => p.Numero).HasColumnName("numero").HasColumnType("bigint");

        builder.Property(p => p.FechaEmision).HasColumnName("fecha_emision").IsRequired();
        builder.Property(p => p.FechaEnvio).HasColumnName("fecha_envio");
        builder.Property(p => p.Vencimiento).HasColumnName("vencimiento").HasColumnType("date");

        builder.Property(p => p.Observaciones).HasColumnName("observaciones").HasColumnType("text");

        builder.Property(p => p.Subtotal).HasColumnName("subtotal").HasColumnType("numeric(14,2)").IsRequired();
        builder.Property(p => p.DescuentoTotal).HasColumnName("descuento_total").HasColumnType("numeric(14,2)").IsRequired();
        builder.Property(p => p.Total).HasColumnName("total").HasColumnType("numeric(14,2)").IsRequired();

        builder.Property(p => p.Estado)
            .HasColumnName("estado")
            .HasColumnType("estado_presupuesto")
            .IsRequired();

        builder.Property(p => p.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(p => p.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(p => p.DeletedAt).HasColumnName("deleted_at");

        builder.Ignore(p => p.EstaEliminada);

        builder.HasIndex(p => p.IdTenant).HasDatabaseName("ix_presupuestos_tenant");

        // Cubre la listing por PV Y el soporte de FK 2 por prefijo de columna líder — mismo
        // criterio que ix_ordenes_compra_punto_venta_fecha/ix_comprobantes_venta_punto_venta_fecha
        // (sin índice separado para la PV).
        builder.HasIndex(p => new { p.IdPuntoVenta, p.IdTenant, p.FechaEmision })
            .HasDatabaseName("ix_presupuestos_punto_venta_fecha");

        builder.HasIndex(p => new { p.IdCliente, p.IdTenant }).HasDatabaseName("ix_presupuestos_cliente");

        // Simple, no compuesto: un índice liderado por id_tenant NO cubre una FK simple
        // (proposal §C, la trampa documentada de la enmienda de la etapa 14).
        builder.HasIndex(p => p.IdEmpleado).HasDatabaseName("ix_presupuestos_empleado");

        // ux_presupuestos_numero: UNIQUE PARCIAL (proposal §C, gate §A) WHERE numero IS NOT
        // NULL — un borrador no tiene número. ⚠ Su nombre contiene "_numero" —
        // ManejadorDeErrores tiene que resolverla por nombre EXACTO ANTES de
        // ClasificarUnicidad, CUARTA ocurrencia del ordering trap (design decisión 18).
        builder.HasIndex(p => new { p.IdTenant, p.IdPuntoVenta, p.Numero })
            .HasDatabaseName("ux_presupuestos_numero")
            .IsUnique()
            .HasFilter("numero IS NOT NULL");

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(p => p.IdTenant)
            .HasConstraintName("fk_presupuestos_tenant")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<PuntoVenta>()
            .WithMany()
            .HasForeignKey(p => new { p.IdPuntoVenta, p.IdTenant })
            .HasPrincipalKey(pv => new { pv.Id, pv.IdTenant })
            .HasConstraintName("fk_presupuestos_punto_venta")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Cliente>()
            .WithMany()
            .HasForeignKey(p => new { p.IdCliente, p.IdTenant })
            .HasPrincipalKey(c => new { c.Id, c.IdTenant })
            .HasConstraintName("fk_presupuestos_cliente")
            .OnDelete(DeleteBehavior.Restrict);

        // FK simple, deliberada (proposal §C): una AK compuesta forzaría id_tenant NOT NULL en
        // usuarios y rompería el sentinel NULL de staff de plataforma — mismo criterio que
        // fk_comprobantes_venta_empleado/fk_ordenes_compra_empleado.
        builder.HasOne<Usuario>()
            .WithMany()
            .HasForeignKey(p => p.IdEmpleado)
            .HasConstraintName("fk_presupuestos_empleado")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
