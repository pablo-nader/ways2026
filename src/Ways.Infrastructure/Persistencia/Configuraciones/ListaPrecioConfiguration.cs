using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Catalogos;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

/// <summary>
/// <see cref="ListaPrecio"/> sí reusa <see cref="ConfiguracionDeCatalogo{T}"/> (design
/// decision 1): su forma (nombre + flags) calza con la base genérica. Acá solo se agregan
/// sus columnas propias y el segundo par de índices únicos parciales que garantiza "una
/// lista <c>es_default</c> por alcance" (design: Table Shapes).
/// </summary>
public class ListaPrecioConfiguration : ConfiguracionDeCatalogo<ListaPrecio>
{
    protected override string Tabla => "listas_precio";
    protected override string ColumnaId => "id_lista_precio";

    protected override void ConfigurarPropio(EntityTypeBuilder<ListaPrecio> builder)
    {
        builder.Property(l => l.EsDefault)
            .HasColumnName("es_default")
            .IsRequired();

        builder.Property(l => l.Modo)
            .HasColumnName("modo")
            .HasColumnType("modo_lista")
            .HasDefaultValue(ModoLista.Fija)
            .IsRequired();

        builder.Property(l => l.IdListaBase)
            .HasColumnName("id_lista_base");

        builder.Property(l => l.Porcentaje)
            .HasColumnName("porcentaje")
            .HasColumnType("numeric(5,2)");

        // DB CHANGE GATE aprobado 2026-08-02 (judgment-day ronda 1, hardening de esquema):
        // clave alterna (Id, IdTenant) para que fk_clientes_lista_precio pueda ser compuesta
        // -- mismo patrón que Empresa.HasAlternateKey/fk_clientes_empresa, fk_proveedores_empresa
        // y fk_listas_precio_empresa. Sin esto, un id_lista_precio de OTRO tenant pasaba la FK
        // simple (la columna referenciada, id_lista_precio, es única globalmente por ser PK) y
        // solo RLS lo frenaba -- con la clave compuesta, la propia FK ya lo rechaza (23503).
        builder.HasAlternateKey(l => new { l.Id, l.IdTenant })
            .HasName("ak_listas_precio_id_lista_precio_id_tenant");

        builder.HasOne<ListaPrecio>()
            .WithMany()
            .HasForeignKey(l => l.IdListaBase)
            .HasConstraintName("fk_listas_precio_lista_base")
            .OnDelete(DeleteBehavior.Restrict);

        // Nombre explícito en snake_case (doc 10), mismo motivo que ClienteConfiguration.
        builder.HasIndex(l => l.IdListaBase).HasDatabaseName("ix_listas_precio_lista_base");

        builder.HasIndex(l => new { l.IdTenant, l.EsDefault })
            .HasDatabaseName("ux_listas_precio_default_compartido")
            .HasFilter("id_empresa IS NULL AND deleted_at IS NULL AND es_default = true")
            .IsUnique();

        builder.HasIndex(l => new { l.IdTenant, l.IdEmpresa, l.EsDefault })
            .HasDatabaseName("ux_listas_precio_default_empresa")
            .HasFilter("id_empresa IS NOT NULL AND deleted_at IS NULL AND es_default = true")
            .IsUnique();
    }
}
