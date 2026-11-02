using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Caja;
using Ways.Domain.MercadoPago;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

/// <summary>
/// Mapea <see cref="PropuestaDeTesoreriaMercadoPago"/>: lo que una conciliación propone a la tesorería. Una propuesta por origen dentro de la
/// conciliación (<c>ux_propuestas_tesoreria_mercadopago_origen</c>): lo escribe solo la importación, dentro de la transacción que crea la
/// conciliación, así que ese índice respalda que recalcular nunca duplique. Las cuentas se referencian con la empresa, así que la base rechaza una
/// cuenta de otra empresa.
/// </summary>
public class PropuestaDeTesoreriaMercadoPagoConfiguration : IEntityTypeConfiguration<PropuestaDeTesoreriaMercadoPago>
{
    public void Configure(EntityTypeBuilder<PropuestaDeTesoreriaMercadoPago> builder)
    {
        builder.ToTable("propuestas_tesoreria_mercadopago", t =>
        {
            t.HasCheckConstraint("ck_propuestas_tesoreria_mercadopago_tipo", "tipo IN ('diferencia_comision', 'retiro')");
            t.HasCheckConstraint("ck_propuestas_tesoreria_mercadopago_estado", "estado IN ('pendiente', 'aplicada', 'descartada')");
            t.HasCheckConstraint("ck_propuestas_tesoreria_mercadopago_importe", "importe <> 0");
            t.HasCheckConstraint(
                "ck_propuestas_tesoreria_mercadopago_forma",
                "(tipo = 'diferencia_comision' AND id_fila_conciliacion_mercadopago IS NULL AND id_cuenta_origen IS NOT NULL AND id_cuenta_destino IS NULL AND detalle IS NOT NULL AND id_movimiento_cuenta_destino IS NULL) " +
                "OR (tipo = 'retiro' AND id_fila_conciliacion_mercadopago IS NOT NULL AND importe > 0 AND detalle IS NULL)");
            t.HasCheckConstraint(
                "ck_propuestas_tesoreria_mercadopago_cuentas",
                "id_cuenta_origen IS NULL OR id_cuenta_destino IS NULL OR id_cuenta_origen <> id_cuenta_destino");
            t.HasCheckConstraint(
                "ck_propuestas_tesoreria_mercadopago_aplicada",
                "(estado = 'aplicada') = (id_movimiento_cuenta IS NOT NULL) " +
                "AND (estado = 'aplicada' AND tipo = 'retiro') = (id_movimiento_cuenta_destino IS NOT NULL) " +
                "AND (estado = 'aplicada' AND tipo = 'retiro') = (id_cuenta_destino IS NOT NULL) " +
                "AND (estado <> 'aplicada' OR id_cuenta_origen IS NOT NULL)");
            t.HasCheckConstraint(
                "ck_propuestas_tesoreria_mercadopago_resolucion",
                "(estado = 'pendiente') = (id_usuario_resolucion IS NULL AND fecha_resolucion IS NULL)");
        });

        builder.HasKey(p => p.Id).HasName("pk_propuestas_tesoreria_mercadopago");

        builder.Property(p => p.Id)
            .HasColumnName("id_propuesta_tesoreria_mercadopago")
            .UseIdentityByDefaultColumn();

        builder.Property(p => p.IdTenant).HasColumnName("id_tenant").IsRequired();
        builder.Property(p => p.IdEmpresa).HasColumnName("id_empresa").IsRequired();
        builder.Property(p => p.IdConciliacion).HasColumnName("id_conciliacion_mercadopago").IsRequired();
        builder.Property(p => p.Tipo).HasColumnName("tipo").HasMaxLength(20).IsRequired();
        builder.Property(p => p.ClaveOrigen).HasColumnName("clave_origen").HasMaxLength(64).IsRequired();
        builder.Property(p => p.Importe).HasColumnName("importe").HasColumnType("numeric(14,2)").IsRequired();
        builder.Property(p => p.IdFila).HasColumnName("id_fila_conciliacion_mercadopago");
        builder.Property(p => p.IdCuentaOrigen).HasColumnName("id_cuenta_origen");
        builder.Property(p => p.IdCuentaDestino).HasColumnName("id_cuenta_destino");
        builder.Property(p => p.Estado).HasColumnName("estado").HasMaxLength(12).IsRequired();
        builder.Property(p => p.Detalle).HasColumnName("detalle").HasColumnType("jsonb");
        builder.Property(p => p.IdMovimientoCuenta).HasColumnName("id_movimiento_cuenta");
        builder.Property(p => p.IdMovimientoCuentaDestino).HasColumnName("id_movimiento_cuenta_destino");
        builder.Property(p => p.IdUsuarioResolucion).HasColumnName("id_usuario_resolucion");
        builder.Property(p => p.FechaResolucion).HasColumnName("fecha_resolucion");

        builder.Property(p => p.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(p => p.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(p => p.DeletedAt).HasColumnName("deleted_at");

        builder.Ignore(p => p.EstaEliminada);

        builder.HasIndex(p => p.IdTenant).HasDatabaseName("ix_propuestas_tesoreria_mercadopago_tenant");
        builder.HasIndex(p => new { p.IdEmpresa, p.IdTenant }).HasDatabaseName("ix_propuestas_tesoreria_mercadopago_empresa");
        builder.HasIndex(p => new { p.IdConciliacion, p.IdTenant }).HasDatabaseName("ix_propuestas_tesoreria_mercadopago_conciliacion");
        builder.HasIndex(p => new { p.IdFila, p.IdTenant }).HasDatabaseName("ix_propuestas_tesoreria_mercadopago_fila");
        builder.HasIndex(p => new { p.IdCuentaOrigen, p.IdEmpresa, p.IdTenant }).HasDatabaseName("ix_propuestas_tesoreria_mercadopago_cuenta_origen");
        builder.HasIndex(p => new { p.IdCuentaDestino, p.IdEmpresa, p.IdTenant }).HasDatabaseName("ix_propuestas_tesoreria_mercadopago_cuenta_destino");
        builder.HasIndex(p => new { p.IdMovimientoCuenta, p.IdTenant }).HasDatabaseName("ix_propuestas_tesoreria_mercadopago_movimiento");
        builder.HasIndex(p => new { p.IdMovimientoCuentaDestino, p.IdTenant }).HasDatabaseName("ix_propuestas_tesoreria_mercadopago_movimiento_destino");
        builder.HasIndex(p => p.IdUsuarioResolucion).HasDatabaseName("ix_propuestas_tesoreria_mercadopago_usuario");

        // Una propuesta por origen dentro de la conciliación: recalcular no duplica.
        builder.HasIndex(p => new { p.IdConciliacion, p.Tipo, p.ClaveOrigen })
            .HasDatabaseName("ux_propuestas_tesoreria_mercadopago_origen")
            .IsUnique();

        // Un retiro se propone una sola vez por empresa, aunque dos reportes de rangos que se pisan lo traigan: su clave es el SOURCE_ID de
        // Mercado Pago y vale para toda propuesta de la empresa, pendiente, aplicada o descartada. Respalda la carrera de dos importaciones.
        builder.HasIndex(p => new { p.IdTenant, p.IdEmpresa, p.ClaveOrigen })
            .HasDatabaseName("ux_propuestas_tesoreria_mercadopago_retiro")
            .HasFilter("tipo = 'retiro'")
            .IsUnique();

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(p => p.IdTenant)
            .HasConstraintName("fk_propuestas_tesoreria_mercadopago_tenant")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Empresa>()
            .WithMany()
            .HasForeignKey(p => new { p.IdEmpresa, p.IdTenant })
            .HasPrincipalKey(e => new { e.Id, e.IdTenant })
            .HasConstraintName("fk_propuestas_tesoreria_mercadopago_empresa")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<ConciliacionMercadoPago>()
            .WithMany()
            .HasForeignKey(p => new { p.IdConciliacion, p.IdTenant })
            .HasPrincipalKey(c => new { c.Id, c.IdTenant })
            .HasConstraintName("fk_propuestas_tesoreria_mercadopago_conciliacion")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<FilaConciliacionMercadoPago>()
            .WithMany()
            .HasForeignKey(p => new { p.IdFila, p.IdTenant })
            .HasPrincipalKey(f => new { f.Id, f.IdTenant })
            .HasConstraintName("fk_propuestas_tesoreria_mercadopago_fila")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<CuentaTesoreria>()
            .WithMany()
            .HasForeignKey(p => new { p.IdCuentaOrigen, p.IdEmpresa, p.IdTenant })
            .HasPrincipalKey(c => new { c.Id, c.IdEmpresa, c.IdTenant })
            .HasConstraintName("fk_propuestas_tesoreria_mercadopago_cuenta_origen")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<CuentaTesoreria>()
            .WithMany()
            .HasForeignKey(p => new { p.IdCuentaDestino, p.IdEmpresa, p.IdTenant })
            .HasPrincipalKey(c => new { c.Id, c.IdEmpresa, c.IdTenant })
            .HasConstraintName("fk_propuestas_tesoreria_mercadopago_cuenta_destino")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<MovimientoCuenta>()
            .WithMany()
            .HasForeignKey(p => new { p.IdMovimientoCuenta, p.IdTenant })
            .HasPrincipalKey(m => new { m.Id, m.IdTenant })
            .HasConstraintName("fk_propuestas_tesoreria_mercadopago_movimiento")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<MovimientoCuenta>()
            .WithMany()
            .HasForeignKey(p => new { p.IdMovimientoCuentaDestino, p.IdTenant })
            .HasPrincipalKey(m => new { m.Id, m.IdTenant })
            .HasConstraintName("fk_propuestas_tesoreria_mercadopago_movimiento_destino")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Usuario>()
            .WithMany()
            .HasForeignKey(p => p.IdUsuarioResolucion)
            .HasConstraintName("fk_propuestas_tesoreria_mercadopago_usuario")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
