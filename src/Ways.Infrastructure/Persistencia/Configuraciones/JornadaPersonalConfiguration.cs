using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Organizacion;
using Ways.Domain.Plantel;
using Ways.Domain.Usuarios;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

/// <summary>
/// Jornadas confirmadas del personal (doc 10 §8c). Una fila por <c>(id_personal, fecha)</c>: el índice único
/// es la defensa de la unicidad, pero la escritura es un upsert (<c>ON CONFLICT</c>) y ningún camino de la
/// aplicación puede violarlo. El total va de 0 a 24 (<c>ck_jornadas_personal_horas</c>).
/// </summary>
public class JornadaPersonalConfiguration : IEntityTypeConfiguration<JornadaPersonal>
{
    public void Configure(EntityTypeBuilder<JornadaPersonal> builder)
    {
        builder.ToTable("jornadas_personal", t =>
        {
            t.HasCheckConstraint("ck_jornadas_personal_horas", "horas >= 0 AND horas <= 24");
        });

        builder.HasKey(j => j.Id).HasName("pk_jornadas_personal");

        builder.Property(j => j.Id).HasColumnName("id_jornada").UseIdentityByDefaultColumn();
        builder.Property(j => j.IdTenant).HasColumnName("id_tenant").IsRequired();
        builder.Property(j => j.IdPersonal).HasColumnName("id_personal").IsRequired();
        builder.Property(j => j.IdEmpleado).HasColumnName("id_empleado").IsRequired();
        builder.Property(j => j.Fecha).HasColumnName("fecha").HasColumnType("date").IsRequired();
        builder.Property(j => j.Horas).HasColumnName("horas").HasColumnType("numeric(4,2)").IsRequired();
        builder.Property(j => j.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(j => j.UpdatedAt).HasColumnName("updated_at").IsRequired();

        builder.HasIndex(j => new { j.IdPersonal, j.IdTenant, j.Fecha })
            .IsUnique()
            .HasDatabaseName("ux_jornadas_personal_personal_fecha");

        builder.HasIndex(j => j.IdTenant).HasDatabaseName("ix_jornadas_personal_tenant");

        // FK simple a usuarios: un índice compuesto liderado por otra columna no la cubriría.
        builder.HasIndex(j => j.IdEmpleado).HasDatabaseName("ix_jornadas_personal_empleado");

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(j => j.IdTenant)
            .HasConstraintName("fk_jornadas_personal_tenant")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Personal>()
            .WithMany()
            .HasForeignKey(j => new { j.IdPersonal, j.IdTenant })
            .HasPrincipalKey(p => new { p.Id, p.IdTenant })
            .HasConstraintName("fk_jornadas_personal_personal")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Usuario>()
            .WithMany()
            .HasForeignKey(j => j.IdEmpleado)
            .HasConstraintName("fk_jornadas_personal_empleado")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
