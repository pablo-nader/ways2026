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

        // Nombre explícito en snake_case (doc 10): sin esto, EF nombra el índice de soporte
        // de fk_articulos_empresas_articulo con su convención propia
        // (IX_articulos_empresas_id_articulo_id_tenant, PascalCase) — mismo fix que el resto
        // de las FKs compuestas de esta etapa (ver ArticuloConfiguration).
        builder.HasIndex(ae => new { ae.IdArticulo, ae.IdTenant }).HasDatabaseName("ix_articulos_empresas_articulo");

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
