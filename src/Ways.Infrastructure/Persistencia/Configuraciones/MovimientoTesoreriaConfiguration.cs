using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Caja;
using Ways.Domain.Gastos;
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

        // GastosOrigenFondosYTesoreriaPorEmpresa: backfill desde puntos_venta.id_empresa — la
        // cadena inicio→final se re-ancla a (id_tenant, id_empresa).
        builder.Property(m => m.IdEmpresa).HasColumnName("id_empresa").IsRequired();

        // Nullable (design aprobado): un movimiento originado por un gasto de tesorería (etapa
        // futura) no nace de ningún cierre de punto de venta puntual.
        builder.Property(m => m.IdPuntoVenta).HasColumnName("id_punto_venta");
        builder.Property(m => m.Fecha).HasColumnName("fecha").IsRequired();

        builder.Property(m => m.Tipo)
            .HasColumnName("tipo")
            .HasColumnType("tipo_movimiento_tesoreria")
            .IsRequired();

        builder.Property(m => m.IdTurnoCaja).HasColumnName("id_turno_caja");

        // Nullable: ningún escritor de esta etapa lo puebla todavía (ver doc-comment de la
        // entidad) — aterriza la columna + FK + índice único para la etapa futura de tesorería.
        builder.Property(m => m.IdGasto).HasColumnName("id_gasto");

        builder.Property(m => m.Concepto).HasColumnName("concepto").HasColumnType("text").IsRequired();

        builder.Property(m => m.Inicio).HasColumnName("inicio").HasColumnType("numeric(14,2)").IsRequired();
        builder.Property(m => m.Ingreso).HasColumnName("ingreso").HasColumnType("numeric(14,2)").IsRequired();
        builder.Property(m => m.Egreso).HasColumnName("egreso").HasColumnType("numeric(14,2)").IsRequired();
        builder.Property(m => m.Final).HasColumnName("final").HasColumnType("numeric(14,2)").IsRequired();

        builder.Property(m => m.IdEmpleado).HasColumnName("id_empleado").IsRequired();

        builder.HasIndex(m => m.IdTenant).HasDatabaseName("ix_movimientos_tesoreria_tenant");

        // Soporta la lectura encadenada "ORDER BY id DESC LIMIT 1 por empresa"
        // (GastosOrigenFondosYTesoreriaPorEmpresa re-ancla la cadena de (id_punto_venta) a
        // (id_tenant, id_empresa) — varios puntos de venta comparten un solo fondo).
        builder.HasIndex(m => new { m.IdEmpresa, m.IdTenant, m.Id })
            .HasDatabaseName("ix_movimientos_tesoreria_empresa_id");

        // Ya no es la clave de la cadena, pero se mantiene para consultas de origen ("qué
        // movimientos nacieron en este punto de venta").
        builder.HasIndex(m => new { m.IdPuntoVenta, m.IdTenant, m.Id })
            .HasDatabaseName("ix_movimientos_tesoreria_punto_venta_id");

        // Índices de soporte de FK (evitan el índice implícito PascalCase de EF).
        builder.HasIndex(m => m.IdEmpleado).HasDatabaseName("ix_movimientos_tesoreria_empleado");
        builder.HasIndex(m => new { m.IdTurnoCaja, m.IdTenant }).HasDatabaseName("ix_movimientos_tesoreria_turno");

        // Índice de soporte de la FK compuesta (evita el implícito PascalCase de EF, mismo motivo
        // que ix_gastos_comprobante_compra).
        builder.HasIndex(m => new { m.IdGasto, m.IdTenant }).HasDatabaseName("ix_movimientos_tesoreria_gasto");

        // ux_movimientos_tesoreria_id_gasto: parcial (solo cuando no es nulo) — un gasto nunca
        // puede originar dos movimientos de tesorería.
        builder.HasIndex(m => m.IdGasto)
            .HasDatabaseName("ux_movimientos_tesoreria_id_gasto")
            .HasFilter("id_gasto IS NOT NULL")
            .IsUnique();

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(m => m.IdTenant)
            .HasConstraintName("fk_movimientos_tesoreria_tenant")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Empresa>()
            .WithMany()
            .HasForeignKey(m => new { m.IdEmpresa, m.IdTenant })
            .HasPrincipalKey(e => new { e.Id, e.IdTenant })
            .HasConstraintName("fk_movimientos_tesoreria_empresa")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<PuntoVenta>()
            .WithMany()
            .HasForeignKey(m => new { m.IdPuntoVenta, m.IdTenant })
            .HasPrincipalKey(p => new { p.Id, p.IdTenant })
            .HasConstraintName("fk_movimientos_tesoreria_punto_venta")
            .OnDelete(DeleteBehavior.Restrict);

        // Ways.Domain.Gastos.Gasto.ak_gastos_id_gasto_id_tenant habilita esta FK compuesta —
        // mismo patrón que fk_movimientos_cuenta_corriente_proveedor_gasto
        // (GastoConfiguration.cs). Restrict: un gasto con tesorería vinculada no se puede borrar
        // por debajo (gastos no tiene baja física de todos modos).
        builder.HasOne<Gasto>()
            .WithMany()
            .HasForeignKey(m => new { m.IdGasto, m.IdTenant })
            .HasPrincipalKey(g => new { g.Id, g.IdTenant })
            .HasConstraintName("fk_movimientos_tesoreria_gasto")
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
