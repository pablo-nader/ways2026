using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Articulos;
using Ways.Domain.Organizacion;
using Ways.Domain.Stock;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

/// <summary>
/// Mapea <see cref="Lote"/> (proposal gate §A, DB CHANGE GATE aprobado con enmiendas). Catálogo
/// tenant-wide con auditoría completa, mismo criterio que <c>ArticuloConfiguration</c>. Todos
/// los nombres son explícitos — la convención PascalCase por default de EF se pisa siempre.
/// </summary>
public class LoteConfiguration : IEntityTypeConfiguration<Lote>
{
    public void Configure(EntityTypeBuilder<Lote> builder)
    {
        builder.ToTable("lotes", t =>
        {
            t.HasCheckConstraint(
                "ck_lotes_vencimiento_segun_tipo",
                "(es_sin_identificar AND fecha_vencimiento IS NULL) OR (NOT es_sin_identificar AND fecha_vencimiento IS NOT NULL)");

            t.HasCheckConstraint("ck_lotes_codigo_no_vacio", "length(btrim(codigo)) > 0");
        });

        builder.HasKey(l => l.Id).HasName("pk_lotes");

        builder.Property(l => l.Id)
            .HasColumnName("id_lote")
            .UseIdentityByDefaultColumn();

        builder.Property(l => l.IdTenant)
            .HasColumnName("id_tenant")
            .IsRequired();

        // Clave alterna — principal de las FKs compuestas de stock_lotes/movimientos_stock/
        // items_comprobante_venta/items_comprobante_compra (proposal gate §A): así la base misma
        // garantiza "el lote pertenece a ese artículo". Nombre literal del gate, no "ak_" pese a
        // ser una alternate key — el contrato lo pinea así.
        builder.HasAlternateKey(l => new { l.Id, l.IdArticulo, l.IdTenant })
            .HasName("ux_lotes_id_articulo_tenant");

        builder.Property(l => l.IdArticulo).HasColumnName("id_articulo").IsRequired();

        builder.Property(l => l.Codigo)
            .HasColumnName("codigo")
            .HasColumnType("text")
            .IsRequired();

        builder.Property(l => l.FechaVencimiento).HasColumnName("fecha_vencimiento").HasColumnType("date");

        builder.Property(l => l.EsSinIdentificar)
            .HasColumnName("es_sin_identificar")
            .HasDefaultValue(false)
            .IsRequired();

        builder.Property(l => l.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(l => l.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(l => l.DeletedAt).HasColumnName("deleted_at");

        builder.Ignore(l => l.EstaEliminada);

        // Clave natural — target del get-or-create (slice 3, ServicioDeLotes.ResolverOCrearAsync).
        builder.HasIndex(l => new { l.IdTenant, l.IdArticulo, l.Codigo })
            .HasDatabaseName("ux_lotes_articulo_codigo")
            .HasFilter("deleted_at IS NULL")
            .IsUnique();

        // A lo sumo un lote "sin identificar" por artículo (proposal decisión 3).
        builder.HasIndex(l => new { l.IdTenant, l.IdArticulo })
            .HasDatabaseName("ux_lotes_sin_identificar")
            .HasFilter("es_sin_identificar AND deleted_at IS NULL")
            .IsUnique();

        builder.HasIndex(l => l.IdTenant).HasDatabaseName("ix_lotes_tenant");

        // Nombre explícito (mismo fix que ArticuloConfiguration): sin esto EF nombra el índice
        // de soporte de fk_lotes_articulo con su convención propia (PascalCase).
        builder.HasIndex(l => new { l.IdArticulo, l.IdTenant }).HasDatabaseName("ix_lotes_articulo");

        // Filtro del reporte de vencimientos (slice 13).
        builder.HasIndex(l => new { l.IdTenant, l.FechaVencimiento })
            .HasDatabaseName("ix_lotes_vencimiento")
            .HasFilter("deleted_at IS NULL");

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(l => l.IdTenant)
            .HasConstraintName("fk_lotes_tenant")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Articulo>()
            .WithMany()
            .HasForeignKey(l => new { l.IdArticulo, l.IdTenant })
            .HasPrincipalKey(a => new { a.Id, a.IdTenant })
            .HasConstraintName("fk_lotes_articulo")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
