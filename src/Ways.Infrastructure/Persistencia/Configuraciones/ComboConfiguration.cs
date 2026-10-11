using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Combos;
using Ways.Domain.Organizacion;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

/// <summary>
/// Mapea <see cref="Combo"/>: catálogo-scoped como <c>OfertaConfiguration</c>, con
/// <c>id_empresa</c> opcional. El <c>codigo</c> es único entre los combos vivos del tenant (es lo
/// que se tipea tras la <c>C</c> en el POS); el nombre no es único.
/// </summary>
public class ComboConfiguration : IEntityTypeConfiguration<Combo>
{
    public void Configure(EntityTypeBuilder<Combo> builder)
    {
        builder.ToTable("combos", t =>
        {
            t.HasCheckConstraint("ck_combos_precio_positivo", "precio > 0");
        });

        builder.HasKey(c => c.Id);

        builder.Property(c => c.Id)
            .HasColumnName("id_combo")
            .UseIdentityByDefaultColumn();

        builder.Property(c => c.IdTenant)
            .HasColumnName("id_tenant")
            .IsRequired();

        // Habilita la FK compuesta de combos_componentes.
        builder.HasAlternateKey(c => new { c.Id, c.IdTenant })
            .HasName("ak_combos_id_combo_id_tenant");

        builder.Property(c => c.IdEmpresa).HasColumnName("id_empresa");

        builder.Property(c => c.Codigo)
            .HasColumnName("codigo")
            .IsRequired();

        builder.Property(c => c.Nombre)
            .HasColumnName("nombre")
            .HasColumnType("citext")
            .HasMaxLength(150)
            .IsRequired();

        builder.Property(c => c.Precio)
            .HasColumnName("precio")
            .HasColumnType("numeric(14,2)")
            .IsRequired();

        builder.Property(c => c.Activo)
            .HasColumnName("activo")
            .HasDefaultValue(true)
            .IsRequired();

        builder.Property(c => c.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(c => c.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(c => c.DeletedAt).HasColumnName("deleted_at");

        builder.Ignore(c => c.EstaEliminada);

        builder.HasIndex(c => c.IdTenant).HasDatabaseName("ix_combos_tenant");
        builder.HasIndex(c => new { c.IdEmpresa, c.IdTenant }).HasDatabaseName("ix_combos_empresa");

        builder.HasIndex(c => new { c.IdTenant, c.Codigo })
            .HasDatabaseName("ux_combos_codigo")
            .HasFilter("deleted_at IS NULL")
            .IsUnique();

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(c => c.IdTenant)
            .HasConstraintName("fk_combos_tenant")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Empresa>()
            .WithMany()
            .HasForeignKey(c => new { c.IdEmpresa, c.IdTenant })
            .HasPrincipalKey(e => new { e.Id, e.IdTenant })
            .HasConstraintName("fk_combos_empresa")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
