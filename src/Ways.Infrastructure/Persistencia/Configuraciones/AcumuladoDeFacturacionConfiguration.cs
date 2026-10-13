using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Catalogos;
using Ways.Domain.Fiscal;
using Ways.Domain.Organizacion;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

/// <summary>
/// Mapea <see cref="AcumuladoDeFacturacion"/>, espejo de <see cref="NumeracionFiscalConfiguration"/>: tabla
/// PK-only con RLS, filtro de tenant escrito a mano y un único escritor crudo. Este mapeo existe para que
/// el modelo de EF conozca la forma de la tabla (RLS, FKs compuestas, lecturas), no para que
/// <c>SaveChangesAsync</c> la toque.
/// </summary>
public class AcumuladoDeFacturacionConfiguration : IEntityTypeConfiguration<AcumuladoDeFacturacion>
{
    public void Configure(EntityTypeBuilder<AcumuladoDeFacturacion> builder)
    {
        builder.ToTable("acumulados_de_facturacion", t =>
        {
            t.HasCheckConstraint(
                "ck_acumulados_de_facturacion_periodo_dia_uno",
                "EXTRACT(DAY FROM periodo) = 1");
        });

        builder.HasKey(a => new { a.IdEmpresa, a.IdMedioPago, a.Periodo })
            .HasName("pk_acumulados_de_facturacion");

        builder.Property(a => a.IdEmpresa).HasColumnName("id_empresa").ValueGeneratedNever();
        builder.Property(a => a.IdMedioPago).HasColumnName("id_medio_pago").ValueGeneratedNever();
        builder.Property(a => a.Periodo).HasColumnName("periodo").HasColumnType("date").ValueGeneratedNever();

        builder.Property(a => a.IdTenant).HasColumnName("id_tenant").IsRequired();

        // 6 decimales: importe (2) por porcentaje (2) dividido 100 no se redondea nunca.
        builder.Property(a => a.SaldoAFacturar)
            .HasColumnName("saldo_a_facturar")
            .HasColumnType("numeric(18,6)")
            .HasDefaultValue(0m)
            .IsRequired();

        builder.Property(a => a.TotalVendido)
            .HasColumnName("total_vendido")
            .HasColumnType("numeric(14,2)")
            .HasDefaultValue(0m)
            .IsRequired();

        builder.Property(a => a.TotalFacturado)
            .HasColumnName("total_facturado")
            .HasColumnType("numeric(14,2)")
            .HasDefaultValue(0m)
            .IsRequired();

        builder.HasIndex(a => a.IdTenant).HasDatabaseName("ix_acumulados_de_facturacion_tenant");

        // Soporte de las FKs compuestas: la PK empieza por la columna de negocio, no por id_tenant.
        builder.HasIndex(a => new { a.IdEmpresa, a.IdTenant }).HasDatabaseName("ix_acumulados_de_facturacion_empresa");
        builder.HasIndex(a => new { a.IdMedioPago, a.IdTenant }).HasDatabaseName("ix_acumulados_de_facturacion_medio_pago");

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(a => a.IdTenant)
            .HasConstraintName("fk_acumulados_de_facturacion_tenant")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Empresa>()
            .WithMany()
            .HasForeignKey(a => new { a.IdEmpresa, a.IdTenant })
            .HasPrincipalKey(e => new { e.Id, e.IdTenant })
            .HasConstraintName("fk_acumulados_de_facturacion_empresa")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<MedioPago>()
            .WithMany()
            .HasForeignKey(a => new { a.IdMedioPago, a.IdTenant })
            .HasPrincipalKey(m => new { m.Id, m.IdTenant })
            .HasConstraintName("fk_acumulados_de_facturacion_medio_pago")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
