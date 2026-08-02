using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Catalogos;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

public class MarcaConfiguration : ConfiguracionDeCatalogo<Marca>
{
    protected override string Tabla => "marcas";
    protected override string ColumnaId => "id_marca";

    protected override void ConfigurarPropio(EntityTypeBuilder<Marca> builder)
    {
        // Sin columnas propias: marcas no tiene nada más allá de lo que ya mapea la base.
    }
}
