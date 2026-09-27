using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ways.Domain.Dispositivos;
using Ways.Domain.Organizacion;
using Ways.Domain.Ventas;

namespace Ways.Infrastructure.Persistencia.Configuraciones;

/// <summary>
/// Mapea <see cref="ReservaNumeracion"/> (stage-pos-reserva-de-numeracion, DB CHANGE GATE
/// aprobado): scoping <c>[operativa]</c> (<c>id_tenant</c> + <c>id_punto_venta</c>, doc 09), mismo
/// criterio que <see cref="DispositivoConfiguration"/>. Esta tabla se escribe SIEMPRE con SQL crudo y
/// solo desde dos lugares: <c>AsignadorDeNumeroComprobante</c> (los bloques y las columnas del
/// reporte) y <c>ServicioDeTurnos.MarcarRendicionSaldadaAsync</c> (únicamente
/// <c>rendicion_saldada_at</c>) — este mapeo existe para que el modelo de EF conozca la forma
/// de la tabla (RLS, FKs, CHECK, índice único parcial, lecturas), no para que
/// <c>SaveChangesAsync</c> la toque (mismo criterio que <c>NumeracionComprobanteConfiguration</c>).
/// </summary>
public class ReservaNumeracionConfiguration : IEntityTypeConfiguration<ReservaNumeracion>
{
    public void Configure(EntityTypeBuilder<ReservaNumeracion> builder)
    {
        builder.ToTable("reservas_numeracion", t =>
        {
            t.HasCheckConstraint("ck_reservas_numeracion_rango", "hasta >= desde");

            // desde - 1 ⇒ el dispositivo reportó sin haber repartido ningún número todavía; hasta
            // ⇒ agotó el bloque. Fuera de ese rango el reporte es de otro bloque o está corrupto.
            t.HasCheckConstraint(
                "ck_reservas_numeracion_entregado_en_rango",
                "entregado_hasta IS NULL OR (entregado_hasta >= desde - 1 AND entregado_hasta <= hasta)");

            // Las tres columnas del reporte son un solo hecho ("el dispositivo rindió ESTO en ESTE
            // momento"): media rendición no existe, y dejarla entrar volvería ambiguo el
            // fail-closed de la guarda (reportado_at IS NULL ⇒ nunca rindió).
            t.HasCheckConstraint(
                "ck_reservas_numeracion_reporte_consistente",
                "(entregado_hasta IS NULL AND pendientes IS NULL AND reportado_at IS NULL) " +
                "OR (entregado_hasta IS NOT NULL AND pendientes IS NOT NULL AND reportado_at IS NOT NULL)");

            t.HasCheckConstraint(
                "ck_reservas_numeracion_pendientes_no_negativo",
                "pendientes IS NULL OR pendientes >= 0");
        });

        builder.HasKey(r => r.Id).HasName("pk_reservas_numeracion");

        builder.Property(r => r.Id)
            .HasColumnName("id_reserva_numeracion")
            .UseIdentityByDefaultColumn();

        builder.Property(r => r.IdTenant).HasColumnName("id_tenant").IsRequired();
        builder.Property(r => r.IdPuntoVenta).HasColumnName("id_punto_venta").IsRequired();

        // Mismo tipo/largo que NumeracionComprobante.TipoComprobante (character varying(30)) —
        // esta reserva vive en el MISMO espacio de numeración de esa tabla, así que su columna
        // tiene que aceptar exactamente los mismos valores.
        builder.Property(r => r.TipoComprobante)
            .HasColumnName("tipo_comprobante")
            .HasMaxLength(30)
            .IsRequired();

        builder.Property(r => r.IdDispositivo).HasColumnName("id_dispositivo").IsRequired();
        builder.Property(r => r.Desde).HasColumnName("desde").IsRequired();
        builder.Property(r => r.Hasta).HasColumnName("hasta").IsRequired();
        builder.Property(r => r.AbandonadaAt).HasColumnName("abandonada_at");

        // Rendición del dispositivo (guarda de cierre de turno): las escribe
        // AsignadorDeNumeroComprobante.RegistrarRendicionAsync con SQL crudo, como el resto de la
        // tabla. bigint para entregado_hasta, mismo tipo que desde/hasta — es un número de la misma
        // serie, no un conteo.
        builder.Property(r => r.EntregadoHasta).HasColumnName("entregado_hasta");
        builder.Property(r => r.Pendientes).HasColumnName("pendientes");
        builder.Property(r => r.ReportadoAt).HasColumnName("reportado_at");

        // Sin CHECK a propósito (gate del owner): el forzado tiene que poder saldar un bloque
        // abandonado, con reporte o sin ninguno — un bloque que nunca rindió es en sí mismo un motivo
        // de bloqueo, así que no hay ninguna invariante que afirmar contra las otras columnas.
        //
        // Sin índice nuevo tampoco, y el costo se dice completo (judgment-day): las filas que la
        // guarda de cierre escanea NO están acotadas. ix_reservas_numeracion_punto_venta cubre su
        // entrada (id_punto_venta, id_tenant), pero adentro entra TODO bloque no saldado del punto de
        // venta —uno más cada ~81 ventas offline por dispositivo (UMBRAL_DE_REPOSICION = 20 de
        // outboxOffline.ts sobre CANTIDAD_A_RESERVAR = 100 de useSincronizacionOffline.ts)— y cada uno
        // paga su propio escaneo lateral de comprobantes_venta. Un bloque sale de ese escaneo solo si
        // un forzado lo salda o si se revoca el dispositivo, así que el costo crece con la HISTORIA
        // del punto de venta y no con su estado. Acotarlo pide un índice parcial por
        // rendicion_saldada_at: migración aparte, fuera del gate ya aprobado.
        builder.Property(r => r.RendicionSaldadaAt).HasColumnName("rendicion_saldada_at");

        builder.Property(r => r.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(r => r.UpdatedAt).HasColumnName("updated_at").IsRequired();

        builder.HasIndex(r => r.IdTenant).HasDatabaseName("ix_reservas_numeracion_tenant");

        // Nombre explícito en snake_case — mismo fix documentado en
        // NumeracionComprobanteConfiguration/ArticuloEmpresaConfiguration: sin esto, EF nombra el
        // índice de soporte de la FK compuesta con su convención propia (PascalCase).
        builder.HasIndex(r => new { r.IdPuntoVenta, r.IdTenant })
            .HasDatabaseName("ix_reservas_numeracion_punto_venta");
        builder.HasIndex(r => new { r.IdDispositivo, r.IdTenant })
            .HasDatabaseName("ix_reservas_numeracion_dispositivo");

        // Un solo bloque VIVO por dispositivo y serie (gate del owner): la petición de un bloque
        // nuevo abandona el anterior ANTES de insertar este, así que el camino normal nunca choca
        // acá — el backstop real es para la carrera de dos pedidos concurrentes del mismo
        // dispositivo (db-error-backstops).
        builder.HasIndex(r => new { r.IdTenant, r.IdPuntoVenta, r.TipoComprobante, r.IdDispositivo })
            .HasDatabaseName("ux_reservas_numeracion_dispositivo_activo")
            .IsUnique()
            .HasFilter("abandonada_at IS NULL");

        // FK compuesta (id_punto_venta, id_tenant) → puntos_venta (ADR-9), mismo criterio que
        // NumeracionComprobanteConfiguration/DispositivoConfiguration.
        builder.HasOne<PuntoVenta>()
            .WithMany()
            .HasForeignKey(r => new { r.IdPuntoVenta, r.IdTenant })
            .HasPrincipalKey(p => new { p.Id, p.IdTenant })
            .HasConstraintName("fk_reservas_numeracion_punto_venta")
            .OnDelete(DeleteBehavior.Restrict);

        // FK compuesta (id_dispositivo, id_tenant) → dispositivos, vía la alternate key nueva
        // ak_dispositivos_id_dispositivo_id_tenant (gate del owner: dispositivos no tenía una FK
        // compuesta hacia sí mismo todavía) — mismo criterio ADR-9 que la de puntos_venta arriba.
        builder.HasOne<Dispositivo>()
            .WithMany()
            .HasForeignKey(r => new { r.IdDispositivo, r.IdTenant })
            .HasPrincipalKey(d => new { d.Id, d.IdTenant })
            .HasConstraintName("fk_reservas_numeracion_dispositivo")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(r => r.IdTenant)
            .HasConstraintName("fk_reservas_numeracion_tenant")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
