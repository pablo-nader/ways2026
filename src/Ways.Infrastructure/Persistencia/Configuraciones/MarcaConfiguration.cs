using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Catalogos;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

public class MarcaConfiguration : ConfiguracionDeCatalogo<Marca>
{
    protected override string Tabla => "marcas";
    protected override string ColumnaId => "id_marca";

    protected override void ConfigurarPropio(EntityTypeBuilder<Marca> builder)
    {
        // stage-3-articulos-y-precios (DB CHANGE GATE, design decision 7): habilita la FK
        // compuesta fk_articulos_marca, mismo motivo que AreaConfiguration.
        builder.HasAlternateKey(m => new { m.Id, m.IdTenant })
            .HasName("ak_marcas_id_marca_id_tenant");
    }
}
