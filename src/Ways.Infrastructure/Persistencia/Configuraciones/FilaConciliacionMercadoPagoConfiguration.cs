using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.MercadoPago;
using Ways.Domain.Organizacion;
using Ways.Domain.Ventas;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

/// <summary>
/// Mapea <see cref="FilaConciliacionMercadoPago"/>: una fila del reporte con su resultado. Una fila inválida lleva solo línea y motivo;
/// las demás, los datos leídos. Un pago de venta sin verificar se confirma contra a lo sumo una fila (<c>ux_filas_conciliacion_mercadopago_cobro</c>).
/// </summary>
public class FilaConciliacionMercadoPagoConfiguration : IEntityTypeConfiguration<FilaConciliacionMercadoPago>
{
    public void Configure(EntityTypeBuilder<FilaConciliacionMercadoPago> builder)
    {
        builder.ToTable("filas_conciliacion_mercadopago", t =>
        {
            t.HasCheckConstraint(
                "ck_filas_conciliacion_mercadopago_resultado",
                "resultado IN ('conciliado', 'ya_conciliado', 'duplicada', 'sin_venta', 'importe_distinto', 'sin_cobro_en_ways', 'devolucion', 'contracargo', 'retiro', 'invalida')");
            t.HasCheckConstraint("ck_filas_conciliacion_mercadopago_linea", "numero_linea >= 2");
            t.HasCheckConstraint(
                "ck_filas_conciliacion_mercadopago_datos",
                "(resultado = 'invalida' AND motivo IS NOT NULL AND source_id IS NULL AND importe IS NULL AND fecha_transaccion IS NULL AND id_cobro_mercadopago IS NULL AND id_cobro IS NULL) " +
                "OR (resultado <> 'invalida' AND motivo IS NULL AND source_id IS NOT NULL AND tipo_transaccion IS NOT NULL AND importe IS NOT NULL AND fecha_transaccion IS NOT NULL)");
            t.HasCheckConstraint("ck_filas_conciliacion_mercadopago_confirmada", "id_cobro IS NULL OR resultado = 'sin_cobro_en_ways'");
        });

        builder.HasKey(f => f.Id).HasName("pk_filas_conciliacion_mercadopago");

        builder.Property(f => f.Id)
            .HasColumnName("id_fila_conciliacion_mercadopago")
            .UseIdentityByDefaultColumn();

        builder.Property(f => f.IdTenant).HasColumnName("id_tenant").IsRequired();
        builder.Property(f => f.IdEmpresa).HasColumnName("id_empresa").IsRequired();
        builder.Property(f => f.IdConciliacion).HasColumnName("id_conciliacion_mercadopago").IsRequired();
        builder.Property(f => f.NumeroLinea).HasColumnName("numero_linea").IsRequired();
        builder.Property(f => f.Resultado).HasColumnName("resultado").HasMaxLength(20).IsRequired();
        builder.Property(f => f.SourceId).HasColumnName("source_id").HasMaxLength(64);
        builder.Property(f => f.ExternalReference).HasColumnName("external_reference").HasMaxLength(128);
        builder.Property(f => f.TipoTransaccion).HasColumnName("tipo_transaccion").HasMaxLength(20);
        builder.Property(f => f.Importe).HasColumnName("importe").HasColumnType("numeric(18,2)");
        builder.Property(f => f.Comision).HasColumnName("comision").HasColumnType("numeric(18,2)");
        builder.Property(f => f.Neto).HasColumnName("neto").HasColumnType("numeric(18,2)");
        builder.Property(f => f.FechaTransaccion).HasColumnName("fecha_transaccion");
        builder.Property(f => f.FechaLiberacion).HasColumnName("fecha_liberacion");
        builder.Property(f => f.ExternalPosId).HasColumnName("external_pos_id").HasMaxLength(80);
        builder.Property(f => f.Motivo).HasColumnName("motivo").HasMaxLength(300);
        builder.Property(f => f.IdCobroMercadoPago).HasColumnName("id_cobro_mercadopago");
        builder.Property(f => f.IdCobro).HasColumnName("id_cobro");

        builder.Property(f => f.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(f => f.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(f => f.DeletedAt).HasColumnName("deleted_at");

        builder.Ignore(f => f.EstaEliminada);

        // El cobro de Mercado Pago referencia la fila que lo concilió (cobros_mercadopago.id_fila_conciliacion).
        builder.HasAlternateKey(f => new { f.Id, f.IdTenant }).HasName("ak_filas_conciliacion_mercadopago_id_id_tenant");

        builder.HasIndex(f => f.IdTenant).HasDatabaseName("ix_filas_conciliacion_mercadopago_tenant");
        builder.HasIndex(f => new { f.IdEmpresa, f.IdTenant }).HasDatabaseName("ix_filas_conciliacion_mercadopago_empresa");
        builder.HasIndex(f => new { f.IdCobroMercadoPago, f.IdTenant }).HasDatabaseName("ix_filas_conciliacion_mercadopago_cobro_mp");

        // Las filas de una conciliación se numeran por línea del archivo; el índice sirve también a la FK compuesta y al listado ordenado.
        builder.HasIndex(f => new { f.IdConciliacion, f.NumeroLinea })
            .HasDatabaseName("ux_filas_conciliacion_mercadopago_linea")
            .IsUnique();

        builder.HasIndex(f => new { f.IdConciliacion, f.Resultado }).HasDatabaseName("ix_filas_conciliacion_mercadopago_resultado");

        // Las cancelaciones de un retiro se buscan por empresa, tipo y SOURCE_ID (generador de propuestas de tesorería).
        builder.HasIndex(f => new { f.IdEmpresa, f.TipoTransaccion, f.SourceId }).HasDatabaseName("ix_filas_conciliacion_mercadopago_cancelaciones");

        // Un pago de venta sin verificar se confirma contra a lo sumo una fila. Los nulos no chocan.
        builder.HasIndex(f => new { f.IdCobro, f.IdTenant })
            .HasDatabaseName("ux_filas_conciliacion_mercadopago_cobro")
            .IsUnique()
            .HasFilter("id_cobro IS NOT NULL");

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(f => f.IdTenant)
            .HasConstraintName("fk_filas_conciliacion_mercadopago_tenant")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Empresa>()
            .WithMany()
            .HasForeignKey(f => new { f.IdEmpresa, f.IdTenant })
            .HasPrincipalKey(e => new { e.Id, e.IdTenant })
            .HasConstraintName("fk_filas_conciliacion_mercadopago_empresa")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<ConciliacionMercadoPago>()
            .WithMany()
            .HasForeignKey(f => new { f.IdConciliacion, f.IdTenant })
            .HasPrincipalKey(c => new { c.Id, c.IdTenant })
            .HasConstraintName("fk_filas_conciliacion_mercadopago_conciliacion")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<CobroMercadoPago>()
            .WithMany()
            .HasForeignKey(f => new { f.IdCobroMercadoPago, f.IdTenant })
            .HasPrincipalKey(c => new { c.Id, c.IdTenant })
            .HasConstraintName("fk_filas_conciliacion_mercadopago_cobro_mp")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Cobro>()
            .WithMany()
            .HasForeignKey(f => new { f.IdCobro, f.IdTenant })
            .HasPrincipalKey(c => new { c.Id, c.IdTenant })
            .HasConstraintName("fk_filas_conciliacion_mercadopago_cobro")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
