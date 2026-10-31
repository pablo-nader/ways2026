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

        var porPago = await LineasPorPagoAsync(db, pagos.Select(p => p.Id).ToList(), ct);
        return pagos.Select(p => p with { Lineas = porPago[p.Id].ToList() }).ToList();
    }

    /// <summary>Lo mismo que <see cref="ConLineasAsync(IWaysDbContext, IReadOnlyList{PagoListado}, CancellationToken)"/>
    /// para el historial de administración: una sola lectura de los ítems vivos de toda la página.</summary>
    public static async Task<IReadOnlyList<PagoDeAdministracionListado>> ConLineasAsync(
        IWaysDbContext db, IReadOnlyList<PagoDeAdministracionListado> pagos, CancellationToken ct)
    {
        if (pagos.Count == 0)
        {
            return pagos;
        }

        var ids = pagos.Select(p => p.Id).ToList();
        var porPago = await LineasPorPagoAsync(db, ids, ct);
        var aplicaciones = await AplicacionesPorPagoAsync(db, ids, ct);
        var redondeos = await LectorDeDisponiblePorPago.LeerRedondeoAsync(db, ids, ct);

        return pagos
            .Select(p =>
            {
                var delPago = aplicaciones[p.Id].ToList();
                var aplicado = delPago.Sum(a => a.Importe);
                return p with
                {
                    Lineas = porPago[p.Id].ToList(),
                    Aplicado = aplicado,
                    // Solo un pago de proveedor tiene crédito; el resto (caja, personal) no se aplica a compras.
                    Disponible = p.Categoria == CategoriaPago.Proveedor
                        ? LectorDeDisponiblePorPago.Disponible(p.Importe, aplicado, redondeos.GetValueOrDefault(p.Id, 0m))
                        : null,
                    Aplicaciones = delPago
                };
            })
            .ToList();
    }

    /// <summary>Aplicaciones vivas de toda la página en UNA lectura; el número de la compra sale de una subquery
    /// correlacionada, así que una compra no visible deja el número en <c>null</c> sin tirar la fila.</summary>
    private static async Task<ILookup<int, AplicacionDePagoListada>> AplicacionesPorPagoAsync(
        IWaysDbContext db, List<int> ids, CancellationToken ct)
    {
        var filas = await db.AplicacionesPago
            .Where(a => ids.Contains(a.IdPago))
            .OrderBy(a => a.Id)
            .Select(a => new
            {
                a.IdPago,
                Aplicacion = new AplicacionDePagoListada(
                    a.Id,
                    a.IdComprobanteCompra,
                    db.ComprobantesCompra.Where(c => c.Id == a.IdComprobanteCompra).Select(c => c.NumeroExterno).FirstOrDefault(),
                    a.Importe,
                    a.Fecha)
            })
            .ToListAsync(ct);

        return filas.ToLookup(x => x.IdPago, x => x.Aplicacion);
    }

    private static async Task<ILookup<int, LineaDePagoListada>> LineasPorPagoAsync(
        IWaysDbContext db, List<int> ids, CancellationToken ct)
    {
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

        return lineas.ToLookup(x => x.IdPago, x => x.Linea);
    }
}
