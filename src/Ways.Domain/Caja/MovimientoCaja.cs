namespace Ways.Domain.Caja;

/// <summary>
/// Ledger de movimientos físicos de caja fuera de la venta (doc 10 §7, design: Table Shapes —
/// write path B): retiros, refuerzos y apertura de cajón (F12 del legacy). Append-only por
/// contrato — ningún endpoint actualiza ni elimina una fila.
///
/// A propósito NO hereda de <see cref="Common.EntidadBase"/>/<see cref="Common.EntidadTenant"/>
/// — mismo criterio que <see cref="Ways.Domain.Stock.MovimientoStock"/>: un ledger append-only
/// no tiene <c>updated_at</c>/<c>deleted_at</c> con sentido, así que su única columna de fecha
/// es <c>creado_el</c>, con filtro de tenant escrito a mano en
/// <c>WaysDbContext.AplicarFiltroDeTenantEnMovimientoCaja</c>.
/// </summary>
public class MovimientoCaja
{
    public int Id { get; set; }
    public int IdTenant { get; set; }

    public int IdTurnoCaja { get; set; }

    public TipoMovimientoCaja Tipo { get; set; }

    /// <summary>Siempre <c>0</c> para <see cref="TipoMovimientoCaja.AperturaCajon"/>, siempre
    /// <c>&gt; 0</c> para los demás (design decisión 8, <c>ck_movimientos_caja_importe</c>).</summary>
    public decimal Importe { get; set; }

    /// <summary>Obligatorio y con longitud mínima uniformada para los tres tipos por igual
    /// (design decisión 8, <c>ck_movimientos_caja_motivo_minimo</c>): mover dinero físico o
    /// abrir el cajón merecen una razón registrada.</summary>
    public required string Motivo { get; set; }

    public int IdEmpleado { get; set; }

    public DateTimeOffset CreadoEl { get; set; }
}
