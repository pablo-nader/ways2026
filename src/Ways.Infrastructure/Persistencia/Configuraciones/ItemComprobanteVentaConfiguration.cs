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
/// Mapea <see cref="ItemComprobanteVenta"/> (design: Table Shapes — write path A). Child scope:
/// <c>id_tenant</c> únicamente, sin FK compuesta a <c>puntos_venta</c> (se deriva del
/// comprobante padre).
/// </summary>
public class ItemComprobanteVentaConfiguration : IEntityTypeConfiguration<ItemComprobanteVenta>
{
    public void Configure(EntityTypeBuilder<ItemComprobanteVenta> builder)
    {
        builder.ToTable("items_comprobante_venta", t =>
        {
            // stage 9: costo NULL es "desconocido", nunca colapsa a cero (decisión 4).
            t.HasCheckConstraint(
                "ck_items_comprobante_venta_costo_no_negativo",
                "costo_unitario IS NULL OR costo_unitario >= 0");

            // Una marca "estimado" sin costo es irrepresentable (decisión 2).
            t.HasCheckConstraint(
                "ck_items_comprobante_venta_estimado_con_costo",
                "NOT costo_es_estimado OR costo_unitario IS NOT NULL");
        });

        builder.HasKey(i => i.Id).HasName("pk_items_comprobante_venta");

        builder.Property(i => i.Id)
            .HasColumnName("id_item")
            .UseIdentityByDefaultColumn();

        builder.Property(i => i.IdTenant)
            .HasColumnName("id_tenant")
            .IsRequired();

        builder.Property(i => i.IdComprobanteVenta).HasColumnName("id_comprobante_venta").IsRequired();
        builder.Property(i => i.Orden).HasColumnName("orden").IsRequired();

        // NULL solo en líneas de concepto libre (doc 10 §4) — sin camino de escritura en esta
        // etapa, la columna queda lista.
        builder.Property(i => i.IdArticulo).HasColumnName("id_articulo");

        builder.Property(i => i.Descripcion).HasColumnName("descripcion").HasColumnType("text").IsRequired();
        builder.Property(i => i.CodigoBarra).HasColumnName("codigo_barra").HasColumnType("text");

        builder.Property(i => i.IdArea).HasColumnName("id_area").IsRequired();
        builder.Property(i => i.IdListaPrecio).HasColumnName("id_lista_precio").IsRequired();
        builder.Property(i => i.IdOferta).HasColumnName("id_oferta");

        builder.Property(i => i.IdAlicuotaIva).HasColumnName("id_alicuota_iva").IsRequired();
        builder.Property(i => i.PorcentajeIva).HasColumnName("porcentaje_iva").HasColumnType("numeric(5,2)").IsRequired();

        builder.Property(i => i.Cantidad).HasColumnName("cantidad").HasColumnType("numeric(12,3)").IsRequired();
        builder.Property(i => i.PrecioUnitario).HasColumnName("precio_unitario").HasColumnType("numeric(14,2)").IsRequired();

        builder.Property(i => i.Descuento)
            .HasColumnName("descuento")
            .HasColumnType("numeric(14,2)")
            .HasDefaultValue(0m)
            .IsRequired();

        builder.Property(i => i.Total).HasColumnName("total").HasColumnType("numeric(14,2)").IsRequired();

        builder.Property(i => i.CostoUnitario).HasColumnName("costo_unitario").HasColumnType("numeric(14,2)");

        builder.Property(i => i.CostoEsEstimado)
            .HasColumnName("costo_es_estimado")
            .HasDefaultValue(false)
            .IsRequired();

        // Etapa 12 (proposal decisión 8, gate §F): snapshot del lote, columna creada acá,
        // escrita recién en slice 8.
        builder.Property(i => i.IdLote).HasColumnName("id_lote");

        builder.Property(i => i.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(i => i.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(i => i.DeletedAt).HasColumnName("deleted_at");

        builder.Ignore(i => i.EstaEliminada);

        builder.HasIndex(i => new { i.IdComprobanteVenta, i.Orden })
            .HasDatabaseName("ux_items_comprobante_venta_orden")
            .IsUnique();

        builder.HasIndex(i => i.IdTenant).HasDatabaseName("ix_items_comprobante_venta_tenant");
        builder.HasIndex(i => new { i.IdComprobanteVenta, i.IdTenant }).HasDatabaseName("ix_items_comprobante_venta_comprobante");
        builder.HasIndex(i => new { i.IdArticulo, i.IdTenant }).HasDatabaseName("ix_items_comprobante_venta_articulo");
        builder.HasIndex(i => new { i.IdArea, i.IdTenant }).HasDatabaseName("ix_items_comprobante_venta_area");
        builder.HasIndex(i => new { i.IdListaPrecio, i.IdTenant }).HasDatabaseName("ix_items_comprobante_venta_lista_precio");
        builder.HasIndex(i => new { i.IdOferta, i.IdTenant }).HasDatabaseName("ix_items_comprobante_venta_oferta");
        builder.HasIndex(i => i.IdAlicuotaIva).HasDatabaseName("ix_items_comprobante_venta_alicuota_iva");

        // Etapa 12 (proposal decisión 8, gate §F): soporte de fk_items_comprobante_venta_lote.
        builder.HasIndex(i => new { i.IdLote, i.IdArticulo, i.IdTenant }).HasDatabaseName("ix_items_comprobante_venta_lote");

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(i => i.IdTenant)
            .HasConstraintName("fk_items_comprobante_venta_tenant")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<ComprobanteVenta>()
            .WithMany()
            .HasForeignKey(i => new { i.IdComprobanteVenta, i.IdTenant })
            .HasPrincipalKey(c => new { c.Id, c.IdTenant })
            .HasConstraintName("fk_items_comprobante_venta_comprobante")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Articulo>()
            .WithMany()
            .HasForeignKey(i => new { i.IdArticulo, i.IdTenant })
            .HasPrincipalKey(a => new { a.Id, a.IdTenant })
            .HasConstraintName("fk_items_comprobante_venta_articulo")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Area>()
            .WithMany()
            .HasForeignKey(i => new { i.IdArea, i.IdTenant })
            .HasPrincipalKey(a => new { a.Id, a.IdTenant })
            .HasConstraintName("fk_items_comprobante_venta_area")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<ListaPrecio>()
            .WithMany()
            .HasForeignKey(i => new { i.IdListaPrecio, i.IdTenant })
            .HasPrincipalKey(l => new { l.Id, l.IdTenant })
            .HasConstraintName("fk_items_comprobante_venta_lista_precio")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Oferta>()
            .WithMany()
            .HasForeignKey(i => new { i.IdOferta, i.IdTenant })
            .HasPrincipalKey(o => new { o.Id, o.IdTenant })
            .HasConstraintName("fk_items_comprobante_venta_oferta")
            .OnDelete(DeleteBehavior.Restrict);

        // alicuotas_iva es global (ADR-11) — FK simple, sin id_tenant.
        builder.HasOne<AlicuotaIva>()
            .WithMany()
            .HasForeignKey(i => i.IdAlicuotaIva)
            .HasConstraintName("fk_items_comprobante_venta_alicuota_iva")
            .OnDelete(DeleteBehavior.Restrict);

        // Etapa 12 (proposal decisión 8, gate §F, gate amendment 2): MATCH SIMPLE (default de
        // Postgres) — IdArticulo es nullable acá (líneas de concepto libre, doc 10 §4), pero una
        // línea con lote necesariamente referencia un artículo; la FK se evalúa solo cuando las
        // tres columnas son no-NULL, la aplicación valida ese emparejamiento.
        builder.HasOne<Lote>()
            .WithMany()
            .HasForeignKey(i => new { i.IdLote, i.IdArticulo, i.IdTenant })
            .HasPrincipalKey(l => new { l.Id, l.IdArticulo, l.IdTenant })
            .HasConstraintName("fk_items_comprobante_venta_lote")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
