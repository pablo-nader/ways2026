namespace Ways.Domain.CuentaCorriente;

/// <summary>
/// Tipo de un <see cref="MovimientoCuentaCorriente"/> (doc 10 §8). Enum nativo de Postgres
/// (<c>tipo_movimiento_cc</c>). Stage 5 abrió camino de escritura para <see cref="Consumo"/>
/// (venta con cuenta corriente) y filas con forma de <see cref="Ajuste"/> usadas como
/// contramovimiento de anulación (spec: consumo-cuenta-corriente / Movimiento Schema At Rest).
/// Stage 7 abre <see cref="Pago"/> (pago a cuenta, comprobante RC) y
/// <see cref="ActualizacionPrecios"/> (reliquidación a precio del día).
/// </summary>
public enum TipoMovimientoCc
{
    Consumo,
    Pago,
    Ajuste,
    ActualizacionPrecios
}
