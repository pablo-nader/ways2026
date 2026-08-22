using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Fiscal;
using Ways.Domain.Organizacion;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

/// <summary>
/// Mapea <see cref="CertificadoFiscal"/> — proposal.md §E del gate (DB CHANGE GATE, ratificado):
/// 18 columnas (15+3 auditoría), scoping <c>id_tenant + id_empresa NOT NULL</c> (desviación
/// documentada del catálogo doc-09, misma forma que <c>puntos_venta</c>), 2 FKs (tenant simple;
/// empresa compuesta contra la AK que <c>puntos_venta</c> ya usa), 3 CHECKs, 3 índices (2 de
/// soporte de FK + el unique parcial de "a lo sumo un activo por empresa+ambiente"). Nada de esto
/// se altera fuera de esta sección — cualquier DDL extra reabre el gate.
/// </summary>
public class CertificadoFiscalConfiguration : IEntityTypeConfiguration<CertificadoFiscal>
{
    public void Configure(EntityTypeBuilder<CertificadoFiscal> builder)
    {
        builder.ToTable("certificados_fiscales", t =>
        {
            t.HasCheckConstraint("ck_certificados_fiscales_vigencia", "vigencia_hasta > vigencia_desde");
            t.HasCheckConstraint("ck_certificados_fiscales_cuit", "cuit_titular ~ '^[0-9]{11}$'");
            t.HasCheckConstraint(
                "ck_certificados_fiscales_material",
                "octet_length(nonce) = 12 AND octet_length(tag_autenticacion) = 16 AND octet_length(clave_privada_cifrada) > 0");
        });

        builder.HasKey(c => c.Id).HasName("pk_certificados_fiscales");

        builder.Property(c => c.Id)
            .HasColumnName("id_certificado")
            .UseIdentityByDefaultColumn();

        builder.Property(c => c.IdTenant).HasColumnName("id_tenant").IsRequired();
        builder.Property(c => c.IdEmpresa).HasColumnName("id_empresa").IsRequired();

        builder.Property(c => c.Ambiente)
            .HasColumnName("ambiente")
            .HasColumnType("ambiente_fiscal")
            .IsRequired();

        builder.Property(c => c.Alias).HasColumnName("alias").HasMaxLength(60).IsRequired();
        builder.Property(c => c.CuitTitular).HasColumnName("cuit_titular").HasMaxLength(11).IsRequired();
        builder.Property(c => c.CertificadoPem).HasColumnName("certificado_pem").HasColumnType("text").IsRequired();

        builder.Property(c => c.ClavePrivadaCifrada).HasColumnName("clave_privada_cifrada").IsRequired();
        builder.Property(c => c.Nonce).HasColumnName("nonce").IsRequired();
        builder.Property(c => c.TagAutenticacion).HasColumnName("tag_autenticacion").IsRequired();

        builder.Property(c => c.IdClaveMaestra).HasColumnName("id_clave_maestra").HasMaxLength(30).IsRequired();
        builder.Property(c => c.HuellaSha256).HasColumnName("huella_sha256").HasMaxLength(64).IsRequired();

        builder.Property(c => c.VigenciaDesde).HasColumnName("vigencia_desde").IsRequired();
        builder.Property(c => c.VigenciaHasta).HasColumnName("vigencia_hasta").IsRequired();

        builder.Property(c => c.Activo).HasColumnName("activo").IsRequired();

        builder.Property(c => c.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(c => c.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(c => c.DeletedAt).HasColumnName("deleted_at");

        builder.Ignore(c => c.EstaEliminada);

        builder.HasIndex(c => c.IdTenant).HasDatabaseName("ix_certificados_fiscales_tenant");
        builder.HasIndex(c => new { c.IdEmpresa, c.IdTenant }).HasDatabaseName("ix_certificados_fiscales_empresa");

        // A lo sumo un certificado activo por empresa+ambiente (proposal.md decisión 1) — el
        // filtro con 2 conjuntos ('activo' Y 'deleted_at IS NULL') es lo que vuelve a la rotación
        // (dar de baja + activar dentro de una transacción) libre de una ventana con dos activos.
        builder.HasIndex(c => new { c.IdTenant, c.IdEmpresa, c.Ambiente })
            .HasDatabaseName("ux_certificados_fiscales_activo")
            .IsUnique()
            .HasFilter("activo AND deleted_at IS NULL");

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(c => c.IdTenant)
            .HasConstraintName("fk_certificados_fiscales_tenant")
            .OnDelete(DeleteBehavior.Restrict);

        // Compuesta (id_empresa, id_tenant) MATCH SIMPLE contra la AK que puntos_venta ya usa
        // (ak_empresas_id_empresa_id_tenant, EmpresaConfiguration.cs:25-26) — ADR-9: una fila de
        // un tenant no puede referenciar la empresa de otro tenant ni por bug.
        builder.HasOne<Empresa>()
            .WithMany()
            .HasForeignKey(c => new { c.IdEmpresa, c.IdTenant })
            .HasPrincipalKey(e => new { e.Id, e.IdTenant })
            .HasConstraintName("fk_certificados_fiscales_empresa")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
