using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Domain.Caja;
using Ways.Domain.Pagos;

namespace Ways.Application.Pagos;

/// <summary>Una sola proyección de <see cref="PagoListado"/> para <c>GET /api/gastos</c> y el
/// detalle del turno. Los ids de proveedor y área salen crudos (no anulados por una baja lógica del
/// catálogo): son los valores con los que se precarga la edición, y anularlos haría que guardarla
/// cambie el proveedor del pago sin que nadie lo haya pedido.</summary>
internal static class ProyeccionesDePago
{
    public static Expression<Func<Pago, PagoListado>> Listado(IWaysDbContext db) => g =>
        new PagoListado(
            g.Id,
            g.IdPuntoVenta,
            g.Fecha,
            g.Categoria,
            db.ItemsPago.Where(i => i.IdPago == g.Id && i.DeletedAt == null).OrderBy(i => i.Id)
                .Select(i => i.IdMedioPago).FirstOrDefault(),
            g.Importe,
            g.OrigenFondos,
            g.IdTurnoCaja,
            g.IdTurnoCaja != null && db.TurnosCaja.Any(t => t.Id == g.IdTurnoCaja && t.Estado == EstadoTurno.Abierto),
            g.IdProveedor,
            g.IdArea,
            g.Concepto,
            g.Detalle,
            g.NumeroFactura,
            g.IdComprobanteCompra,
            g.IdMovimientoPersonal,
            null!);

    /// <summary>Completa las líneas por medio de una página ya materializada con UNA lectura de los ítems vivos de todos sus
    /// pagos (el nombre del medio sale de una subquery correlacionada: un medio dado de baja deja el nombre en <c>null</c>).</summary>
    public static async Task<IReadOnlyList<PagoListado>> ConLineasAsync(
        IWaysDbContext db, IReadOnlyList<PagoListado> pagos, CancellationToken ct)
    {
        if (pagos.Count == 0)
        {
            return pagos;
        }

        var ids = pagos.Select(p => p.Id).ToList();
        var lineas = await db.ItemsPago
            .Where(i => ids.Contains(i.IdPago) && i.DeletedAt == null)
            .OrderBy(i => i.Id)
            .Select(i => new
            {
                i.IdPago,
                Linea = new LineaDePagoListada(
                    i.Id,
                    i.IdMedioPago,
                    db.MediosPago.Where(m => m.Id == i.IdMedioPago).Select(m => m.Nombre).FirstOrDefault(),
                    i.Importe,
                    i.Referencia)
            })
            .ToListAsync(ct);

        var porPago = lineas.ToLookup(x => x.IdPago, x => x.Linea);
        return pagos.Select(p => p with { Lineas = porPago[p.Id].ToList() }).ToList();
    }
}
