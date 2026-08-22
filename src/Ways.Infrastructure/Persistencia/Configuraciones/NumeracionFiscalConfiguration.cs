using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Fiscal;
using Ways.Domain.Organizacion;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

/// <summary>
/// Mapea <see cref="NumeracionFiscal"/> (proposal.md §F, design.md fact 2) — MIRROR LÍNEA A
/// LÍNEA de <see cref="NumeracionComprobanteConfiguration"/> (design.md: "design lo verifica; si
/// la hermana la omite, esta se omite también en vez de divergir en silencio",
/// <c>NumeracionComprobanteConfiguration.cs:51-59</c> declara la FK compuesta a
/// <c>puntos_venta</c> — por lo tanto ESTA la declara también). Solo
/// <c>AsignadorDeNumeroFiscal</c> (slice 4) escribe esta tabla, con SQL crudo — este mapeo existe
/// para que el modelo de EF conozca la forma de la tabla (RLS, FK compuesta, lecturas de
/// diagnóstico), no para que <c>SaveChangesAsync</c> la toque.
/// </summary>
public class NumeracionFiscalConfiguration : IEntityTypeConfiguration<NumeracionFiscal>
{
    public void Configure(EntityTypeBuilder<NumeracionFiscal> builder)
    {
        builder.ToTable("numeraciones_fiscales", t =>
        {
            t.HasCheckConstraint(
                "ck_numeraciones_fiscales_rango",
                "proximo_numero BETWEEN 1 AND 99999999 AND (ultimo_autorizado_arca IS NULL OR ultimo_autorizado_arca BETWEEN 0 AND 99999999)");
            t.HasCheckConstraint(
                "ck_numeraciones_fiscales_sincronizacion",
                "(ultimo_autorizado_arca IS NULL) = (sincronizado_en IS NULL)");
        });

        builder.HasKey(n => new { n.IdPuntoVenta, n.CodigoAfip })
            .HasName("pk_numeraciones_fiscales");

        builder.Property(n => n.IdPuntoVenta)
            .HasColumnName("id_punto_venta")
            .ValueGeneratedNever();

        builder.Property(n => n.CodigoAfip)
            .HasColumnName("codigo_afip")
            .ValueGeneratedNever();

        builder.Property(n => n.IdTenant)
            .HasColumnName("id_tenant")
            .IsRequired();

        builder.Property(n => n.ProximoNumero)
            .HasColumnName("proximo_numero")
            .HasDefaultValue(1L)
            .IsRequired();

        builder.Property(n => n.UltimoAutorizadoArca).HasColumnName("ultimo_autorizado_arca");
        builder.Property(n => n.SincronizadoEn).HasColumnName("sincronizado_en");

        builder.HasIndex(n => n.IdTenant).HasDatabaseName("ix_numeraciones_fiscales_tenant");

        // Nombre explícito en snake_case (mismo fix que NumeracionComprobanteConfiguration.cs:44-49
        // — la trampa PascalCase): sin esto EF nombra el índice de soporte de
        // fk_numeraciones_fiscales_punto_venta con su convención propia. NO cubierto por la PK:
        // su segunda columna es codigo_afip, no id_tenant.
        builder.HasIndex(n => new { n.IdPuntoVenta, n.IdTenant })
            .HasDatabaseName("ix_numeraciones_fiscales_punto_venta");

        // FK compuesta (id_punto_venta, id_tenant) → puntos_venta — ESPEJADA de
        // NumeracionComprobanteConfiguration.cs:54-59 (design.md fact 2, verificado: la hermana
        // SÍ la declara, así que esta la declara también).
        builder.HasOne<PuntoVenta>()
            .WithMany()
            .HasForeignKey(n => new { n.IdPuntoVenta, n.IdTenant })
            .HasPrincipalKey(p => new { p.Id, p.IdTenant })
            .HasConstraintName("fk_numeraciones_fiscales_punto_venta")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(n => n.IdTenant)
            .HasConstraintName("fk_numeraciones_fiscales_tenant")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
