using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Articulos;
using Ways.Domain.Organizacion;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

/// <summary>
/// Mapea <see cref="CodigoBarra"/> (design decision 1, Table Shapes): tenant-wide, N filas
/// por artículo, único por tenant.
/// </summary>
public class CodigoBarraConfiguration : IEntityTypeConfiguration<CodigoBarra>
{
    public void Configure(EntityTypeBuilder<CodigoBarra> builder)
    {
        builder.ToTable("codigos_barra");

        builder.HasKey(c => c.Id);

        builder.Property(c => c.Id)
            .HasColumnName("id_codigo_barra")
            .UseIdentityByDefaultColumn();

        builder.Property(c => c.IdTenant)
            .HasColumnName("id_tenant")
            .IsRequired();

        builder.Property(c => c.IdArticulo)
            .HasColumnName("id_articulo")
            .IsRequired();

        builder.Property(c => c.Codigo)
            .HasColumnName("codigo")
            .HasColumnType("citext")
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(c => c.Activo)
            .HasColumnName("activo")
            .HasDefaultValue(true)
            .IsRequired();

        builder.Property(c => c.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(c => c.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(c => c.DeletedAt).HasColumnName("deleted_at");

        builder.Ignore(c => c.EstaEliminada);

        // spec "Barcode Uniqueness Per Tenant": un código pertenece a exactamente un artículo
        // del tenant, sin overrides; el mismo código puede repetirse entre tenants distintos.
        builder.HasIndex(c => new { c.Codigo, c.IdTenant })
            .HasDatabaseName("ux_codigos_barra_codigo_tenant")
            .HasFilter("deleted_at IS NULL")
            .IsUnique();

        builder.HasIndex(c => c.IdTenant).HasDatabaseName("ix_codigos_barra_tenant");
        builder.HasIndex(c => new { c.IdArticulo, c.IdTenant }).HasDatabaseName("ix_codigos_barra_articulo");

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(c => c.IdTenant)
            .HasConstraintName("fk_codigos_barra_tenant")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Articulo>()
            .WithMany()
            .HasForeignKey(c => new { c.IdArticulo, c.IdTenant })
            .HasPrincipalKey(a => new { a.Id, a.IdTenant })
            .HasConstraintName("fk_codigos_barra_articulo")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
