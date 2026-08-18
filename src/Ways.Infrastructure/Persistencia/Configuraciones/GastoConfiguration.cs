using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Compras;
using Ways.Domain.Gastos;
using Ways.Domain.Organizacion;
using Ways.Domain.Proveedores;
using Ways.Domain.Usuarios;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

/// <summary>
/// Mapea <see cref="Gasto"/> (design: Table Shapes — write path C). La CHECK es defensa en
/// profundidad — <c>ServicioDeGastos.RegistrarAsync</c> (Slice 3) ya rechaza un importe
/// <c>&lt;= 0</c> en el camino de servicio.
/// </summary>
public class GastoConfiguration : IEntityTypeConfiguration<Gasto>
{
    public void Configure(EntityTypeBuilder<Gasto> builder)
    {
        builder.ToTable("gastos", t =>
        {
            t.HasCheckConstraint("ck_gastos_importe_positivo", "importe > 0");
        });

        builder.HasKey(g => g.Id).HasName("pk_gastos");

        builder.Property(g => g.Id)
            .HasColumnName("id_gasto")
            .UseIdentityByDefaultColumn();

        builder.Property(g => g.IdTenant).HasColumnName("id_tenant").IsRequired();

        builder.Property(g => g.Fecha).HasColumnName("fecha").IsRequired();
        builder.Property(g => g.IdPuntoVenta).HasColumnName("id_punto_venta").IsRequired();
        builder.Property(g => g.IdTurnoCaja).HasColumnName("id_turno_caja").IsRequired();
        builder.Property(g => g.IdEmpleado).HasColumnName("id_empleado").IsRequired();

        builder.Property(g => g.Categoria)
            .HasColumnName("categoria")
            .HasColumnType("categoria_gasto")
            .IsRequired();

        builder.Property(g => g.IdProveedor).HasColumnName("id_proveedor");
        builder.Property(g => g.IdArea).HasColumnName("id_area");

        builder.Property(g => g.Concepto).HasColumnName("concepto").HasColumnType("text").IsRequired();
        builder.Property(g => g.Detalle).HasColumnName("detalle").HasColumnType("text");

        builder.Property(g => g.IdMedioPago).HasColumnName("id_medio_pago").IsRequired();
        builder.Property(g => g.NumeroFactura).HasColumnName("numero_factura").HasColumnType("text");

        builder.Property(g => g.Importe).HasColumnName("importe").HasColumnType("numeric(14,2)").IsRequired();

        // stage-8-compras-transferencias-inventario, Slice 1 (design: Table Shapes — D, la FK
        // diferida de doc-10:426-434): columna + FK compuesta aterrizan juntas en esta migración.
        builder.Property(g => g.IdComprobanteCompra).HasColumnName("id_comprobante_compra");

        builder.Property(g => g.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(g => g.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(g => g.DeletedAt).HasColumnName("deleted_at");

        builder.Ignore(g => g.EstaEliminada);

        builder.HasIndex(g => g.IdTenant).HasDatabaseName("ix_gastos_tenant");
        builder.HasIndex(g => new { g.IdTurnoCaja, g.IdTenant }).HasDatabaseName("ix_gastos_turno");
        builder.HasIndex(g => new { g.IdPuntoVenta, g.IdTenant, g.Fecha }).HasDatabaseName("ix_gastos_punto_venta_fecha");
        builder.HasIndex(g => new { g.IdProveedor, g.IdTenant }).HasDatabaseName("ix_gastos_proveedor");
        builder.HasIndex(g => new { g.IdComprobanteCompra, g.IdTenant }).HasDatabaseName("ix_gastos_comprobante_compra");

        // Índices de soporte de FK (evitan el índice implícito PascalCase de EF, misma trampa
        // que documenta ComprobanteVentaConfiguration).
        builder.HasIndex(g => g.IdEmpleado).HasDatabaseName("ix_gastos_empleado");
        builder.HasIndex(g => new { g.IdArea, g.IdTenant }).HasDatabaseName("ix_gastos_area");
        builder.HasIndex(g => new { g.IdMedioPago, g.IdTenant }).HasDatabaseName("ix_gastos_medio_pago");

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(g => g.IdTenant)
            .HasConstraintName("fk_gastos_tenant")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<PuntoVenta>()
            .WithMany()
            .HasForeignKey(g => new { g.IdPuntoVenta, g.IdTenant })
            .HasPrincipalKey(p => new { p.Id, p.IdTenant })
            .HasConstraintName("fk_gastos_punto_venta")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<TurnoCaja>()
            .WithMany()
            .HasForeignKey(g => new { g.IdTurnoCaja, g.IdTenant })
            .HasPrincipalKey(t => new { t.Id, t.IdTenant })
            .HasConstraintName("fk_gastos_turno")
            .OnDelete(DeleteBehavior.Restrict);

        // id_empleado: FK simple, mismo motivo que TurnoCajaConfiguration.fk_turnos_caja_empleado_apertura.
        builder.HasOne<Usuario>()
            .WithMany()
            .HasForeignKey(g => g.IdEmpleado)
            .HasConstraintName("fk_gastos_empleado")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Proveedor>()
            .WithMany()
            .HasForeignKey(g => new { g.IdProveedor, g.IdTenant })
            .HasPrincipalKey(p => new { p.Id, p.IdTenant })
            .HasConstraintName("fk_gastos_proveedor")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<ComprobanteCompra>()
            .WithMany()
            .HasForeignKey(g => new { g.IdComprobanteCompra, g.IdTenant })
            .HasPrincipalKey(c => new { c.Id, c.IdTenant })
            .HasConstraintName("fk_gastos_comprobante_compra")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Area>()
            .WithMany()
            .HasForeignKey(g => new { g.IdArea, g.IdTenant })
            .HasPrincipalKey(a => new { a.Id, a.IdTenant })
            .HasConstraintName("fk_gastos_area")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<MedioPago>()
            .WithMany()
            .HasForeignKey(g => new { g.IdMedioPago, g.IdTenant })
            .HasPrincipalKey(m => new { m.Id, m.IdTenant })
            .HasConstraintName("fk_gastos_medio_pago")
            .OnDelete(DeleteBehavior.Restrict);

        // stage-15-cc-proveedores-ledger, Slice 1 (gate §D): habilita la FK compuesta
        // fk_movimientos_cuenta_corriente_proveedor_gasto — id_gasto ya es único vía pk_gastos,
        // así que la constraint es estructuralmente inviolable (no agrega ningún modo de fallo
        // nuevo, solo el target de referencia compuesto que el resto de las FKs operativas de
        // este esquema usa).
        builder.HasAlternateKey(g => new { g.Id, g.IdTenant })
            .HasName("ak_gastos_id_gasto_id_tenant");
    }
}
