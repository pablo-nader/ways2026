using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Organizacion;
using Ways.Domain.Stock;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

/// <summary>
/// Mapea <see cref="StockLote"/> (proposal gate §B). PK-only, sin auditoría — mismo criterio que
/// <c>StockConfiguration</c>. Sin CHECK sobre <c>cantidad</c>: un saldo de lote negativo está
/// permitido en el mostrador (legacy parity, proposal decisión 7).
/// </summary>
public class StockLoteConfiguration : IEntityTypeConfiguration<StockLote>
{
    public void Configure(EntityTypeBuilder<StockLote> builder)
    {
        builder.ToTable("stock_lotes");

        builder.HasKey(s => new { s.IdArticulo, s.IdPuntoVenta, s.IdLote }).HasName("pk_stock_lotes");

        builder.Property(s => s.IdArticulo).HasColumnName("id_articulo").ValueGeneratedNever();
        builder.Property(s => s.IdPuntoVenta).HasColumnName("id_punto_venta").ValueGeneratedNever();
        builder.Property(s => s.IdLote).HasColumnName("id_lote").ValueGeneratedNever();

        builder.Property(s => s.IdTenant)
            .HasColumnName("id_tenant")
            .IsRequired();

        builder.Property(s => s.Cantidad)
            .HasColumnName("cantidad")
            .HasColumnType("numeric(12,3)")
            .HasDefaultValue(0m)
            .IsRequired();

        builder.HasIndex(s => s.IdTenant).HasDatabaseName("ix_stock_lotes_tenant");

        // Ruta de acceso del reporte de vencimientos (slice 13) — mismo shape que
        // ix_stock_punto_venta.
        builder.HasIndex(s => new { s.IdPuntoVenta, s.IdTenant }).HasDatabaseName("ix_stock_lotes_punto_venta");

        // Soporte de fk_stock_lotes_lote (evita el índice implícito PascalCase de EF).
        builder.HasIndex(s => new { s.IdLote, s.IdArticulo, s.IdTenant }).HasDatabaseName("ix_stock_lotes_lote");

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(s => s.IdTenant)
            .HasConstraintName("fk_stock_lotes_tenant")
            .OnDelete(DeleteBehavior.Restrict);

        // Coherencia lote/artículo a nivel de base — mismo target que el resto de las FKs de
        // lote (proposal gate §A/§B).
        builder.HasOne<Lote>()
            .WithMany()
            .HasForeignKey(s => new { s.IdLote, s.IdArticulo, s.IdTenant })
            .HasPrincipalKey(l => new { l.Id, l.IdArticulo, l.IdTenant })
            .HasConstraintName("fk_stock_lotes_lote")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<PuntoVenta>()
            .WithMany()
            .HasForeignKey(s => new { s.IdPuntoVenta, s.IdTenant })
            .HasPrincipalKey(p => new { p.Id, p.IdTenant })
            .HasConstraintName("fk_stock_lotes_punto_venta")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
