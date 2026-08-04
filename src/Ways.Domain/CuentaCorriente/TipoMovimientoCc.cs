namespace Ways.Domain.CuentaCorriente;

/// <summary>
/// Tipo de un <see cref="MovimientoCuentaCorriente"/> (doc 10 §8). Enum nativo de Postgres
/// (<c>tipo_movimiento_cc</c>). Stage 5 solo abre camino de escritura para <see cref="Consumo"/>
/// (venta con cuenta corriente) y filas con forma de <see cref="Ajuste"/> usadas como
/// contramovimiento de anulación (spec: consumo-cuenta-corriente / Movimiento Schema At Rest) —
/// <see cref="Pago"/> y <see cref="ActualizacionPrecios"/> son valores reservados de stage 7
/// (reliquidación F4, fuera de alcance).
/// </summary>
public enum TipoMovimientoCc
{
    Consumo,
    Pago,
    Ajuste,
    ActualizacionPrecios
}
