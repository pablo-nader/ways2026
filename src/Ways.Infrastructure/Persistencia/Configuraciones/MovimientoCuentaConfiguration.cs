using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Organizacion;
using Ways.Domain.Pagos;
using Ways.Domain.Usuarios;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

/// <summary>
/// Mapea <see cref="MovimientoCuenta"/>. Las CHECK son defensa en profundidad: quien escriba la
/// tabla ya calcula <c>Final</c> como <c>Inicio + Ingreso − Egreso</c> antes de insertar.
/// </summary>
public class MovimientoCuentaConfiguration : IEntityTypeConfiguration<MovimientoCuenta>
{
    public void Configure(EntityTypeBuilder<MovimientoCuenta> builder)
    {
        builder.ToTable("movimientos_cuenta", t =>
        {
            t.HasCheckConstraint("ck_movimientos_cuenta_cadena", "final = inicio + ingreso - egreso");
            t.HasCheckConstraint("ck_movimientos_cuenta_importes_no_negativos", "ingreso >= 0 AND egreso >= 0");
            t.HasCheckConstraint("ck_movimientos_cuenta_un_solo_lado", "ingreso = 0 OR egreso = 0");
            // Una fila de tipo pago siempre nombra su pago, y ninguna otra lo nombra.
            t.HasCheckConstraint("ck_movimientos_cuenta_pago", "(tipo = 'pago') = (id_pago IS NOT NULL)");
        });

        builder.HasKey(m => m.Id).HasName("pk_movimientos_cuenta");

        builder.Property(m => m.Id)
            .HasColumnName("id_movimiento_cuenta")
            .UseIdentityByDefaultColumn();

        builder.Property(m => m.IdTenant).HasColumnName("id_tenant").IsRequired();
        builder.Property(m => m.IdCuentaTesoreria).HasColumnName("id_cuenta_tesoreria").IsRequired();
        builder.Property(m => m.Fecha).HasColumnName("fecha").IsRequired();

        builder.Property(m => m.Tipo)
            .HasColumnName("tipo")
            .HasColumnType("tipo_movimiento_cuenta")
            .IsRequired();

        builder.Property(m => m.IdTurnoCaja).HasColumnName("id_turno_caja");
        builder.Property(m => m.IdMedioPago).HasColumnName("id_medio_pago");
        builder.Property(m => m.IdPago).HasColumnName("id_pago");

        builder.Property(m => m.Concepto).HasColumnName("concepto").HasColumnType("text").IsRequired();

        builder.Property(m => m.Inicio).HasColumnName("inicio").HasColumnType("numeric(14,2)").IsRequired();
        builder.Property(m => m.Ingreso).HasColumnName("ingreso").HasColumnType("numeric(14,2)").IsRequired();
        builder.Property(m => m.Egreso).HasColumnName("egreso").HasColumnType("numeric(14,2)").IsRequired();
        builder.Property(m => m.Final).HasColumnName("final").HasColumnType("numeric(14,2)").IsRequired();

        builder.Property(m => m.IdEmpleado).HasColumnName("id_empleado").IsRequired();

        builder.Property(m => m.BaseBruta).HasColumnName("base_bruta").HasColumnType("numeric(14,2)");
        builder.Property(m => m.PorcentajeAplicado).HasColumnName("porcentaje_aplicado").HasColumnType("numeric(6,3)");

        builder.HasIndex(m => m.IdTenant).HasDatabaseName("ix_movimientos_cuenta_tenant");

        // Índice de soporte de la FK compuesta a la cuenta (columnas de la FK primero, como
        // ix_movimientos_tesoreria_empresa_id) y de la lectura de la cola de la cadena y del libro, que se ordenan por (fecha, id).
        builder.HasIndex(m => new { m.IdCuentaTesoreria, m.IdTenant, m.Fecha, m.Id })
            .HasDatabaseName("ix_movimientos_cuenta_cuenta_fecha_id");

        // Índices de soporte de FK (evitan el índice implícito PascalCase de EF).
        builder.HasIndex(m => new { m.IdTurnoCaja, m.IdTenant }).HasDatabaseName("ix_movimientos_cuenta_turno");
        builder.HasIndex(m => new { m.IdMedioPago, m.IdTenant }).HasDatabaseName("ix_movimientos_cuenta_medio_pago");
        builder.HasIndex(m => m.IdEmpleado).HasDatabaseName("ix_movimientos_cuenta_empleado");
        builder.HasIndex(m => new { m.IdPago, m.IdTenant }).HasDatabaseName("ix_movimientos_cuenta_pago");

        // Respaldo de idempotencia de las filas que escribe el cierre de turno (cobro, comisión y
        // retenciones): un turno no puede escribir dos veces el mismo tipo para el mismo medio de pago.
        // Un ajuste o una transferencia con turno queda fuera del índice.
        builder.HasIndex(m => new { m.IdTenant, m.IdTurnoCaja, m.IdMedioPago, m.Tipo })
            .HasDatabaseName("ux_movimientos_cuenta_cierre")
            .HasFilter("id_turno_caja IS NOT NULL AND tipo IN ('cobro', 'comision', 'retencion_iibb', 'retencion_iva', 'retencion_ganancias')")
            .IsUnique();

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(m => m.IdTenant)
            .HasConstraintName("fk_movimientos_cuenta_tenant")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<CuentaTesoreria>()
            .WithMany()
            .HasForeignKey(m => new { m.IdCuentaTesoreria, m.IdTenant })
            .HasPrincipalKey(c => new { c.Id, c.IdTenant })
            .HasConstraintName("fk_movimientos_cuenta_cuenta_tesoreria")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<TurnoCaja>()
            .WithMany()
            .HasForeignKey(m => new { m.IdTurnoCaja, m.IdTenant })
            .HasPrincipalKey(t => new { t.Id, t.IdTenant })
            .HasConstraintName("fk_movimientos_cuenta_turno")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<MedioPago>()
            .WithMany()
            .HasForeignKey(m => new { m.IdMedioPago, m.IdTenant })
            .HasPrincipalKey(p => new { p.Id, p.IdTenant })
            .HasConstraintName("fk_movimientos_cuenta_medio_pago")
            .OnDelete(DeleteBehavior.Restrict);

        // Un pago con débitos no se borra físicamente: su baja es lógica y deja las filas con la reversa.
        builder.HasOne<Pago>()
            .WithMany()
            .HasForeignKey(m => new { m.IdPago, m.IdTenant })
            .HasPrincipalKey(p => new { p.Id, p.IdTenant })
            .HasConstraintName("fk_movimientos_cuenta_pago")
            .OnDelete(DeleteBehavior.Restrict);

        // id_empleado: FK simple, mismo criterio que fk_movimientos_tesoreria_empleado.
        builder.HasOne<Usuario>()
            .WithMany()
            .HasForeignKey(m => m.IdEmpleado)
            .HasConstraintName("fk_movimientos_cuenta_empleado")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
