using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Caja;
using Ways.Domain.Catalogos;
using Ways.Domain.Compras;
using Ways.Domain.Pagos;
using Ways.Domain.Organizacion;
using Ways.Domain.Plantel;
using Ways.Domain.Proveedores;
using Ways.Domain.Usuarios;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

/// <summary>
/// Mapea <see cref="Pago"/> (design: Table Shapes — write path C). La CHECK es defensa en
/// profundidad — <c>ServicioDePagos.RegistrarAsync</c> (Slice 3) ya rechaza un importe
/// <c>&lt;= 0</c> en el camino de servicio.
/// </summary>
public class PagoConfiguration : IEntityTypeConfiguration<Pago>
{
    public void Configure(EntityTypeBuilder<Pago> builder)
    {
        builder.ToTable("pagos", t =>
        {
            t.HasCheckConstraint("ck_pagos_importe_positivo", "importe > 0");

            // origen_fondos = caja_turno exige un turno; tesoreria (stage-gastos-origen-fondos-pos,
            // PR2) no lo exige aunque ServicioDePagos igual lo puebla (trazabilidad) — la CHECK es
            // defensa en profundidad, nunca el único guard: RegistrarAsync ya resuelve el turno
            // abierto antes de insertar cualquier pago, sea cual sea su origen.
            t.HasCheckConstraint(
                "ck_pagos_caja_turno_requiere_turno",
                "origen_fondos <> 'caja_turno' OR id_turno_caja IS NOT NULL");

            // Un pago nacido de un pago de personal es siempre un sueldo.
            t.HasCheckConstraint(
                "ck_pagos_movimiento_personal_es_sueldo",
                "id_movimiento_personal IS NULL OR categoria = 'sueldos'");
        });

        builder.HasKey(g => g.Id).HasName("pk_pagos");

        builder.Property(g => g.Id)
            .HasColumnName("id_pago")
            .UseIdentityByDefaultColumn();

        builder.Property(g => g.IdTenant).HasColumnName("id_tenant").IsRequired();

        builder.Property(g => g.Fecha).HasColumnName("fecha").IsRequired();

        // GastosOrigenFondosYTesoreriaPorEmpresa: backfill desde puntos_venta.id_empresa de cada
        // pago existente (ver el comentario del backfill en la migración).
        builder.Property(g => g.IdEmpresa).HasColumnName("id_empresa").IsRequired();

        // Nullable en el esquema (design aprobado) aunque ServicioDePagos siempre lo puebla hoy,
        // sea cual sea el origen — mismo idioma "sin .IsRequired()" que IdTurnoCaja.
        builder.Property(g => g.IdPuntoVenta).HasColumnName("id_punto_venta");

        // Nullable desde GastosOrigenFondosYTesoreriaPorEmpresa (ck_pagos_caja_turno_requiere_turno
        // exige un turno solo para origen_fondos = caja_turno) — mismo idioma "sin .IsRequired()"
        // que MovimientoTesoreriaConfiguration.IdTurnoCaja.
        builder.Property(g => g.IdTurnoCaja).HasColumnName("id_turno_caja");

        builder.Property(g => g.IdEmpleado).HasColumnName("id_empleado").IsRequired();

        builder.Property(g => g.Categoria)
            .HasColumnName("categoria")
            .HasColumnType("categoria_pago")
            .IsRequired();

        builder.Property(g => g.OrigenFondos)
            .HasColumnName("origen_fondos")
            .HasColumnType("origen_fondos_pago")
            .IsRequired();

        builder.Property(g => g.IdProveedor).HasColumnName("id_proveedor");
        builder.Property(g => g.IdArea).HasColumnName("id_area");

        builder.Property(g => g.Concepto).HasColumnName("concepto").HasColumnType("text").IsRequired();
        builder.Property(g => g.Detalle).HasColumnName("detalle").HasColumnType("text");

        builder.Property(g => g.IdMedioPago).HasColumnName("id_medio_pago").IsRequired();
        builder.Property(g => g.NumeroFactura).HasColumnName("numero_factura").HasColumnType("text");

        builder.Property(g => g.Importe).HasColumnName("importe").HasColumnType("numeric(14,2)").IsRequired();

        // stage-8-compras-transferencias-inventario, Slice 1 (design: Table Shapes — D, la FK
        // diferida de doc-10:426-434): columna + FK compuesta aterrizan juntas en esta migración.
        builder.Property(g => g.IdComprobanteCompra).HasColumnName("id_comprobante_compra");

        builder.Property(g => g.IdMovimientoPersonal).HasColumnName("id_movimiento_personal");

        builder.Property(g => g.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(g => g.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(g => g.DeletedAt).HasColumnName("deleted_at");

        builder.Ignore(g => g.EstaEliminada);

        builder.HasIndex(g => g.IdTenant).HasDatabaseName("ix_pagos_tenant");
        builder.HasIndex(g => new { g.IdEmpresa, g.IdTenant }).HasDatabaseName("ix_pagos_empresa");
        builder.HasIndex(g => new { g.IdTurnoCaja, g.IdTenant }).HasDatabaseName("ix_pagos_turno");
        builder.HasIndex(g => new { g.IdPuntoVenta, g.IdTenant, g.Fecha }).HasDatabaseName("ix_pagos_punto_venta_fecha");
        builder.HasIndex(g => new { g.IdProveedor, g.IdTenant }).HasDatabaseName("ix_pagos_proveedor");
        builder.HasIndex(g => new { g.IdComprobanteCompra, g.IdTenant }).HasDatabaseName("ix_pagos_comprobante_compra");
        builder.HasIndex(g => new { g.IdMovimientoPersonal, g.IdTenant }).HasDatabaseName("ix_pagos_movimiento_personal");

        // Índices de soporte de FK (evitan el índice implícito PascalCase de EF, misma trampa
        // que documenta ComprobanteVentaConfiguration).
        builder.HasIndex(g => g.IdEmpleado).HasDatabaseName("ix_pagos_empleado");
        builder.HasIndex(g => new { g.IdArea, g.IdTenant }).HasDatabaseName("ix_pagos_area");
        builder.HasIndex(g => new { g.IdMedioPago, g.IdTenant }).HasDatabaseName("ix_pagos_medio_pago");

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(g => g.IdTenant)
            .HasConstraintName("fk_pagos_tenant")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Empresa>()
            .WithMany()
            .HasForeignKey(g => new { g.IdEmpresa, g.IdTenant })
            .HasPrincipalKey(e => new { e.Id, e.IdTenant })
            .HasConstraintName("fk_pagos_empresa")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<PuntoVenta>()
            .WithMany()
            .HasForeignKey(g => new { g.IdPuntoVenta, g.IdTenant })
            .HasPrincipalKey(p => new { p.Id, p.IdTenant })
            .HasConstraintName("fk_pagos_punto_venta")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<TurnoCaja>()
            .WithMany()
            .HasForeignKey(g => new { g.IdTurnoCaja, g.IdTenant })
            .HasPrincipalKey(t => new { t.Id, t.IdTenant })
            .HasConstraintName("fk_pagos_turno")
            .OnDelete(DeleteBehavior.Restrict);

        // id_empleado: FK simple, mismo motivo que TurnoCajaConfiguration.fk_turnos_caja_empleado_apertura.
        builder.HasOne<Usuario>()
            .WithMany()
            .HasForeignKey(g => g.IdEmpleado)
            .HasConstraintName("fk_pagos_empleado")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Proveedor>()
            .WithMany()
            .HasForeignKey(g => new { g.IdProveedor, g.IdTenant })
            .HasPrincipalKey(p => new { p.Id, p.IdTenant })
            .HasConstraintName("fk_pagos_proveedor")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<ComprobanteCompra>()
            .WithMany()
            .HasForeignKey(g => new { g.IdComprobanteCompra, g.IdTenant })
            .HasPrincipalKey(c => new { c.Id, c.IdTenant })
            .HasConstraintName("fk_pagos_comprobante_compra")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<MovimientoPersonal>()
            .WithMany()
            .HasForeignKey(g => new { g.IdMovimientoPersonal, g.IdTenant })
            .HasPrincipalKey(m => new { m.Id, m.IdTenant })
            .HasConstraintName("fk_pagos_movimiento_personal")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Area>()
            .WithMany()
            .HasForeignKey(g => new { g.IdArea, g.IdTenant })
            .HasPrincipalKey(a => new { a.Id, a.IdTenant })
            .HasConstraintName("fk_pagos_area")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<MedioPago>()
            .WithMany()
            .HasForeignKey(g => new { g.IdMedioPago, g.IdTenant })
            .HasPrincipalKey(m => new { m.Id, m.IdTenant })
            .HasConstraintName("fk_pagos_medio_pago")
            .OnDelete(DeleteBehavior.Restrict);

        // stage-15-cc-proveedores-ledger, Slice 1 (gate §D): habilita la FK compuesta
        // fk_movimientos_cuenta_corriente_proveedor_pago — id_pago ya es único vía pk_pagos,
        // así que la constraint es estructuralmente inviolable (no agrega ningún modo de fallo
        // nuevo, solo el target de referencia compuesto que el resto de las FKs operativas de
        // este esquema usa).
        builder.HasAlternateKey(g => new { g.Id, g.IdTenant })
            .HasName("ak_pagos_id_pago_id_tenant");
    }
}
