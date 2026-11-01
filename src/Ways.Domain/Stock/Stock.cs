namespace Ways.Domain.Stock;

/// <summary>
/// Caché de saldo de stock por <c>(articulo, sucursal)</c> (doc 10 §6): los puntos de venta de una misma sucursal
/// comparten esta fila. PK-only, sin auditoría ni baja lógica — mismo criterio que
/// <see cref="Ways.Domain.Ventas.NumeracionComprobante"/>: la PK natural (<see cref="IdArticulo"/>,
/// <see cref="IdSucursal"/>) ya identifica la fila, <see cref="IdTenant"/> es columna no-key solo para RLS (filtro
/// manual en <c>WaysDbContext.AplicarFiltroDeTenantEnStock</c>, esta clase no hereda de
/// <see cref="Common.EntidadTenant"/>).
///
/// <see cref="Cantidad"/> es un CACHÉ MANTENIDO del ledger de <see cref="MovimientoStock"/> (doc 10 principio 7): se
/// escribe con <c>INSERT ... ON CONFLICT (id_articulo, id_sucursal) DO UPDATE SET cantidad = stock.cantidad + $delta
/// RETURNING cantidad</c> — un único statement atómico que toma el lock de fila y devuelve el post-estado, nunca un
/// read-modify-write vía <c>SaveChangesAsync</c>. Sin CHECK sobre <see cref="Cantidad"/> — stock negativo está
/// permitido (legacy parity).
///
/// <see cref="IdTenant"/> NO se auto-estampa (no hereda <see cref="Common.EntidadTenant"/>) — el único escritor
/// (<c>Ways.Application.Stock.EscritorDeStock</c>) lo asigna a mano; el RLS <c>WITH CHECK</c> rechaza el INSERT con
/// SQLSTATE 42501 si falta.
/// </summary>
public class Stock
{
    public int IdArticulo { get; set; }
    public int IdSucursal { get; set; }
    public int IdTenant { get; set; }

    public decimal Cantidad { get; set; }

    public decimal? Minimo { get; set; }
    public decimal? Reposicion { get; set; }
}
