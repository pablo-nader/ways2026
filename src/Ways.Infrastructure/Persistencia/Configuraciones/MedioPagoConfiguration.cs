using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

public class MedioPagoConfiguration : ConfiguracionDeCatalogo<MedioPago>
{
    protected override string Tabla => "medios_pago";
    protected override string ColumnaId => "id_medio_pago";

    protected override void ConfigurarPropio(EntityTypeBuilder<MedioPago> builder)
    {
        builder.Property(m => m.Orden).HasColumnName("orden").IsRequired();

        builder.Property(m => m.Comportamiento)
            .HasColumnName("comportamiento")
            .HasColumnType("comportamiento_medio_pago")
            .IsRequired();

        builder.Property(m => m.AdmiteVuelto).HasColumnName("admite_vuelto").IsRequired();
        builder.Property(m => m.RequiereReferencia).HasColumnName("requiere_referencia").IsRequired();

        builder.Property(m => m.RecargoPorcentaje)
            .HasColumnName("recargo_porcentaje")
            .HasColumnType("numeric(5,2)");

        builder.Property(m => m.PorcentajeFacturacion)
            .HasColumnName("porcentaje_facturacion")
            .HasColumnType("numeric(5,2)")
            .HasDefaultValue(0m)
            .IsRequired();

        builder.Property(m => m.IdCuentaTesoreria).HasColumnName("id_cuenta_tesoreria");

        builder.Property(m => m.ComisionPorcentaje)
            .HasColumnName("comision_porcentaje")
            .HasColumnType("numeric(6,3)")
            .HasDefaultValue(0m)
            .IsRequired();

        builder.Property(m => m.RetencionIibbPorcentaje)
            .HasColumnName("retencion_iibb_porcentaje")
            .HasColumnType("numeric(6,3)")
            .HasDefaultValue(0m)
            .IsRequired();

        builder.Property(m => m.RetencionIvaPorcentaje)
            .HasColumnName("retencion_iva_porcentaje")
            .HasColumnType("numeric(6,3)")
            .HasDefaultValue(0m)
            .IsRequired();

        builder.Property(m => m.RetencionGananciasPorcentaje)
            .HasColumnName("retencion_ganancias_porcentaje")
            .HasColumnType("numeric(6,3)")
            .HasDefaultValue(0m)
            .IsRequired();

        builder.ToTable(t =>
        {
            t.HasCheckConstraint(
                "ck_medios_pago_porcentaje_facturacion",
                "porcentaje_facturacion >= 0 AND porcentaje_facturacion <= 100");

            t.HasCheckConstraint(
                "ck_medios_pago_porcentajes_cuenta_rango",
                "comision_porcentaje BETWEEN 0 AND 100 AND retencion_iibb_porcentaje BETWEEN 0 AND 100 " +
                "AND retencion_iva_porcentaje BETWEEN 0 AND 100 AND retencion_ganancias_porcentaje BETWEEN 0 AND 100");

            t.HasCheckConstraint(
                "ck_medios_pago_porcentajes_cuenta_suma",
                "comision_porcentaje + retencion_iibb_porcentaje + retencion_iva_porcentaje " +
                "+ retencion_ganancias_porcentaje <= 100");

            t.HasCheckConstraint(
                "ck_medios_pago_cuenta_solo_electronico",
                "comportamiento = 'electronico' OR (id_cuenta_tesoreria IS NULL AND comision_porcentaje = 0 " +
                "AND retencion_iibb_porcentaje = 0 AND retencion_iva_porcentaje = 0 AND retencion_ganancias_porcentaje = 0)");
        });

        builder.HasIndex(m => new { m.IdCuentaTesoreria, m.IdTenant })
            .HasDatabaseName("ix_medios_pago_cuenta_tesoreria");

        builder.HasOne<CuentaTesoreria>()
            .WithMany()
            .HasForeignKey(m => new { m.IdCuentaTesoreria, m.IdTenant })
            .HasPrincipalKey(c => new { c.Id, c.IdTenant })
            .HasConstraintName("fk_medios_pago_cuenta_tesoreria")
            .OnDelete(DeleteBehavior.Restrict);

        // stage-5-pos-ventas (Slice 3, design: Table Shapes — write path A): clave alterna
        // (Id, IdTenant) para que fk_pagos_comprobante_medio_pago pueda ser compuesta — mismo
        // patrón que AreaConfiguration/ListaPrecioConfiguration.
        builder.HasAlternateKey(m => new { m.Id, m.IdTenant })
            .HasName("ak_medios_pago_id_medio_pago_id_tenant");
    }
}
