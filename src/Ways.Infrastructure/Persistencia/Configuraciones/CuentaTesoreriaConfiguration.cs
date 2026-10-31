using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Caja;
using Ways.Domain.Organizacion;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

/// <summary>
/// Mapea <see cref="CuentaTesoreria"/>: cuenta digital por empresa. El nombre es único entre las
/// cuentas vivas de la misma empresa.
/// </summary>
public class CuentaTesoreriaConfiguration : IEntityTypeConfiguration<CuentaTesoreria>
{
    public void Configure(EntityTypeBuilder<CuentaTesoreria> builder)
    {
        builder.ToTable("cuentas_tesoreria");

        builder.HasKey(c => c.Id).HasName("pk_cuentas_tesoreria");

        builder.Property(c => c.Id)
            .HasColumnName("id_cuenta_tesoreria")
            .UseIdentityByDefaultColumn();

        builder.Property(c => c.IdTenant).HasColumnName("id_tenant").IsRequired();
        builder.Property(c => c.IdEmpresa).HasColumnName("id_empresa").IsRequired();

        // Habilita las FK compuestas de medios_pago y movimientos_cuenta.
        builder.HasAlternateKey(c => new { c.Id, c.IdTenant })
            .HasName("ak_cuentas_tesoreria_id_cuenta_tesoreria_id_tenant");

        // Permite que conexiones_mercadopago referencie la cuenta incluyendo la empresa: la base rechaza una cuenta de otra empresa.
        builder.HasAlternateKey(c => new { c.Id, c.IdEmpresa, c.IdTenant })
            .HasName("ak_cuentas_tesoreria_id_cuenta_tesoreria_id_empresa_id_tenant");

        builder.Property(c => c.Nombre)
            .HasColumnName("nombre")
            .HasColumnType("citext")
            .HasMaxLength(150)
            .IsRequired();

        builder.Property(c => c.SaldoInicial)
            .HasColumnName("saldo_inicial")
            .HasColumnType("numeric(14,2)")
            .HasDefaultValue(0m)
            .IsRequired();

        builder.Property(c => c.Activo)
            .HasColumnName("activo")
            .HasDefaultValue(true)
            .IsRequired();

        builder.Property(c => c.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(c => c.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(c => c.DeletedAt).HasColumnName("deleted_at");

        builder.Ignore(c => c.EstaEliminada);

        builder.HasIndex(c => c.IdTenant).HasDatabaseName("ix_cuentas_tesoreria_tenant");
        builder.HasIndex(c => new { c.IdEmpresa, c.IdTenant }).HasDatabaseName("ix_cuentas_tesoreria_empresa");

        builder.HasIndex(c => new { c.IdTenant, c.IdEmpresa, c.Nombre })
            .HasDatabaseName("ux_cuentas_tesoreria_nombre_empresa")
            .HasFilter("deleted_at IS NULL")
            .IsUnique();

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(c => c.IdTenant)
            .HasConstraintName("fk_cuentas_tesoreria_tenant")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Empresa>()
            .WithMany()
            .HasForeignKey(c => new { c.IdEmpresa, c.IdTenant })
            .HasPrincipalKey(e => new { e.Id, e.IdTenant })
            .HasConstraintName("fk_cuentas_tesoreria_empresa")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
