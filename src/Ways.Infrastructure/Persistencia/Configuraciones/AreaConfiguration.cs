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

        // stage-3-articulos-y-precios (DB CHANGE GATE, design decision 7): habilita la FK
        // compuesta fk_articulos_area — sin esto, un id_area de OTRO tenant pasaba una FK
        // simple (id_area es único globalmente por ser PK) y solo RLS lo frenaba en runtime,
        // mismo gap que ADR-9/ADR-10 ya cerraron para empresas/categorias/listas_precio.
        builder.HasAlternateKey(a => new { a.Id, a.IdTenant })
            .HasName("ak_areas_id_area_id_tenant");
    }
}
