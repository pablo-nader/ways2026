using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Catalogos;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

public class GrupoConfiguration : ConfiguracionDeCatalogo<Grupo>
{
    protected override string Tabla => "grupos";
    protected override string ColumnaId => "id_grupo";

    protected override void ConfigurarPropio(EntityTypeBuilder<Grupo> builder)
    {
        builder.Property(g => g.Margen)
            .HasColumnName("margen")
            .HasColumnType("numeric(5,2)");

        // stage-3-articulos-y-precios (DB CHANGE GATE, design decision 7): habilita la FK
        // compuesta fk_articulos_grupo, mismo motivo que AreaConfiguration.
        builder.HasAlternateKey(g => new { g.Id, g.IdTenant })
            .HasName("ak_grupos_id_grupo_id_tenant");
    }
}
