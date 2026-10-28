using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Catalogos;
using Ways.Domain.Organizacion;
using Ways.Domain.Pagos;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

/// <summary>
/// Mapea <see cref="ItemPago"/> (doc 10 §5, hija operativa de <c>pagos</c>). Las dos CHECKs son defensa en
/// profundidad: <c>ServicioDePagos</c> ya escribe un ítem por línea, con el medio y el importe validados por
/// <c>ReglaDeLineasDePago</c>. El constraint trigger <c>tg_items_pago_suma_igual_importe</c> lo crea la migración con SQL
/// (EF no lo modela).
/// </summary>
public class ItemPagoConfiguration : IEntityTypeConfiguration<ItemPago>
{
    public void Configure(EntityTypeBuilder<ItemPago> builder)
    {
        builder.ToTable("items_pago", t =>
        {
            t.HasCheckConstraint("ck_items_pago_importe_positivo", "importe > 0");
            t.HasCheckConstraint("ck_items_pago_referencia_longitud", "referencia IS NULL OR char_length(referencia) <= 100");
        });

        builder.HasKey(i => i.Id).HasName("pk_items_pago");

        builder.Property(i => i.Id)
            .HasColumnName("id_item_pago")
            .UseIdentityByDefaultColumn();

        builder.Property(i => i.IdTenant).HasColumnName("id_tenant").IsRequired();
        builder.Property(i => i.IdPago).HasColumnName("id_pago").IsRequired();
        builder.Property(i => i.IdMedioPago).HasColumnName("id_medio_pago").IsRequired();
        builder.Property(i => i.Importe).HasColumnName("importe").HasColumnType("numeric(14,2)").IsRequired();
        builder.Property(i => i.Referencia).HasColumnName("referencia").HasColumnType("text");

        builder.Property(i => i.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(i => i.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(i => i.DeletedAt).HasColumnName("deleted_at");

        builder.Ignore(i => i.EstaEliminada);

        builder.HasIndex(i => i.IdTenant).HasDatabaseName("ix_items_pago_tenant");
        builder.HasIndex(i => new { i.IdPago, i.IdTenant }).HasDatabaseName("ix_items_pago_pago");
        builder.HasIndex(i => new { i.IdMedioPago, i.IdTenant }).HasDatabaseName("ix_items_pago_medio_pago");

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(i => i.IdTenant)
            .HasConstraintName("fk_items_pago_tenant")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Pago>()
            .WithMany()
            .HasForeignKey(i => new { i.IdPago, i.IdTenant })
            .HasPrincipalKey(p => new { p.Id, p.IdTenant })
            .HasConstraintName("fk_items_pago_pago")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<MedioPago>()
            .WithMany()
            .HasForeignKey(i => new { i.IdMedioPago, i.IdTenant })
            .HasPrincipalKey(m => new { m.Id, m.IdTenant })
            .HasConstraintName("fk_items_pago_medio_pago")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
