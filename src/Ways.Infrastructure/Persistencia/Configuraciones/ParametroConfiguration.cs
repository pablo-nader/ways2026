using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Catalogos;
using Ways.Domain.Organizacion;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

/// <summary>
/// Operativa con fallback a empresa (ADR-13): dos índices únicos parciales en vez de un
/// único índice con <c>id_punto_venta</c> nullable, que no deduplicaría (Postgres trata
/// cada <c>NULL</c> como distinto).
/// </summary>
public class ParametroConfiguration : IEntityTypeConfiguration<Parametro>
{
    public void Configure(EntityTypeBuilder<Parametro> builder)
    {
        builder.ToTable("parametros");

        builder.HasKey(p => p.Id);

        builder.Property(p => p.Id)
            .HasColumnName("id_parametro")
            .UseIdentityByDefaultColumn();

        builder.Property(p => p.IdTenant).HasColumnName("id_tenant").IsRequired();
        builder.Property(p => p.IdEmpresa).HasColumnName("id_empresa").IsRequired();
        builder.Property(p => p.IdPuntoVenta).HasColumnName("id_punto_venta");

        builder.Property(p => p.Clave)
            .HasColumnName("clave")
            .HasColumnType("citext")
            .HasMaxLength(80)
            .IsRequired();

        builder.Property(p => p.Valor)
            .HasColumnName("valor")
            .HasColumnType("jsonb")
            .IsRequired();

        builder.Property(p => p.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(p => p.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(p => p.DeletedAt).HasColumnName("deleted_at");

        builder.Ignore(p => p.EstaEliminada);

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(p => p.IdTenant)
            .HasConstraintName("fk_parametros_tenant")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Empresa>()
            .WithMany()
            .HasForeignKey(p => new { p.IdEmpresa, p.IdTenant })
            .HasPrincipalKey(e => new { e.Id, e.IdTenant })
            .HasConstraintName("fk_parametros_empresa")
            .OnDelete(DeleteBehavior.Restrict);

        // FK compuesta opcional a puntos_venta: NULL ⇒ default de la empresa.
        builder.HasOne<PuntoVenta>()
            .WithMany()
            .HasForeignKey(p => new { p.IdPuntoVenta, p.IdTenant })
            .HasPrincipalKey(pv => new { pv.Id, pv.IdTenant })
            .HasConstraintName("fk_parametros_punto_venta")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(p => p.IdTenant).HasDatabaseName("ix_parametros_tenant");
        builder.HasIndex(p => new { p.IdEmpresa, p.IdTenant }).HasDatabaseName("ix_parametros_empresa");

        // Sin esto, EF nombra por convención el índice que respalda la FK a puntos_venta
        // ("IX_parametros_id_punto_venta_id_tenant") — inconsistente con la convención
        // ix_<tabla>_<columna> del resto (judgment-day, slice 3 ronda 1).
        builder.HasIndex(p => new { p.IdPuntoVenta, p.IdTenant }).HasDatabaseName("ix_parametros_punto_venta");

        builder.HasIndex(p => new { p.IdTenant, p.IdEmpresa, p.IdPuntoVenta, p.Clave })
            .HasDatabaseName("ux_parametros_punto_venta")
            .HasFilter("id_punto_venta IS NOT NULL AND deleted_at IS NULL")
            .IsUnique();

        builder.HasIndex(p => new { p.IdTenant, p.IdEmpresa, p.Clave })
            .HasDatabaseName("ux_parametros_empresa")
            .HasFilter("id_punto_venta IS NULL AND deleted_at IS NULL")
            .IsUnique();
    }
}
