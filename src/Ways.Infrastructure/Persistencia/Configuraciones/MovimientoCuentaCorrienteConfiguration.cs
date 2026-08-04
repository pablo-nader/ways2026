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

        builder.HasIndex(m => m.IdTenant).HasDatabaseName("ix_movimientos_cuenta_corriente_tenant");

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
    }
}
