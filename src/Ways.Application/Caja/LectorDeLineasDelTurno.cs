using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Pagos;
using Ways.Application.Ventas;
using Ways.Domain.Ventas;

namespace Ways.Application.Caja;

/// <summary>
/// Líneas del detalle de turno (stage-11-exportacion-reportes, spec historico-de-cajas: G2 Detail
/// Reuses ResumenDeTurno Plus Ticket And Pago Listings) — dos lecturas indexadas llanas por
/// <c>id_turno_caja</c>, ninguna derivación nueva: los tickets del turno (anulados excluidos,
/// MISMO filtro que <see cref="ServicioDeResumenDeTurno"/>/<see cref="LectorDeContenidoDeResumen"/>
/// — spec: Anulados Are Excluded From The Derivation) y sus pagos, sin filtrar por categoría.
/// Reusa los DTOs de listado ya existentes (<see cref="ComprobanteListado"/>/
/// <see cref="PagoListado"/>) — mismo shape que <c>GET /api/ventas</c>/<c>GET /api/gastos</c>,
/// nunca un tercer contrato para la misma fila.
/// </summary>
public class LectorDeLineasDelTurno(IWaysDbContext db)
{
    public async Task<IReadOnlyList<ComprobanteListado>> LeerTicketsAsync(int idTurnoCaja, CancellationToken ct = default)
    {
        var crudos = await db.ComprobantesVenta
            .Where(c => c.IdTurnoCaja == idTurnoCaja && c.Estado == EstadoComprobante.Emitido)
            .OrderBy(c => c.Fecha).ThenBy(c => c.Id)
            .Select(c => new
            {
                c.Id, c.Numero, c.Estado, c.Fecha, c.IdPuntoVenta, c.IdCliente, c.Total,
                c.DescuentoManualTotal, c.RecargoManualTotal
            })
            .ToListAsync(ct);

        // NumeroDeComprobante.Formatear no traduce a SQL (mismo criterio que
        // ServicioDeVentas.ListarAsync): se arma en memoria después de traer la página.
        return crudos
            .Select(c => new ComprobanteListado(
                c.Id, c.Numero, NumeroDeComprobante.Formatear(c.IdPuntoVenta, c.Numero), c.Estado, c.Fecha,
                c.IdPuntoVenta, c.IdCliente, c.Total, c.DescuentoManualTotal, c.RecargoManualTotal))
            .ToList();
    }

    public async Task<IReadOnlyList<PagoListado>> LeerPagosAsync(int idTurnoCaja, CancellationToken ct = default)
    {
        var pagos = await db.Pagos
            .Where(g => g.IdTurnoCaja == idTurnoCaja)
            .OrderBy(g => g.Fecha).ThenBy(g => g.Id)
            .Select(ProyeccionesDePago.Listado(db))
            .ToListAsync(ct);

        return await ProyeccionesDePago.ConLineasAsync(db, pagos, ct);
    }
}
