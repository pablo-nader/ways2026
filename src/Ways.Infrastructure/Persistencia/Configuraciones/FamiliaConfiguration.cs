using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Articulos;
using Ways.Domain.Organizacion;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

/// <summary>
/// Mapea <see cref="Familia"/> (doc 10 §3): tabla tenant-wide sin <c>id_empresa</c>, mismo alcance
/// que <c>articulos</c>. La familia no guarda valores — solo su nombre —, así que no hay nada más
/// que mapear; la pertenencia vive en <c>articulos.id_familia</c> (<see cref="ArticuloConfiguration"/>).
/// </summary>
public class FamiliaConfiguration : IEntityTypeConfiguration<Familia>
{
    public void Configure(EntityTypeBuilder<Familia> builder)
    {
        builder.ToTable("familias");

        builder.HasKey(f => f.Id).HasName("pk_familias");

        builder.Property(f => f.Id)
            .HasColumnName("id_familia")
            .UseIdentityByDefaultColumn();

        builder.Property(f => f.IdTenant)
            .HasColumnName("id_tenant")
            .IsRequired();

        // Habilita la FK compuesta (id_familia, id_tenant) de articulos — mismo patrón que
        // Articulo/Empresa/Categoria (ADR-9).
        builder.HasAlternateKey(f => new { f.Id, f.IdTenant })
            .HasName("ak_familias_id_familia_id_tenant");

        builder.Property(f => f.Nombre)
            .HasColumnName("nombre")
            .HasColumnType("citext")
            .HasMaxLength(150)
            .IsRequired();

        builder.Property(f => f.Activo)
            .HasColumnName("activo")
            .HasDefaultValue(true)
            .IsRequired();

        builder.Property(f => f.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(f => f.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(f => f.DeletedAt).HasColumnName("deleted_at");

        builder.Ignore(f => f.EstaEliminada);

        // Unicidad del nombre por tenant entre las familias vivas (parcial, como toda unicidad
        // sobre una tabla con baja lógica — doc 10, principio 3). La constraint es el contrato:
        // ManejadorDeErrores la traduce a 409 familia_nombre_duplicado.
        builder.HasIndex(f => new { f.IdTenant, f.Nombre })
            .HasDatabaseName("ux_familias_nombre")
            .HasFilter("deleted_at IS NULL")
            .IsUnique();

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(f => f.IdTenant)
            .HasConstraintName("fk_familias_tenant")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
