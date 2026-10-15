using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Fiscal;
using Ways.Domain.Organizacion;
using Ways.Domain.Ventas;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

/// <summary>
/// Mapea <see cref="FacturacionDeVenta"/>: la decisión de facturar una venta. Operativa con RLS estándar
/// (id_tenant + id_empresa NOT NULL, doc 10). <c>ux_facturaciones_de_venta_comprobante</c> es la clave de
/// idempotencia de la decisión y además el índice de soporte de su FK a la venta.
/// </summary>
public class FacturacionDeVentaConfiguration : IEntityTypeConfiguration<FacturacionDeVenta>
{
    public void Configure(EntityTypeBuilder<FacturacionDeVenta> builder)
    {
        builder.ToTable("facturaciones_de_venta", t =>
        {
            t.HasCheckConstraint(
                "ck_facturaciones_de_venta_periodo_dia_uno",
                "EXTRACT(DAY FROM periodo) = 1");
            t.HasCheckConstraint(
                "ck_facturaciones_de_venta_aportes_arreglo",
                "jsonb_typeof(aportes) = 'array'");
            t.HasCheckConstraint(
                "ck_facturaciones_de_venta_intentos_no_negativo",
                "intentos >= 0");
            // Una venta que no se factura no tiene fila: el tipo nativo conserva el valor 0 del enum de
            // dominio (no_requerida) para que el mapeo sea 1:1, pero no es un motivo válido acá.
            t.HasCheckConstraint(
                "ck_facturaciones_de_venta_motivo_requerido",
                "motivo <> 'no_requerida'::motivo_facturacion");
            t.HasCheckConstraint(
                "ck_facturaciones_de_venta_emitida_con_comprobante",
                "estado <> 'emitida'::estado_facturacion_venta OR id_comprobante_fiscal IS NOT NULL");
            // La decisión de una nota de crédito no mueve saldos y no puede ser su propio origen.
            t.HasCheckConstraint(
                "ck_facturaciones_de_venta_nc_sin_aportes",
                "id_facturacion_origen IS NULL OR aportes = '[]'::jsonb");
            t.HasCheckConstraint(
                "ck_facturaciones_de_venta_origen_distinto",
                "id_facturacion_origen IS NULL OR id_facturacion_origen <> id_facturacion_venta");
        });

        builder.HasKey(f => f.Id).HasName("pk_facturaciones_de_venta");

        builder.Property(f => f.Id)
            .HasColumnName("id_facturacion_venta")
            .UseIdentityByDefaultColumn();

        // Destino de la FK autorreferenciada de la nota de crédito.
        builder.HasAlternateKey(f => new { f.Id, f.IdTenant })
            .HasName("ak_facturaciones_de_venta_id_facturacion_venta_id_tenant");

        builder.Property(f => f.IdTenant).HasColumnName("id_tenant").IsRequired();
        builder.Property(f => f.IdEmpresa).HasColumnName("id_empresa").IsRequired();
        builder.Property(f => f.IdComprobanteVenta).HasColumnName("id_comprobante_venta").IsRequired();

        builder.Property(f => f.Motivo)
            .HasColumnName("motivo")
            .HasColumnType("motivo_facturacion")
            .IsRequired();

        builder.Property(f => f.Estado)
            .HasColumnName("estado")
            .HasColumnType("estado_facturacion_venta")
            .IsRequired();

        builder.Property(f => f.Aportes).HasColumnName("aportes").HasColumnType("jsonb").IsRequired();
        builder.Property(f => f.Periodo).HasColumnName("periodo").HasColumnType("date").IsRequired();
        builder.Property(f => f.IdComprobanteFiscal).HasColumnName("id_comprobante_fiscal");
        builder.Property(f => f.IdFacturacionOrigen).HasColumnName("id_facturacion_origen");

        builder.Property(f => f.Intentos)
            .HasColumnName("intentos")
            .HasDefaultValue(0)
            .IsRequired();

        builder.Property(f => f.UltimoError).HasColumnName("ultimo_error").HasColumnType("text");
        builder.Property(f => f.ProximoIntentoEn).HasColumnName("proximo_intento_en");

        builder.Property(f => f.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(f => f.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(f => f.DeletedAt).HasColumnName("deleted_at");

        builder.Ignore(f => f.EstaEliminada);

        builder.HasIndex(f => f.IdTenant).HasDatabaseName("ix_facturaciones_de_venta_tenant");
        builder.HasIndex(f => new { f.IdEmpresa, f.IdTenant }).HasDatabaseName("ix_facturaciones_de_venta_empresa");

        // Clave de idempotencia de la decisión de la factura: una venta, una fila de factura. Parcial: la decisión de
        // la nota de crédito comparte la venta y queda afuera, así que no sirve de soporte de
        // fk_facturaciones_de_venta_comprobante: lo es ix_facturaciones_de_venta_comprobante.
        builder.HasIndex(f => new { f.IdComprobanteVenta, f.IdTenant })
            .HasDatabaseName("ux_facturaciones_de_venta_comprobante")
            .IsUnique()
            .HasFilter("id_facturacion_origen IS NULL");

        // Soporte total de fk_facturaciones_de_venta_comprobante. Con nombre para que EF no lo funda con el único.
        builder.HasIndex(f => new { f.IdComprobanteVenta, f.IdTenant }, "ix_facturaciones_de_venta_comprobante")
            .HasDatabaseName("ix_facturaciones_de_venta_comprobante");

        // Una decisión de factura tiene a lo sumo una nota de crédito. Soporte de fk_facturaciones_de_venta_origen.
        builder.HasIndex(f => new { f.IdFacturacionOrigen, f.IdTenant })
            .HasDatabaseName("ux_facturaciones_de_venta_origen")
            .IsUnique()
            .HasFilter("id_facturacion_origen IS NOT NULL");

        // Un espejo emitido pertenece a una sola decisión. Soporte de fk_facturaciones_de_venta_comprobante_fiscal.
        builder.HasIndex(f => new { f.IdComprobanteFiscal, f.IdTenant })
            .HasDatabaseName("ux_facturaciones_de_venta_comprobante_fiscal")
            .IsUnique()
            .HasFilter("id_comprobante_fiscal IS NOT NULL");

        // Cola de pendientes: parcial sobre el estado que casi nunca tiene filas.
        builder.HasIndex(f => new { f.IdTenant, f.ProximoIntentoEn })
            .HasDatabaseName("ix_facturaciones_de_venta_pendientes")
            .HasFilter("estado = 'pendiente'::estado_facturacion_venta");

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(f => f.IdTenant)
            .HasConstraintName("fk_facturaciones_de_venta_tenant")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Empresa>()
            .WithMany()
            .HasForeignKey(f => new { f.IdEmpresa, f.IdTenant })
            .HasPrincipalKey(e => new { e.Id, e.IdTenant })
            .HasConstraintName("fk_facturaciones_de_venta_empresa")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<ComprobanteVenta>()
            .WithMany()
            .HasForeignKey(f => new { f.IdComprobanteVenta, f.IdTenant })
            .HasPrincipalKey(c => new { c.Id, c.IdTenant })
            .HasConstraintName("fk_facturaciones_de_venta_comprobante")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<ComprobanteVenta>()
            .WithMany()
            .HasForeignKey(f => new { f.IdComprobanteFiscal, f.IdTenant })
            .HasPrincipalKey(c => new { c.Id, c.IdTenant })
            .HasConstraintName("fk_facturaciones_de_venta_comprobante_fiscal")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<FacturacionDeVenta>()
            .WithMany()
            .HasForeignKey(f => new { f.IdFacturacionOrigen, f.IdTenant })
            .HasPrincipalKey(f => new { f.Id, f.IdTenant })
            .HasConstraintName("fk_facturaciones_de_venta_origen")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
