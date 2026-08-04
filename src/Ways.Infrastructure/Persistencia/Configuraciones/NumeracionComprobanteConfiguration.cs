using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Organizacion;
using Ways.Domain.Ventas;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

/// <summary>
/// Mapea <see cref="NumeracionComprobante"/> (design: Table Shapes — write path A, decisión 8):
/// PK compuesta <c>(id_punto_venta, tipo_comprobante)</c>, <c>id_tenant</c> columna no-key para
/// RLS. Solo <c>AsignadorDeNumeroComprobante</c> escribe esta tabla, con SQL crudo (design
/// decisión 9) — este mapeo existe para que el modelo de EF conozca la forma de la tabla (RLS,
/// FK compuesta, lecturas de diagnóstico), no para que <c>SaveChangesAsync</c> la toque.
/// </summary>
public class NumeracionComprobanteConfiguration : IEntityTypeConfiguration<NumeracionComprobante>
{
    public void Configure(EntityTypeBuilder<NumeracionComprobante> builder)
    {
        builder.ToTable("numeraciones_comprobante");

        builder.HasKey(n => new { n.IdPuntoVenta, n.TipoComprobante })
            .HasName("pk_numeraciones_comprobante");

        builder.Property(n => n.IdPuntoVenta)
            .HasColumnName("id_punto_venta")
            .ValueGeneratedNever();

        builder.Property(n => n.TipoComprobante)
            .HasColumnName("tipo_comprobante")
            .HasMaxLength(30)
            .ValueGeneratedNever();

        builder.Property(n => n.IdTenant)
            .HasColumnName("id_tenant")
            .IsRequired();

        builder.Property(n => n.ProximoNumero)
            .HasColumnName("proximo_numero")
            .HasDefaultValue(1L)
            .IsRequired();

        builder.HasIndex(n => n.IdTenant).HasDatabaseName("ix_numeraciones_comprobante_tenant");

        // Nombre explícito en snake_case (mismo fix documentado en ArticuloEmpresaConfiguration):
        // sin esto, EF nombra el índice de soporte de fk_numeraciones_comprobante_punto_venta
        // con su convención propia (IX_numeraciones_comprobante_id_punto_venta_id_tenant,
        // PascalCase) — el mismo trap de stage-3-articulos-y-precios.
        builder.HasIndex(n => new { n.IdPuntoVenta, n.IdTenant })
            .HasDatabaseName("ix_numeraciones_comprobante_punto_venta");

        // FK compuesta (id_punto_venta, id_tenant) → puntos_venta (mismo criterio que
        // PuntoVentaConfiguration.ak_puntos_venta_id_punto_venta_id_tenant): un tenant no
        // puede tener un contador colgado del punto de venta de otro tenant ni por bug.
        builder.HasOne<PuntoVenta>()
            .WithMany()
            .HasForeignKey(n => new { n.IdPuntoVenta, n.IdTenant })
            .HasPrincipalKey(p => new { p.Id, p.IdTenant })
            .HasConstraintName("fk_numeraciones_comprobante_punto_venta")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(n => n.IdTenant)
            .HasConstraintName("fk_numeraciones_comprobante_tenant")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
