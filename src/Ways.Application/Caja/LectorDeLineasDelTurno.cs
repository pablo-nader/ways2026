using Microsoft.EntityFrameworkCore;
using Ways.Application.Abstracciones;
using Ways.Application.Pagos;
using Ways.Application.Ventas;

namespace Ways.Application.Caja;

/// <summary>
/// Líneas del detalle de turno (stage-11-exportacion-reportes, spec historico-de-cajas: G2 Detail
/// Reuses ResumenDeTurno Plus Ticket And Pago Listings) — dos lecturas indexadas llanas por
/// <c>id_turno_caja</c>, ninguna derivación nueva: los tickets del turno, con la MISMA proyección que la
/// pantalla "Ventas del turno" del POS (<see cref="VentaDeTurnoListado"/>, incluye anulados con su estado), y
/// sus pagos, sin filtrar por categoría. Reusa los DTOs de listado ya existentes (<see cref="PagoListado"/> para los
/// pagos) — nunca un tercer contrato para la misma fila.
/// </summary>
public class LectorDeLineasDelTurno(IWaysDbContext db)
{
    public Task<IReadOnlyList<VentaDeTurnoListado>> LeerTicketsAsync(int idTurnoCaja, CancellationToken ct = default) =>
        ServicioDeVentas.LeerVentasDelTurnoAsync(db, idTurnoCaja, ct);

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
