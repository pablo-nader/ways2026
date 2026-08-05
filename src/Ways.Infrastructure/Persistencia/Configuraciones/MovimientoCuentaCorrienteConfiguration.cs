using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Clientes;
using Ways.Domain.CuentaCorriente;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

/// <summary>
/// Mapea <see cref="MovimientoCuentaCorriente"/> (design: Table Shapes — write path C).
/// </summary>
public class MovimientoCuentaCorrienteConfiguration : IEntityTypeConfiguration<MovimientoCuentaCorriente>
{
    public void Configure(EntityTypeBuilder<MovimientoCuentaCorriente> builder)
    {
        builder.ToTable("movimientos_cuenta_corriente");

        builder.HasKey(m => m.Id).HasName("pk_movimientos_cuenta_corriente");

        builder.Property(m => m.Id)
            .HasColumnName("id_movimiento")
            .UseIdentityByDefaultColumn();

        builder.Property(m => m.IdTenant)
            .HasColumnName("id_tenant")
            .IsRequired();

        builder.Property(m => m.IdCliente).HasColumnName("id_cliente").IsRequired();
        builder.Property(m => m.Fecha).HasColumnName("fecha").IsRequired();
        builder.Property(m => m.IdPuntoVenta).HasColumnName("id_punto_venta").IsRequired();
        builder.Property(m => m.IdEmpleado).HasColumnName("id_empleado").IsRequired();

        builder.Property(m => m.Tipo)
            .HasColumnName("tipo")
            .HasColumnType("tipo_movimiento_cc")
            .IsRequired();

        builder.Property(m => m.IdComprobanteVenta).HasColumnName("id_comprobante_venta");
        builder.Property(m => m.IdPagoComprobante).HasColumnName("id_pago_comprobante");

        builder.Property(m => m.Importe).HasColumnName("importe").HasColumnType("numeric(14,2)").IsRequired();
        builder.Property(m => m.SaldoResultante).HasColumnName("saldo_resultante").HasColumnType("numeric(14,2)").IsRequired();
        builder.Property(m => m.Detalle).HasColumnName("detalle").HasColumnType("text");
        builder.Property(m => m.IdMovimientoActualizacion).HasColumnName("id_movimiento_actualizacion");

        builder.HasAlternateKey(m => new { m.Id, m.IdTenant })
            .HasName("ak_movimientos_cuenta_corriente_id_movimiento_id_tenant");

        builder.HasIndex(m => m.IdTenant).HasDatabaseName("ix_movimientos_cuenta_corriente_tenant");

        // La predicate de elegibilidad de la reliquidación ES este índice (design: Table Shapes A):
        // self-vacuuming a medida que los consumos se reliquidan.
        builder.HasIndex(m => new { m.IdCliente, m.IdTenant })
            .HasDatabaseName("ix_movimientos_cuenta_corriente_consumos_pendientes")
            .HasFilter("tipo = 'consumo' AND id_movimiento_actualizacion IS NULL");

        // Índice de negocio (extracto de cuenta corriente por cliente, spec: Saldo Is The
        // Maintained Cache Of The Ledger) que además sirve de soporte de la FK compuesta a
        // clientes (columnas líderes en el mismo orden).
        builder.HasIndex(m => new { m.IdCliente, m.IdTenant, m.Fecha })
            .HasDatabaseName("ix_movimientos_cuenta_corriente_cliente_fecha");

        builder.HasIndex(m => new { m.IdComprobanteVenta, m.IdTenant })
            .HasDatabaseName("ix_movimientos_cuenta_corriente_comprobante_venta");

        builder.HasIndex(m => new { m.IdPuntoVenta, m.IdTenant }).HasDatabaseName("ix_movimientos_cuenta_corriente_punto_venta");
        builder.HasIndex(m => m.IdEmpleado).HasDatabaseName("ix_movimientos_cuenta_corriente_empleado");
        builder.HasIndex(m => new { m.IdPagoComprobante, m.IdTenant }).HasDatabaseName("ix_movimientos_cuenta_corriente_pago_comprobante");

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(m => m.IdTenant)
            .HasConstraintName("fk_movimientos_cuenta_corriente_tenant")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Cliente>()
            .WithMany()
            .HasForeignKey(m => new { m.IdCliente, m.IdTenant })
            .HasPrincipalKey(c => new { c.Id, c.IdTenant })
            .HasConstraintName("fk_movimientos_cuenta_corriente_cliente")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<PuntoVenta>()
            .WithMany()
            .HasForeignKey(m => new { m.IdPuntoVenta, m.IdTenant })
            .HasPrincipalKey(p => new { p.Id, p.IdTenant })
            .HasConstraintName("fk_movimientos_cuenta_corriente_punto_venta")
            .OnDelete(DeleteBehavior.Restrict);

        // id_empleado: FK SIMPLE (no compuesta) — misma deviación deliberada que
        // ComprobanteVentaConfiguration.fk_comprobantes_venta_empleado documenta.
        builder.HasOne<Usuario>()
            .WithMany()
            .HasForeignKey(m => m.IdEmpleado)
            .HasConstraintName("fk_movimientos_cuenta_corriente_empleado")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<ComprobanteVenta>()
            .WithMany()
            .HasForeignKey(m => new { m.IdComprobanteVenta, m.IdTenant })
            .HasPrincipalKey(c => new { c.Id, c.IdTenant })
            .HasConstraintName("fk_movimientos_cuenta_corriente_comprobante_venta")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<PagoComprobante>()
            .WithMany()
            .HasForeignKey(m => new { m.IdPagoComprobante, m.IdTenant })
            .HasPrincipalKey(p => new { p.Id, p.IdTenant })
            .HasConstraintName("fk_movimientos_cuenta_corriente_pago_comprobante")
            .OnDelete(DeleteBehavior.Restrict);

        // DESVIACIÓN DOCUMENTADA vs. design (Table Shapes A, "no second index for the reverse
        // audit lookup"): EF Core re-agrega un índice de soporte para toda FK sin cobertura en
        // ModelFinalizingConvention, aun si se lo remueve a mano dentro de este Configure() —
        // suprimirlo por completo exigiría desregistrar ForeignKeyIndexConvention para todo el
        // modelo, un cambio global fuera de alcance de esta slice. Se declara explícito con el
        // nombre de la convención del resto de la tabla en vez de dejar el nombre autogenerado
        // de EF (IX_movimientos_cuenta_corriente_id_movimiento_actualizacion_id~).
        builder.HasIndex(m => new { m.IdMovimientoActualizacion, m.IdTenant })
            .HasDatabaseName("ix_movimientos_cuenta_corriente_actualizacion");

        // Self-FK (design decision 2 — el marcador de reliquidación): apunta a la fila
        // ActualizacionPrecios que cubrió este consumo. Requiere la AK compuesta declarada
        // arriba, mismo criterio que el resto de las FKs de esta tabla.
        builder.HasOne<MovimientoCuentaCorriente>()
            .WithMany()
            .HasForeignKey(m => new { m.IdMovimientoActualizacion, m.IdTenant })
            .HasPrincipalKey(m => new { m.Id, m.IdTenant })
            .HasConstraintName("fk_movimientos_cuenta_corriente_actualizacion")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
