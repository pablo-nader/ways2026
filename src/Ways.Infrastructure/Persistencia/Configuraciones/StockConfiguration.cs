using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Articulos;
using Ways.Domain.Organizacion;
using Ways.Domain.Stock;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

/// <summary>
/// Mapea <see cref="Stock"/> (design: Table Shapes — write path B). PK-only, sin auditoría —
/// mismo criterio que <c>NumeracionComprobanteConfiguration</c>. Sin CHECK sobre
/// <c>cantidad</c>: stock negativo está permitido (legacy parity, proposal decisión 7).
/// </summary>
public class StockConfiguration : IEntityTypeConfiguration<Stock>
{
    public void Configure(EntityTypeBuilder<Stock> builder)
    {
        builder.ToTable("stock");

        builder.HasKey(s => new { s.IdArticulo, s.IdSucursal }).HasName("pk_stock");

        builder.Property(s => s.IdArticulo).HasColumnName("id_articulo").ValueGeneratedNever();
        builder.Property(s => s.IdSucursal).HasColumnName("id_sucursal").ValueGeneratedNever();

        builder.Property(s => s.IdTenant)
            .HasColumnName("id_tenant")
            .IsRequired();

        builder.Property(s => s.Cantidad)
            .HasColumnName("cantidad")
            .HasColumnType("numeric(12,3)")
            .HasDefaultValue(0m)
            .IsRequired();

        builder.Property(s => s.Minimo).HasColumnName("minimo").HasColumnType("numeric(12,3)");
        builder.Property(s => s.Reposicion).HasColumnName("reposicion").HasColumnType("numeric(12,3)");

        builder.HasIndex(s => s.IdTenant).HasDatabaseName("ix_stock_tenant");

        // Nombre explícito (mismo fix documentado en ArticuloEmpresaConfiguration/
        // NumeracionComprobanteConfiguration): sin esto, EF nombra el índice de soporte de
        // fk_stock_sucursal con su convención propia (PascalCase).
        builder.HasIndex(s => new { s.IdSucursal, s.IdTenant }).HasDatabaseName("ix_stock_sucursal");
        builder.HasIndex(s => new { s.IdArticulo, s.IdTenant }).HasDatabaseName("ix_stock_articulo");

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(s => s.IdTenant)
            .HasConstraintName("fk_stock_tenant")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Articulo>()
            .WithMany()
            .HasForeignKey(s => new { s.IdArticulo, s.IdTenant })
            .HasPrincipalKey(a => new { a.Id, a.IdTenant })
            .HasConstraintName("fk_stock_articulo")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Sucursal>()
            .WithMany()
            .HasForeignKey(s => new { s.IdSucursal, s.IdTenant })
            .HasPrincipalKey(p => new { p.Id, p.IdTenant })
            .HasConstraintName("fk_stock_sucursal")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
