using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Articulos;
using Ways.Domain.Organizacion;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

/// <summary>
/// Mapea <see cref="NumeracionArticulo"/> (design decision 6, Table Shapes): <c>id_tenant</c>
/// ES la PK, sin identity propia — mismo mapeo que <c>NumeracionClienteConfiguration</c>. Solo
/// <see cref="Application.Articulos.AsignadorDeCodigoInternoArticulo"/> escribe esta tabla, con
/// SQL crudo; este mapeo existe para que el modelo de EF conozca la forma de la tabla (RLS,
/// FK, lecturas de diagnóstico), no para que <c>SaveChangesAsync</c> la toque.
/// </summary>
public class NumeracionArticuloConfiguration : IEntityTypeConfiguration<NumeracionArticulo>
{
    public void Configure(EntityTypeBuilder<NumeracionArticulo> builder)
    {
        builder.ToTable("numeraciones_articulos");

        builder.HasKey(n => n.IdTenant);

        builder.Property(n => n.IdTenant)
            .HasColumnName("id_tenant")
            .ValueGeneratedNever();

        builder.Property(n => n.ProximoNumero)
            .HasColumnName("proximo_numero")
            .HasDefaultValue(1)
            .IsRequired();

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(n => n.IdTenant)
            .HasConstraintName("fk_numeraciones_articulos_tenant")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
