using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.MercadoPago;
using Ways.Domain.Organizacion;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

/// <summary>
/// Mapea <see cref="LocalMercadoPago"/>: el vínculo de una sucursal con su local en una cuenta de Mercado Pago. Una fila por
/// sucursal y cuenta (<c>ux_locales_mercadopago_sucursal_usuario</c>); la de una cuenta anterior queda como historia. El
/// <c>id_usuario_mercadopago</c> no es FK: la conexión que lo trajo puede desconectarse y el vínculo sigue siendo historia.
/// </summary>
public class LocalMercadoPagoConfiguration : IEntityTypeConfiguration<LocalMercadoPago>
{
    public void Configure(EntityTypeBuilder<LocalMercadoPago> builder)
    {
        builder.ToTable("locales_mercadopago", t =>
        {
            t.HasCheckConstraint("ck_locales_mercadopago_external_id", "length(btrim(external_id)) > 0");
        });

        builder.HasKey(l => l.Id).HasName("pk_locales_mercadopago");

        builder.Property(l => l.Id)
            .HasColumnName("id_local_mercadopago")
            .UseIdentityByDefaultColumn();

        builder.Property(l => l.IdTenant).HasColumnName("id_tenant").IsRequired();
        builder.Property(l => l.IdEmpresa).HasColumnName("id_empresa").IsRequired();
        builder.Property(l => l.IdSucursal).HasColumnName("id_sucursal").IsRequired();
        builder.Property(l => l.IdUsuarioMercadoPago).HasColumnName("id_usuario_mercadopago").IsRequired();
        builder.Property(l => l.IdStoreMercadoPago).HasColumnName("id_store_mercadopago").IsRequired();
        builder.Property(l => l.ExternalId).HasColumnName("external_id").HasMaxLength(60).IsRequired();
        builder.Property(l => l.FechaRegistro).HasColumnName("fecha_registro").IsRequired();
        builder.Property(l => l.FechaSincronizacion).HasColumnName("fecha_sincronizacion").IsRequired();

        builder.Property(l => l.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(l => l.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(l => l.DeletedAt).HasColumnName("deleted_at");

        builder.Ignore(l => l.EstaEliminada);

        builder.HasIndex(l => l.IdTenant).HasDatabaseName("ix_locales_mercadopago_tenant");
        builder.HasIndex(l => new { l.IdEmpresa, l.IdTenant }).HasDatabaseName("ix_locales_mercadopago_empresa");

        builder.HasIndex(l => new { l.IdSucursal, l.IdEmpresa, l.IdTenant }).HasDatabaseName("ix_locales_mercadopago_sucursal");

        // Una fila por sucursal y cuenta de Mercado Pago.
        builder.HasIndex(l => new { l.IdSucursal, l.IdUsuarioMercadoPago })
            .HasDatabaseName("ux_locales_mercadopago_sucursal_usuario")
            .IsUnique();

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(l => l.IdTenant)
            .HasConstraintName("fk_locales_mercadopago_tenant")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Empresa>()
            .WithMany()
            .HasForeignKey(l => new { l.IdEmpresa, l.IdTenant })
            .HasPrincipalKey(e => new { e.Id, e.IdTenant })
            .HasConstraintName("fk_locales_mercadopago_empresa")
            .OnDelete(DeleteBehavior.Restrict);

        // Incluye la empresa: la base rechaza una sucursal de otra empresa.
        builder.HasOne<Sucursal>()
            .WithMany()
            .HasForeignKey(l => new { l.IdSucursal, l.IdEmpresa, l.IdTenant })
            .HasPrincipalKey(s => new { s.Id, s.IdEmpresa, s.IdTenant })
            .HasConstraintName("fk_locales_mercadopago_sucursal")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
