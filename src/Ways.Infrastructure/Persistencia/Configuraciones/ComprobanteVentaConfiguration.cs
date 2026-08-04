using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

/// <summary>
/// Mapea <see cref="ComprobanteVenta"/> (design: Table Shapes — write path A). La CHECK
/// <c>ck_comprobantes_venta_numero_positivo</c> es defensa en profundidad —
/// <c>AsignadorDeNumeroComprobante</c> (Slice 2) nunca entrega un número ≤ 0, esto cubre una
/// escritura cruda/fuera de banda (misma familia que <c>ck_clientes_cf_protegido</c>).
/// </summary>
public class ComprobanteVentaConfiguration : IEntityTypeConfiguration<ComprobanteVenta>
{
    public void Configure(EntityTypeBuilder<ComprobanteVenta> builder)
    {
        builder.ToTable("comprobantes_venta", t =>
        {
            t.HasCheckConstraint("ck_comprobantes_venta_numero_positivo", "numero > 0");
        });

        builder.HasKey(c => c.Id).HasName("pk_comprobantes_venta");

        builder.Property(c => c.Id)
            .HasColumnName("id_comprobante_venta")
            .UseIdentityByDefaultColumn();

        builder.Property(c => c.IdTenant)
            .HasColumnName("id_tenant")
            .IsRequired();

        // Habilita la FK compuesta autorreferenciada de fk_comprobantes_venta_comprobante_asociado
        // y las FKs compuestas de items_comprobante_venta/pagos_comprobante/movimientos_stock/
        // movimientos_cuenta_corriente — mismo patrón que Oferta/Cliente.
        builder.HasAlternateKey(c => new { c.Id, c.IdTenant })
            .HasName("ak_comprobantes_venta_id_comprobante_venta_id_tenant");

        builder.Property(c => c.IdTipoComprobante).HasColumnName("id_tipo_comprobante").IsRequired();
        builder.Property(c => c.Numero).HasColumnName("numero").IsRequired();
        builder.Property(c => c.Fecha).HasColumnName("fecha").IsRequired();
        builder.Property(c => c.IdPuntoVenta).HasColumnName("id_punto_venta").IsRequired();

        // stage-6-turnos-caja, Slice 1 (design: Table Shapes — write path E): la columna ya
        // existía NULL desde la migración de stage 5; acá se agrega la FK/índice. Slice 5 es
        // quien empieza a poblarla en cada venta nueva — las filas de stage 5 quedan NULL para
        // siempre (proposal decisión 8, sin backfill).
        builder.Property(c => c.IdTurnoCaja).HasColumnName("id_turno_caja");

        builder.Property(c => c.IdEmpleado).HasColumnName("id_empleado").IsRequired();
        builder.Property(c => c.IdCliente).HasColumnName("id_cliente").IsRequired();
        builder.Property(c => c.IdComprobanteAsociado).HasColumnName("id_comprobante_asociado");

        builder.Property(c => c.Subtotal).HasColumnName("subtotal").HasColumnType("numeric(14,2)").IsRequired();
        builder.Property(c => c.DescuentoTotal).HasColumnName("descuento_total").HasColumnType("numeric(14,2)").IsRequired();
        builder.Property(c => c.Total).HasColumnName("total").HasColumnType("numeric(14,2)").IsRequired();

        // NULL mientras discrimina_iva = false (TX/NCX de esta etapa nunca discriminan IVA).
        builder.Property(c => c.NetoGravado).HasColumnName("neto_gravado").HasColumnType("numeric(14,2)");
        builder.Property(c => c.IvaTotal).HasColumnName("iva_total").HasColumnType("numeric(14,2)");

        builder.Property(c => c.DireccionEntrega).HasColumnName("direccion_entrega").HasColumnType("text");
        builder.Property(c => c.Observaciones).HasColumnName("observaciones").HasColumnType("text");

        builder.Property(c => c.Estado)
            .HasColumnName("estado")
            .HasColumnType("estado_comprobante")
            .IsRequired();

        builder.Property(c => c.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(c => c.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(c => c.DeletedAt).HasColumnName("deleted_at");

        builder.Ignore(c => c.EstaEliminada);

        // ux_comprobantes_venta_numero: backstop del asignador atómico (design: Backstop Map,
        // "ordering trap" — su nombre contiene "_numero", ManejadorDeErrores.ClasificarUnicidad
        // tiene que resolverla ANTES de la rama genérica de esa substring).
        builder.HasIndex(c => new { c.IdPuntoVenta, c.IdTipoComprobante, c.Numero })
            .HasDatabaseName("ux_comprobantes_venta_numero")
            .IsUnique();

        builder.HasIndex(c => c.IdTenant).HasDatabaseName("ix_comprobantes_venta_tenant");

        // Índice de negocio (listado por punto de venta ordenado por fecha, spec: GET
        // /api/ventas filtros idPuntoVenta/desde/hasta) que además sirve de índice de soporte
        // de la FK compuesta a puntos_venta (columnas líderes en el mismo orden que la FK) —
        // evita el índice implícito PascalCase que EF generaría sin esto (la "trampa" que
        // Migration Sequencing documenta).
        builder.HasIndex(c => new { c.IdPuntoVenta, c.IdTenant, c.Fecha })
            .HasDatabaseName("ix_comprobantes_venta_punto_venta_fecha");

        builder.HasIndex(c => new { c.IdCliente, c.IdTenant }).HasDatabaseName("ix_comprobantes_venta_cliente");
        builder.HasIndex(c => new { c.IdComprobanteAsociado, c.IdTenant }).HasDatabaseName("ix_comprobantes_venta_asociado");
        builder.HasIndex(c => c.IdEmpleado).HasDatabaseName("ix_comprobantes_venta_empleado");
        builder.HasIndex(c => c.IdTipoComprobante).HasDatabaseName("ix_comprobantes_venta_tipo_comprobante");

        // stage-6-turnos-caja, Slice 1 (design: Table Shapes — write path E): índice de
        // soporte de fk_comprobantes_venta_turno, además el acceso de la derivación
        // (LectorDeMovimientosDelTurno, Slice 4) para "pagos/vueltos de este turno".
        builder.HasIndex(c => new { c.IdTurnoCaja, c.IdTenant }).HasDatabaseName("ix_comprobantes_venta_turno");

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(c => c.IdTenant)
            .HasConstraintName("fk_comprobantes_venta_tenant")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<PuntoVenta>()
            .WithMany()
            .HasForeignKey(c => new { c.IdPuntoVenta, c.IdTenant })
            .HasPrincipalKey(p => new { p.Id, p.IdTenant })
            .HasConstraintName("fk_comprobantes_venta_punto_venta")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Cliente>()
            .WithMany()
            .HasForeignKey(c => new { c.IdCliente, c.IdTenant })
            .HasPrincipalKey(cl => new { cl.Id, cl.IdTenant })
            .HasConstraintName("fk_comprobantes_venta_cliente")
            .OnDelete(DeleteBehavior.Restrict);

        // id_empleado = IContextoDeUsuario.UsuarioId (design decisión 11) — FK SIMPLE sobre
        // usuarios.id_usuario, DEVIACIÓN deliberada de la FK compuesta que design describe
        // textualmente: usuarios.IdTenant es NULLABLE a propósito (staff de plataforma,
        // UsuarioConfiguration) y una clave alterna (Id, IdTenant) fuerza esa columna a NOT
        // NULL por convención de EF (lo confirmó un intento real: el scaffold generó un
        // AlterColumn que la volvía NOT NULL DEFAULT 0, corrompiendo el sentinel de plataforma
        // de cualquier usuario existente). id_empleado NUNCA es input de cliente — siempre
        // deriva del actor autenticado (design decisión 11) — así que el riesgo que la FK
        // compuesta cerraba en otras tablas (un id ajeno colado por el cliente) no aplica acá;
        // RLS sigue siendo la protección real de lectura cross-tenant.
        builder.HasOne<Usuario>()
            .WithMany()
            .HasForeignKey(c => c.IdEmpleado)
            .HasConstraintName("fk_comprobantes_venta_empleado")
            .OnDelete(DeleteBehavior.Restrict);

        // tipos_comprobante es global (ADR-11) — FK simple, sin id_tenant.
        builder.HasOne<TipoComprobante>()
            .WithMany()
            .HasForeignKey(c => c.IdTipoComprobante)
            .HasConstraintName("fk_comprobantes_venta_tipo_comprobante")
            .OnDelete(DeleteBehavior.Restrict);

        // Autorreferenciada (spec: Devoluciones As NCX Comprobantes) — NULL salvo en un NCX que
        // referencia el TX que corrige (ReglaDeComprobantes.ValidarComprobanteAsociado).
        builder.HasOne<ComprobanteVenta>()
            .WithMany()
            .HasForeignKey(c => new { c.IdComprobanteAsociado, c.IdTenant })
            .HasPrincipalKey(c => new { c.Id, c.IdTenant })
            .HasConstraintName("fk_comprobantes_venta_comprobante_asociado")
            .OnDelete(DeleteBehavior.Restrict);

        // stage-6-turnos-caja, Slice 1 (design: Table Shapes — write path E): la columna es
        // nullable para siempre (stage-5 rows nunca se backfillean) — FK compuesta igual que el
        // resto, sin marcar la relación requerida.
        builder.HasOne<TurnoCaja>()
            .WithMany()
            .HasForeignKey(c => new { c.IdTurnoCaja, c.IdTenant })
            .HasPrincipalKey(t => new { t.Id, t.IdTenant })
            .HasConstraintName("fk_comprobantes_venta_turno")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
