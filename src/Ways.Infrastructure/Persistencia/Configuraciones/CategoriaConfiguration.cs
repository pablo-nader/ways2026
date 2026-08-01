using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Catalogos;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

public class CategoriaConfiguration : ConfiguracionDeCatalogo<Categoria>
{
    protected override string Tabla => "categorias";
    protected override string ColumnaId => "id_categoria";

    protected override void ConfigurarPropio(EntityTypeBuilder<Categoria> builder)
    {
        builder.Property(c => c.Orden).HasColumnName("orden").IsRequired();
        builder.Property(c => c.IdCategoriaPadre).HasColumnName("id_categoria_padre");

        // Habilita la FK compuesta a sí misma (ADR-9): una categoría de un tenant no puede
        // colgar de la de otro tenant ni por bug. Sin restricción de profundidad acá — eso
        // lo valida ReglaDeCategorias en dominio (ADR-12), no una constraint de SQL.
        builder.HasAlternateKey(c => new { c.Id, c.IdTenant })
            .HasName("ak_categorias_id_categoria_id_tenant");

        builder.HasOne<Categoria>()
            .WithMany()
            .HasForeignKey(c => new { c.IdCategoriaPadre, c.IdTenant })
            .HasPrincipalKey(c => new { c.Id, c.IdTenant })
            .HasConstraintName("fk_categorias_padre")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(c => new { c.IdCategoriaPadre, c.IdTenant })
            .HasDatabaseName("ix_categorias_padre");
    }
}
