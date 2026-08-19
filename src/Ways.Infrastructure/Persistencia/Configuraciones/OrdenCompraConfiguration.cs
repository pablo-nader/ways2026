using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Compras;
using Ways.Domain.Organizacion;
using Ways.Domain.Proveedores;
using Ways.Domain.Usuarios;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

/// <summary>
/// Mapea <see cref="OrdenCompra"/> (proposal: Modelo de datos propuesto — §B, DB CHANGE GATE
/// aprobado). Shaped on <c>ComprobanteCompraConfiguration.cs:17-136</c>. Todos los índices de
/// soporte se declaran a mano con nombres doc-10 — cero sorpresa de
/// <c>ForeignKeyIndexConvention</c> (conteo vinculante: 7 índices en esta tabla, incl. el
/// implícito de la AK).
/// </summary>
public class OrdenCompraConfiguration : IEntityTypeConfiguration<OrdenCompra>
{
    public void Configure(EntityTypeBuilder<OrdenCompra> builder)
    {
        builder.ToTable("ordenes_compra", t =>
        {
            // CHECK 1 (proposal §B): numero y fecha_envio llegan JUNTOS, y todo estado
            // posterior a enviada tiene los dos. anulada se admite sin número porque un
            // borrador puede anularse antes de ser enviado (design decisión 9).
            t.HasCheckConstraint(
                "ck_ordenes_compra_envio_completo",
                "((numero IS NULL) = (fecha_envio IS NULL)) AND (estado IN ('borrador','anulada') OR numero IS NOT NULL)");

            // CHECK 2 (proposal §B): cerrada y fecha_cierre son el mismo hecho; un cierre
            // manual sin fecha_cierre es irrepresentable (design decisión 5).
            t.HasCheckConstraint(
                "ck_ordenes_compra_cierre",
                "((fecha_cierre IS NULL) = (estado <> 'cerrada')) AND (id_empleado_cierre IS NULL OR fecha_cierre IS NOT NULL)");
        });

        builder.HasKey(o => o.Id).HasName("pk_ordenes_compra");

        builder.Property(o => o.Id)
            .HasColumnName("id_orden_compra")
            .UseIdentityByDefaultColumn();

        builder.Property(o => o.IdTenant)
            .HasColumnName("id_tenant")
            .IsRequired();

        // Habilita las FKs compuestas de items_orden_compra y comprobantes_compra.id_orden_compra
        // (mismo patrón que ak_comprobantes_compra_id_comprobante_compra_id_tenant).
        builder.HasAlternateKey(o => new { o.Id, o.IdTenant })
            .HasName("ak_ordenes_compra_id_orden_compra_id_tenant");

        builder.Property(o => o.IdPuntoVenta).HasColumnName("id_punto_venta").IsRequired();
        builder.Property(o => o.IdProveedor).HasColumnName("id_proveedor").IsRequired();
        builder.Property(o => o.IdEmpleado).HasColumnName("id_empleado").IsRequired();

        builder.Property(o => o.Numero).HasColumnName("numero").HasColumnType("bigint");

        builder.Property(o => o.FechaEmision).HasColumnName("fecha_emision").IsRequired();
        builder.Property(o => o.FechaEnvio).HasColumnName("fecha_envio");
        builder.Property(o => o.FechaEsperada).HasColumnName("fecha_esperada").HasColumnType("date");
        builder.Property(o => o.FechaCierre).HasColumnName("fecha_cierre");
        builder.Property(o => o.IdEmpleadoCierre).HasColumnName("id_empleado_cierre");

        builder.Property(o => o.Observaciones).HasColumnName("observaciones").HasColumnType("text");

        builder.Property(o => o.Estado)
            .HasColumnName("estado")
            .HasColumnType("estado_orden_compra")
            .IsRequired();

        builder.Property(o => o.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(o => o.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(o => o.DeletedAt).HasColumnName("deleted_at");

        builder.Ignore(o => o.EstaEliminada);

        builder.HasIndex(o => o.IdTenant).HasDatabaseName("ix_ordenes_compra_tenant");

        // Cubre la listing por PV Y el soporte de FK 2 por prefijo de columna líder — mismo
        // criterio que ix_comprobantes_compra_punto_venta_fecha (sin índice separado para la PV).
        builder.HasIndex(o => new { o.IdPuntoVenta, o.IdTenant, o.FechaEmision })
            .HasDatabaseName("ix_ordenes_compra_punto_venta_fecha");

        builder.HasIndex(o => new { o.IdProveedor, o.IdTenant }).HasDatabaseName("ix_ordenes_compra_proveedor");

        // Simple, no compuesto: un índice liderado por id_tenant NO cubre una FK simple
        // (proposal §B, la trampa documentada de la enmienda de la etapa 14).
        builder.HasIndex(o => o.IdEmpleado).HasDatabaseName("ix_ordenes_compra_empleado");
        builder.HasIndex(o => o.IdEmpleadoCierre).HasDatabaseName("ix_ordenes_compra_empleado_cierre");

        // ux_ordenes_compra_numero: UNIQUE PARCIAL (proposal §B, gate §A). Filtra SOLO
        // numero IS NOT NULL, sin excluir anuladas a propósito — a diferencia de
        // ux_comprobantes_compra_numero_externo: una serie propia jamás reusa números (los
        // huecos son legítimos, el reuso confunde), el numero_externo es del proveedor y puede
        // repetirse tras anular (state.yaml db_gate_approval, nota aceptada). ⚠ Su nombre
        // contiene "_numero" — ManejadorDeErrores tiene que resolverla por nombre EXACTO ANTES
        // de ClasificarUnicidad, tercera ocurrencia del ordering trap (design decisión 11).
        builder.HasIndex(o => new { o.IdTenant, o.IdPuntoVenta, o.Numero })
            .HasDatabaseName("ux_ordenes_compra_numero")
            .IsUnique()
            .HasFilter("numero IS NOT NULL");

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(o => o.IdTenant)
            .HasConstraintName("fk_ordenes_compra_tenant")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<PuntoVenta>()
            .WithMany()
            .HasForeignKey(o => new { o.IdPuntoVenta, o.IdTenant })
            .HasPrincipalKey(p => new { p.Id, p.IdTenant })
            .HasConstraintName("fk_ordenes_compra_punto_venta")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Proveedor>()
            .WithMany()
            .HasForeignKey(o => new { o.IdProveedor, o.IdTenant })
            .HasPrincipalKey(p => new { p.Id, p.IdTenant })
            .HasConstraintName("fk_ordenes_compra_proveedor")
            .OnDelete(DeleteBehavior.Restrict);

        // FK simple, deliberada (proposal §B, doc-10:563-567): una AK compuesta forzaría
        // id_tenant NOT NULL en usuarios y rompería el sentinel NULL de staff de plataforma —
        // mismo criterio que fk_comprobantes_compra_empleado.
        builder.HasOne<Usuario>()
            .WithMany()
            .HasForeignKey(o => o.IdEmpleado)
            .HasConstraintName("fk_ordenes_compra_empleado")
            .OnDelete(DeleteBehavior.Restrict);

        // Nullable, misma razón que la de arriba.
        builder.HasOne<Usuario>()
            .WithMany()
            .HasForeignKey(o => o.IdEmpleadoCierre)
            .HasConstraintName("fk_ordenes_compra_empleado_cierre")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
