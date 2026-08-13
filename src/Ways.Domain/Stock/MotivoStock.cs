namespace Ways.Domain.Stock;

/// <summary>
/// Motivo de un <see cref="MovimientoStock"/> (doc 10 §6). Enum nativo de Postgres
/// (<c>motivo_stock</c>). <see cref="Compra"/> abre camino de escritura en stage-8 Slice 2
/// (<c>ServicioDeCompras.ConfirmarAsync</c>/<c>AnularAsync</c>); <see cref="Transferencia"/> e
/// <see cref="Inventario"/> lo abren en Slice 3 (<c>ServicioDeStock.TransferirAsync</c>/
/// <c>ContarAsync</c>).
///
/// <see cref="Decomiso"/> y <see cref="Reclasificacion"/> (etapa 12, proposal decisiones 3/9,
/// gate §D): dos valores agregados por esta migración vía <c>ALTER TYPE ... ADD VALUE</c> —
/// ningún escritor de esta slice los usa todavía (schema + seed gate); ningún <c>Sql()</c> de
/// esta misma migración puede nombrarlos (Postgres prohíbe usar un valor de enum agregado
/// dentro de la misma transacción que lo agregó). <see cref="Decomiso"/> abre camino de
/// escritura recién en slice 11 (<c>ServicioDeStock.EjecutarDecomisoAsync</c>);
/// <see cref="Reclasificacion"/> en slice 4 (<c>ServicioDeLotes.ReconciliarAsync</c>).
/// </summary>
public enum MotivoStock
{
    Venta,
    Compra,
    Anulacion,
    Ajuste,
    Transferencia,
    Inventario,
    Decomiso,
    Reclasificacion
}
