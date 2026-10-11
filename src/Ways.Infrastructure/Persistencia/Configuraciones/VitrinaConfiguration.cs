using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Organizacion;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

/// <summary>
/// Mapea <see cref="Vitrina"/>: scoping de empresa (<c>id_tenant</c> + <c>id_empresa</c> NOT NULL,
/// doc 09), como <c>certificados_fiscales</c>. La administran los admins del tenant; la lee además
/// el modo de acceso <c>publico</c> (política <c>vitrinas_publico_lectura</c>), solo la publicada.
/// </summary>
public class VitrinaConfiguration : IEntityTypeConfiguration<Vitrina>
{
    public void Configure(EntityTypeBuilder<Vitrina> builder)
    {
        builder.ToTable("vitrinas", t =>
        {
            t.HasCheckConstraint("ck_vitrinas_tema", "tema IN ('claro', 'oscuro')");
            t.HasCheckConstraint("ck_vitrinas_color_acento", "color_acento IS NULL OR color_acento ~ '^#[0-9a-f]{6}$'");
            t.HasCheckConstraint("ck_vitrinas_tagline_largo", "tagline IS NULL OR char_length(tagline) <= 120");
            t.HasCheckConstraint("ck_vitrinas_descripcion_largo", "descripcion IS NULL OR char_length(descripcion) <= 2000");

            // El logo, su tipo y su marca de tiempo viajan juntos: todos o ninguno. El tamaño y el tipo
            // repiten lo que valida Vitrina.ValidarLogo. El IS NOT NULL del tipo es explícito: un CHECK que
            // evalúa a NULL pasa, y NULL IN (...) es NULL.
            t.HasCheckConstraint(
                "ck_vitrinas_logo",
                "(logo IS NULL AND logo_content_type IS NULL AND logo_actualizado_en IS NULL) OR " +
                "(logo IS NOT NULL AND octet_length(logo) BETWEEN 1 AND 1048576 AND " +
                "logo_content_type IS NOT NULL AND logo_content_type IN ('image/png', 'image/jpeg', 'image/webp') AND " +
                "logo_actualizado_en IS NOT NULL)");
        });

        builder.HasKey(v => v.Id).HasName("pk_vitrinas");

        builder.Property(v => v.Id)
            .HasColumnName("id_vitrina")
            .UseIdentityByDefaultColumn();

        builder.Property(v => v.IdTenant).HasColumnName("id_tenant").IsRequired();
        builder.Property(v => v.IdEmpresa).HasColumnName("id_empresa").IsRequired();

        builder.Property(v => v.Publicada)
            .HasColumnName("publicada")
            .HasDefaultValue(false)
            .IsRequired();

        builder.Property(v => v.Tagline).HasColumnName("tagline").HasColumnType("text");
        builder.Property(v => v.Descripcion).HasColumnName("descripcion").HasColumnType("text");
        builder.Property(v => v.ColorAcento).HasColumnName("color_acento").HasColumnType("text");

        builder.Property(v => v.Tema)
            .HasColumnName("tema")
            .HasColumnType("text")
            .HasDefaultValue(Vitrina.TemaOscuro)
            .IsRequired();

        builder.Property(v => v.Logo).HasColumnName("logo").HasColumnType("bytea");
        builder.Property(v => v.LogoContentType).HasColumnName("logo_content_type").HasColumnType("text");
        builder.Property(v => v.LogoActualizadoEn).HasColumnName("logo_actualizado_en");

        builder.Property(v => v.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(v => v.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(v => v.DeletedAt).HasColumnName("deleted_at");

        builder.Ignore(v => v.EstaEliminada);

        // Una vitrina viva por empresa.
        builder.HasIndex(v => v.IdEmpresa)
            .HasDatabaseName("ux_vitrinas_empresa")
            .IsUnique()
            .HasFilter("deleted_at IS NULL");

        // A lo sumo una vitrina publicada y viva por tenant: la landing del dominio muestra una sola.
        builder.HasIndex(v => v.IdTenant, "ux_vitrinas_publicada_por_tenant")
            .HasDatabaseName("ux_vitrinas_publicada_por_tenant")
            .IsUnique()
            .HasFilter("publicada AND deleted_at IS NULL");

        // Soporte de las FKs: los índices parciales de arriba no las cubren.
        builder.HasIndex(v => v.IdTenant, "ix_vitrinas_tenant").HasDatabaseName("ix_vitrinas_tenant");
        builder.HasIndex(v => new { v.IdEmpresa, v.IdTenant }).HasDatabaseName("ix_vitrinas_empresa");

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(v => v.IdTenant)
            .HasConstraintName("fk_vitrinas_tenant")
            .OnDelete(DeleteBehavior.Restrict);

        // Compuesta (id_empresa, id_tenant) contra la AK de empresas: una vitrina de un tenant no puede
        // colgar de la empresa de otro (ADR-9).
        builder.HasOne<Empresa>()
            .WithMany()
            .HasForeignKey(v => new { v.IdEmpresa, v.IdTenant })
            .HasPrincipalKey(e => new { e.Id, e.IdTenant })
            .HasConstraintName("fk_vitrinas_empresa")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
