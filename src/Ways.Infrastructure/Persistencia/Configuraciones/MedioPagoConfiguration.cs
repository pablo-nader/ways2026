using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Catalogos;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

public class MedioPagoConfiguration : ConfiguracionDeCatalogo<MedioPago>
{
    protected override string Tabla => "medios_pago";
    protected override string ColumnaId => "id_medio_pago";

    protected override void ConfigurarPropio(EntityTypeBuilder<MedioPago> builder)
    {
        builder.Property(m => m.Orden).HasColumnName("orden").IsRequired();

        builder.Property(m => m.Comportamiento)
            .HasColumnName("comportamiento")
            .HasColumnType("comportamiento_medio_pago")
            .IsRequired();

        builder.Property(m => m.AdmiteVuelto).HasColumnName("admite_vuelto").IsRequired();
        builder.Property(m => m.RequiereReferencia).HasColumnName("requiere_referencia").IsRequired();

        builder.Property(m => m.RecargoPorcentaje)
            .HasColumnName("recargo_porcentaje")
            .HasColumnType("numeric(5,2)");
    }
}
