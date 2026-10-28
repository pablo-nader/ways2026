using System.Linq.Expressions;
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
            g.IdMedioPago,
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
            g.IdMovimientoPersonal);
}
