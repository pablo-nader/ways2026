using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Catalogos;
using Ways.Domain.Ofertas;
using Ways.Domain.Organizacion;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

/// <summary>
/// Mapea <see cref="OfertaLista"/> (design decision 1, Table Shapes): junction PK-only, sin
/// auditoría ni baja lógica — mismo criterio que <c>ArticuloEmpresaConfiguration</c>. La PK se
/// nombra explícita <c>pk_ofertas_listas</c> (a diferencia del default de EF que dejó
/// <c>PK_articulos_empresas</c> en PascalCase) y las tres FKs compuestas incluyen
/// <c>id_tenant</c> — las claves alternas ya existen en las tres tablas referenciadas.
/// </summary>
public class OfertaListaConfiguration : IEntityTypeConfiguration<OfertaLista>
{
    public void Configure(EntityTypeBuilder<OfertaLista> builder)
    {
        builder.ToTable("ofertas_listas");

        builder.HasKey(ol => new { ol.IdOferta, ol.IdListaPrecio }).HasName("pk_ofertas_listas");

        builder.Property(ol => ol.IdOferta).HasColumnName("id_oferta");
        builder.Property(ol => ol.IdListaPrecio).HasColumnName("id_lista_precio");

        builder.Property(ol => ol.IdTenant)
            .HasColumnName("id_tenant")
            .IsRequired();

        builder.HasIndex(ol => ol.IdTenant).HasDatabaseName("ix_ofertas_listas_tenant");
        builder.HasIndex(ol => new { ol.IdOferta, ol.IdTenant }).HasDatabaseName("ix_ofertas_listas_oferta");
        builder.HasIndex(ol => new { ol.IdListaPrecio, ol.IdTenant }).HasDatabaseName("ix_ofertas_listas_lista_precio");

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(ol => ol.IdTenant)
            .HasConstraintName("fk_ofertas_listas_tenant")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Oferta>()
            .WithMany()
            .HasForeignKey(ol => new { ol.IdOferta, ol.IdTenant })
            .HasPrincipalKey(o => new { o.Id, o.IdTenant })
            .HasConstraintName("fk_ofertas_listas_oferta")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<ListaPrecio>()
            .WithMany()
            .HasForeignKey(ol => new { ol.IdListaPrecio, ol.IdTenant })
            .HasPrincipalKey(l => new { l.Id, l.IdTenant })
            .HasConstraintName("fk_ofertas_listas_lista_precio")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
