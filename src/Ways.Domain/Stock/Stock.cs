namespace Ways.Domain.Stock;

/// <summary>
/// Caché de saldo de stock por <c>(articulo, punto_venta)</c> (doc 10 §6, design: Table Shapes —
/// write path B). PK-only, sin auditoría ni baja lógica — mismo criterio que
/// <see cref="Ways.Domain.Ventas.NumeracionComprobante"/>: la PK natural (<see cref="IdArticulo"/>,
/// <see cref="IdPuntoVenta"/>) ya identifica la fila, <see cref="IdTenant"/> es columna no-key
/// solo para RLS (filtro manual en <c>WaysDbContext.AplicarFiltroDeTenantEnStock</c>, esta clase
/// no hereda de <see cref="Common.EntidadTenant"/>).
///
/// <see cref="Cantidad"/> es un CACHÉ MANTENIDO del ledger de <see cref="MovimientoStock"/> (doc
/// 10 principio 7): se escribe con <c>INSERT ... ON CONFLICT (id_articulo, id_punto_venta) DO
/// UPDATE SET cantidad = stock.cantidad + $delta RETURNING cantidad</c> — un único statement
/// atómico que toma el lock de fila y devuelve el post-estado (design decisión 1), nunca un
/// read-modify-write vía <c>SaveChangesAsync</c>. Sin CHECK sobre <see cref="Cantidad"/> — stock
/// negativo está permitido (legacy parity, proposal decisión 7).
///
/// <see cref="IdTenant"/> NO se auto-estampa (no hereda <see cref="Common.EntidadTenant"/>) —
/// quien escriba la fila (Slice 4/5, <c>ServicioDeVentas</c>/<c>ServicioDeStock</c>) DEBE
/// asignarlo a mano; el RLS <c>WITH CHECK</c> rechaza el INSERT con SQLSTATE 42501 si falta.
/// </summary>
public class Stock
{
    public int IdArticulo { get; set; }
    public int IdPuntoVenta { get; set; }
    public int IdTenant { get; set; }

    public decimal Cantidad { get; set; }

    public decimal? Minimo { get; set; }
    public decimal? Reposicion { get; set; }
}
