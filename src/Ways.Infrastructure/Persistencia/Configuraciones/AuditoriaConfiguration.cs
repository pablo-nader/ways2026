using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Auditoria;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

/// <summary>
/// Mapea <see cref="Auditoria"/> — proposal §A del gate (DB CHANGE GATE, ratificado y aprobado):
/// 10 columnas, 1 PK, 3 FKs, 2 CHECKs de no-vacío, 3 índices, RLS estándar
/// (<see cref="Infrastructure.Multitenancy.RlsMigrationBuilderExtensions.HabilitarRlsDeTenant"/>,
/// aplicado en la migración). Nada de esto se altera fuera de esta sección — cualquier DDL extra
/// reabre el gate.
/// </summary>
public class AuditoriaConfiguration : IEntityTypeConfiguration<Auditoria>
{
    public void Configure(EntityTypeBuilder<Auditoria> builder)
    {
        builder.ToTable("auditoria", t =>
        {
            t.HasCheckConstraint("ck_auditoria_accion_no_vacia", "length(btrim(accion)) > 0");
            t.HasCheckConstraint("ck_auditoria_entidad_no_vacia", "length(btrim(entidad)) > 0");
        });

        builder.HasKey(a => a.Id).HasName("pk_auditoria");

        builder.Property(a => a.Id)
            .HasColumnName("id_auditoria")
            .UseIdentityByDefaultColumn();

        builder.Property(a => a.IdTenant).HasColumnName("id_tenant").IsRequired();
        builder.Property(a => a.IdPuntoVenta).HasColumnName("id_punto_venta");
        builder.Property(a => a.IdActor).HasColumnName("id_actor").IsRequired();

        builder.Property(a => a.Accion).HasColumnName("accion").HasColumnType("text").IsRequired();
        builder.Property(a => a.Entidad).HasColumnName("entidad").HasColumnType("text").IsRequired();
        builder.Property(a => a.IdEntidad).HasColumnName("id_entidad").IsRequired();

        // string?/string mapeados como jsonb (proposal §A) — serializados por
        // SerializadorDeAuditoria, nunca por la convención por defecto de EF.
        builder.Property(a => a.ValorAnterior).HasColumnName("valor_anterior").HasColumnType("jsonb");
        builder.Property(a => a.ValorNuevo).HasColumnName("valor_nuevo").HasColumnType("jsonb").IsRequired();

        // Sin DEFAULT: IRelojDelSistema es la única fuente de tiempo (proposal §A).
        builder.Property(a => a.CreadoEl).HasColumnName("creado_el").IsRequired();

        builder.HasIndex(a => new { a.IdTenant, a.CreadoEl })
            .HasDatabaseName("ix_auditoria_tenant_creado")
            .IsDescending(false, true);

        builder.HasIndex(a => new { a.IdTenant, a.Entidad, a.IdEntidad })
            .HasDatabaseName("ix_auditoria_entidad");

        builder.HasIndex(a => new { a.IdTenant, a.IdActor, a.CreadoEl })
            .HasDatabaseName("ix_auditoria_actor")
            .IsDescending(false, false, true);

        // Índices de soporte de FK (evitan el índice implícito PascalCase de EF, mismo criterio
        // que ix_movimientos_stock_empleado/ix_comprobantes_venta_empleado): ninguno de los 3
        // índices de arriba empieza por id_actor ni por (id_punto_venta, id_tenant) — los dos
        // llevan id_tenant primero (RLS/consulta), así que EF generaría
        // IX_auditoria_id_actor/IX_auditoria_id_punto_venta_id_tenant si no se nombran acá.
        // Desvío registrado (tasks.md, slice 1): el gate cuenta 3 índices "de negocio"; estos 2
        // son la misma infraestructura mecánica de soporte de FK que TODA otra tabla de este
        // esquema ya lleva (nunca contada aparte en ningún gate anterior) — Postgres no la exige
        // para la integridad referencial, pero SIN ella un DELETE sobre tenants/puntos_venta/
        // usuarios haría table scan sobre auditoria para validar la RESTRICT.
        builder.HasIndex(a => a.IdActor).HasDatabaseName("ix_auditoria_id_actor");
        builder.HasIndex(a => new { a.IdPuntoVenta, a.IdTenant }).HasDatabaseName("ix_auditoria_punto_venta");

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(a => a.IdTenant)
            .HasConstraintName("fk_auditoria_tenant")
            .OnDelete(DeleteBehavior.Restrict);

        // Compuesta (id_punto_venta, id_tenant) MATCH SIMPLE (default de Postgres): con
        // id_punto_venta NULL la constraint no se chequea — la integridad de tenant queda en
        // fk_auditoria_tenant (proposal §A). Contra la AK existente de PuntoVenta
        // (ak_puntos_venta_id_punto_venta_id_tenant, ya establecida por otros FKs compuestos
        // sobre PuntoVenta, p.ej. fk_movimientos_stock_punto_venta).
        builder.HasOne<PuntoVenta>()
            .WithMany()
            .HasForeignKey(a => new { a.IdPuntoVenta, a.IdTenant })
            .HasPrincipalKey(p => new { p.Id, p.IdTenant })
            .HasConstraintName("fk_auditoria_punto_venta")
            .OnDelete(DeleteBehavior.Restrict);

        // FK SIMPLE (no compuesta) — mismo criterio documentado en doc-10:563-567 para
        // id_empleado/fk_movimientos_stock_empleado: una alterna (Id, IdTenant) sobre Usuario
        // forzaría IdTenant NOT NULL, rompiendo el sentinel de plataforma. id_actor nunca es
        // input de cliente (gate §B).
        builder.HasOne<Usuario>()
            .WithMany()
            .HasForeignKey(a => a.IdActor)
            .HasConstraintName("fk_auditoria_actor")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
