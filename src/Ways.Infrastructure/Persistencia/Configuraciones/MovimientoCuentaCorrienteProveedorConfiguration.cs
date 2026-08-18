using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Compras;
using Ways.Domain.CuentaCorriente;
using Ways.Domain.Gastos;
using Ways.Domain.Organizacion;
using Ways.Domain.Proveedores;
using Ways.Domain.Usuarios;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

/// <summary>
/// Mapea <see cref="MovimientoCuentaCorrienteProveedor"/> (gate §B, design decisión 16): espeja
/// <see cref="MovimientoCuentaCorrienteConfiguration"/> MENOS la clave alterna, el self-FK y su
/// índice de soporte (no hay reliquidación en esta etapa, así que ninguna tabla referencia este
/// ledger — gate §B "No alternate key on this table"). Declara las 6 FKs y sus 6 índices de
/// soporte a mano, con nombres doc-10, para que el conteo del gate ("cero índices que este
/// contrato no nombró") sea verificable leyendo la migración — la lección de la enmienda 1 de
/// la etapa 14 (<c>ForeignKeyIndexConvention</c> re-agrega un índice de soporte para toda FK sin
/// cobertura, incluso si se remueve a mano dentro de <c>Configure()</c>).
/// </summary>
public class MovimientoCuentaCorrienteProveedorConfiguration
    : IEntityTypeConfiguration<MovimientoCuentaCorrienteProveedor>
{
    public void Configure(EntityTypeBuilder<MovimientoCuentaCorrienteProveedor> builder)
    {
        builder.ToTable("movimientos_cuenta_corriente_proveedor", t =>
        {
            t.HasCheckConstraint(
                "ck_movimientos_cuenta_corriente_proveedor_apertura",
                "(tipo = 'apertura' AND id_punto_venta IS NULL AND id_empleado IS NULL) "
                + "OR (tipo <> 'apertura' AND id_punto_venta IS NOT NULL AND id_empleado IS NOT NULL)");
        });

        builder.HasKey(m => m.Id).HasName("pk_movimientos_cuenta_corriente_proveedor");

        builder.Property(m => m.Id)
            .HasColumnName("id_movimiento")
            .UseIdentityByDefaultColumn();

        builder.Property(m => m.IdTenant)
            .HasColumnName("id_tenant")
            .IsRequired();

        builder.Property(m => m.IdProveedor).HasColumnName("id_proveedor").IsRequired();
        builder.Property(m => m.Fecha).HasColumnName("fecha").IsRequired();
        builder.Property(m => m.IdPuntoVenta).HasColumnName("id_punto_venta");
        builder.Property(m => m.IdEmpleado).HasColumnName("id_empleado");

        builder.Property(m => m.Tipo)
            .HasColumnName("tipo")
            .HasColumnType("tipo_movimiento_cc_proveedor")
            .IsRequired();

        builder.Property(m => m.IdComprobanteCompra).HasColumnName("id_comprobante_compra");
        builder.Property(m => m.IdGasto).HasColumnName("id_gasto");

        builder.Property(m => m.Importe).HasColumnName("importe").HasColumnType("numeric(14,2)").IsRequired();
        builder.Property(m => m.SaldoResultante).HasColumnName("saldo_resultante").HasColumnType("numeric(14,2)").IsRequired();
        builder.Property(m => m.Detalle).HasColumnName("detalle").HasColumnType("text");

        // Índice 1 — RLS predicate + soporte de FK 1 (id_tenant). Mirrors
        // ix_movimientos_cuenta_corriente_tenant.
        builder.HasIndex(m => m.IdTenant).HasDatabaseName("ix_movimientos_cuenta_corriente_proveedor_tenant");

        // Índice 2 — el listado de estado de cuenta (design decisión 10) Y soporte de FK 2 por
        // prefijo de columnas líderes en el mismo orden.
        builder.HasIndex(m => new { m.IdProveedor, m.IdTenant, m.Fecha })
            .HasDatabaseName("ix_movimientos_cuenta_corriente_proveedor_proveedor_fecha");

        // Índice 3 — la agregación de imputación por compra (design decisión 7) Y soporte de FK 5.
        builder.HasIndex(m => new { m.IdComprobanteCompra, m.IdTenant })
            .HasDatabaseName("ix_movimientos_cuenta_corriente_proveedor_comprobante_compra");

        // Índice 4 — soporte de FK 3, declarado a mano con el nombre doc-10 en vez de dejar que
        // EF autogenere IX_..._id_punto_venta_id_tenant.
        builder.HasIndex(m => new { m.IdPuntoVenta, m.IdTenant })
            .HasDatabaseName("ix_movimientos_cuenta_corriente_proveedor_punto_venta");

        // Índice 5 — soporte de FK 4, SIMPLE: un índice compuesto liderado por id_tenant NO
        // cubriría una FK simple (la trampa exacta que produjo la enmienda de la etapa 14).
        builder.HasIndex(m => m.IdEmpleado).HasDatabaseName("ix_movimientos_cuenta_corriente_proveedor_empleado");

        // Índice 6 — soporte de FK 6.
        builder.HasIndex(m => new { m.IdGasto, m.IdTenant })
            .HasDatabaseName("ix_movimientos_cuenta_corriente_proveedor_gasto");

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(m => m.IdTenant)
            .HasConstraintName("fk_movimientos_cuenta_corriente_proveedor_tenant")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Proveedor>()
            .WithMany()
            .HasForeignKey(m => new { m.IdProveedor, m.IdTenant })
            .HasPrincipalKey(p => new { p.Id, p.IdTenant })
            .HasConstraintName("fk_movimientos_cuenta_corriente_proveedor_proveedor")
            .OnDelete(DeleteBehavior.Restrict);

        // MATCH SIMPLE (el default): con id_punto_venta NULL la FK no se chequea; la integridad
        // de tenant viene de la FK 1 — el mismo motivo por el que ambas existen
        // (precedente fk_auditoria_punto_venta).
        builder.HasOne<PuntoVenta>()
            .WithMany()
            .HasForeignKey(m => new { m.IdPuntoVenta, m.IdTenant })
            .HasPrincipalKey(p => new { p.Id, p.IdTenant })
            .HasConstraintName("fk_movimientos_cuenta_corriente_proveedor_punto_venta")
            .OnDelete(DeleteBehavior.Restrict);

        // id_empleado: FK SIMPLE (no compuesta) — doc-10:563-567, una AK compuesta forzaría
        // id_tenant NOT NULL en usuarios y rompería el sentinel NULL del staff de plataforma.
        // Mismo criterio que fk_movimientos_cuenta_corriente_empleado / fk_auditoria_actor.
        builder.HasOne<Usuario>()
            .WithMany()
            .HasForeignKey(m => m.IdEmpleado)
            .HasConstraintName("fk_movimientos_cuenta_corriente_proveedor_empleado")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<ComprobanteCompra>()
            .WithMany()
            .HasForeignKey(m => new { m.IdComprobanteCompra, m.IdTenant })
            .HasPrincipalKey(c => new { c.Id, c.IdTenant })
            .HasConstraintName("fk_movimientos_cuenta_corriente_proveedor_comprobante_compra")
            .OnDelete(DeleteBehavior.Restrict);

        // Requiere la clave alterna nueva de gastos (gate §D, GastoConfiguration).
        builder.HasOne<Gasto>()
            .WithMany()
            .HasForeignKey(m => new { m.IdGasto, m.IdTenant })
            .HasPrincipalKey(g => new { g.Id, g.IdTenant })
            .HasConstraintName("fk_movimientos_cuenta_corriente_proveedor_gasto")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
