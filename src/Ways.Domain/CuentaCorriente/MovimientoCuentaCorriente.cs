namespace Ways.Domain.CuentaCorriente;

/// <summary>
/// Ledger de movimientos de cuenta corriente de clientes (doc 10 §8, design: Table Shapes —
/// write path C): la tabla que reconstruye y audita <c>Cliente.Saldo</c> (doc 10 principio 7),
/// mismo criterio que <see cref="Ways.Domain.Stock.MovimientoStock"/> sobre
/// <c>Ways.Domain.Stock.Stock.Cantidad</c>. Inmutable una vez insertado (spec: Movimiento Schema
/// At Rest) — ningún endpoint edita ni elimina una fila.
///
/// A propósito NO hereda de <see cref="Common.EntidadBase"/>/<see cref="Common.EntidadTenant"/>
/// — mismo family shape que <see cref="Ways.Domain.Stock.MovimientoStock"/>: un ledger
/// append-only no tiene <c>updated_at</c>/<c>deleted_at</c> con sentido. A diferencia de
/// <c>MovimientoStock</c>, no necesita una columna <c>creado_el</c> separada: <see cref="Fecha"/>
/// ya cumple ese rol (el <c>momento</c> pineado de la venta, doc 10 §8). Filtro de tenant escrito
/// a mano en <c>WaysDbContext.AplicarFiltroDeTenantEnMovimientoCuentaCorriente</c>.
/// </summary>
public class MovimientoCuentaCorriente
{
    public int Id { get; set; }
    public int IdTenant { get; set; }

    public int IdCliente { get; set; }
    public DateTimeOffset Fecha { get; set; }
    public int IdPuntoVenta { get; set; }
    public int IdEmpleado { get; set; }

    public TipoMovimientoCc Tipo { get; set; }

    /// <summary>Consumo/devolución que lo originó (design: The Sale Transaction — <c>INSERT
    /// movimientos_cuenta_corriente (tipo = consumo, ...)</c>). También poblado en el
    /// contramovimiento de anulación (spec: Anulación Produces A Contramovimiento).</summary>
    public int? IdComprobanteVenta { get; set; }

    /// <summary>El pago con medio cuenta corriente que generó este movimiento — clave alterna
    /// de <see cref="Ways.Domain.Ventas.PagoComprobante"/>.</summary>
    public int? IdPagoComprobante { get; set; }

    /// <summary>Con signo: positivo aumenta la deuda (consumo), negativo la reduce
    /// (contramovimiento de anulación).</summary>
    public decimal Importe { get; set; }

    /// <summary>Snapshot de <c>Cliente.Saldo</c> al momento del INSERT (spec: Consumo snapshots
    /// the resulting saldo) — nunca se re-deriva.</summary>
    public decimal SaldoResultante { get; set; }

    public string? Detalle { get; set; }
}
