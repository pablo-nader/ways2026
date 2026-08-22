using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Clientes;
using Ways.Domain.Organizacion;
using Ways.Domain.Usuarios;
using Ways.Domain.Ventas;
// stage-19a: NO se agrega `using Ways.Domain.Fiscal;` a propósito — `ResultadoFiscal` (la
// propiedad de ComprobanteVenta) y `ResultadoFiscal` (el enum) comparten nombre; el tipo se
// referencia siempre calificado más abajo, mismo criterio que Ways.Domain.Auditoria.Auditoria
// en WaysDbContext.cs.

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

            // stage-19a (proposal.md §D, 4 conjuntos): o las cuatro columnas fiscales están
            // NULL (100% del tráfico no fiscal), o resultado_fiscal está seteado con cae/
            // cae_vencimiento llegando JUNTOS y presentes SII resultado_fiscal es una de las dos
            // aprobaciones. Valida trivialmente en toda fila existente (las cuatro NULL).
            t.HasCheckConstraint(
                "ck_comprobantes_venta_fiscal_coherente",
                "(resultado_fiscal IS NULL AND cae IS NULL AND cae_vencimiento IS NULL AND observaciones_fiscales IS NULL) " +
                "OR (resultado_fiscal IS NOT NULL AND ((cae IS NULL) = (cae_vencimiento IS NULL)) " +
                "AND ((resultado_fiscal IN ('aprobado','aprobado_con_observaciones')) = (cae IS NOT NULL)))");

            t.HasCheckConstraint("ck_comprobantes_venta_cae_digitos", "cae IS NULL OR cae ~ '^[0-9]{14}$'");
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

        // stage-17-presupuestos-y-remitos (proposal §G): la columna ya existía NULL desde esta
        // migración de slice 1 — nullable, metadata-only, sin rewrite de tabla. FK 23 e índice
        // 29 se declaran más abajo.
        builder.Property(c => c.IdPresupuestoOrigen).HasColumnName("id_presupuesto_origen");

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

        // stage-19a (proposal.md §D): aditivas, NULLABLE — resultado_fiscal NULL significa "no
        // es un comprobante fiscal", el 100% del tráfico TX/NCX/TXR/RC de siempre.
        builder.Property(c => c.Cae).HasColumnName("cae").HasMaxLength(14);
        builder.Property(c => c.CaeVencimiento).HasColumnName("cae_vencimiento").HasColumnType("date");
        builder.Property(c => c.ResultadoFiscal).HasColumnName("resultado_fiscal").HasColumnType("resultado_fiscal");

        // jsonb — precedente Auditoria.cs:40-45/AuditoriaConfiguration.cs:42-43: string ya
        // serializado, nunca la respuesta cruda de ARCA (persistiría Token/Sign sin cifrado).
        builder.Property(c => c.ObservacionesFiscales).HasColumnName("observaciones_fiscales").HasColumnType("jsonb");

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

        // Index 29 (proposal §G): UNIQUE PARCIAL — la garantía de 1:1 de conversión (decisión 8)
        // Y el índice de soporte de FK 23 a la vez, declarada explícita con nombre doc-10 en vez
        // de dejar que EF autogenere una PascalCase (trampa documentada en
        // NumeracionComprobanteConfiguration.cs:44-49). Mismas columnas que la FK, mismo orden.
        builder.HasIndex(c => new { c.IdPresupuestoOrigen, c.IdTenant })
            .HasDatabaseName("ux_comprobantes_venta_presupuesto_origen")
            .IsUnique()
            .HasFilter("id_presupuesto_origen IS NOT NULL");

        // stage-19a (proposal.md §D, index 3): PARCIAL sobre un estado vacío en el 100% de las
        // filas existentes — sin esto, resolver los 'pendiente' hace table scan sobre la tabla
        // más caliente del sistema. Su consumidor (la reconciliación/reintento) llega en slice 5
        // — criterio anti-especulación de la etapa 13.
        builder.HasIndex(c => new { c.IdPuntoVenta, c.IdTenant })
            .HasDatabaseName("ix_comprobantes_venta_fiscal_pendientes")
            .HasFilter("resultado_fiscal = 'pendiente'::resultado_fiscal");

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

        // FK 23 (proposal §G): compuesta, nullable, MATCH SIMPLE (default) — con
        // id_presupuesto_origen NULL la constraint no se chequea (100% del tráfico anterior a
        // esta etapa, permanentemente legítimo). Ninguna CHECK ata esta columna a nada: el
        // acuerdo presupuesto↔venta (mismo tenant/cliente, no vencido, todavía enviado) es una
        // regla cross-table que el esquema no puede expresar — la aplica el UPDATE guardado de
        // EscriturasDePresupuesto.MarcarConvertidoAsync (slice 3).
        builder.HasOne<Presupuesto>()
            .WithMany()
            .HasForeignKey(c => new { c.IdPresupuestoOrigen, c.IdTenant })
            .HasPrincipalKey(p => new { p.Id, p.IdTenant })
            .HasConstraintName("fk_comprobantes_venta_presupuesto_origen")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
