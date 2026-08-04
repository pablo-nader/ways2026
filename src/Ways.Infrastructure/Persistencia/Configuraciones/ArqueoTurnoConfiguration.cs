using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Organizacion;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

/// <summary>
/// Mapea <see cref="ArqueoTurno"/> (design: Table Shapes — write path A; The Cierre
/// Transaction). <see cref="ArqueoTurno.Diferencia"/> es una columna
/// <c>GENERATED ALWAYS ... STORED</c> (design decisión 6) — EF nunca la incluye en el INSERT.
/// </summary>
public class ArqueoTurnoConfiguration : IEntityTypeConfiguration<ArqueoTurno>
{
    public void Configure(EntityTypeBuilder<ArqueoTurno> builder)
    {
        builder.ToTable("arqueos_turno");

        builder.HasKey(a => a.Id).HasName("pk_arqueos_turno");

        builder.Property(a => a.Id)
            .HasColumnName("id_arqueo")
            .UseIdentityByDefaultColumn();

        builder.Property(a => a.IdTenant).HasColumnName("id_tenant").IsRequired();
        builder.Property(a => a.IdTurnoCaja).HasColumnName("id_turno_caja").IsRequired();
        builder.Property(a => a.IdMedioPago).HasColumnName("id_medio_pago").IsRequired();

        builder.Property(a => a.ImporteEsperado)
            .HasColumnName("importe_esperado")
            .HasColumnType("numeric(14,2)")
            .IsRequired();

        builder.Property(a => a.ImporteDeclarado)
            .HasColumnName("importe_declarado")
            .HasColumnType("numeric(14,2)")
            .IsRequired();

        builder.Property(a => a.Diferencia)
            .HasColumnName("diferencia")
            .HasColumnType("numeric(14,2)")
            .HasComputedColumnSql("(importe_esperado - importe_declarado)", stored: true);

        // design decisión 6: una fila por (turno, medio) — backstop 23505 -> 409
        // arqueo_duplicado (ManejadorDeErrores); exención documentada de prueba de carrera
        // (design: Backstop Map): el cierre deriva el set de filas bajo su propio lock
        // exclusivo, así que el camino normal nunca puede chocar acá.
        builder.HasIndex(a => new { a.IdTurnoCaja, a.IdMedioPago })
            .HasDatabaseName("ux_arqueos_turno_medio")
            .IsUnique();

        builder.HasIndex(a => a.IdTenant).HasDatabaseName("ix_arqueos_turno_tenant");
        builder.HasIndex(a => new { a.IdTurnoCaja, a.IdTenant }).HasDatabaseName("ix_arqueos_turno_turno");

        // Índice de soporte de la FK compuesta a medios_pago (evita el índice implícito
        // PascalCase de EF).
        builder.HasIndex(a => new { a.IdMedioPago, a.IdTenant }).HasDatabaseName("ix_arqueos_turno_medio_pago");

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(a => a.IdTenant)
            .HasConstraintName("fk_arqueos_turno_tenant")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<TurnoCaja>()
            .WithMany()
            .HasForeignKey(a => new { a.IdTurnoCaja, a.IdTenant })
            .HasPrincipalKey(t => new { t.Id, t.IdTenant })
            .HasConstraintName("fk_arqueos_turno_turno")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<MedioPago>()
            .WithMany()
            .HasForeignKey(a => new { a.IdMedioPago, a.IdTenant })
            .HasPrincipalKey(m => new { m.Id, m.IdTenant })
            .HasConstraintName("fk_arqueos_turno_medio_pago")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
