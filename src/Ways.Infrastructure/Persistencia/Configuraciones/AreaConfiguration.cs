using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Catalogos;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

public class AreaConfiguration : ConfiguracionDeCatalogo<Area>
{
    protected override string Tabla => "areas";
    protected override string ColumnaId => "id_area";

    protected override void ConfigurarPropio(EntityTypeBuilder<Area> builder)
    {
        builder.Property(a => a.Orden).HasColumnName("orden").IsRequired();
    }
}
