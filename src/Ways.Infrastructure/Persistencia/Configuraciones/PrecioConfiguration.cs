using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Articulos;
using Ways.Domain.Catalogos;
using Ways.Domain.Organizacion;
using Ways.Domain.Precios;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

/// <summary>
/// Mapea <see cref="Precio"/> (design decisions 3/4, Table Shapes): append-only, como mucho
/// una fila abierta (<c>vigente_hasta IS NULL</c>) por par <c>(articulo, lista_precio)</c> —
/// <c>ux_precios_vigente</c> es la constraint que hace cumplir "at most one pending future
/// price" (design: Technical Approach, "no extra constraint" insight) junto con la disciplina
/// transaccional de <c>ServicioDePrecios.AbrirNuevoPrecioAsync</c> (Slice 3).
/// </summary>
public class PrecioConfiguration : IEntityTypeConfiguration<Precio>
{
    public void Configure(EntityTypeBuilder<Precio> builder)
    {
        // ck_precios_ventana_valida (judgment-day, slice 3 ronda 2, GATE-APROBADO 2026-08-03):
        // backstop de esquema contra un intervalo INVERTIDO (<c>vigente_hasta &lt;
        // vigente_desde</c>) — ServicioDePrecios.AbrirNuevoPrecioAsync ya lo garantiza en el
        // camino de servicio (validación simétrica contra la fila activa y contra el predecesor
        // de una pendiente reemplazada), esto cubre una escritura cruda/fuera de banda que lo
        // bypasee (misma familia que ck_clientes_cf_protegido).
        //
        // `>=`, NO `>` estricto: una pendiente reemplazada se cierra deliberadamente con
        // <c>vigente_hasta == vigente_desde</c> (ventana VACÍA, no invertida — ver el
        // doc-comment de AbrirNuevoPrecioAsync) para que nunca se vuelva visible sin necesidad de
        // borrar la fila. Un `>` estricto rechazaría ese estado legítimo y ya probado
        // (ReemplazarUnPendienteConUnaFechaPosteriorALaOriginalNoDejaUnHueco); `>=` sigue
        // atrapando el bug real (una fila con vigente_hasta ANTERIOR a su propio vigente_desde).
        builder.ToTable("precios", t => t.HasCheckConstraint(
            "ck_precios_ventana_valida", "vigente_hasta IS NULL OR vigente_hasta >= vigente_desde"));

        builder.HasKey(p => p.Id);

        builder.Property(p => p.Id)
            .HasColumnName("id_precio")
            .UseIdentityByDefaultColumn();

        builder.Property(p => p.IdTenant)
            .HasColumnName("id_tenant")
            .IsRequired();

        builder.Property(p => p.IdArticulo)
            .HasColumnName("id_articulo")
            .IsRequired();

        builder.Property(p => p.IdListaPrecio)
            .HasColumnName("id_lista_precio")
            .IsRequired();

        builder.Property(p => p.Monto)
            .HasColumnName("precio")
            .HasColumnType("numeric(14,2)")
            .IsRequired();

        builder.Property(p => p.VigenteDesde)
            .HasColumnName("vigente_desde")
            .IsRequired();

        builder.Property(p => p.VigenteHasta)
            .HasColumnName("vigente_hasta");

        builder.Property(p => p.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(p => p.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(p => p.DeletedAt).HasColumnName("deleted_at");

        builder.Ignore(p => p.EstaEliminada);

        // spec "Price History Never Overwrites" / "Programmable Future Prices, At Most One
        // Pending": como mucho una fila abierta por par (articulo, lista_precio).
        builder.HasIndex(p => new { p.IdArticulo, p.IdListaPrecio })
            .HasDatabaseName("ux_precios_vigente")
            .HasFilter("vigente_hasta IS NULL AND deleted_at IS NULL")
            .IsUnique();

        builder.HasIndex(p => p.IdTenant).HasDatabaseName("ix_precios_tenant");
        builder.HasIndex(p => new { p.IdArticulo, p.IdTenant }).HasDatabaseName("ix_precios_articulo");
        builder.HasIndex(p => new { p.IdListaPrecio, p.IdTenant }).HasDatabaseName("ix_precios_lista_precio");

        // Soporta las consultas de precio vigente por fecha (spec: Current-Price Query
        // Semantics By Date): filtra primero por articulo+lista y despues rango-escanea
        // vigente_desde/vigente_hasta para la fila que cubre una fecha dada.
        builder.HasIndex(p => new { p.IdArticulo, p.IdListaPrecio, p.VigenteDesde })
            .HasDatabaseName("ix_precios_vigencia");

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(p => p.IdTenant)
            .HasConstraintName("fk_precios_tenant")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Articulo>()
            .WithMany()
            .HasForeignKey(p => new { p.IdArticulo, p.IdTenant })
            .HasPrincipalKey(a => new { a.Id, a.IdTenant })
            .HasConstraintName("fk_precios_articulo")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<ListaPrecio>()
            .WithMany()
            .HasForeignKey(p => new { p.IdListaPrecio, p.IdTenant })
            .HasPrincipalKey(l => new { l.Id, l.IdTenant })
            .HasConstraintName("fk_precios_lista_precio")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
