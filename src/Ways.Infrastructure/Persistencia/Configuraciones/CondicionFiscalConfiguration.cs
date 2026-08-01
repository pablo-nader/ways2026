using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Catalogos;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

/// <summary>
/// <c>[global]</c> (ADR-11, gate #4): sin <c>id_tenant</c>; RLS lectura-todos/escritura-plataforma
/// (override de ADR-11, decisión del usuario 2026-08-01) además del filtro de superficie de
/// API (solo <c>GET</c> para un tenant).
/// </summary>
public class CondicionFiscalConfiguration : IEntityTypeConfiguration<CondicionFiscal>
{
    public void Configure(EntityTypeBuilder<CondicionFiscal> builder)
    {
        builder.ToTable("condiciones_fiscales");

        builder.HasKey(c => c.Id);

        builder.Property(c => c.Id)
            .HasColumnName("id_condicion_fiscal")
            .UseIdentityByDefaultColumn();

        builder.Property(c => c.Codigo)
            .HasColumnName("codigo")
            .HasColumnType("citext")
            .HasMaxLength(30)
            .IsRequired();

        builder.Property(c => c.Nombre)
            .HasColumnName("nombre")
            .HasColumnType("citext")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(c => c.CodigoAfip).HasColumnName("codigo_afip");
        builder.Property(c => c.Activo).HasColumnName("activo").HasDefaultValue(true).IsRequired();

        builder.Property(c => c.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(c => c.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(c => c.DeletedAt).HasColumnName("deleted_at");

        builder.Ignore(c => c.EstaEliminada);

        builder.HasIndex(c => c.Codigo)
            .HasDatabaseName("ux_condiciones_fiscales_codigo")
            .HasFilter("deleted_at IS NULL")
            .IsUnique();
    }
}
