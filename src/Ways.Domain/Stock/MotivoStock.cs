namespace Ways.Domain.Stock;

/// <summary>
/// Motivo de un <see cref="MovimientoStock"/> (doc 10 §6). Enum nativo de Postgres
/// (<c>motivo_stock</c>). <see cref="Compra"/> abre camino de escritura en stage-8 Slice 2
/// (<c>ServicioDeCompras.ConfirmarAsync</c>/<c>AnularAsync</c>); <see cref="Transferencia"/> e
/// <see cref="Inventario"/> lo abren en Slice 3 (<c>ServicioDeStock.TransferirAsync</c>/
/// <c>ContarAsync</c>) — ningún escritor nuevo se agrega en esta slice (schema + seed gate).
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
