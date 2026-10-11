using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Organizacion;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

/// <summary>
/// Mapea <see cref="Dominio"/>: scoping de organización (<c>id_tenant</c>, doc 09), como
/// <c>empresas</c>. Lo escribe solo la plataforma; lo lee además el login (política
/// <c>dominios_login_lectura</c>) porque el host se resuelve antes de que exista una sesión.
/// </summary>
public class DominioConfiguration : IEntityTypeConfiguration<Dominio>
{
    /// <summary>Misma forma que <see cref="Dominio.NormalizarHost"/>: minúscula, etiquetas de DNS de
    /// 1 a 63 caracteres sin guion en los extremos, 253 en total, sin puerto ni esquema.</summary>
    private const string FormatoDelHost =
        "host = lower(host) AND char_length(host) <= 253 AND " +
        "host ~ '^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)*$'";

    public void Configure(EntityTypeBuilder<Dominio> builder)
    {
        builder.ToTable("dominios", t => t.HasCheckConstraint("ck_dominios_host_formato", FormatoDelHost));

        builder.HasKey(d => d.Id).HasName("pk_dominios");

        builder.Property(d => d.Id)
            .HasColumnName("id_dominio")
            .UseIdentityByDefaultColumn();

        builder.Property(d => d.IdTenant).HasColumnName("id_tenant").IsRequired();

        builder.Property(d => d.Host)
            .HasColumnName("host")
            .HasColumnType("text")
            .IsRequired();

        builder.Property(d => d.EsPrincipal)
            .HasColumnName("es_principal")
            .HasDefaultValue(false)
            .IsRequired();

        builder.Property(d => d.VerificadoEn).HasColumnName("verificado_en");

        builder.Property(d => d.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(d => d.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(d => d.DeletedAt).HasColumnName("deleted_at");

        builder.Ignore(d => d.EstaEliminada);
        builder.Ignore(d => d.EstaVerificado);

        // Un host resuelve a un solo tenant: único entre los vivos, así que dar de baja un dominio
        // libera el host para volver a cargarlo.
        builder.HasIndex(d => d.Host)
            .HasDatabaseName("ux_dominios_host")
            .IsUnique()
            .HasFilter("deleted_at IS NULL");

        // A lo sumo un dominio principal vivo por tenant.
        builder.HasIndex(d => d.IdTenant, "ux_dominios_principal_por_tenant")
            .HasDatabaseName("ux_dominios_principal_por_tenant")
            .IsUnique()
            .HasFilter("es_principal AND deleted_at IS NULL");

        // Soporte de la FK: el índice parcial de arriba no la cubre.
        builder.HasIndex(d => d.IdTenant, "ix_dominios_tenant").HasDatabaseName("ix_dominios_tenant");

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(d => d.IdTenant)
            .HasConstraintName("fk_dominios_tenant")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
