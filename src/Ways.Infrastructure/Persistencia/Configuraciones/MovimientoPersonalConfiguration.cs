using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Organizacion;
using Ways.Domain.Plantel;
using Ways.Domain.Usuarios;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

/// <summary>
/// Libro de personal (doc 10 §8c). Sin <c>id_punto_venta</c> a propósito: las cargas de horas y los
/// ajustes son de back office y no pasan por un turno. Cada CHECK por tipo nombra una sola forma y cada
/// cláusula de <c>ck_movimientos_personal_horas</c> tiene una fila de prueba que viola solo esa
/// cláusula. El importe no negativo de una carga no es una cláusula aparte: lo implican
/// <c>horas &gt; 0</c>, <c>valor_hora &gt;= 0</c> y <c>importe = round(horas * valor_hora, 2)</c>.
/// </summary>
public class MovimientoPersonalConfiguration : IEntityTypeConfiguration<MovimientoPersonal>
{
    public void Configure(EntityTypeBuilder<MovimientoPersonal> builder)
    {
        builder.ToTable("movimientos_personal", t =>
        {
            t.HasCheckConstraint(
                "ck_movimientos_personal_horas",
                "tipo <> 'horas' OR (horas IS NOT NULL AND horas > 0 AND horas <= 24 "
                + "AND valor_hora IS NOT NULL AND valor_hora >= 0 "
                + "AND importe = round(horas * valor_hora, 2))");

            t.HasCheckConstraint(
                "ck_movimientos_personal_ajuste",
                "tipo <> 'ajuste' OR (horas IS NULL AND valor_hora IS NULL AND importe <> 0)");

            t.HasCheckConstraint(
                "ck_movimientos_personal_pago",
                "tipo <> 'pago' OR (horas IS NULL AND valor_hora IS NULL AND importe < 0)");
        });

        builder.HasKey(m => m.Id).HasName("pk_movimientos_personal");

        // Destino de las FKs compuestas de gastos y movimientos_cuenta_corriente: el pago es la cabecera.
        builder.HasAlternateKey(m => new { m.Id, m.IdTenant })
            .HasName("ak_movimientos_personal_id_movimiento_id_tenant");

        builder.Property(m => m.Id).HasColumnName("id_movimiento").UseIdentityByDefaultColumn();
        builder.Property(m => m.IdTenant).HasColumnName("id_tenant").IsRequired();
        builder.Property(m => m.IdPersonal).HasColumnName("id_personal").IsRequired();
        builder.Property(m => m.IdEmpleado).HasColumnName("id_empleado").IsRequired();
        builder.Property(m => m.Fecha).HasColumnName("fecha").HasColumnType("date").IsRequired();

        builder.Property(m => m.Tipo)
            .HasColumnName("tipo")
            .HasColumnType("tipo_movimiento_personal")
            .IsRequired();

        builder.Property(m => m.Horas).HasColumnName("horas").HasColumnType("numeric(6,2)");
        builder.Property(m => m.ValorHora).HasColumnName("valor_hora").HasColumnType("numeric(14,2)");
        builder.Property(m => m.Importe).HasColumnName("importe").HasColumnType("numeric(14,2)").IsRequired();
        builder.Property(m => m.SaldoResultante)
            .HasColumnName("saldo_resultante").HasColumnType("numeric(14,2)").IsRequired();
        builder.Property(m => m.Detalle).HasColumnName("detalle").HasColumnType("text");
        builder.Property(m => m.CreatedAt).HasColumnName("created_at").IsRequired();

        builder.HasIndex(m => m.IdTenant).HasDatabaseName("ix_movimientos_personal_tenant");

        // Estado de cuenta: personal + rango de fechas. También cubre por prefijo la FK compuesta al personal.
        builder.HasIndex(m => new { m.IdPersonal, m.IdTenant, m.Fecha })
            .HasDatabaseName("ix_movimientos_personal_personal_fecha");

        // FK simple a usuarios: un índice compuesto liderado por otra columna no la cubriría.
        builder.HasIndex(m => m.IdEmpleado).HasDatabaseName("ix_movimientos_personal_empleado");

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(m => m.IdTenant)
            .HasConstraintName("fk_movimientos_personal_tenant")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Personal>()
            .WithMany()
            .HasForeignKey(m => new { m.IdPersonal, m.IdTenant })
            .HasPrincipalKey(p => new { p.Id, p.IdTenant })
            .HasConstraintName("fk_movimientos_personal_personal")
            .OnDelete(DeleteBehavior.Restrict);

        // Simple: Usuario no tiene clave alterna (id_usuario, id_tenant) porque su tenant es nullable.
        builder.HasOne<Usuario>()
            .WithMany()
            .HasForeignKey(m => m.IdEmpleado)
            .HasConstraintName("fk_movimientos_personal_empleado")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
