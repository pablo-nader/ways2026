using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Articulos;
using Ways.Domain.Organizacion;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

/// <summary>
/// Mapea <see cref="ImagenDeArticulo"/>: scoping tenant-wide (<c>id_tenant</c>, sin <c>id_empresa</c>),
/// como el artículo del que cuelga. La administran quienes editan el catálogo; la lee además el modo de
/// acceso <c>publico</c> (política <c>imagenes_articulo_publico_lectura</c>).
/// </summary>
public class ImagenDeArticuloConfiguration : IEntityTypeConfiguration<ImagenDeArticulo>
{
    public void Configure(EntityTypeBuilder<ImagenDeArticulo> builder)
    {
        builder.ToTable("imagenes_articulo", t =>
            // Repite lo que valida ImagenDeArticulo.Validar. El IS NOT NULL del tipo es explícito: un CHECK
            // que evalúa a NULL pasa, y NULL IN (...) es NULL.
            t.HasCheckConstraint(
                "ck_imagenes_articulo_contenido",
                "octet_length(contenido) BETWEEN 1 AND 1048576 AND " +
                "content_type IS NOT NULL AND content_type IN ('image/png', 'image/jpeg', 'image/webp')"));

        builder.HasKey(i => i.Id).HasName("pk_imagenes_articulo");

        builder.Property(i => i.Id)
            .HasColumnName("id_imagen_articulo")
            .UseIdentityByDefaultColumn();

        builder.Property(i => i.IdTenant).HasColumnName("id_tenant").IsRequired();
        builder.Property(i => i.IdArticulo).HasColumnName("id_articulo").IsRequired();

        builder.Property(i => i.Contenido).HasColumnName("contenido").HasColumnType("bytea").IsRequired();
        builder.Property(i => i.ContentType).HasColumnName("content_type").HasColumnType("text").IsRequired();
        builder.Property(i => i.ActualizadoEn).HasColumnName("actualizado_en").IsRequired();

        builder.Property(i => i.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(i => i.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(i => i.DeletedAt).HasColumnName("deleted_at");

        builder.Ignore(i => i.EstaEliminada);

        // Una imagen viva por artículo.
        builder.HasIndex(i => i.IdArticulo)
            .HasDatabaseName("ux_imagenes_articulo_articulo")
            .IsUnique()
            .HasFilter("deleted_at IS NULL");

        // Soporte de las FKs: el índice parcial de arriba no cubre las imágenes dadas de baja.
        builder.HasIndex(i => i.IdTenant).HasDatabaseName("ix_imagenes_articulo_tenant");
        builder.HasIndex(i => new { i.IdArticulo, i.IdTenant }).HasDatabaseName("ix_imagenes_articulo_articulo");

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(i => i.IdTenant)
            .HasConstraintName("fk_imagenes_articulo_tenant")
            .OnDelete(DeleteBehavior.Restrict);

        // Compuesta (id_articulo, id_tenant) contra la AK de articulos: la imagen de un tenant no puede
        // colgar del artículo de otro (ADR-9).
        builder.HasOne<Articulo>()
            .WithMany()
            .HasForeignKey(i => new { i.IdArticulo, i.IdTenant })
            .HasPrincipalKey(a => new { a.Id, a.IdTenant })
            .HasConstraintName("fk_imagenes_articulo_articulo")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
