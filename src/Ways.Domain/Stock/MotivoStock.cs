namespace Ways.Domain.Stock;

/// <summary>
/// Motivo de un <see cref="MovimientoStock"/> (doc 10 §6). Enum nativo de Postgres
/// (<c>motivo_stock</c>). Stage 5 solo abre camino de escritura para <see cref="Venta"/>,
/// <see cref="Anulacion"/> y <see cref="Ajuste"/> — <see cref="Compra"/>,
/// <see cref="Transferencia"/> e <see cref="Inventario"/> son valores reservados sin escritor
/// todavía (design: Table Shapes — write path B).
/// </summary>
public enum MotivoStock
{
    Venta,
    Compra,
    Anulacion,
    Ajuste,
    Transferencia,
    Inventario
}
