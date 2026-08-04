using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Ofertas;
using Ways.Domain.Organizacion;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

/// <summary>
/// Mapea <see cref="Oferta"/> (design decision 1, Table Shapes): entidad dedicada, catálogo
/// scope con <see cref="Oferta.IdEmpresa"/> opcional — a diferencia de
/// <c>ConfiguracionDeCatalogo&lt;T&gt;</c>, sin dedupe por nombre (design decision 6: el
/// nombre es una etiqueta de ticket, deliberadamente no único — sin índice único sobre
/// <c>nombre</c>, exención documentada, no un descuido).
/// </summary>
public class OfertaConfiguration : IEntityTypeConfiguration<Oferta>
{
    public void Configure(EntityTypeBuilder<Oferta> builder)
    {
        builder.ToTable("ofertas", t =>
        {
            // Las cuatro CHECKs son defensa en profundidad (design: Backstop Map,
            // reachability note): ReglaDeOfertas ya valida los cuatro invariantes en el
            // camino de servicio, así que en operación normal ninguna es alcanzable — quedan
            // como respaldo de esquema ante una escritura cruda/fuera de banda.
            t.HasCheckConstraint(
                "ck_ofertas_alcance_exclusivo",
                "num_nonnulls(id_articulo, id_grupo, id_categoria) = 1");

            t.HasCheckConstraint(
                "ck_ofertas_beneficio_exclusivo",
                "num_nonnulls(precio_unitario, porcentaje, importe_fijo) = 1");

            // NULL-tolerant en ambos ejes (fecha y hora): cada eje de vigencia es
            // independientemente opcional (spec: Vigencia Window Semantics).
            t.HasCheckConstraint(
                "ck_ofertas_ventana_valida",
                "(fecha_desde IS NULL OR fecha_hasta IS NULL OR fecha_hasta >= fecha_desde) " +
                "AND (hora_desde IS NULL OR hora_hasta IS NULL OR hora_hasta >= hora_desde)");

            t.HasCheckConstraint(
                "ck_ofertas_dias_semana",
                "dias_semana IS NULL OR dias_semana <@ ARRAY[1,2,3,4,5,6,7]::smallint[]");
        });

        builder.HasKey(o => o.Id);

        builder.Property(o => o.Id)
            .HasColumnName("id_oferta")
            .UseIdentityByDefaultColumn();

        builder.Property(o => o.IdTenant)
            .HasColumnName("id_tenant")
            .IsRequired();

        // Habilita la FK compuesta del dependiente (ofertas_listas) — mismo patrón que
        // Articulo/Empresa/Categoria/ListaPrecio (ADR-9).
        builder.HasAlternateKey(o => new { o.Id, o.IdTenant })
            .HasName("ak_ofertas_id_oferta_id_tenant");

        builder.Property(o => o.IdEmpresa).HasColumnName("id_empresa");

        builder.Property(o => o.Nombre)
            .HasColumnName("nombre")
            .HasColumnType("citext")
            .HasMaxLength(150)
            .IsRequired();

        builder.Property(o => o.IdArticulo).HasColumnName("id_articulo");
        builder.Property(o => o.IdGrupo).HasColumnName("id_grupo");
        builder.Property(o => o.IdCategoria).HasColumnName("id_categoria");

        builder.Property(o => o.FechaDesde).HasColumnName("fecha_desde").HasColumnType("date");
        builder.Property(o => o.FechaHasta).HasColumnName("fecha_hasta").HasColumnType("date");
        builder.Property(o => o.HoraDesde).HasColumnName("hora_desde").HasColumnType("time");
        builder.Property(o => o.HoraHasta).HasColumnName("hora_hasta").HasColumnType("time");

        // smallint[] nativo de Npgsql, sin converter (design: Migration Sequencing).
        builder.Property(o => o.DiasSemana).HasColumnName("dias_semana").HasColumnType("smallint[]");

        builder.Property(o => o.CantidadMinima)
            .HasColumnName("cantidad_minima")
            .HasColumnType("numeric(12,3)");

        builder.Property(o => o.PrecioUnitario)
            .HasColumnName("precio_unitario")
            .HasColumnType("numeric(14,2)");

        builder.Property(o => o.Porcentaje)
            .HasColumnName("porcentaje")
            .HasColumnType("numeric(5,2)");

        builder.Property(o => o.ImporteFijo)
            .HasColumnName("importe_fijo")
            .HasColumnType("numeric(14,2)");

        builder.Property(o => o.Prioridad)
            .HasColumnName("prioridad")
            .HasDefaultValue(0)
            .IsRequired();

        builder.Property(o => o.Acumulable)
            .HasColumnName("acumulable")
            .HasDefaultValue(false)
            .IsRequired();

        builder.Property(o => o.Activo)
            .HasColumnName("activo")
            .HasDefaultValue(true)
            .IsRequired();

        builder.Property(o => o.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(o => o.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(o => o.DeletedAt).HasColumnName("deleted_at");

        builder.Ignore(o => o.EstaEliminada);

        // Nombres explícitos en snake_case (doc 10): sin esto EF nombra los índices de
        // soporte de cada FK con su convención propia (PascalCase) — mismo fix que
        // ArticuloConfiguration (stage-3-articulos-y-precios).
        builder.HasIndex(o => o.IdTenant).HasDatabaseName("ix_ofertas_tenant");
        builder.HasIndex(o => new { o.IdEmpresa, o.IdTenant }).HasDatabaseName("ix_ofertas_empresa");
        builder.HasIndex(o => new { o.IdArticulo, o.IdTenant }).HasDatabaseName("ix_ofertas_articulo");
        builder.HasIndex(o => new { o.IdGrupo, o.IdTenant }).HasDatabaseName("ix_ofertas_grupo");
        builder.HasIndex(o => new { o.IdCategoria, o.IdTenant }).HasDatabaseName("ix_ofertas_categoria");

        // Deliberadamente SIN índice único sobre nombre (design decision 6, Table Shapes):
        // "nombre" es una etiqueta de ticket, no un identificador de negocio — dos ofertas
        // "2x1 Verano" con ventanas distintas son legítimas. Exención documentada del
        // backstop map, no un descuido.

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(o => o.IdTenant)
            .HasConstraintName("fk_ofertas_tenant")
            .OnDelete(DeleteBehavior.Restrict);

        // FK compuesta opcional a empresas (ADR-9): id_empresa NULL ⇒ todo el tenant, MATCH
        // SIMPLE salta el chequeo cuando esa columna es NULL — mismo patrón que
        // ConfiguracionDeCatalogo<T>.
        builder.HasOne<Empresa>()
            .WithMany()
            .HasForeignKey(o => new { o.IdEmpresa, o.IdTenant })
            .HasPrincipalKey(e => new { e.Id, e.IdTenant })
            .HasConstraintName("fk_ofertas_empresa")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Articulo>()
            .WithMany()
            .HasForeignKey(o => new { o.IdArticulo, o.IdTenant })
            .HasPrincipalKey(a => new { a.Id, a.IdTenant })
            .HasConstraintName("fk_ofertas_articulo")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Grupo>()
            .WithMany()
            .HasForeignKey(o => new { o.IdGrupo, o.IdTenant })
            .HasPrincipalKey(g => new { g.Id, g.IdTenant })
            .HasConstraintName("fk_ofertas_grupo")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Categoria>()
            .WithMany()
            .HasForeignKey(o => new { o.IdCategoria, o.IdTenant })
            .HasPrincipalKey(c => new { c.Id, c.IdTenant })
            .HasConstraintName("fk_ofertas_categoria")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
