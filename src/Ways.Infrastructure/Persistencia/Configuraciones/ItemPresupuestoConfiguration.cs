using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Ofertas;
using Ways.Domain.Organizacion;
using Ways.Domain.Ventas;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

/// <summary>
/// Mapea <see cref="ItemPresupuesto"/> (proposal: Modelo de datos propuesto — §D, DB CHANGE
/// GATE aprobado). Shaped on <c>ItemComprobanteVentaConfiguration.cs</c>. Child scope:
/// <c>id_tenant</c> únicamente, sin FK propia a <c>puntos_venta</c> (criterio verbatim
/// <c>ItemComprobanteVentaConfiguration.cs:13-15</c>). Todos los índices de soporte se declaran
/// a mano (conteo vinculante: 7 índices en esta tabla, incl. la unicidad de <c>orden</c>).
/// </summary>
public class ItemPresupuestoConfiguration : IEntityTypeConfiguration<ItemPresupuesto>
{
    public void Configure(EntityTypeBuilder<ItemPresupuesto> builder)
    {
        builder.ToTable("items_presupuesto", t =>
        {
            // CHECK 2 (proposal §D): mirrors ck_items_comprobante_venta_cantidad_positiva /
            // ck_items_orden_compra_cantidad_positiva.
            t.HasCheckConstraint("ck_items_presupuesto_cantidad_positiva", "cantidad > 0");
        });

        builder.HasKey(i => i.Id).HasName("pk_items_presupuesto");

        builder.Property(i => i.Id)
            .HasColumnName("id_item")
            .UseIdentityByDefaultColumn();

        builder.Property(i => i.IdTenant)
            .HasColumnName("id_tenant")
            .IsRequired();

        builder.Property(i => i.IdPresupuesto).HasColumnName("id_presupuesto").IsRequired();
        builder.Property(i => i.Orden).HasColumnName("orden").IsRequired();

        // Deliberadamente NOT NULL (proposal §D): un presupuesto no tiene líneas de concepto
        // libre — mismo criterio que ItemOrdenCompra.IdArticulo.
        builder.Property(i => i.IdArticulo).HasColumnName("id_articulo").IsRequired();

        builder.Property(i => i.Descripcion).HasColumnName("descripcion").HasColumnType("text").IsRequired();

        builder.Property(i => i.Cantidad).HasColumnName("cantidad").HasColumnType("numeric(12,3)").IsRequired();
        builder.Property(i => i.PrecioUnitario).HasColumnName("precio_unitario").HasColumnType("numeric(14,2)").IsRequired();
        builder.Property(i => i.Descuento).HasColumnName("descuento").HasColumnType("numeric(14,2)").HasDefaultValue(0m).IsRequired();
        builder.Property(i => i.Total).HasColumnName("total").HasColumnType("numeric(14,2)").IsRequired();

        builder.Property(i => i.IdListaPrecio).HasColumnName("id_lista_precio").IsRequired();
        builder.Property(i => i.IdOferta).HasColumnName("id_oferta");
        builder.Property(i => i.IdAlicuotaIva).HasColumnName("id_alicuota_iva").IsRequired();
        builder.Property(i => i.PorcentajeIva).HasColumnName("porcentaje_iva").HasColumnType("numeric(5,2)").IsRequired();

        builder.Property(i => i.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(i => i.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(i => i.DeletedAt).HasColumnName("deleted_at");

        builder.Ignore(i => i.EstaEliminada);

        builder.HasIndex(i => i.IdTenant).HasDatabaseName("ix_items_presupuesto_tenant");
        builder.HasIndex(i => new { i.IdPresupuesto, i.IdTenant }).HasDatabaseName("ix_items_presupuesto_presupuesto");
        builder.HasIndex(i => new { i.IdArticulo, i.IdTenant }).HasDatabaseName("ix_items_presupuesto_articulo");
        builder.HasIndex(i => new { i.IdListaPrecio, i.IdTenant }).HasDatabaseName("ix_items_presupuesto_lista_precio");
        builder.HasIndex(i => new { i.IdOferta, i.IdTenant }).HasDatabaseName("ix_items_presupuesto_oferta");

        // Simple: alicuotas_iva es global (ADR-11) — mismo criterio que
        // fk_items_comprobante_venta_alicuota_iva/fk_items_orden_compra_*.
        builder.HasIndex(i => i.IdAlicuotaIva).HasDatabaseName("ix_items_presupuesto_alicuota_iva");

        // ux_items_presupuesto_orden: mirrors ux_items_comprobante_venta_orden/
        // ux_items_orden_compra_orden — NO cubre la FK 6 (segunda columna difiere), por lo que
        // ix_items_presupuesto_presupuesto existe aparte.
        builder.HasIndex(i => new { i.IdPresupuesto, i.Orden })
            .HasDatabaseName("ux_items_presupuesto_orden")
            .IsUnique();

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(i => i.IdTenant)
            .HasConstraintName("fk_items_presupuesto_tenant")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Presupuesto>()
            .WithMany()
            .HasForeignKey(i => new { i.IdPresupuesto, i.IdTenant })
            .HasPrincipalKey(p => new { p.Id, p.IdTenant })
            .HasConstraintName("fk_items_presupuesto_presupuesto")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Articulo>()
            .WithMany()
            .HasForeignKey(i => new { i.IdArticulo, i.IdTenant })
            .HasPrincipalKey(a => new { a.Id, a.IdTenant })
            .HasConstraintName("fk_items_presupuesto_articulo")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<ListaPrecio>()
            .WithMany()
            .HasForeignKey(i => new { i.IdListaPrecio, i.IdTenant })
            .HasPrincipalKey(l => new { l.Id, l.IdTenant })
            .HasConstraintName("fk_items_presupuesto_lista_precio")
            .OnDelete(DeleteBehavior.Restrict);

        // Nullable, MATCH SIMPLE (default) — mismo criterio que fk_items_comprobante_venta_oferta.
        builder.HasOne<Oferta>()
            .WithMany()
            .HasForeignKey(i => new { i.IdOferta, i.IdTenant })
            .HasPrincipalKey(o => new { o.Id, o.IdTenant })
            .HasConstraintName("fk_items_presupuesto_oferta")
            .OnDelete(DeleteBehavior.Restrict);

        // alicuotas_iva es global (ADR-11) — FK simple, sin id_tenant, mismo criterio que
        // fk_items_comprobante_venta_alicuota_iva.
        builder.HasOne<AlicuotaIva>()
            .WithMany()
            .HasForeignKey(i => i.IdAlicuotaIva)
            .HasConstraintName("fk_items_presupuesto_alicuota_iva")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
