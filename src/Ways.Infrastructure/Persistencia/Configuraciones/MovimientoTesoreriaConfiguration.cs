using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Caja;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

/// <summary>
/// Mapea <see cref="MovimientoTesoreria"/> (design: Table Shapes — write path D).
/// <c>ck_movimientos_tesoreria_cadena</c> es defensa en profundidad — el único escritor de esta
/// etapa (<c>ServicioDeTurnos.CerrarAsync</c>, Slice 4) ya calcula <c>Final</c> como
/// <c>Inicio + Ingreso − Egreso</c> antes de insertar.
/// </summary>
public class MovimientoTesoreriaConfiguration : IEntityTypeConfiguration<MovimientoTesoreria>
{
    public void Configure(EntityTypeBuilder<MovimientoTesoreria> builder)
    {
        builder.ToTable("movimientos_tesoreria", t =>
        {
            t.HasCheckConstraint("ck_movimientos_tesoreria_cadena", "final = inicio + ingreso - egreso");
        });

        builder.HasKey(m => m.Id).HasName("pk_movimientos_tesoreria");

        builder.Property(m => m.Id)
            .HasColumnName("id_movimiento")
            .UseIdentityByDefaultColumn();

        builder.Property(m => m.IdTenant).HasColumnName("id_tenant").IsRequired();
        builder.Property(m => m.IdPuntoVenta).HasColumnName("id_punto_venta").IsRequired();
        builder.Property(m => m.Fecha).HasColumnName("fecha").IsRequired();

        builder.Property(m => m.Tipo)
            .HasColumnName("tipo")
            .HasColumnType("tipo_movimiento_tesoreria")
            .IsRequired();

        builder.Property(m => m.IdTurnoCaja).HasColumnName("id_turno_caja");
        builder.Property(m => m.Concepto).HasColumnName("concepto").HasColumnType("text").IsRequired();

        builder.Property(m => m.Inicio).HasColumnName("inicio").HasColumnType("numeric(14,2)").IsRequired();
        builder.Property(m => m.Ingreso).HasColumnName("ingreso").HasColumnType("numeric(14,2)").IsRequired();
        builder.Property(m => m.Egreso).HasColumnName("egreso").HasColumnType("numeric(14,2)").IsRequired();
        builder.Property(m => m.Final).HasColumnName("final").HasColumnType("numeric(14,2)").IsRequired();

        builder.Property(m => m.IdEmpleado).HasColumnName("id_empleado").IsRequired();

        builder.HasIndex(m => m.IdTenant).HasDatabaseName("ix_movimientos_tesoreria_tenant");

        // Soporta la lectura encadenada "ORDER BY id DESC LIMIT 1 por punto de venta" (design:
        // The Cierre Transaction, paso 6).
        builder.HasIndex(m => new { m.IdPuntoVenta, m.IdTenant, m.Id })
            .HasDatabaseName("ix_movimientos_tesoreria_punto_venta_id");

        // Índices de soporte de FK (evitan el índice implícito PascalCase de EF).
        builder.HasIndex(m => m.IdEmpleado).HasDatabaseName("ix_movimientos_tesoreria_empleado");
        builder.HasIndex(m => new { m.IdTurnoCaja, m.IdTenant }).HasDatabaseName("ix_movimientos_tesoreria_turno");

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(m => m.IdTenant)
            .HasConstraintName("fk_movimientos_tesoreria_tenant")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<PuntoVenta>()
            .WithMany()
            .HasForeignKey(m => new { m.IdPuntoVenta, m.IdTenant })
            .HasPrincipalKey(p => new { p.Id, p.IdTenant })
            .HasConstraintName("fk_movimientos_tesoreria_punto_venta")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<TurnoCaja>()
            .WithMany()
            .HasForeignKey(m => new { m.IdTurnoCaja, m.IdTenant })
            .HasPrincipalKey(t => new { t.Id, t.IdTenant })
            .HasConstraintName("fk_movimientos_tesoreria_turno")
            .OnDelete(DeleteBehavior.Restrict);

        // id_empleado: FK simple, mismo motivo que TurnoCajaConfiguration.fk_turnos_caja_empleado_apertura.
        builder.HasOne<Usuario>()
            .WithMany()
            .HasForeignKey(m => m.IdEmpleado)
            .HasConstraintName("fk_movimientos_tesoreria_empleado")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
