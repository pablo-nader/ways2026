using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Clientes;
using Ways.Domain.Organizacion;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

/// <summary>
/// Mapea <see cref="NumeracionCliente"/> (design: Table Shapes): <c>id_tenant</c> ES la PK,
/// sin identity propia. Solo <c>AsignadorDeNumeroCliente</c> escribe esta tabla, y lo hace
/// con SQL crudo (design decision 3) — este mapeo existe para que el modelo de EF conozca
/// la forma de la tabla (RLS, FK, lecturas de diagnóstico), no para que <c>SaveChangesAsync</c>
/// la toque.
/// </summary>
public class NumeracionClienteConfiguration : IEntityTypeConfiguration<NumeracionCliente>
{
    public void Configure(EntityTypeBuilder<NumeracionCliente> builder)
    {
        builder.ToTable("numeraciones_clientes");

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
            .HasConstraintName("fk_numeraciones_clientes_tenant")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
