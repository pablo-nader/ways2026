using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Articulos;
using Ways.Domain.Compras;
using Ways.Domain.Organizacion;
using Ways.Domain.Stock;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

/// <summary>
/// Mapea <see cref="MovimientoStock"/> (design: Table Shapes — write path B). La CHECK
/// <c>ck_movimientos_stock_cantidad_no_cero</c> es defensa en profundidad — ningún camino de
/// escritura (Slice 4/5) construye un movimiento con cantidad cero, no tendría sentido de
/// negocio.
/// </summary>
public class MovimientoStockConfiguration : IEntityTypeConfiguration<MovimientoStock>
{
    public void Configure(EntityTypeBuilder<MovimientoStock> builder)
    {
        builder.ToTable("movimientos_stock", t =>
        {
            t.HasCheckConstraint("ck_movimientos_stock_cantidad_no_cero", "cantidad <> 0");
        });

        builder.HasKey(m => m.Id).HasName("pk_movimientos_stock");

        builder.Property(m => m.Id)
            .HasColumnName("id_movimiento")
            .UseIdentityByDefaultColumn();

        builder.Property(m => m.IdTenant)
            .HasColumnName("id_tenant")
            .IsRequired();

        builder.Property(m => m.IdArticulo).HasColumnName("id_articulo").IsRequired();
        builder.Property(m => m.IdSucursal).HasColumnName("id_sucursal").IsRequired();
        builder.Property(m => m.IdPuntoVenta).HasColumnName("id_punto_venta").IsRequired();

        builder.Property(m => m.Cantidad).HasColumnName("cantidad").HasColumnType("numeric(12,3)").IsRequired();

        builder.Property(m => m.Motivo)
            .HasColumnName("motivo")
            .HasColumnType("motivo_stock")
            .IsRequired();

        builder.Property(m => m.IdComprobanteVenta).HasColumnName("id_comprobante_venta");

        // stage-8-compras-transferencias-inventario, Slice 1 (design: Table Shapes — D, la FK
        // diferida de doc-10:457-465): columna + FK compuesta aterrizan juntas en esta migración.
        builder.Property(m => m.IdComprobanteCompra).HasColumnName("id_comprobante_compra");

        builder.Property(m => m.IdPuntoVentaDestino).HasColumnName("id_punto_venta_destino");
        builder.Property(m => m.IdSucursalDestino).HasColumnName("id_sucursal_destino");

        // Etapa 12 (proposal decisión 5, gate §C): columna creada acá, escrita recién desde
        // slice 4.
        builder.Property(m => m.IdLote).HasColumnName("id_lote");

        // Etapa 17 (proposal §H, decisión 5 del explore): columna creada acá, escrita recién
        // desde slice 5 (ServicioDeRemitos.EmitirAsync/AnularAsync, el cuarto write site).
        builder.Property(m => m.IdRemito).HasColumnName("id_remito");

        builder.Property(m => m.IdEmpleado).HasColumnName("id_empleado").IsRequired();
        builder.Property(m => m.Observaciones).HasColumnName("observaciones").HasColumnType("text");

        builder.Property(m => m.CreadoEl).HasColumnName("creado_el").IsRequired();

        builder.HasIndex(m => m.IdTenant).HasDatabaseName("ix_movimientos_stock_tenant");

        // Índice de negocio: reconstruir/consultar el saldo de un par articulo+sucursal, la clave de stock.
        builder.HasIndex(m => new { m.IdArticulo, m.IdSucursal, m.IdTenant })
            .HasDatabaseName("ix_movimientos_stock_articulo_sucursal");

        builder.HasIndex(m => new { m.IdComprobanteVenta, m.IdTenant }).HasDatabaseName("ix_movimientos_stock_comprobante_venta");
        builder.HasIndex(m => new { m.IdComprobanteCompra, m.IdTenant }).HasDatabaseName("ix_movimientos_stock_comprobante_compra");

        // Índices de soporte de FK compuesta (evitan el índice implícito PascalCase de EF): la
        // columna líder de cada uno no coincide con la de ix_movimientos_stock_articulo_sucursal
        // (esa empieza por id_articulo con id_sucursal en el medio), así que hacen falta aparte.
        builder.HasIndex(m => new { m.IdArticulo, m.IdTenant }).HasDatabaseName("ix_movimientos_stock_articulo");
        builder.HasIndex(m => new { m.IdSucursal, m.IdTenant }).HasDatabaseName("ix_movimientos_stock_sucursal");
        builder.HasIndex(m => new { m.IdSucursalDestino, m.IdTenant }).HasDatabaseName("ix_movimientos_stock_sucursal_destino");
        builder.HasIndex(m => new { m.IdPuntoVenta, m.IdTenant }).HasDatabaseName("ix_movimientos_stock_punto_venta");
        builder.HasIndex(m => new { m.IdPuntoVentaDestino, m.IdTenant }).HasDatabaseName("ix_movimientos_stock_punto_venta_destino");
        builder.HasIndex(m => m.IdEmpleado).HasDatabaseName("ix_movimientos_stock_empleado");

        // Etapa 12 (proposal decisión 5, gate §C): soporte de fk_movimientos_stock_lote y
        // reconstrucción del ledger por lote.
        builder.HasIndex(m => new { m.IdLote, m.IdArticulo, m.IdTenant }).HasDatabaseName("ix_movimientos_stock_lote");

        // Etapa 17 (proposal §H, index 30, gate §H): soporte de FK 24 + la lectura "los
        // movimientos de este remito", mirrors ix_movimientos_stock_comprobante_venta.
        builder.HasIndex(m => new { m.IdRemito, m.IdTenant }).HasDatabaseName("ix_movimientos_stock_remito");

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(m => m.IdTenant)
            .HasConstraintName("fk_movimientos_stock_tenant")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Articulo>()
            .WithMany()
            .HasForeignKey(m => new { m.IdArticulo, m.IdTenant })
            .HasPrincipalKey(a => new { a.Id, a.IdTenant })
            .HasConstraintName("fk_movimientos_stock_articulo")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<PuntoVenta>()
            .WithMany()
            .HasForeignKey(m => new { m.IdPuntoVenta, m.IdTenant })
            .HasPrincipalKey(p => new { p.Id, p.IdTenant })
            .HasConstraintName("fk_movimientos_stock_punto_venta")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Sucursal>()
            .WithMany()
            .HasForeignKey(m => new { m.IdSucursal, m.IdTenant })
            .HasPrincipalKey(s => new { s.Id, s.IdTenant })
            .HasConstraintName("fk_movimientos_stock_sucursal")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Sucursal>()
            .WithMany()
            .HasForeignKey(m => new { m.IdSucursalDestino, m.IdTenant })
            .HasPrincipalKey(s => new { s.Id, s.IdTenant })
            .HasConstraintName("fk_movimientos_stock_sucursal_destino")
            .OnDelete(DeleteBehavior.Restrict);

        // Transferencias: la caja de destino, misma FK compuesta que fk_movimientos_stock_punto_venta.
        builder.HasOne<PuntoVenta>()
            .WithMany()
            .HasForeignKey(m => new { m.IdPuntoVentaDestino, m.IdTenant })
            .HasPrincipalKey(p => new { p.Id, p.IdTenant })
            .HasConstraintName("fk_movimientos_stock_punto_venta_destino")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<ComprobanteVenta>()
            .WithMany()
            .HasForeignKey(m => new { m.IdComprobanteVenta, m.IdTenant })
            .HasPrincipalKey(c => new { c.Id, c.IdTenant })
            .HasConstraintName("fk_movimientos_stock_comprobante_venta")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<ComprobanteCompra>()
            .WithMany()
            .HasForeignKey(m => new { m.IdComprobanteCompra, m.IdTenant })
            .HasPrincipalKey(c => new { c.Id, c.IdTenant })
            .HasConstraintName("fk_movimientos_stock_comprobante_compra")
            .OnDelete(DeleteBehavior.Restrict);

        // Etapa 12 (proposal decisión 5, gate §C): garantiza a nivel de base que el lote del
        // movimiento pertenece a su mismo artículo.
        builder.HasOne<Lote>()
            .WithMany()
            .HasForeignKey(m => new { m.IdLote, m.IdArticulo, m.IdTenant })
            .HasPrincipalKey(l => new { l.Id, l.IdArticulo, l.IdTenant })
            .HasConstraintName("fk_movimientos_stock_lote")
            .OnDelete(DeleteBehavior.Restrict);

        // FK 24 (proposal §H): composite, nullable, MATCH SIMPLE — el documento del cuarto
        // write site, misma forma exacta que fk_movimientos_stock_comprobante_venta/
        // ..._comprobante_compra.
        builder.HasOne<Remito>()
            .WithMany()
            .HasForeignKey(m => new { m.IdRemito, m.IdTenant })
            .HasPrincipalKey(r => new { r.Id, r.IdTenant })
            .HasConstraintName("fk_movimientos_stock_remito")
            .OnDelete(DeleteBehavior.Restrict);

        // id_empleado: FK SIMPLE (no compuesta), misma deviación deliberada que
        // ComprobanteVentaConfiguration.fk_comprobantes_venta_empleado documenta — una clave
        // alterna (Id, IdTenant) sobre Usuario fuerza IdTenant a NOT NULL por convención de EF,
        // corrompiendo el sentinel de plataforma. id_empleado nunca es input de cliente.
        builder.HasOne<Usuario>()
            .WithMany()
            .HasForeignKey(m => m.IdEmpleado)
            .HasConstraintName("fk_movimientos_stock_empleado")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
