using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Caja;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

/// <summary>
/// Mapea <see cref="TurnoCaja"/> (design: Table Shapes — write path A). Las dos CHECKs son
/// defensa en profundidad — <c>ReglaDeTurnos</c> (Slice 2)/<c>ServicioDeTurnos.CerrarAsync</c>
/// (Slice 4) garantizan los mismos invariantes en el camino de servicio.
/// </summary>
public class TurnoCajaConfiguration : IEntityTypeConfiguration<TurnoCaja>
{
    public void Configure(EntityTypeBuilder<TurnoCaja> builder)
    {
        builder.ToTable("turnos_caja", t =>
        {
            t.HasCheckConstraint("ck_turnos_caja_fondo_inicial_no_negativo", "fondo_inicial >= 0");
            t.HasCheckConstraint(
                "ck_turnos_caja_cierre_consistente",
                "(estado = 'abierto' AND fecha_cierre IS NULL AND id_empleado_cierre IS NULL) " +
                "OR (estado = 'cerrado' AND fecha_cierre IS NOT NULL AND id_empleado_cierre IS NOT NULL)");
        });

        builder.HasKey(t => t.Id).HasName("pk_turnos_caja");

        builder.Property(t => t.Id)
            .HasColumnName("id_turno_caja")
            .UseIdentityByDefaultColumn();

        builder.Property(t => t.IdTenant).HasColumnName("id_tenant").IsRequired();

        // Habilita las FKs compuestas de movimientos_caja/arqueos_turno/movimientos_tesoreria/
        // gastos/comprobantes_venta — mismo patrón que ComprobanteVenta/Oferta/Cliente.
        builder.HasAlternateKey(t => new { t.Id, t.IdTenant })
            .HasName("ak_turnos_caja_id_turno_caja_id_tenant");

        builder.Property(t => t.IdPuntoVenta).HasColumnName("id_punto_venta").IsRequired();
        builder.Property(t => t.IdEmpleadoApertura).HasColumnName("id_empleado_apertura").IsRequired();
        builder.Property(t => t.IdEmpleadoCierre).HasColumnName("id_empleado_cierre");

        builder.Property(t => t.FechaApertura).HasColumnName("fecha_apertura").IsRequired();
        builder.Property(t => t.FechaCierre).HasColumnName("fecha_cierre");

        builder.Property(t => t.FondoInicial)
            .HasColumnName("fondo_inicial")
            .HasColumnType("numeric(14,2)")
            .HasDefaultValue(0m)
            .IsRequired();

        builder.Property(t => t.Estado)
            .HasColumnName("estado")
            .HasColumnType("estado_turno")
            .IsRequired();

        builder.Property(t => t.Observaciones).HasColumnName("observaciones").HasColumnType("text");

        builder.Property(t => t.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(t => t.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(t => t.DeletedAt).HasColumnName("deleted_at");

        builder.Ignore(t => t.EstaEliminada);

        // design: One Open Turno Per Punto De Venta — 23505 -> 409 turno_ya_abierto
        // (ManejadorDeErrores.ClasificarUnicidad).
        builder.HasIndex(t => t.IdPuntoVenta)
            .HasDatabaseName("ux_turnos_caja_abierto")
            .HasFilter("estado = 'abierto'")
            .IsUnique();

        builder.HasIndex(t => t.IdTenant).HasDatabaseName("ix_turnos_caja_tenant");
        builder.HasIndex(t => new { t.IdPuntoVenta, t.IdTenant, t.FechaApertura })
            .HasDatabaseName("ix_turnos_caja_punto_venta_fecha");

        // Índices de soporte de FK simple (evitan el índice implícito PascalCase de EF, misma
        // trampa que documenta ComprobanteVentaConfiguration).
        builder.HasIndex(t => t.IdEmpleadoApertura).HasDatabaseName("ix_turnos_caja_empleado_apertura");
        builder.HasIndex(t => t.IdEmpleadoCierre).HasDatabaseName("ix_turnos_caja_empleado_cierre");

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(t => t.IdTenant)
            .HasConstraintName("fk_turnos_caja_tenant")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<PuntoVenta>()
            .WithMany()
            .HasForeignKey(t => new { t.IdPuntoVenta, t.IdTenant })
            .HasPrincipalKey(p => new { p.Id, p.IdTenant })
            .HasConstraintName("fk_turnos_caja_punto_venta")
            .OnDelete(DeleteBehavior.Restrict);

        // FKs simples (no compuestas) — mismo patrón/motivo que
        // ComprobanteVentaConfiguration.fk_comprobantes_venta_empleado: usuarios.IdTenant es
        // nullable a propósito (sentinel de plataforma), una clave alterna (Id, IdTenant)
        // forzaría esa columna a NOT NULL por convención de EF.
        builder.HasOne<Usuario>()
            .WithMany()
            .HasForeignKey(t => t.IdEmpleadoApertura)
            .HasConstraintName("fk_turnos_caja_empleado_apertura")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Usuario>()
            .WithMany()
            .HasForeignKey(t => t.IdEmpleadoCierre)
            .HasConstraintName("fk_turnos_caja_empleado_cierre")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
