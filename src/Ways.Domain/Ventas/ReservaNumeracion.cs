namespace Ways.Domain.Ventas;

/// <summary>
/// Un bloque de <c>comprobantes_venta.numero</c> reservado por un dispositivo de escritorio para
/// vender offline (stage-pos-reserva-de-numeracion, DB CHANGE GATE aprobado): <see cref="Desde"/>/
/// <see cref="Hasta"/> son inclusive, y a lo sumo UNA fila viva (<see cref="AbandonadaAt"/> null)
/// por <c>(IdTenant, IdPuntoVenta, TipoComprobante, IdDispositivo)</c> — invariante que impone el
/// índice parcial <c>ux_reservas_numeracion_dispositivo_activo</c>, nunca este tipo.
///
/// Sin <c>ConsumidoHasta</c> a propósito (gate del owner): cuántos números del bloque ya se
/// vendieron es derivable contando <c>comprobantes_venta</c> en el rango — duplicar ese estado
/// acá es la misma fuente de divergencia que el arqueo evita al derivarse de los comprobantes del
/// turno, nunca de un contador propio.
///
/// <see cref="EntregadoHasta"/> NO es ese contador rechazado, aunque se parezca: un número que el
/// dispositivo ya le imprimió a un cliente y que todavía está en su outbox local no tiene fila en
/// <c>comprobantes_venta</c>, así que ese dato NO es derivable del servidor — es exactamente el
/// hueco que la guarda de cierre necesita ver. "Vendido y llegado" sigue derivándose de los
/// comprobantes; acá solo vive "entregado localmente", que nadie más puede saber.
///
/// PK-only en espíritu de auditoría (created_at/updated_at, SIN deleted_at): <see cref="AbandonadaAt"/>
/// ya es el único ciclo de vida que esta fila necesita — un <c>deleted_at</c> paralelo sería un
/// segundo "estoy muerta" redundante. <see cref="Application.Ventas.AsignadorDeNumeroComprobante"/>
/// es el único escritor legítimo (SQL crudo, misma convención que <c>NumeracionComprobante</c>) —
/// nunca vía <c>SaveChangesAsync</c>.
/// </summary>
public class ReservaNumeracion
{
    public long Id { get; set; }

    public int IdTenant { get; set; }

    public int IdPuntoVenta { get; set; }

    /// <summary>Mismo significado que <see cref="NumeracionComprobante.TipoComprobante"/>
    /// (<c>tipos_comprobante.codigo</c>) — nunca input de cliente sin resolver antes.</summary>
    public required string TipoComprobante { get; set; }

    public int IdDispositivo { get; set; }

    /// <summary>Primer número del bloque, inclusive.</summary>
    public long Desde { get; set; }

    /// <summary>Último número del bloque, inclusive.</summary>
    public long Hasta { get; set; }

    /// <summary><c>null</c> ⇒ bloque vigente. Poblado cuando el dispositivo pide un bloque nuevo
    /// mientras todavía tiene uno vivo (pérdida de almacenamiento local) — el bloque abandonado
    /// nunca se reactiva ni se reparte de nuevo, aunque queden números sin consumir.</summary>
    public DateTimeOffset? AbandonadaAt { get; set; }

    /// <summary>Número más alto que el dispositivo declara haber repartido de este bloque
    /// (<c>proximo - 1</c> de su puntero local); <c>Desde - 1</c> ⇒ todavía no repartió ninguno.
    /// Dato NO derivable del servidor — ver el doc-comment de la clase.</summary>
    public long? EntregadoHasta { get; set; }

    /// <summary>Ventas que el dispositivo declara sin llegar al servidor (outbox + rechazadas).
    /// Existe para que el rechazo del cierre pueda decir CUÁNTAS faltan, no solo que faltan.</summary>
    public int? Pendientes { get; set; }

    /// <summary><c>null</c> ⇒ este bloque nunca rindió, y la guarda de cierre falla cerrado. Es la
    /// frescura del reporte: un reporte viejo no prueba nada sobre lo que el dispositivo vendió
    /// después de mandarlo.</summary>
    public DateTimeOffset? ReportadoAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
