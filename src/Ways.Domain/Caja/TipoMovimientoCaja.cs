namespace Ways.Domain.Caja;

/// <summary>
/// Tipo de un <see cref="MovimientoCaja"/> (doc 10 §7). Enum nativo de Postgres
/// (<c>tipo_movimiento_caja</c>). <see cref="AperturaCajon"/> es la paridad del F12 del
/// legacy — la fila es el rastro de auditoría, nunca dinero (siempre <c>importe = 0</c>).
/// </summary>
public enum TipoMovimientoCaja
{
    Retiro,
    Refuerzo,
    AperturaCajon
}
