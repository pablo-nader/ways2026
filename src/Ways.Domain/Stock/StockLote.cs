namespace Ways.Domain.Stock;

/// <summary>
/// Caché de saldo de lote por <c>(articulo, punto_venta, lote)</c> (doc 10 §6, proposal gate §B).
/// PK-only, sin auditoría ni baja lógica — el mismo criterio que <see cref="Stock"/>: la PK
/// natural (<see cref="IdArticulo"/>, <see cref="IdPuntoVenta"/>, <see cref="IdLote"/>) ya
/// identifica la fila, <see cref="IdTenant"/> es columna no-key solo para RLS (filtro manual en
/// <c>WaysDbContext.AplicarFiltroDeTenantEnStockLote</c>, design decisión 20 — esta clase no
/// hereda de <see cref="Common.EntidadTenant"/>).
///
/// <see cref="Cantidad"/> es un CACHÉ MANTENIDO del ledger de <see cref="MovimientoStock"/>
/// (proposal decisión 5): se escribe con <c>INSERT ... ON CONFLICT (id_articulo, id_punto_venta,
/// id_lote) DO UPDATE SET cantidad = stock_lotes.cantidad + $delta RETURNING cantidad</c> — la
/// misma forma atómica que <see cref="Stock.Cantidad"/> usa, nunca un read-modify-write vía
/// <c>SaveChangesAsync</c>. Sin CHECK sobre <see cref="Cantidad"/> — un saldo de lote negativo
/// está permitido en el mostrador (legacy parity, proposal decisión 7), refusado solo en
/// caminos de back-office (transferencia, decomiso, en Application).
///
/// <see cref="IdTenant"/> NO se auto-estampa (no hereda <c>EntidadTenant</c>) — quien escriba la
/// fila (slices 4-12) DEBE asignarlo a mano; el RLS <c>WITH CHECK</c> rechaza el INSERT con
/// SQLSTATE 42501 si falta.
/// </summary>
public class StockLote
{
    public int IdArticulo { get; set; }
    public int IdPuntoVenta { get; set; }
    public int IdLote { get; set; }
    public int IdTenant { get; set; }

    public decimal Cantidad { get; set; }
}
