using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Ofertas;
using Ways.Domain.Organizacion;
using Ways.Domain.Stock;
using Ways.Domain.Ventas;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

/// <summary>
/// Mapea <see cref="ItemRemito"/> (proposal: Modelo de datos propuesto — §F, DB CHANGE GATE
/// aprobado). Shaped on <c>ItemComprobanteVentaConfiguration.cs</c> (a diferencia de
/// <see cref="ItemPresupuestoConfiguration"/>: esta línea SÍ congela costo y lote, la mercadería
/// efectivamente sale por este write site). Todos los índices de soporte se declaran a mano
/// (conteo vinculante: 8 índices en esta tabla, incl. la unicidad de <c>orden</c>).
/// </summary>
public class ItemRemitoConfiguration : IEntityTypeConfiguration<ItemRemito>
{
    public void Configure(EntityTypeBuilder<ItemRemito> builder)
    {
        builder.ToTable("items_remito", t =>
        {
            // CHECK 5 (proposal §F): mirrors ck_items_presupuesto_cantidad_positiva/
            // ck_items_comprobante_venta_cantidad_positiva.
            t.HasCheckConstraint("ck_items_remito_cantidad_positiva", "cantidad > 0");

            // CHECK 6 (proposal §F): costo NULL es "desconocido" (aún no salió), nunca colapsa a
            // cero — mirrors ck_items_comprobante_venta_costo_no_negativo.
            t.HasCheckConstraint(
                "ck_items_remito_costo_no_negativo",
                "costo_unitario IS NULL OR costo_unitario >= 0");

            // CHECK 7 (proposal §F): una marca "estimado" sin costo es irrepresentable — mirrors
            // ck_items_comprobante_venta_estimado_con_costo.
            t.HasCheckConstraint(
                "ck_items_remito_estimado_con_costo",
                "NOT costo_es_estimado OR costo_unitario IS NOT NULL");
        });

        builder.HasKey(i => i.Id).HasName("pk_items_remito");

        builder.Property(i => i.Id)
            .HasColumnName("id_item")
            .UseIdentityByDefaultColumn();

        builder.Property(i => i.IdTenant)
            .HasColumnName("id_tenant")
            .IsRequired();

        builder.Property(i => i.IdRemito).HasColumnName("id_remito").IsRequired();
        builder.Property(i => i.Orden).HasColumnName("orden").IsRequired();

        // Deliberadamente NOT NULL (proposal §F): un remito entrega mercadería, nunca un
        // servicio — mismo criterio que ItemPresupuesto.IdArticulo.
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

        builder.Property(i => i.CostoUnitario).HasColumnName("costo_unitario").HasColumnType("numeric(14,2)");

        builder.Property(i => i.CostoEsEstimado)
            .HasColumnName("costo_es_estimado")
            .HasDefaultValue(false)
            .IsRequired();

        builder.Property(i => i.IdLote).HasColumnName("id_lote");

        builder.Property(i => i.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(i => i.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(i => i.DeletedAt).HasColumnName("deleted_at");

        builder.Ignore(i => i.EstaEliminada);

        builder.HasIndex(i => i.IdTenant).HasDatabaseName("ix_items_remito_tenant");
        builder.HasIndex(i => new { i.IdRemito, i.IdTenant }).HasDatabaseName("ix_items_remito_remito");
        builder.HasIndex(i => new { i.IdArticulo, i.IdTenant }).HasDatabaseName("ix_items_remito_articulo");
        builder.HasIndex(i => new { i.IdListaPrecio, i.IdTenant }).HasDatabaseName("ix_items_remito_lista_precio");
        builder.HasIndex(i => new { i.IdOferta, i.IdTenant }).HasDatabaseName("ix_items_remito_oferta");

        // Simple: alicuotas_iva es global (ADR-11) — mismo criterio que
        // fk_items_presupuesto_alicuota_iva/fk_items_comprobante_venta_alicuota_iva.
        builder.HasIndex(i => i.IdAlicuotaIva).HasDatabaseName("ix_items_remito_alicuota_iva");

        // Soporte de fk_items_remito_lote (FK 22) — mismo criterio que
        // ix_items_comprobante_venta_lote.
        builder.HasIndex(i => new { i.IdLote, i.IdArticulo, i.IdTenant }).HasDatabaseName("ix_items_remito_lote");

        // ux_items_remito_orden: mirrors ux_items_presupuesto_orden/ux_items_comprobante_venta_orden
        // — NO cubre la FK 17 (segunda columna difiere), por lo que ix_items_remito_remito existe
        // aparte.
        builder.HasIndex(i => new { i.IdRemito, i.Orden })
            .HasDatabaseName("ux_items_remito_orden")
            .IsUnique();

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(i => i.IdTenant)
            .HasConstraintName("fk_items_remito_tenant")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Remito>()
            .WithMany()
            .HasForeignKey(i => new { i.IdRemito, i.IdTenant })
            .HasPrincipalKey(r => new { r.Id, r.IdTenant })
            .HasConstraintName("fk_items_remito_remito")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Articulo>()
            .WithMany()
            .HasForeignKey(i => new { i.IdArticulo, i.IdTenant })
            .HasPrincipalKey(a => new { a.Id, a.IdTenant })
            .HasConstraintName("fk_items_remito_articulo")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<ListaPrecio>()
            .WithMany()
            .HasForeignKey(i => new { i.IdListaPrecio, i.IdTenant })
            .HasPrincipalKey(l => new { l.Id, l.IdTenant })
            .HasConstraintName("fk_items_remito_lista_precio")
            .OnDelete(DeleteBehavior.Restrict);

        // Nullable, MATCH SIMPLE (default) — mismo criterio que fk_items_presupuesto_oferta.
        builder.HasOne<Oferta>()
            .WithMany()
            .HasForeignKey(i => new { i.IdOferta, i.IdTenant })
            .HasPrincipalKey(o => new { o.Id, o.IdTenant })
            .HasConstraintName("fk_items_remito_oferta")
            .OnDelete(DeleteBehavior.Restrict);

        // alicuotas_iva es global (ADR-11) — FK simple, sin id_tenant.
        builder.HasOne<AlicuotaIva>()
            .WithMany()
            .HasForeignKey(i => i.IdAlicuotaIva)
            .HasConstraintName("fk_items_remito_alicuota_iva")
            .OnDelete(DeleteBehavior.Restrict);

        // FK 22 (proposal §F): MATCH SIMPLE — garantiza a nivel de base que el lote de la línea
        // pertenece a su mismo artículo, mismo criterio que fk_items_comprobante_venta_lote.
        builder.HasOne<Lote>()
            .WithMany()
            .HasForeignKey(i => new { i.IdLote, i.IdArticulo, i.IdTenant })
            .HasPrincipalKey(l => new { l.Id, l.IdArticulo, l.IdTenant })
            .HasConstraintName("fk_items_remito_lote")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
