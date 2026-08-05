using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Compras;
using Ways.Domain.Organizacion;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

/// <summary>
/// Mapea <see cref="ItemComprobanteCompra"/> (design: Table Shapes — B). Child scope:
/// <c>id_tenant</c> únicamente, sin FK propia a <c>puntos_venta</c> (se deriva del comprobante
/// padre) — mismo criterio que <c>ItemComprobanteVentaConfiguration</c>.
/// </summary>
public class ItemComprobanteCompraConfiguration : IEntityTypeConfiguration<ItemComprobanteCompra>
{
    public void Configure(EntityTypeBuilder<ItemComprobanteCompra> builder)
    {
        builder.ToTable("items_comprobante_compra", t =>
        {
            t.HasCheckConstraint("ck_items_comprobante_compra_cantidad_positiva", "cantidad > 0");

            // >= 0, no > 0: una línea de bonificación (costo cero) es real (design decisión 4;
            // Table Shapes — B).
            t.HasCheckConstraint("ck_items_comprobante_compra_costo_no_negativo", "costo_unitario >= 0");

            t.HasCheckConstraint(
                "ck_items_comprobante_compra_importes_no_negativos",
                "descuento >= 0 AND total >= 0");
        });

        builder.HasKey(i => i.Id).HasName("pk_items_comprobante_compra");

        builder.Property(i => i.Id)
            .HasColumnName("id_item")
            .UseIdentityByDefaultColumn();

        builder.Property(i => i.IdTenant)
            .HasColumnName("id_tenant")
            .IsRequired();

        builder.Property(i => i.IdComprobanteCompra).HasColumnName("id_comprobante_compra").IsRequired();
        builder.Property(i => i.Orden).HasColumnName("orden").IsRequired();

        // Deliberadamente NOT NULL (design: Table Shapes — B), a diferencia de
        // ItemComprobanteVenta.IdArticulo: una línea sin artículo no puede mover stock ni
        // actualizar costo — sería un gasto.
        builder.Property(i => i.IdArticulo).HasColumnName("id_articulo").IsRequired();

        builder.Property(i => i.Descripcion).HasColumnName("descripcion").HasColumnType("text").IsRequired();

        builder.Property(i => i.Cantidad).HasColumnName("cantidad").HasColumnType("numeric(12,3)").IsRequired();
        builder.Property(i => i.Bultos).HasColumnName("bultos").HasColumnType("numeric(10,2)");
        builder.Property(i => i.UnidadesPorBulto).HasColumnName("unidades_por_bulto").HasColumnType("numeric(10,2)");

        builder.Property(i => i.CostoUnitario).HasColumnName("costo_unitario").HasColumnType("numeric(14,4)").IsRequired();

        builder.Property(i => i.Descuento)
            .HasColumnName("descuento")
            .HasColumnType("numeric(14,2)")
            .HasDefaultValue(0m)
            .IsRequired();

        builder.Property(i => i.IdAlicuotaIva).HasColumnName("id_alicuota_iva").IsRequired();
        builder.Property(i => i.PorcentajeIva).HasColumnName("porcentaje_iva").HasColumnType("numeric(5,2)").IsRequired();

        builder.Property(i => i.Total).HasColumnName("total").HasColumnType("numeric(14,2)").IsRequired();

        builder.Property(i => i.ActualizaCosto)
            .HasColumnName("actualiza_costo")
            .HasDefaultValue(true)
            .IsRequired();

        builder.Property(i => i.PrecioSugerido).HasColumnName("precio_sugerido").HasColumnType("numeric(14,2)");

        builder.Property(i => i.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(i => i.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(i => i.DeletedAt).HasColumnName("deleted_at");

        builder.Ignore(i => i.EstaEliminada);

        builder.HasIndex(i => new { i.IdComprobanteCompra, i.Orden })
            .HasDatabaseName("ux_items_comprobante_compra_orden")
            .IsUnique();

        builder.HasIndex(i => i.IdTenant).HasDatabaseName("ix_items_comprobante_compra_tenant");
        builder.HasIndex(i => new { i.IdComprobanteCompra, i.IdTenant }).HasDatabaseName("ix_items_comprobante_compra_comprobante");
        builder.HasIndex(i => new { i.IdArticulo, i.IdTenant }).HasDatabaseName("ix_items_comprobante_compra_articulo");
        builder.HasIndex(i => i.IdAlicuotaIva).HasDatabaseName("ix_items_comprobante_compra_alicuota_iva");

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(i => i.IdTenant)
            .HasConstraintName("fk_items_comprobante_compra_tenant")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<ComprobanteCompra>()
            .WithMany()
            .HasForeignKey(i => new { i.IdComprobanteCompra, i.IdTenant })
            .HasPrincipalKey(c => new { c.Id, c.IdTenant })
            .HasConstraintName("fk_items_comprobante_compra_comprobante")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Articulo>()
            .WithMany()
            .HasForeignKey(i => new { i.IdArticulo, i.IdTenant })
            .HasPrincipalKey(a => new { a.Id, a.IdTenant })
            .HasConstraintName("fk_items_comprobante_compra_articulo")
            .OnDelete(DeleteBehavior.Restrict);

        // alicuotas_iva es global (ADR-11) — FK simple, sin id_tenant.
        builder.HasOne<AlicuotaIva>()
            .WithMany()
            .HasForeignKey(i => i.IdAlicuotaIva)
            .HasConstraintName("fk_items_comprobante_compra_alicuota_iva")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
