using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.MercadoPago;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

/// <summary>
/// Mapea <see cref="ConciliacionMercadoPago"/>: un reporte de transacciones de Mercado Pago subido por el Admin. El mismo archivo
/// (huella SHA-256) entra una sola vez por empresa (<c>ux_conciliaciones_mercadopago_archivo</c>).
/// </summary>
public class ConciliacionMercadoPagoConfiguration : IEntityTypeConfiguration<ConciliacionMercadoPago>
{
    public void Configure(EntityTypeBuilder<ConciliacionMercadoPago> builder)
    {
        builder.ToTable("conciliaciones_mercadopago", t =>
        {
            t.HasCheckConstraint("ck_conciliaciones_mercadopago_hash", "length(hash_archivo) = 64");
            t.HasCheckConstraint("ck_conciliaciones_mercadopago_cantidades", "cantidad_filas >= 0 AND cantidad_invalidas >= 0 AND cantidad_invalidas <= cantidad_filas");
            t.HasCheckConstraint("ck_conciliaciones_mercadopago_periodo", "fecha_desde IS NULL OR fecha_hasta IS NULL OR fecha_desde <= fecha_hasta");
        });

        builder.HasKey(c => c.Id).HasName("pk_conciliaciones_mercadopago");

        builder.Property(c => c.Id)
            .HasColumnName("id_conciliacion_mercadopago")
            .UseIdentityByDefaultColumn();

        builder.Property(c => c.IdTenant).HasColumnName("id_tenant").IsRequired();
        builder.Property(c => c.IdEmpresa).HasColumnName("id_empresa").IsRequired();
        builder.Property(c => c.HashArchivo).HasColumnName("hash_archivo").HasMaxLength(64).IsRequired();
        builder.Property(c => c.NombreArchivo).HasColumnName("nombre_archivo").HasMaxLength(255);
        builder.Property(c => c.FechaDesde).HasColumnName("fecha_desde");
        builder.Property(c => c.FechaHasta).HasColumnName("fecha_hasta");
        builder.Property(c => c.IdUsuario).HasColumnName("id_usuario").IsRequired();
        builder.Property(c => c.CantidadFilas).HasColumnName("cantidad_filas").IsRequired();
        builder.Property(c => c.CantidadInvalidas).HasColumnName("cantidad_invalidas").IsRequired();

        builder.Property(c => c.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(c => c.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(c => c.DeletedAt).HasColumnName("deleted_at");

        builder.Ignore(c => c.EstaEliminada);

        // Las filas referencian la conciliación con el tenant.
        builder.HasAlternateKey(c => new { c.Id, c.IdTenant }).HasName("ak_conciliaciones_mercadopago_id_id_tenant");

        builder.HasIndex(c => c.IdTenant).HasDatabaseName("ix_conciliaciones_mercadopago_tenant");
        builder.HasIndex(c => c.IdUsuario).HasDatabaseName("ix_conciliaciones_mercadopago_usuario");

        // El mismo archivo no se importa dos veces en la misma empresa; el índice también sirve a la FK compuesta de la empresa.
        builder.HasIndex(c => new { c.IdEmpresa, c.HashArchivo })
            .HasDatabaseName("ux_conciliaciones_mercadopago_archivo")
            .IsUnique();

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(c => c.IdTenant)
            .HasConstraintName("fk_conciliaciones_mercadopago_tenant")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Empresa>()
            .WithMany()
            .HasForeignKey(c => new { c.IdEmpresa, c.IdTenant })
            .HasPrincipalKey(e => new { e.Id, e.IdTenant })
            .HasConstraintName("fk_conciliaciones_mercadopago_empresa")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Usuario>()
            .WithMany()
            .HasForeignKey(c => c.IdUsuario)
            .HasConstraintName("fk_conciliaciones_mercadopago_usuario")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
