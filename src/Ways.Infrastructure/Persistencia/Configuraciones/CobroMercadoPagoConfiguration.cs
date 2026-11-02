using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Catalogos;
using Ways.Domain.Dispositivos;
using Ways.Domain.MercadoPago;
using Ways.Domain.Organizacion;
using Ways.Domain.Ventas;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

/// <summary>
/// Mapea <see cref="CobroMercadoPago"/>: cada orden que el POS pidió a Mercado Pago para cobrar un número de venta. A lo sumo un
/// cobro vivo (creando, pendiente o aprobado) por punto de venta y número (<c>ux_cobros_mercadopago_vivo_numero</c>); los cancelados,
/// vencidos, devueltos, rechazados en la terminal (fallido) y apartados (sin venta) quedan como historia y el siguiente intento suma uno a <c>intento</c>.
/// </summary>
public class CobroMercadoPagoConfiguration : IEntityTypeConfiguration<CobroMercadoPago>
{
    public void Configure(EntityTypeBuilder<CobroMercadoPago> builder)
    {
        builder.ToTable("cobros_mercadopago", t =>
        {
            t.HasCheckConstraint("ck_cobros_mercadopago_tipo", "tipo IN ('qr', 'point')");
            t.HasCheckConstraint(
                "ck_cobros_mercadopago_estado",
                "estado IN ('creando', 'pendiente', 'aprobado', 'cancelado', 'expirado', 'devuelto', 'sin_venta', 'fallido')");
            t.HasCheckConstraint("ck_cobros_mercadopago_importe", "importe > 0");
            t.HasCheckConstraint("ck_cobros_mercadopago_importe_pagado", "importe_pagado IS NULL OR importe_pagado >= 0");
            t.HasCheckConstraint("ck_cobros_mercadopago_intento", "intento >= 1");
            t.HasCheckConstraint("ck_cobros_mercadopago_numero", "numero_preasignado > 0");
            t.HasCheckConstraint(
                "ck_cobros_mercadopago_aprobacion", "estado <> 'aprobado' OR fecha_aprobacion IS NOT NULL");
            t.HasCheckConstraint(
                "ck_cobros_mercadopago_orden", "estado IN ('creando', 'expirado') OR id_orden_mercadopago IS NOT NULL OR (estado = 'cancelado' AND tipo = 'point')");
            t.HasCheckConstraint(
                "ck_cobros_mercadopago_aprobado_con_pago", "estado <> 'aprobado' OR id_pago_mercadopago IS NOT NULL");
            t.HasCheckConstraint(
                "ck_cobros_mercadopago_terminal", "(tipo = 'point') = (id_terminal_point IS NOT NULL)");
            t.HasCheckConstraint(
                "ck_cobros_mercadopago_situacion_terminal",
                "situacion_terminal IS NULL OR (tipo = 'point' AND estado = 'pendiente' AND situacion_terminal IN ('en_terminal', 'revisar_terminal'))");
            t.HasCheckConstraint(
                "ck_cobros_mercadopago_sin_venta",
                "estado <> 'sin_venta' OR (fecha_apartado IS NOT NULL AND id_comprobante_venta IS NULL AND id_pago_mercadopago IS NOT NULL)");
        });

        builder.HasKey(c => c.Id).HasName("pk_cobros_mercadopago");

        builder.Property(c => c.Id)
            .HasColumnName("id_cobro_mercadopago")
            .UseIdentityByDefaultColumn();

        builder.Property(c => c.IdTenant).HasColumnName("id_tenant").IsRequired();
        builder.Property(c => c.IdEmpresa).HasColumnName("id_empresa").IsRequired();
        builder.Property(c => c.IdPuntoVenta).HasColumnName("id_punto_venta").IsRequired();
        builder.Property(c => c.IdDispositivo).HasColumnName("id_dispositivo").IsRequired();
        builder.Property(c => c.IdMedioPago).HasColumnName("id_medio_pago").IsRequired();
        builder.Property(c => c.NumeroPreasignado).HasColumnName("numero_preasignado").IsRequired();
        builder.Property(c => c.Intento).HasColumnName("intento").IsRequired();
        builder.Property(c => c.Tipo).HasColumnName("tipo").HasMaxLength(10).IsRequired();
        builder.Property(c => c.Importe).HasColumnName("importe").HasColumnType("numeric(18,2)").IsRequired();
        builder.Property(c => c.ExternalReference).HasColumnName("external_reference").HasMaxLength(64).IsRequired();
        builder.Property(c => c.ExternalPosId).HasColumnName("external_pos_id").HasMaxLength(40).IsRequired();
        builder.Property(c => c.ClaveIdempotencia).HasColumnName("clave_idempotencia").HasMaxLength(64).IsRequired();
        builder.Property(c => c.IdOrdenMercadoPago).HasColumnName("id_orden_mercadopago").HasMaxLength(64);
        builder.Property(c => c.Estado).HasColumnName("estado").HasMaxLength(20).IsRequired();
        builder.Property(c => c.IdPagoMercadoPago).HasColumnName("id_pago_mercadopago").HasMaxLength(64);
        builder.Property(c => c.ReferenciaPagoMercadoPago).HasColumnName("referencia_pago_mercadopago");
        builder.Property(c => c.ImportePagado).HasColumnName("importe_pagado").HasColumnType("numeric(18,2)");
        builder.Property(c => c.IdTerminalPoint).HasColumnName("id_terminal_point").HasMaxLength(80);
        builder.Property(c => c.SituacionTerminal).HasColumnName("situacion_terminal").HasMaxLength(20);
        builder.Property(c => c.QrData).HasColumnName("qr_data").HasColumnType("text");
        builder.Property(c => c.ExpiraEn).HasColumnName("expira_en").IsRequired();
        builder.Property(c => c.FechaUltimaConsulta).HasColumnName("fecha_ultima_consulta");
        builder.Property(c => c.FechaAprobacion).HasColumnName("fecha_aprobacion");
        builder.Property(c => c.FechaApartado).HasColumnName("fecha_apartado");
        builder.Property(c => c.FechaProcesadaMercadoPago).HasColumnName("fecha_procesada_mercadopago");

        builder.Property(c => c.IdComprobanteVenta).HasColumnName("id_comprobante_venta");

        // La venta (más el tenant) referencia al cobro desde cobros.id_cobro_mercadopago.
        builder.HasAlternateKey(c => new { c.Id, c.IdTenant }).HasName("ak_cobros_mercadopago_id_id_tenant");

        builder.Property(c => c.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(c => c.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(c => c.DeletedAt).HasColumnName("deleted_at");

        builder.Ignore(c => c.EstaEliminada);

        builder.HasIndex(c => c.IdTenant).HasDatabaseName("ix_cobros_mercadopago_tenant");
        builder.HasIndex(c => new { c.IdEmpresa, c.IdTenant }).HasDatabaseName("ix_cobros_mercadopago_empresa");
        builder.HasIndex(c => new { c.IdDispositivo, c.IdTenant }).HasDatabaseName("ix_cobros_mercadopago_dispositivo");
        builder.HasIndex(c => new { c.IdMedioPago, c.IdTenant }).HasDatabaseName("ix_cobros_mercadopago_medio_pago");

        // A lo sumo un cobro vivo por número de venta: el que se está creando, el que espera el pago o el ya aprobado.
        builder.HasIndex(c => new { c.IdPuntoVenta, c.NumeroPreasignado })
            .HasDatabaseName("ux_cobros_mercadopago_vivo_numero")
            .IsUnique()
            .HasFilter("estado IN ('creando', 'pendiente', 'aprobado')");

        // Los intentos de un número son una secuencia: el servicio los numera bajo lock y la base lo respalda.
        builder.HasIndex(c => new { c.IdPuntoVenta, c.NumeroPreasignado, c.Intento })
            .HasDatabaseName("ux_cobros_mercadopago_intento")
            .IsUnique();

        // El id de la orden de Mercado Pago es único en toda la tabla (todos los tenants): el webhook localiza el cobro por él sin conocer
        // el tenant. Los nulos (creando, expirado sin orden) no chocan.
        builder.HasIndex(c => c.IdOrdenMercadoPago)
            .HasDatabaseName("ux_cobros_mercadopago_orden")
            .IsUnique()
            .HasFilter("id_orden_mercadopago IS NOT NULL");

        // Una venta liga a lo sumo un cobro (a lo sumo un cobro vivo por número) y un cobro paga una sola venta. Los nulos no chocan.
        // Incluye el tenant para servir también de índice de la FK compuesta; el id de la venta ya es único por sí solo.
        builder.HasIndex(c => new { c.IdComprobanteVenta, c.IdTenant })
            .HasDatabaseName("ux_cobros_mercadopago_comprobante_venta")
            .IsUnique();

        builder.HasOne<ComprobanteVenta>()
            .WithMany()
            .HasForeignKey(c => new { c.IdComprobanteVenta, c.IdTenant })
            .HasPrincipalKey(v => new { v.Id, v.IdTenant })
            .HasConstraintName("fk_cobros_mercadopago_comprobante_venta")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(c => c.IdTenant)
            .HasConstraintName("fk_cobros_mercadopago_tenant")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Empresa>()
            .WithMany()
            .HasForeignKey(c => new { c.IdEmpresa, c.IdTenant })
            .HasPrincipalKey(e => new { e.Id, e.IdTenant })
            .HasConstraintName("fk_cobros_mercadopago_empresa")
            .OnDelete(DeleteBehavior.Restrict);

        // Solo (id, tenant): la empresa del cobro sale del propio punto de venta en el servicio (mismo criterio que las cajas).
        builder.HasOne<PuntoVenta>()
            .WithMany()
            .HasForeignKey(c => new { c.IdPuntoVenta, c.IdTenant })
            .HasPrincipalKey(p => new { p.Id, p.IdTenant })
            .HasConstraintName("fk_cobros_mercadopago_punto_venta")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Dispositivo>()
            .WithMany()
            .HasForeignKey(c => new { c.IdDispositivo, c.IdTenant })
            .HasPrincipalKey(d => new { d.Id, d.IdTenant })
            .HasConstraintName("fk_cobros_mercadopago_dispositivo")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<MedioPago>()
            .WithMany()
            .HasForeignKey(c => new { c.IdMedioPago, c.IdTenant })
            .HasPrincipalKey(m => new { m.Id, m.IdTenant })
            .HasConstraintName("fk_cobros_mercadopago_medio_pago")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
