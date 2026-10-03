using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Articulos;
using Ways.Domain.Organizacion;
using Ways.Domain.Proveedores;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

/// <summary>
/// Mapea <see cref="CodigoProveedor"/>: tenant-wide, N filas por (artículo, proveedor), único por
/// (tenant, proveedor, código) entre las filas vivas.
/// </summary>
public class CodigoProveedorConfiguration : IEntityTypeConfiguration<CodigoProveedor>
{
    public void Configure(EntityTypeBuilder<CodigoProveedor> builder)
    {
        builder.ToTable("codigos_proveedor", t => t.HasCheckConstraint(
            "ck_codigos_proveedor_codigo_normalizado", "codigo = btrim(codigo) AND codigo <> ''"));

        builder.HasKey(c => c.Id);

        builder.Property(c => c.Id)
            .HasColumnName("id_codigo_proveedor")
            .UseIdentityByDefaultColumn();

        builder.Property(c => c.IdTenant)
            .HasColumnName("id_tenant")
            .IsRequired();

        builder.Property(c => c.IdArticulo)
            .HasColumnName("id_articulo")
            .IsRequired();

        builder.Property(c => c.IdProveedor)
            .HasColumnName("id_proveedor")
            .IsRequired();

        builder.Property(c => c.Codigo)
            .HasColumnName("codigo")
            .HasColumnType("citext")
            .HasMaxLength(ReglaDeCodigoProveedor.LongitudMaxima)
            .IsRequired();

        builder.Property(c => c.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(c => c.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(c => c.DeletedAt).HasColumnName("deleted_at");

        builder.Ignore(c => c.EstaEliminada);

        builder.HasIndex(c => new { c.IdTenant, c.IdProveedor, c.Codigo })
            .HasDatabaseName("ux_codigos_proveedor_proveedor_codigo")
            .HasFilter("deleted_at IS NULL")
            .IsUnique();

        builder.HasIndex(c => c.IdTenant).HasDatabaseName("ix_codigos_proveedor_tenant");
        builder.HasIndex(c => new { c.IdArticulo, c.IdTenant }).HasDatabaseName("ix_codigos_proveedor_articulo");
        builder.HasIndex(c => new { c.IdProveedor, c.IdTenant }).HasDatabaseName("ix_codigos_proveedor_proveedor");

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(c => c.IdTenant)
            .HasConstraintName("fk_codigos_proveedor_tenant")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Articulo>()
            .WithMany()
            .HasForeignKey(c => new { c.IdArticulo, c.IdTenant })
            .HasPrincipalKey(a => new { a.Id, a.IdTenant })
            .HasConstraintName("fk_codigos_proveedor_articulo")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Proveedor>()
            .WithMany()
            .HasForeignKey(c => new { c.IdProveedor, c.IdTenant })
            .HasPrincipalKey(p => new { p.Id, p.IdTenant })
            .HasConstraintName("fk_codigos_proveedor_proveedor")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
