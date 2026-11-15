using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Domain.Ventas;

namespace Ways.Application.Ventas;

/// <summary>Lo que hace falta leer de un ticket de venta para juzgar una devolución (<see cref="DevolucionDeTicket"/>): sus items y lo
/// que ya devolvieron las notas de crédito emitidas asociadas. Son lecturas por proyección, sin entidades rastreadas.</summary>
public static class LectorDeDevolucionesDelTicket
{
    /// <summary>Los items del ticket que vendieron mercadería (cantidad positiva y con artículo: el envío no se devuelve).</summary>
    public static async Task<IReadOnlyList<ItemVendido>> LeerVendidosAsync(
        IWaysDbContext db, int idVenta, CancellationToken ct)
    {
        var vendidos = await db.ItemsComprobanteVenta
            .Where(i => i.IdComprobanteVenta == idVenta && i.Cantidad > 0m && i.IdArticulo != null)
            .Select(i => new ItemVendido(
                i.Id, i.Orden, i.IdArticulo!.Value, i.PorImporte, i.Cantidad, i.PrecioUnitario, i.Total, i.IdCombo,
                i.IdOferta, i.IdListaPrecio, i.IdAlicuotaIva, i.PorcentajeIva, i.IdLote))
            .ToListAsync(ct);

        return vendidos;
    }

    /// <summary>Las unidades que devolvieron las notas de crédito EMITIDAS asociadas al ticket, por artículo (o por importe): una nota
    /// anulada libera lo que había devuelto. Un espejo fiscal no es una devolución: su asociado es la factura, no la venta.</summary>
    public static async Task<IReadOnlyList<UnidadesYaDevueltas>> LeerYaDevueltasAsync(
        IWaysDbContext db, int idVenta, CancellationToken ct)
    {
        var filas = await (
                from i in db.ItemsComprobanteVenta
                join c in db.ComprobantesVenta.SinEspejosFiscales() on i.IdComprobanteVenta equals c.Id
                where c.IdComprobanteAsociado == idVenta
                    && c.Estado == EstadoComprobante.Emitido
                    && i.IdArticulo != null
                    && i.Cantidad < 0m
                select new { IdArticulo = i.IdArticulo!.Value, i.PorImporte, i.PrecioUnitario, i.Cantidad, i.Total })
            .ToListAsync(ct);

        return
        [
            .. filas
                .GroupBy(f => (f.IdArticulo, f.PorImporte, Precio: f.PorImporte ? f.PrecioUnitario : 0m))
                .Select(g => new UnidadesYaDevueltas(
                    g.Key.IdArticulo, g.Key.PorImporte, g.Key.Precio, -g.Sum(f => f.Cantidad), Credito: -g.Sum(f => f.Total)))
        ];
    }
}
