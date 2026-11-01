using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.MercadoPago;
using Ways.Domain.Organizacion;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

/// <summary>
/// Mapea <see cref="CajaMercadoPago"/>: el vínculo de un punto de venta con su caja (<i>POS</i>) en una cuenta de Mercado Pago. Una
/// fila por punto de venta y cuenta (<c>ux_cajas_mercadopago_punto_venta_usuario</c>); la de una cuenta anterior queda como historia.
/// El <c>id_usuario_mercadopago</c> no es FK: la conexión que lo trajo puede desconectarse y el vínculo sigue siendo historia.
/// </summary>
public class CajaMercadoPagoConfiguration : IEntityTypeConfiguration<CajaMercadoPago>
{
    public void Configure(EntityTypeBuilder<CajaMercadoPago> builder)
    {
        builder.ToTable("cajas_mercadopago", t =>
        {
            t.HasCheckConstraint("ck_cajas_mercadopago_external_id", "length(btrim(external_id)) > 0");
        });

        builder.HasKey(c => c.Id).HasName("pk_cajas_mercadopago");

        builder.Property(c => c.Id)
            .HasColumnName("id_caja_mercadopago")
            .UseIdentityByDefaultColumn();

        builder.Property(c => c.IdTenant).HasColumnName("id_tenant").IsRequired();
        builder.Property(c => c.IdEmpresa).HasColumnName("id_empresa").IsRequired();
        builder.Property(c => c.IdPuntoVenta).HasColumnName("id_punto_venta").IsRequired();
        builder.Property(c => c.IdLocalMercadoPago).HasColumnName("id_local_mercadopago").IsRequired();
        builder.Property(c => c.IdStoreMercadoPago).HasColumnName("id_store_mercadopago").IsRequired();
        builder.Property(c => c.IdUsuarioMercadoPago).HasColumnName("id_usuario_mercadopago").IsRequired();
        builder.Property(c => c.IdPosMercadoPago).HasColumnName("id_pos_mercadopago").IsRequired();
        builder.Property(c => c.ExternalId).HasColumnName("external_id").HasMaxLength(40).IsRequired();
        builder.Property(c => c.UrlQr).HasColumnName("url_qr").HasMaxLength(500);
        builder.Property(c => c.FechaRegistro).HasColumnName("fecha_registro").IsRequired();
        builder.Property(c => c.FechaSincronizacion).HasColumnName("fecha_sincronizacion").IsRequired();

        builder.Property(c => c.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(c => c.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(c => c.DeletedAt).HasColumnName("deleted_at");

        builder.Ignore(c => c.EstaEliminada);

        builder.HasIndex(c => c.IdTenant).HasDatabaseName("ix_cajas_mercadopago_tenant");
        builder.HasIndex(c => new { c.IdEmpresa, c.IdTenant }).HasDatabaseName("ix_cajas_mercadopago_empresa");
        builder.HasIndex(c => new { c.IdPuntoVenta, c.IdTenant }).HasDatabaseName("ix_cajas_mercadopago_punto_venta");
        builder.HasIndex(c => new { c.IdLocalMercadoPago, c.IdEmpresa, c.IdTenant }).HasDatabaseName("ix_cajas_mercadopago_local");

        // Una fila por punto de venta y cuenta de Mercado Pago.
        builder.HasIndex(c => new { c.IdPuntoVenta, c.IdUsuarioMercadoPago })
            .HasDatabaseName("ux_cajas_mercadopago_punto_venta_usuario")
            .IsUnique();

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(c => c.IdTenant)
            .HasConstraintName("fk_cajas_mercadopago_tenant")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Empresa>()
            .WithMany()
            .HasForeignKey(c => new { c.IdEmpresa, c.IdTenant })
            .HasPrincipalKey(e => new { e.Id, e.IdTenant })
            .HasConstraintName("fk_cajas_mercadopago_empresa")
            .OnDelete(DeleteBehavior.Restrict);

        // Solo (id, tenant): una clave alterna con la empresa en puntos_venta obliga a toda alta de punto de venta a conocer la empresa
        // antes del SaveChanges y rompe el alta junto con su sucursal. La empresa de la caja sale del propio punto de venta en el servicio.
        builder.HasOne<PuntoVenta>()
            .WithMany()
            .HasForeignKey(c => new { c.IdPuntoVenta, c.IdTenant })
            .HasPrincipalKey(p => new { p.Id, p.IdTenant })
            .HasConstraintName("fk_cajas_mercadopago_punto_venta")
            .OnDelete(DeleteBehavior.Restrict);

        // Incluye la empresa: la base rechaza un local de otra empresa.
        builder.HasOne<LocalMercadoPago>()
            .WithMany()
            .HasForeignKey(c => new { c.IdLocalMercadoPago, c.IdEmpresa, c.IdTenant })
            .HasPrincipalKey(l => new { l.Id, l.IdEmpresa, l.IdTenant })
            .HasConstraintName("fk_cajas_mercadopago_local")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
