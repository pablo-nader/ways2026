using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Articulos;
using Ways.Domain.Compras;
using Ways.Domain.Organizacion;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

/// <summary>
/// Mapea <see cref="ItemOrdenCompra"/> (proposal: Modelo de datos propuesto — §C, DB CHANGE GATE
/// aprobado). Shaped on <c>ItemComprobanteCompraConfiguration.cs:16-143</c>. Child scope:
/// <c>id_tenant</c> únicamente, sin FK propia a <c>puntos_venta</c> (criterio verbatim
/// <c>ItemComprobanteCompraConfiguration.cs:12-14</c>). Todos los índices de soporte se declaran
/// a mano (conteo vinculante: 4 índices en esta tabla, incl. la unicidad de <c>orden</c>).
/// </summary>
public class ItemOrdenCompraConfiguration : IEntityTypeConfiguration<ItemOrdenCompra>
{
    public void Configure(EntityTypeBuilder<ItemOrdenCompra> builder)
    {
        builder.ToTable("items_orden_compra", t =>
        {
            // CHECK 3 (proposal §C): mirrors ck_items_comprobante_compra_cantidad_positiva.
            t.HasCheckConstraint("ck_items_orden_compra_cantidad_positiva", "cantidad_pedida > 0");

            // CHECK 4 (proposal §C): >= 0, no > 0 — una línea de bonificación es real, y NULL
            // significa "no cotizado" (misma familia que ck_items_comprobante_compra_costo_no_negativo).
            t.HasCheckConstraint(
                "ck_items_orden_compra_costo_no_negativo",
                "costo_unitario_estimado IS NULL OR costo_unitario_estimado >= 0");
        });

        builder.HasKey(i => i.Id).HasName("pk_items_orden_compra");

        builder.Property(i => i.Id)
            .HasColumnName("id_item")
            .UseIdentityByDefaultColumn();

        builder.Property(i => i.IdTenant)
            .HasColumnName("id_tenant")
            .IsRequired();

        builder.Property(i => i.IdOrdenCompra).HasColumnName("id_orden_compra").IsRequired();
        builder.Property(i => i.Orden).HasColumnName("orden").IsRequired();

        // Deliberadamente NOT NULL (proposal §C): una línea sin artículo no puede recibirse
        // contra stock — mismo criterio que ItemComprobanteCompra.IdArticulo.
        builder.Property(i => i.IdArticulo).HasColumnName("id_articulo").IsRequired();

        builder.Property(i => i.Descripcion).HasColumnName("descripcion").HasColumnType("text").IsRequired();

        builder.Property(i => i.CantidadPedida).HasColumnName("cantidad_pedida").HasColumnType("numeric(12,3)").IsRequired();
        builder.Property(i => i.CostoUnitarioEstimado).HasColumnName("costo_unitario_estimado").HasColumnType("numeric(14,4)");

        builder.Property(i => i.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(i => i.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(i => i.DeletedAt).HasColumnName("deleted_at");

        builder.Ignore(i => i.EstaEliminada);

        builder.HasIndex(i => i.IdTenant).HasDatabaseName("ix_items_orden_compra_tenant");
        builder.HasIndex(i => new { i.IdOrdenCompra, i.IdTenant }).HasDatabaseName("ix_items_orden_compra_orden_compra");
        builder.HasIndex(i => new { i.IdArticulo, i.IdTenant }).HasDatabaseName("ix_items_orden_compra_articulo");

        // ux_items_orden_compra_orden: mirrors ux_items_comprobante_compra_orden — NO cubre la
        // FK 7 (segunda columna difiere), por lo que ix_items_orden_compra_orden_compra existe
        // aparte, el mismo par que items_comprobante_compra ya lleva.
        builder.HasIndex(i => new { i.IdOrdenCompra, i.Orden })
            .HasDatabaseName("ux_items_orden_compra_orden")
            .IsUnique();

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(i => i.IdTenant)
            .HasConstraintName("fk_items_orden_compra_tenant")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<OrdenCompra>()
            .WithMany()
            .HasForeignKey(i => new { i.IdOrdenCompra, i.IdTenant })
            .HasPrincipalKey(o => new { o.Id, o.IdTenant })
            .HasConstraintName("fk_items_orden_compra_orden_compra")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Articulo>()
            .WithMany()
            .HasForeignKey(i => new { i.IdArticulo, i.IdTenant })
            .HasPrincipalKey(a => new { a.Id, a.IdTenant })
            .HasConstraintName("fk_items_orden_compra_articulo")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
