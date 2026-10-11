using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Combos;
using Ways.Domain.Organizacion;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

/// <summary>
/// Mapea <see cref="ComboComponente"/>: sin auditoría ni baja lógica. Un artículo o una familia
/// figura una sola vez por combo (dos índices únicos parciales sobre la columna no nula), y
/// borrar el combo borra sus componentes (Cascade), mientras que artículos y familias quedan
/// protegidos por Restrict.
/// </summary>
public class ComboComponenteConfiguration : IEntityTypeConfiguration<ComboComponente>
{
    public void Configure(EntityTypeBuilder<ComboComponente> builder)
    {
        builder.ToTable("combos_componentes", t =>
        {
            t.HasCheckConstraint(
                "ck_combos_componentes_alcance_exclusivo",
                "num_nonnulls(id_articulo, id_familia) = 1");

            t.HasCheckConstraint("ck_combos_componentes_cantidad_positiva", "cantidad > 0");
        });

        builder.HasKey(c => c.Id);

        builder.Property(c => c.Id)
            .HasColumnName("id_combo_componente")
            .UseIdentityByDefaultColumn();

        builder.Property(c => c.IdTenant)
            .HasColumnName("id_tenant")
            .IsRequired();

        builder.Property(c => c.IdCombo).HasColumnName("id_combo").IsRequired();
        builder.Property(c => c.IdArticulo).HasColumnName("id_articulo");
        builder.Property(c => c.IdFamilia).HasColumnName("id_familia");

        builder.Property(c => c.Cantidad)
            .HasColumnName("cantidad")
            .HasColumnType("numeric(12,3)")
            .IsRequired();

        builder.HasIndex(c => c.IdTenant).HasDatabaseName("ix_combos_componentes_tenant");
        builder.HasIndex(c => new { c.IdArticulo, c.IdTenant }).HasDatabaseName("ix_combos_componentes_articulo");
        builder.HasIndex(c => new { c.IdFamilia, c.IdTenant }).HasDatabaseName("ix_combos_componentes_familia");

        // Los dos únicos parciales además sirven las lecturas por combo (id_combo es su
        // primera columna), pero la FK compuesta lleva su propio índice.
        builder.HasIndex(c => new { c.IdCombo, c.IdTenant }).HasDatabaseName("ix_combos_componentes_combo");

        builder.HasIndex(c => new { c.IdCombo, c.IdArticulo })
            .HasDatabaseName("ux_combos_componentes_articulo")
            .HasFilter("id_articulo IS NOT NULL")
            .IsUnique();

        builder.HasIndex(c => new { c.IdCombo, c.IdFamilia })
            .HasDatabaseName("ux_combos_componentes_familia")
            .HasFilter("id_familia IS NOT NULL")
            .IsUnique();

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(c => c.IdTenant)
            .HasConstraintName("fk_combos_componentes_tenant")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Combo>()
            .WithMany()
            .HasForeignKey(c => new { c.IdCombo, c.IdTenant })
            .HasPrincipalKey(c => new { c.Id, c.IdTenant })
            .HasConstraintName("fk_combos_componentes_combo")
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Articulo>()
            .WithMany()
            .HasForeignKey(c => new { c.IdArticulo, c.IdTenant })
            .HasPrincipalKey(a => new { a.Id, a.IdTenant })
            .HasConstraintName("fk_combos_componentes_articulo")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Familia>()
            .WithMany()
            .HasForeignKey(c => new { c.IdFamilia, c.IdTenant })
            .HasPrincipalKey(f => new { f.Id, f.IdTenant })
            .HasConstraintName("fk_combos_componentes_familia")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
