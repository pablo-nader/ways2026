using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Caja;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

/// <summary>
/// Mapea <see cref="MovimientoCaja"/> (design: Table Shapes — write path B). Las dos CHECKs son
/// defensa en profundidad — <c>ReglaDeMovimientosDeCaja</c> (Slice 2) ya garantiza los mismos
/// invariantes en el camino de servicio.
/// </summary>
public class MovimientoCajaConfiguration : IEntityTypeConfiguration<MovimientoCaja>
{
    public void Configure(EntityTypeBuilder<MovimientoCaja> builder)
    {
        builder.ToTable("movimientos_caja", t =>
        {
            t.HasCheckConstraint(
                "ck_movimientos_caja_importe",
                "(tipo = 'apertura_cajon' AND importe = 0) OR (tipo <> 'apertura_cajon' AND importe > 0)");
            t.HasCheckConstraint("ck_movimientos_caja_motivo_minimo", "length(btrim(motivo)) >= 5");
        });

        builder.HasKey(m => m.Id).HasName("pk_movimientos_caja");

        builder.Property(m => m.Id)
            .HasColumnName("id_movimiento")
            .UseIdentityByDefaultColumn();

        builder.Property(m => m.IdTenant).HasColumnName("id_tenant").IsRequired();
        builder.Property(m => m.IdTurnoCaja).HasColumnName("id_turno_caja").IsRequired();

        builder.Property(m => m.Tipo)
            .HasColumnName("tipo")
            .HasColumnType("tipo_movimiento_caja")
            .IsRequired();

        builder.Property(m => m.Importe).HasColumnName("importe").HasColumnType("numeric(14,2)").IsRequired();
        builder.Property(m => m.Motivo).HasColumnName("motivo").HasColumnType("text").IsRequired();
        builder.Property(m => m.IdEmpleado).HasColumnName("id_empleado").IsRequired();
        builder.Property(m => m.CreadoEl).HasColumnName("creado_el").IsRequired();

        builder.HasIndex(m => m.IdTenant).HasDatabaseName("ix_movimientos_caja_tenant");
        builder.HasIndex(m => new { m.IdTurnoCaja, m.IdTenant }).HasDatabaseName("ix_movimientos_caja_turno");

        // Índice de soporte de FK simple (evita el índice implícito PascalCase de EF).
        builder.HasIndex(m => m.IdEmpleado).HasDatabaseName("ix_movimientos_caja_empleado");

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(m => m.IdTenant)
            .HasConstraintName("fk_movimientos_caja_tenant")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<TurnoCaja>()
            .WithMany()
            .HasForeignKey(m => new { m.IdTurnoCaja, m.IdTenant })
            .HasPrincipalKey(t => new { t.Id, t.IdTenant })
            .HasConstraintName("fk_movimientos_caja_turno")
            .OnDelete(DeleteBehavior.Restrict);

        // id_empleado: FK simple, mismo motivo que TurnoCajaConfiguration.fk_turnos_caja_empleado_apertura.
        builder.HasOne<Usuario>()
            .WithMany()
            .HasForeignKey(m => m.IdEmpleado)
            .HasConstraintName("fk_movimientos_caja_empleado")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
