using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Caja;
using Ways.Domain.MercadoPago;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

/// <summary>
/// Mapea <see cref="ConexionMercadoPago"/>: una conexión OAuth por empresa, con los dos tokens cifrados. El estado se guarda
/// como texto (<c>activa</c> / <c>desconectada</c>) con su CHECK, sin enum nativo de Postgres.
/// </summary>
public class ConexionMercadoPagoConfiguration : IEntityTypeConfiguration<ConexionMercadoPago>
{
    private const string TokensPresentes =
        "access_token_cifrado IS NOT NULL AND octet_length(access_token_cifrado) > 0 " +
        "AND access_token_nonce IS NOT NULL AND octet_length(access_token_nonce) = 12 " +
        "AND access_token_tag IS NOT NULL AND octet_length(access_token_tag) = 16 " +
        "AND refresh_token_cifrado IS NOT NULL AND octet_length(refresh_token_cifrado) > 0 " +
        "AND refresh_token_nonce IS NOT NULL AND octet_length(refresh_token_nonce) = 12 " +
        "AND refresh_token_tag IS NOT NULL AND octet_length(refresh_token_tag) = 16";

    private const string TokensAusentes =
        "access_token_cifrado IS NULL AND access_token_nonce IS NULL AND access_token_tag IS NULL " +
        "AND refresh_token_cifrado IS NULL AND refresh_token_nonce IS NULL AND refresh_token_tag IS NULL";

    public void Configure(EntityTypeBuilder<ConexionMercadoPago> builder)
    {
        builder.ToTable("conexiones_mercadopago", t =>
        {
            t.HasCheckConstraint("ck_conexiones_mercadopago_estado", "estado IN ('activa', 'desconectada')");
            t.HasCheckConstraint(
                "ck_conexiones_mercadopago_activa",
                $"estado <> 'activa' OR (fecha_desconexion IS NULL AND {TokensPresentes})");
            t.HasCheckConstraint(
                "ck_conexiones_mercadopago_desconectada",
                $"estado <> 'desconectada' OR (fecha_desconexion IS NOT NULL AND {TokensAusentes})");
        });

        builder.HasKey(c => c.Id).HasName("pk_conexiones_mercadopago");

        builder.Property(c => c.Id)
            .HasColumnName("id_conexion_mercadopago")
            .UseIdentityByDefaultColumn();

        builder.Property(c => c.IdTenant).HasColumnName("id_tenant").IsRequired();
        builder.Property(c => c.IdEmpresa).HasColumnName("id_empresa").IsRequired();
        builder.Property(c => c.IdCuentaTesoreria).HasColumnName("id_cuenta_tesoreria").IsRequired();
        builder.Property(c => c.IdUsuarioMercadoPago).HasColumnName("id_usuario_mercadopago").IsRequired();

        builder.Property(c => c.AccessTokenCifrado).HasColumnName("access_token_cifrado");
        builder.Property(c => c.AccessTokenNonce).HasColumnName("access_token_nonce");
        builder.Property(c => c.AccessTokenTag).HasColumnName("access_token_tag");
        builder.Property(c => c.RefreshTokenCifrado).HasColumnName("refresh_token_cifrado");
        builder.Property(c => c.RefreshTokenNonce).HasColumnName("refresh_token_nonce");
        builder.Property(c => c.RefreshTokenTag).HasColumnName("refresh_token_tag");

        builder.Property(c => c.IdClaveMaestra).HasColumnName("id_clave_maestra").HasMaxLength(30).IsRequired();
        builder.Property(c => c.AccessTokenExpiraEn).HasColumnName("access_token_expira_en").IsRequired();

        builder.Property(c => c.Estado)
            .HasColumnName("estado")
            .HasColumnType("text")
            .HasConversion(
                e => e == EstadoConexionMercadoPago.Activa ? "activa" : "desconectada",
                s => s == "activa" ? EstadoConexionMercadoPago.Activa : EstadoConexionMercadoPago.Desconectada)
            .IsRequired();

        builder.Property(c => c.IdUsuarioConecto).HasColumnName("id_usuario_conecto").IsRequired();
        builder.Property(c => c.FechaConexion).HasColumnName("fecha_conexion").IsRequired();
        builder.Property(c => c.FechaDesconexion).HasColumnName("fecha_desconexion");
        builder.Property(c => c.FechaUltimaRenovacion).HasColumnName("fecha_ultima_renovacion");

        builder.Property(c => c.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(c => c.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(c => c.DeletedAt).HasColumnName("deleted_at");

        builder.Ignore(c => c.EstaEliminada);

        builder.HasIndex(c => c.IdTenant).HasDatabaseName("ix_conexiones_mercadopago_tenant");
        builder.HasIndex(c => new { c.IdEmpresa, c.IdTenant }).HasDatabaseName("ix_conexiones_mercadopago_empresa");
        builder.HasIndex(c => new { c.IdCuentaTesoreria, c.IdEmpresa, c.IdTenant })
            .HasDatabaseName("ix_conexiones_mercadopago_cuenta");
        builder.HasIndex(c => c.IdUsuarioConecto).HasDatabaseName("ix_conexiones_mercadopago_usuario_conecto");

        // Una conexión activa por empresa.
        builder.HasIndex(c => new { c.IdTenant, c.IdEmpresa })
            .HasDatabaseName("ux_conexiones_mercadopago_empresa_activa")
            .IsUnique()
            .HasFilter("estado = 'activa'");

        // Una conexión activa por cuenta de Mercado Pago en TODO el sistema, sin id_tenant: los webhooks resuelven la
        // empresa a partir de este id.
        builder.HasIndex(c => c.IdUsuarioMercadoPago)
            .HasDatabaseName("ux_conexiones_mercadopago_usuario_activa")
            .IsUnique()
            .HasFilter("estado = 'activa'");

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(c => c.IdTenant)
            .HasConstraintName("fk_conexiones_mercadopago_tenant")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Empresa>()
            .WithMany()
            .HasForeignKey(c => new { c.IdEmpresa, c.IdTenant })
            .HasPrincipalKey(e => new { e.Id, e.IdTenant })
            .HasConstraintName("fk_conexiones_mercadopago_empresa")
            .OnDelete(DeleteBehavior.Restrict);

        // Incluye la empresa: la base rechaza una cuenta de tesorería de otra empresa.
        builder.HasOne<CuentaTesoreria>()
            .WithMany()
            .HasForeignKey(c => new { c.IdCuentaTesoreria, c.IdEmpresa, c.IdTenant })
            .HasPrincipalKey(t => new { t.Id, t.IdEmpresa, t.IdTenant })
            .HasConstraintName("fk_conexiones_mercadopago_cuenta_tesoreria")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Usuario>()
            .WithMany()
            .HasForeignKey(c => c.IdUsuarioConecto)
            .HasConstraintName("fk_conexiones_mercadopago_usuario_conecto")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
