using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Articulos;
using Ways.Domain.Organizacion;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

/// <summary>
/// Mapea <see cref="ArticuloEmpresa"/> (design decision 1, Table Shapes): junction PK-only,
/// sin auditoría ni baja lógica — solo tiene filas cuando el artículo tiene
/// <c>disponible_para_todas = false</c> (regla de servicio, <see cref="Domain.Articulos.ReglaDeArticulos"/>,
/// no una constraint de esquema).
/// </summary>
public class ArticuloEmpresaConfiguration : IEntityTypeConfiguration<ArticuloEmpresa>
{
    public void Configure(EntityTypeBuilder<ArticuloEmpresa> builder)
    {
        builder.ToTable("articulos_empresas");

        builder.HasKey(ae => new { ae.IdArticulo, ae.IdEmpresa });

        builder.Property(ae => ae.IdArticulo).HasColumnName("id_articulo");
        builder.Property(ae => ae.IdEmpresa).HasColumnName("id_empresa");

        builder.Property(ae => ae.IdTenant)
            .HasColumnName("id_tenant")
            .IsRequired();

        builder.HasIndex(ae => ae.IdTenant).HasDatabaseName("ix_articulos_empresas_tenant");
        builder.HasIndex(ae => new { ae.IdEmpresa, ae.IdTenant }).HasDatabaseName("ix_articulos_empresas_empresa");

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(ae => ae.IdTenant)
            .HasConstraintName("fk_articulos_empresas_tenant")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Articulo>()
            .WithMany()
            .HasForeignKey(ae => new { ae.IdArticulo, ae.IdTenant })
            .HasPrincipalKey(a => new { a.Id, a.IdTenant })
            .HasConstraintName("fk_articulos_empresas_articulo")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Empresa>()
            .WithMany()
            .HasForeignKey(ae => new { ae.IdEmpresa, ae.IdTenant })
            .HasPrincipalKey(e => new { e.Id, e.IdTenant })
            .HasConstraintName("fk_articulos_empresas_empresa")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
