namespace Ways.Domain.Stock;

/// <summary>
/// Ledger de movimientos de stock (doc 10 §6, design: Table Shapes — write path B): la tabla
/// que reconstruye y audita <see cref="Stock.Cantidad"/> (doc 10 principio 7). Append-only por
/// contrato — ningún endpoint actualiza ni elimina una fila, jamás.
///
/// A propósito NO hereda de <see cref="Common.EntidadBase"/>/<see cref="Common.EntidadTenant"/>
/// aunque tiene identidad propia (<see cref="Id"/>): un ledger append-only no tiene
/// <c>updated_at</c>/<c>deleted_at</c> con sentido (nunca se edita ni se da de baja una fila
/// escrita), así que design nombra su única columna de fecha <c>creado_el</c> (no
/// <c>created_at</c>) para marcar la diferencia — mismo criterio de "columna manual" que
/// <see cref="Ventas.NumeracionComprobante"/>/<see cref="Ways.Domain.Stock.Stock"/>, con filtro
/// de tenant escrito a mano en <c>WaysDbContext.AplicarFiltroDeTenantEnMovimientoStock</c>.
///
/// <see cref="IdComprobanteCompra"/> NO existe todavía (design: Table Shapes — write path B):
/// <c>comprobantes_compra</c> no existe, así que la columna se difiere a la etapa 8 (que la
/// crea junto con su FK) en vez de dejar un <c>int?</c> sin constraint.
/// </summary>
public class MovimientoStock
{
    public int Id { get; set; }
    public int IdTenant { get; set; }

    public int IdArticulo { get; set; }
    public int IdPuntoVenta { get; set; }

    /// <summary>Con signo: venta negativa, ajuste/anulación según corresponda (design: The Sale
    /// Transaction — <c>movimientos_stock (cantidad = −item.cantidad, motivo = venta)</c>).
    /// Nunca cero — <c>ck_movimientos_stock_cantidad_no_cero</c> lo respalda a nivel esquema.</summary>
    public decimal Cantidad { get; set; }

    public MotivoStock Motivo { get; set; }

    /// <summary>Poblado solo cuando <see cref="Motivo"/> es <see cref="MotivoStock.Venta"/> o
    /// <see cref="MotivoStock.Anulacion"/> (design: The Sale Transaction).</summary>
    public int? IdComprobanteVenta { get; set; }

    /// <summary>Transferencias entre locales (doc 10 §6) — columna creada pero nunca escrita
    /// en esta etapa (design: Table Shapes — write path B, "created, never written"): la
    /// feature de transferencia no tiene camino de escritura hasta que <c>motivo_stock.
    /// Transferencia</c> se active.</summary>
    public int? IdPuntoVentaDestino { get; set; }

    public int IdEmpleado { get; set; }

    public string? Observaciones { get; set; }

    public DateTimeOffset CreadoEl { get; set; }
}
